using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LilithBridge;

internal sealed record SensoryRoutingHint(string RecommendedEffort, string Reason);

internal sealed record SensoryPendingAction(string Action, string Reason);

internal sealed record SensoryPendingProactive(string Kind, string Reason, int Seq);

internal sealed record SensorySnapshot(
    int Seq,
    bool Available,
    bool HostConnected,
    string InterruptCost,
    string InterruptReason,
    string RecommendedEffort,
    string RecommendedEffortReason,
    bool FishingActive,
    string TailText,
    int MaxBufferSeq,
    SensoryPendingAction? PendingAction,
    SensoryPendingProactive? PendingProactive)
{
    internal static readonly SensorySnapshot Empty = new(
        0, false, false, "relaxed", "offline", "off", "offline", false, "", 0, null, null);
}

internal sealed record SensoryHostReport(
    int HostPid,
    int[]? LilithRect,
    bool IsSleeping,
    bool IsNativeQuiet,
    bool IsBubbleActive,
    bool IsInputOpen,
    int InputLength,
    string? SilentAction,
    string? SilentReason,
    string[]? MediaAllowedSources);

/// <summary>
/// Non-blocking out-of-process loopback client for LilithBridge's modular Sensory Sidecar.
/// Uses monotonic Stopwatch clocks exclusively and degrades silently when disabled or offline.
/// </summary>
internal sealed class SensorySidecarClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly object _gate = new();
    private Process? _ownedProcess;
    private volatile SensorySnapshot _latest = SensorySnapshot.Empty;
    private long _lastPollTicks;
    private long _lastSnapshotOkTicks;
    private long _backoffUntilTicks;
    private long _lastHostPushTicks;
    private int _pollInFlight;
    private bool _launchAttempted;
    private bool _disposed;
    private string _endpoint = "http://127.0.0.1:6188";

    internal SensorySidecarClient(HttpMessageHandler? handler = null)
    {
        _http = handler != null ? new HttpClient(handler) : new HttpClient();
        _http.Timeout = TimeSpan.FromSeconds(2);
    }

    internal SensorySnapshot Latest => _latest;

    internal static bool IsFreshSnapshot(long lastOkTicks, double maxAgeSeconds = 8.0)
    {
        if (lastOkTicks <= 0) return false;
        double elapsed = (double)(Stopwatch.GetTimestamp() - lastOkTicks) / Stopwatch.Frequency;
        return elapsed <= maxAgeSeconds;
    }

    internal void Tick(BridgeConfig config, SensoryHostReport hostReport)
    {
        if (_disposed || config == null || !config.SensoryEnabled)
        {
            _latest = SensorySnapshot.Empty;
            return;
        }
        string targetEndpoint = NormalizeLoopbackEndpoint(config.SensoryEndpoint);
        lock (_gate)
        {
            if (!string.Equals(_endpoint, targetEndpoint, StringComparison.OrdinalIgnoreCase))
            {
                _endpoint = targetEndpoint;
                _backoffUntilTicks = 0;
            }
        }

        long nowTicks = Stopwatch.GetTimestamp();
        if (_backoffUntilTicks > 0 && nowTicks < _backoffUntilTicks)
            return;

        EnsureAutoStartedIfConfigured(config);

        double sincePoll = _lastPollTicks <= 0 ? 99.0 : (double)(nowTicks - _lastPollTicks) / Stopwatch.Frequency;
        if (sincePoll >= 1.2 && Interlocked.CompareExchange(ref _pollInFlight, 1, 0) == 0)
        {
            _lastPollTicks = nowTicks;
            string url = _endpoint + "/api/snapshot";
            int timeoutMs = Math.Clamp(config.SensoryTimeoutMs, 5, 250);
            _ = Task.Run(async () =>
            {
                try
                {
                    await RefreshSnapshotAsync(url, Math.Max(timeoutMs, 150)).ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Exchange(ref _pollInFlight, 0);
                }
            });
        }

        double sincePush = _lastHostPushTicks <= 0 ? 99.0 : (double)(nowTicks - _lastHostPushTicks) / Stopwatch.Frequency;
        if (!string.IsNullOrEmpty(hostReport.SilentAction) || sincePush >= 1.5)
        {
            _lastHostPushTicks = nowTicks;
            string postUrl = _endpoint + "/api/host-state";
            _ = Task.Run(() => PostJsonAsync(postUrl, new
            {
                host_pid = hostReport.HostPid,
                lilith_rect = hostReport.LilithRect,
                is_sleeping = hostReport.IsSleeping,
                is_native_quiet = hostReport.IsNativeQuiet,
                is_bubble_active = hostReport.IsBubbleActive,
                is_input_open = hostReport.IsInputOpen,
                input_length = hostReport.InputLength,
                silent_action = hostReport.SilentAction ?? "",
                silent_reason = hostReport.SilentReason ?? "",
                media_allowed_sources = hostReport.MediaAllowedSources
            }, 250));
        }
    }

    internal SensorySnapshot CaptureRequestSnapshot(BridgeConfig config)
    {
        if (_disposed || !config.SensoryEnabled)
            return SensorySnapshot.Empty;

        // Prefer cached snapshot when very fresh (< 0.8s) to eliminate even a 1ms wait on submit
        if (_lastSnapshotOkTicks > 0)
        {
            double age = (double)(Stopwatch.GetTimestamp() - _lastSnapshotOkTicks) / Stopwatch.Frequency;
            if (age <= 0.8 && _latest.Available)
                return _latest;
        }

        long nowTicks = Stopwatch.GetTimestamp();
        if (_backoffUntilTicks > 0 && nowTicks < _backoffUntilTicks)
            return IsFreshSnapshot(_lastSnapshotOkTicks) ? _latest : SensorySnapshot.Empty;

        string url = NormalizeLoopbackEndpoint(config.SensoryEndpoint) + "/api/snapshot";
        int budgetMs = Math.Clamp(config.SensoryTimeoutMs, 5, 100);
        try
        {
            using var cts = new CancellationTokenSource(budgetMs);
            var task = RefreshSnapshotAsync(url, budgetMs, cts.Token);
            if (task.Wait(budgetMs + 5))
                return task.Result;
        }
        catch
        {
            // Never delay or break player input because sidecar was slow.
        }
        return IsFreshSnapshot(_lastSnapshotOkTicks) ? _latest : SensorySnapshot.Empty;
    }

    internal void AcknowledgeConsumed(BridgeConfig config, int consumedSeq, string? consumedAction = null, bool consumedProactive = false)
    {
        if (_disposed || !config.SensoryEnabled) return;
        if (consumedSeq <= 0 && string.IsNullOrEmpty(consumedAction) && !consumedProactive) return;
        string url = NormalizeLoopbackEndpoint(config.SensoryEndpoint) + "/api/snapshot/ack";
        _ = Task.Run(() => PostJsonAsync(url, new
        {
            consumed_seq = consumedSeq,
            consumed_action = consumedAction ?? "",
            consumed_proactive = consumedProactive
        }, 250));
    }

    private async Task<SensorySnapshot> RefreshSnapshotAsync(string url, int timeoutMs, CancellationToken externalToken = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
            cts.CancelAfter(timeoutMs);
            using var resp = await _http.GetAsync(url, cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                MarkBackoff();
                return SensorySnapshot.Empty;
            }
            string json = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            var parsed = ParseSnapshot(json);
            _latest = parsed;
            _lastSnapshotOkTicks = Stopwatch.GetTimestamp();
            _backoffUntilTicks = 0;
            return parsed;
        }
        catch
        {
            MarkBackoff();
            return SensorySnapshot.Empty;
        }
    }

    internal static SensorySnapshot ParseSnapshot(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        int seq = root.TryGetProperty("seq", out var sEl) && sEl.TryGetInt32(out int sv) ? sv : 0;
        bool hostConnected = root.TryGetProperty("host_connected", out var hc) && hc.ValueKind == JsonValueKind.True;

        if (!root.TryGetProperty("arbiter", out var arb) || arb.ValueKind != JsonValueKind.Object)
            return SensorySnapshot.Empty;

        string icost = ReadStr(arb, "interrupt_cost", "relaxed");
        string ireason = ReadStr(arb, "interrupt_reason", "");
        string eff = ReadStr(arb, "recommended_effort", "off");
        string effReason = ReadStr(arb, "recommended_effort_reason", "");
        bool fishing = arb.TryGetProperty("fishing_active", out var fa) && fa.ValueKind == JsonValueKind.True;
        string tailText = ReadStr(arb, "tail_text", "").Trim();

        int maxBufSeq = 0;
        if (arb.TryGetProperty("buffer_items", out var bufEl) && bufEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in bufEl.EnumerateArray())
            {
                if (item.TryGetProperty("seq", out var bSeqEl) && bSeqEl.TryGetInt32(out int bSeq) && bSeq > maxBufSeq)
                    maxBufSeq = bSeq;
            }
        }

        SensoryPendingAction? pendingAction = null;
        if (arb.TryGetProperty("pending_silent_action", out var paEl) && paEl.ValueKind == JsonValueKind.Object)
        {
            string act = ReadStr(paEl, "action", "");
            if (act.Length > 0)
                pendingAction = new SensoryPendingAction(act, ReadStr(paEl, "reason", ""));
        }

        SensoryPendingProactive? pendingProactive = null;
        if (arb.TryGetProperty("pending_proactive", out var ppEl) && ppEl.ValueKind == JsonValueKind.Object)
        {
            string kind = ReadStr(ppEl, "kind", "");
            string reason = ReadStr(ppEl, "reason", "");
            int pSeq = ppEl.TryGetProperty("seq", out var pSeqEl) && pSeqEl.TryGetInt32(out int pv) ? pv : 0;
            if (kind.Length > 0 && reason.Length > 0)
                pendingProactive = new SensoryPendingProactive(kind, reason, pSeq);
        }

        return new SensorySnapshot(
            seq, true, hostConnected, icost, ireason, eff, effReason, fishing, tailText,
            maxBufSeq, pendingAction, pendingProactive);
    }

    private async Task PostJsonAsync(string url, object body, int timeoutMs)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            string json = JsonSerializer.Serialize(body);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(url, content, cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) MarkBackoff();
        }
        catch
        {
            MarkBackoff();
        }
    }

    private void MarkBackoff()
    {
        _backoffUntilTicks = Stopwatch.GetTimestamp() + 5 * Stopwatch.Frequency;
    }

    private void EnsureAutoStartedIfConfigured(BridgeConfig config)
    {
        if (!config.SensoryAutoStart || _launchAttempted) return;
        if (string.IsNullOrWhiteSpace(config.SensoryPythonPath) || string.IsNullOrWhiteSpace(config.SensoryScriptPath))
            return;
        _launchAttempted = true;
        try
        {
            string root = Path.GetFullPath(config.DataDirectory);
            string py = Path.GetFullPath(Path.IsPathRooted(config.SensoryPythonPath) ? config.SensoryPythonPath : Path.Combine(root, config.SensoryPythonPath));
            string script = Path.GetFullPath(Path.IsPathRooted(config.SensoryScriptPath) ? config.SensoryScriptPath : Path.Combine(root, config.SensoryScriptPath));
            if (!File.Exists(py) || !File.Exists(script)) return;

            var uri = new Uri(NormalizeLoopbackEndpoint(config.SensoryEndpoint));
            if (IsLoopbackListening(uri.Host, uri.Port))
                return;

            var info = new ProcessStartInfo(py)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(script)!
            };
            info.ArgumentList.Add("-u");
            info.ArgumentList.Add(script);
            info.ArgumentList.Add("--host");
            info.ArgumentList.Add(uri.Host);
            info.ArgumentList.Add("--port");
            info.ArgumentList.Add(uri.Port.ToString());
            _ownedProcess = Process.Start(info);
        }
        catch
        {
            // Optional launch; external manual run remains supported.
        }
    }

    private static bool IsLoopbackListening(string host, int port)
    {
        try
        {
            using var client = new TcpClient();
            IAsyncResult ar = client.BeginConnect(host, port, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(40))
                return false;
            client.EndConnect(ar);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    internal static string NormalizeLoopbackEndpoint(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "http://127.0.0.1:6188";
        if (Uri.TryCreate(raw.Trim(), UriKind.Absolute, out Uri? u)
            && (u.Host == "127.0.0.1" || string.Equals(u.Host, "localhost", StringComparison.OrdinalIgnoreCase)))
            return $"{u.Scheme}://{u.Host}:{u.Port}";
        return "http://127.0.0.1:6188";
    }

    private static string ReadStr(JsonElement el, string prop, string fallback) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            bool ownedAlive = _ownedProcess is { HasExited: false };
            if (_latest.Available || ownedAlive)
            {
                string url = NormalizeLoopbackEndpoint(_endpoint) + "/api/shutdown";
                using var cts = new CancellationTokenSource(220);
                string body = ownedAlive ? "{\"clean\":true,\"exit\":true}" : "{\"clean\":true,\"exit\":false}";
                using var content = new StringContent(body, Encoding.UTF8, "application/json");
                _http.PostAsync(url, content, cts.Token).Wait(250);
            }
            if (ownedAlive && _ownedProcess != null)
                _ownedProcess.WaitForExit(350);
        }
        catch { }
        try
        {
            if (_ownedProcess is { HasExited: false })
                _ownedProcess.Kill(entireProcessTree: true);
        }
        catch { }
        try { _http.Dispose(); } catch { }
    }
}
