using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LilithBridge;

internal sealed record SttWorkerEvent(
    string Kind,
    string RecordingId,
    string Text,
    float Level,
    string ErrorCode,
    string Reason,
    string ModelId,
    string MicName,
    bool MicAvailable,
    int DurationMs,
    double InferMs);

/// <summary>
/// Private out-of-process offline STT worker communicating via UTF-8 NDJSON over stdin/stdout.
/// Decoupled from sensory-sidecar; opens no HTTP ports; raw audio never leaves Python RAM.
/// </summary>
internal sealed class SpeechToTextWorker : IDisposable
{
    private readonly string _python;
    private readonly string _script;
    private readonly string _model;
    private readonly string _manifest;
    private readonly string _vadModel;
    private readonly string _language;
    private readonly bool _useItn;
    private readonly int _numThreads;
    private readonly int _partialIntervalMs;
    private readonly int _maxDurationSeconds;
    private readonly bool _builtinLexicon;
    private readonly string _replacementsB64;
    private readonly bool _testMode;
    private readonly bool _fakeAsr;
    private readonly Action<SttWorkerEvent> _onEvent;

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private TaskCompletionSource<SttWorkerEvent> _handshake = NewHandshake();
    private Process? _process;
    private ChildProcessJobObject? _jobObject;
    private int _restartUsed;
    private volatile bool _ready;
    private volatile bool _disposed;

    internal string Epoch { get; private set; } = Guid.NewGuid().ToString("N");
    internal bool Ready { get => _ready; private set => _ready = value; }
    internal string Status { get; private set; } = "not_started";
    internal string ModelId { get; private set; } = "";
    internal string MicName { get; private set; } = "";
    internal bool MicAvailable { get; private set; }
    internal double LoadMs { get; private set; }
    internal int? ProcessId => _process is { HasExited: false } p ? p.Id : null;
    internal Task Initialization { get; private set; } = Task.CompletedTask;

    internal SpeechToTextWorker(
        BridgeConfig config,
        string dataRoot,
        Action<SttWorkerEvent> onEvent,
        bool testMode = false,
        bool fakeAsr = false)
    {
        string root = Path.GetFullPath(dataRoot);
        string Resolve(string value) =>
            string.IsNullOrWhiteSpace(value)
                ? ""
                : Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(root, value));

        _python = Resolve(config.SttPythonPath);
        _script = Resolve(config.SttWorkerScript);
        _model = Resolve(config.SttModelPath);
        _manifest = string.IsNullOrEmpty(_script)
            ? ""
            : Path.Combine(Path.GetDirectoryName(_script) ?? root, "model-manifest.json");
        _vadModel = Resolve(config.SttVadModelPath);
        _language = string.IsNullOrWhiteSpace(config.SttLanguage) ? "zh" : config.SttLanguage.Trim();
        _useItn = config.SttUseItn;
        _numThreads = Math.Clamp(config.SttNumThreads, 1, 4);
        _partialIntervalMs = Math.Clamp(config.SttPartialIntervalMs, 200, 2000);
        _maxDurationSeconds = Math.Clamp(config.SttMaxDurationSeconds, 10, 300);
        _builtinLexicon = config.SttBuiltinLexicon;
        _replacementsB64 = config.SttReplacements is { Count: > 0 }
            ? Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(config.SttReplacements)))
            : "";
        _onEvent = onEvent ?? throw new ArgumentNullException(nameof(onEvent));
        _testMode = testMode;
        _fakeAsr = fakeAsr;
    }

    private static TaskCompletionSource<SttWorkerEvent> NewHandshake() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal void Start()
    {
        if (Status != "not_started" || _disposed)
            return;
        Status = "initializing";
        Initialization = Task.Run(InitializeAsync);
    }

    private async Task InitializeAsync()
    {
        var watch = Stopwatch.StartNew();
        try
        {
            _lifetime.Token.ThrowIfCancellationRequested();
            if (!File.Exists(_python))
                throw new FileNotFoundException("Missing STT Python executable", _python);
            if (!File.Exists(_script))
                throw new FileNotFoundException("Missing STT worker script", _script);
            if (!_fakeAsr && !Directory.Exists(_model))
                throw new DirectoryNotFoundException("Missing STT model directory: " + _model);

            _handshake = NewHandshake();
            Epoch = Guid.NewGuid().ToString("N");

            var info = new ProcessStartInfo(_python)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Path.GetDirectoryName(_script) ?? Path.GetDirectoryName(_python) ?? "."
            };
            info.ArgumentList.Add("-u");
            info.ArgumentList.Add(_script);
            if (!_fakeAsr)
            {
                info.ArgumentList.Add("--model");
                info.ArgumentList.Add(_model);
                if (!string.IsNullOrEmpty(_manifest) && File.Exists(_manifest))
                {
                    info.ArgumentList.Add("--manifest");
                    info.ArgumentList.Add(_manifest);
                }
            }
            if (!string.IsNullOrEmpty(_vadModel) && File.Exists(_vadModel))
            {
                info.ArgumentList.Add("--vad-model");
                info.ArgumentList.Add(_vadModel);
            }
            info.ArgumentList.Add("--language");
            info.ArgumentList.Add(_language);
            if (!_useItn)
                info.ArgumentList.Add("--no-itn");
            info.ArgumentList.Add("--num-threads");
            info.ArgumentList.Add(_numThreads.ToString());
            info.ArgumentList.Add("--partial-interval-ms");
            info.ArgumentList.Add(_partialIntervalMs.ToString());
            info.ArgumentList.Add("--max-duration-seconds");
            info.ArgumentList.Add(_maxDurationSeconds.ToString());
            if (!_builtinLexicon)
                info.ArgumentList.Add("--no-builtin-lexicon");
            if (_replacementsB64.Length > 0)
            {
                info.ArgumentList.Add("--replacements-b64");
                info.ArgumentList.Add(_replacementsB64);
            }
            if (_testMode)
                info.ArgumentList.Add("--test-mode");
            if (_fakeAsr)
                info.ArgumentList.Add("--fake-asr");

            info.Environment["PYTHONUTF8"] = "1";
            info.Environment["OMP_NUM_THREADS"] = _numThreads.ToString();
            info.Environment["HF_HUB_OFFLINE"] = "1";

            _lifetime.Token.ThrowIfCancellationRequested();
            _process = Process.Start(info) ?? throw new IOException("STT worker could not start");
            _jobObject = ChildProcessJobObject.TryAttach(_process);
            if (_disposed)
            {
                StopOwnedProcess();
                throw new OperationCanceledException();
            }
            _process.StandardInput.NewLine = "\n";
            _ = ReadLoopAsync(_process);
            _ = DrainErrorsAsync(_process);

            SttWorkerEvent ready = await _handshake.Task
                .WaitAsync(TimeSpan.FromSeconds(45), _lifetime.Token)
                .ConfigureAwait(false);
            LoadMs = watch.Elapsed.TotalMilliseconds;
            ModelId = ready.ModelId;
            MicName = ready.MicName;
            MicAvailable = ready.MicAvailable;
            if (!_disposed)
            {
                Status = "ready";
                Ready = true;
                EmitSafe(ready);
            }
        }
        catch (Exception ex)
        {
            Ready = false;
            Status = _disposed ? "stopped" : "unavailable:" + ex.GetType().Name;
            StopOwnedProcess();
        }
    }

    private async Task ReadLoopAsync(Process process)
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (!ReferenceEquals(process, _process))
                    return;
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                SttWorkerEvent? parsed = TryParseEvent(line);
                if (parsed == null)
                    continue;
                if (string.Equals(parsed.Kind, "ready", StringComparison.Ordinal))
                {
                    _handshake.TrySetResult(parsed);
                    continue;
                }
                EmitSafe(parsed);
            }
            throw new EndOfStreamException("STT worker exited");
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(process, _process))
                return;
            _handshake.TrySetException(ex);
            bool wasReady = Ready;
            Ready = false;
            if (!_disposed)
            {
                Status = "unavailable:" + ex.GetType().Name;
                EmitSafe(new SttWorkerEvent("worker_exited", "", "", 0f, "worker_exited", "", "", "", false, 0, 0.0));
                if (wasReady)
                    RestartOnce();
            }
        }
    }

    private async Task DrainErrorsAsync(Process process)
    {
        // Drain stderr unconditionally so child pipe never blocks; never copy text to host logs.
        try
        {
            while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is not null) { }
        }
        catch
        {
        }
    }

    private void RestartOnce()
    {
        if (!_disposed && Interlocked.CompareExchange(ref _restartUsed, 1, 0) == 0)
        {
            StopOwnedProcess();
            Status = "initializing";
            Initialization = Task.Run(InitializeAsync);
        }
    }

    internal void SendStartRecording(string recordingId, bool simulate = false)
    {
        var payload = new Dictionary<string, object?>
        {
            ["op"] = "start",
            ["request_id"] = Guid.NewGuid().ToString("N"),
            ["recording_id"] = recordingId
        };
        if (simulate && _testMode)
            payload["simulate"] = true;
        _ = SendCommandAsync(payload, recordingId);
    }

    internal void SendStopRecording(string recordingId)
    {
        var payload = new Dictionary<string, object?>
        {
            ["op"] = "stop",
            ["request_id"] = Guid.NewGuid().ToString("N"),
            ["recording_id"] = recordingId
        };
        _ = SendCommandAsync(payload, recordingId);
    }

    internal void SendCancelRecording(string recordingId)
    {
        var payload = new Dictionary<string, object?>
        {
            ["op"] = "cancel",
            ["request_id"] = Guid.NewGuid().ToString("N"),
            ["recording_id"] = recordingId
        };
        _ = SendCommandAsync(payload, recordingId);
    }

    internal Task SendInjectPcm16Base64ForTestsAsync(string recordingId, string pcm16Base64)
    {
        var payload = new Dictionary<string, object?>
        {
            ["op"] = "inject_pcm_b64",
            ["request_id"] = Guid.NewGuid().ToString("N"),
            ["recording_id"] = recordingId,
            ["pcm16_b64"] = pcm16Base64
        };
        return SendCommandAsync(payload, recordingId);
    }

    private async Task SendCommandAsync(Dictionary<string, object?> payload, string recordingId)
    {
        if (_disposed)
            return;
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
                return;
            Process? process = _process;
            if (process == null || process.HasExited)
            {
                EmitSafe(new SttWorkerEvent("error", recordingId, "", 0f, "worker_unavailable", "", "", "", false, 0, 0.0));
                return;
            }
            string json = JsonSerializer.Serialize(payload);
            await process.StandardInput.WriteLineAsync(json).ConfigureAwait(false);
            await process.StandardInput.FlushAsync().ConfigureAwait(false);
        }
        catch
        {
            if (!_disposed)
                EmitSafe(new SttWorkerEvent("error", recordingId, "", 0f, "worker_unavailable", "", "", "", false, 0, 0.0));
        }
        finally
        {
            _writeLock.Release();
        }
    }

    internal static SttWorkerEvent? TryParseEvent(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            string ev = ReadStr(root, "event");
            string status = ReadStr(root, "status");
            string recId = ReadStr(root, "recording_id");
            string text = ReadStr(root, "text");
            string code = ReadStr(root, "code");
            string reason = ReadStr(root, "reason");
            string modelId = ReadStr(root, "model_id");
            string micName = ReadStr(root, "mic_name");
            bool micAvail = root.TryGetProperty("mic_available", out var ma) && ma.ValueKind == JsonValueKind.True;
            float level = root.TryGetProperty("level", out var lv) && lv.TryGetDouble(out double d) ? (float)d : 0f;
            int durMs = root.TryGetProperty("duration_ms", out var dm) && dm.TryGetInt32(out int dmVal) ? dmVal : 0;
            double inferMs = root.TryGetProperty("infer_ms", out var im) && im.TryGetDouble(out double imVal) ? imVal : 0.0;

            if (string.IsNullOrEmpty(ev))
            {
                if (string.Equals(status, "error", StringComparison.Ordinal))
                    ev = "error";
                else
                    return null;
            }
            return new SttWorkerEvent(ev, recId, text, level, code, reason, modelId, micName, micAvail, durMs, inferMs);
        }
        catch
        {
            return null;
        }
    }

    private static string ReadStr(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() ?? ""
            : "";

    private void EmitSafe(SttWorkerEvent ev)
    {
        try
        {
            _onEvent(ev);
        }
        catch
        {
        }
    }

    private void StopOwnedProcess()
    {
        Process? p = Interlocked.Exchange(ref _process, null);
        ChildProcessJobObject? job = Interlocked.Exchange(ref _jobObject, null);
        try
        {
            if (p != null && !p.HasExited)
            {
                try
                {
                    p.StandardInput.WriteLine("{\"op\":\"shutdown\",\"request_id\":\"dispose\"}");
                    p.StandardInput.Flush();
                    p.WaitForExit(600);
                }
                catch
                {
                }
                if (!p.HasExited)
                    p.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
        finally
        {
            try { job?.Dispose(); } catch { }
            try { p?.Dispose(); } catch { }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Ready = false;
        Status = "stopped";
        try { _lifetime.Cancel(); } catch { }
        StopOwnedProcess();
        try { _lifetime.Dispose(); } catch { }
        try { _writeLock.Dispose(); } catch { }
    }
}
