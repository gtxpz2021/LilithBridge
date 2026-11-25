using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LilithBridge;

/// <summary>
/// Private out-of-process offline TTS worker communicating via UTF-8 NDJSON over stdin/stdout.
/// Mirrors SpeechToTextWorker; owns the local tts_worker.py + audiocpp_server process tree.
/// </summary>
internal sealed class TextToSpeechWorker : IDisposable
{
    private readonly string _python;
    private readonly string _script;
    private readonly string _serverExe;
    private readonly string _model;
    private readonly string _loraDir;
    private readonly float _loraScale;
    private readonly string _refsManifest;
    private readonly bool _bweEnabled;
    private readonly string _bweModel;
    private readonly int _bweThreads;
    private readonly string _bweDevice;
    private readonly string _backend;
    private readonly int _device;
    private readonly int _threads;
    private readonly int _cacheSlots;
    private readonly int _maxClauseChars;
    private readonly bool _warmupEnabled;
    private readonly string _language;
    private readonly float _temperature;
    private readonly float _topP;
    private readonly int _topK;
    private readonly float _repetitionPenalty;
    private readonly bool _subtalkerDoSample;
    private readonly float _subtalkerTemperature;
    private readonly float _subtalkerTopP;
    private readonly int _subtalkerTopK;
    private readonly bool _singleVoiceAnchor;
    private readonly string _pronunciationOverridesB64;
    private readonly bool _testMode;
    private readonly bool _fakeTts;
    private readonly Action<TtsWorkerEvent> _onEvent;

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private TaskCompletionSource<TtsWorkerEvent> _handshake = NewHandshake();
    private Process? _process;
    private int _restartUsed;
    private volatile bool _ready;
    private volatile bool _disposed;

    internal bool Ready { get => _ready; private set => _ready = value; }
    internal string Status { get; private set; } = "not_started";
    internal string ModelId { get; private set; } = "";
    internal string Backend { get; private set; } = "";
    internal int LoraMergedTensors { get; private set; }
    internal int SampleRate { get; private set; }
    internal bool Bwe48k { get; private set; }
    internal string BweProvider { get; private set; } = "";
    internal int MoodCount { get; private set; }
    internal double LoadMs { get; private set; }
    internal int? ProcessId => _process is { HasExited: false } p ? p.Id : null;
    internal Task Initialization { get; private set; } = Task.CompletedTask;

    internal TextToSpeechWorker(
        BridgeConfig config,
        string dataRoot,
        Action<TtsWorkerEvent> onEvent,
        bool testMode = false,
        bool fakeTts = false)
    {
        string root = Path.GetFullPath(dataRoot);
        string Resolve(string value) =>
            string.IsNullOrWhiteSpace(value)
                ? ""
                : Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(root, value));

        _python = Resolve(config.TtsPythonPath);
        _script = Resolve(config.TtsWorkerScript);
        _serverExe = Resolve(config.TtsServerExePath);
        _model = Resolve(config.TtsModelPath);
        _loraDir = Resolve(config.TtsLoraPath);
        _loraScale = Math.Clamp(config.TtsLoraScale, 0.0f, 1.5f);
        _refsManifest = Resolve(config.TtsRefsManifestPath);
        _bweEnabled = config.TtsBweEnabled;
        _bweModel = Resolve(config.TtsBweModelPath);
        _bweThreads = Math.Clamp(config.TtsBweThreads, 1, 8);
        _bweDevice = string.IsNullOrWhiteSpace(config.TtsBweDevice) ? "auto" : config.TtsBweDevice.Trim();
        _backend = string.IsNullOrWhiteSpace(config.TtsBackend) ? "auto" : config.TtsBackend.Trim();
        _device = Math.Clamp(config.TtsDevice, 0, 8);
        _threads = Math.Clamp(config.TtsThreads, 1, 16);
        _cacheSlots = Math.Clamp(config.TtsVoicePromptCacheSlots, 1, 16);
        _maxClauseChars = Math.Clamp(config.TtsMaxClauseChars, 16, 120);
        _warmupEnabled = config.TtsWarmupEnabled;
        _language = string.IsNullOrWhiteSpace(config.TtsLanguage) ? "auto" : config.TtsLanguage.Trim();
        _temperature = Math.Clamp(config.TtsTemperature, 0.05f, 2.0f);
        _topP = Math.Clamp(config.TtsTopP, 0.1f, 1.0f);
        _topK = Math.Clamp(config.TtsTopK, 0, 200);
        _repetitionPenalty = Math.Clamp(config.TtsRepetitionPenalty, 0.8f, 2.0f);
        _subtalkerDoSample = config.TtsSubtalkerDoSample;
        _subtalkerTemperature = Math.Clamp(config.TtsSubtalkerTemperature, 0.05f, 2.0f);
        _subtalkerTopP = Math.Clamp(config.TtsSubtalkerTopP, 0.1f, 1.0f);
        _subtalkerTopK = Math.Clamp(config.TtsSubtalkerTopK, 0, 200);
        _singleVoiceAnchor = config.TtsSingleVoiceAnchor;
        _pronunciationOverridesB64 = config.TtsPronunciationOverrides is { Count: > 0 }
            ? Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(config.TtsPronunciationOverrides)))
            : "";
        _onEvent = onEvent ?? throw new ArgumentNullException(nameof(onEvent));
        _testMode = testMode;
        _fakeTts = fakeTts;
    }

    private static TaskCompletionSource<TtsWorkerEvent> NewHandshake() =>
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
                throw new FileNotFoundException("Missing TTS Python executable", _python);
            if (!File.Exists(_script))
                throw new FileNotFoundException("Missing TTS worker script", _script);
            if (!_fakeTts)
            {
                if (!File.Exists(_serverExe))
                    throw new FileNotFoundException("Missing audiocpp_server executable", _serverExe);
                if (!File.Exists(_model))
                    throw new FileNotFoundException("Missing Qwen3-TTS GGUF model", _model);
                if (!File.Exists(_refsManifest))
                    throw new FileNotFoundException("Missing TTS refs manifest", _refsManifest);
            }

            _handshake = NewHandshake();

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
            if (!_fakeTts)
            {
                info.ArgumentList.Add("--server-exe");
                info.ArgumentList.Add(_serverExe);
                info.ArgumentList.Add("--model");
                info.ArgumentList.Add(_model);
                if (!string.IsNullOrEmpty(_loraDir) && Directory.Exists(_loraDir))
                {
                    info.ArgumentList.Add("--lora");
                    info.ArgumentList.Add(_loraDir);
                    info.ArgumentList.Add("--lora-scale");
                    info.ArgumentList.Add(_loraScale.ToString("0.###", CultureInfo.InvariantCulture));
                }
                info.ArgumentList.Add("--refs-manifest");
                info.ArgumentList.Add(_refsManifest);
            }
            if (!_bweEnabled)
            {
                info.ArgumentList.Add("--no-bwe");
            }
            else if (!string.IsNullOrEmpty(_bweModel) && File.Exists(_bweModel))
            {
                info.ArgumentList.Add("--bwe-model");
                info.ArgumentList.Add(_bweModel);
                info.ArgumentList.Add("--bwe-threads");
                info.ArgumentList.Add(_bweThreads.ToString(CultureInfo.InvariantCulture));
                info.ArgumentList.Add("--bwe-device");
                info.ArgumentList.Add(_bweDevice);
            }
            info.ArgumentList.Add("--backend");
            info.ArgumentList.Add(_backend);
            info.ArgumentList.Add("--device");
            info.ArgumentList.Add(_device.ToString(CultureInfo.InvariantCulture));
            info.ArgumentList.Add("--threads");
            info.ArgumentList.Add(_threads.ToString(CultureInfo.InvariantCulture));
            info.ArgumentList.Add("--voice-prompt-cache-slots");
            info.ArgumentList.Add(_cacheSlots.ToString(CultureInfo.InvariantCulture));
            info.ArgumentList.Add("--language");
            info.ArgumentList.Add(_language);
            info.ArgumentList.Add("--temperature");
            info.ArgumentList.Add(_temperature.ToString("0.###", CultureInfo.InvariantCulture));
            info.ArgumentList.Add("--top-p");
            info.ArgumentList.Add(_topP.ToString("0.###", CultureInfo.InvariantCulture));
            info.ArgumentList.Add("--top-k");
            info.ArgumentList.Add(_topK.ToString(CultureInfo.InvariantCulture));
            info.ArgumentList.Add("--repetition-penalty");
            info.ArgumentList.Add(_repetitionPenalty.ToString("0.###", CultureInfo.InvariantCulture));
            info.ArgumentList.Add(_subtalkerDoSample ? "--subtalker-do-sample" : "--no-subtalker-sample");
            info.ArgumentList.Add("--subtalker-temperature");
            info.ArgumentList.Add(_subtalkerTemperature.ToString("0.###", CultureInfo.InvariantCulture));
            info.ArgumentList.Add("--subtalker-top-p");
            info.ArgumentList.Add(_subtalkerTopP.ToString("0.###", CultureInfo.InvariantCulture));
            info.ArgumentList.Add("--subtalker-top-k");
            info.ArgumentList.Add(_subtalkerTopK.ToString(CultureInfo.InvariantCulture));
            info.ArgumentList.Add(_singleVoiceAnchor ? "--single-voice-anchor" : "--multi-voice-refs");
            info.ArgumentList.Add("--max-clause-chars");
            info.ArgumentList.Add(_maxClauseChars.ToString(CultureInfo.InvariantCulture));
            if (_pronunciationOverridesB64.Length > 0)
            {
                info.ArgumentList.Add("--pronunciation-overrides-b64");
                info.ArgumentList.Add(_pronunciationOverridesB64);
            }
            if (!_warmupEnabled)
                info.ArgumentList.Add("--no-warmup");
            if (_testMode)
                info.ArgumentList.Add("--test-mode");
            if (_fakeTts)
                info.ArgumentList.Add("--fake-tts");

            info.Environment["PYTHONUTF8"] = "1";
            info.Environment["HF_HUB_OFFLINE"] = "1";

            _lifetime.Token.ThrowIfCancellationRequested();
            _process = Process.Start(info) ?? throw new IOException("TTS worker could not start");
            if (_disposed)
            {
                StopOwnedProcess();
                throw new OperationCanceledException();
            }
            _process.StandardInput.NewLine = "\n";
            _ = ReadLoopAsync(_process);
            _ = DrainErrorsAsync(_process);

            TtsWorkerEvent ready = await _handshake.Task
                .WaitAsync(TimeSpan.FromSeconds(95), _lifetime.Token)
                .ConfigureAwait(false);
            LoadMs = watch.Elapsed.TotalMilliseconds;
            ModelId = ready.ModelId;
            Backend = ready.Backend;
            LoraMergedTensors = ready.LoraMergedTensors;
            SampleRate = ready.SampleRate;
            Bwe48k = ready.BweApplied;
            BweProvider = ready.BweProvider;
            MoodCount = ready.MoodCount;
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
                TtsWorkerEvent? parsed = TryParseEvent(line);
                if (parsed == null)
                    continue;
                if (string.Equals(parsed.Kind, "ready", StringComparison.Ordinal))
                {
                    _handshake.TrySetResult(parsed);
                    continue;
                }
                if (string.Equals(parsed.Kind, "startup_error", StringComparison.Ordinal))
                {
                    _handshake.TrySetException(new InvalidOperationException("TTS startup error: " + parsed.ErrorCode));
                    continue;
                }
                EmitSafe(parsed);
            }
            throw new EndOfStreamException("TTS worker exited");
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
                EmitSafe(new TtsWorkerEvent("worker_exited", "", -1, 0, 0, "", 0, 0, 0, 0.0, 0.0, false, "", "worker_exited", "", "", 0, 0, false));
                if (wasReady)
                    RestartOnce();
            }
        }
    }

    private async Task DrainErrorsAsync(Process process)
    {
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

    internal void SendSpeak(string utteranceId, string text, string emotion = "", string moodHint = "", bool append = false)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        var payload = new Dictionary<string, object?>
        {
            ["op"] = "speak",
            ["request_id"] = Guid.NewGuid().ToString("N"),
            ["utterance_id"] = utteranceId,
            ["text"] = text,
            ["emotion"] = emotion ?? "",
            ["mood_hint"] = moodHint ?? "",
            ["append"] = append
        };
        _ = SendCommandAsync(payload, utteranceId);
    }

    internal void SendSpeakClauses(
        string utteranceId,
        IReadOnlyList<string> clauses,
        int startBubbleIndex = 0,
        string emotion = "",
        string moodHint = "",
        bool append = false)
    {
        if (clauses == null || clauses.Count == 0)
            return;
        var payload = new Dictionary<string, object?>
        {
            ["op"] = "speak",
            ["request_id"] = Guid.NewGuid().ToString("N"),
            ["utterance_id"] = utteranceId,
            ["clauses"] = clauses,
            ["start_bubble_index"] = startBubbleIndex,
            ["emotion"] = emotion ?? "",
            ["mood_hint"] = moodHint ?? "",
            ["append"] = append
        };
        _ = SendCommandAsync(payload, utteranceId);
    }

    internal void SendCancel(string utteranceId = "")
    {
        var payload = new Dictionary<string, object?>
        {
            ["op"] = "cancel",
            ["request_id"] = Guid.NewGuid().ToString("N"),
            ["utterance_id"] = utteranceId ?? ""
        };
        _ = SendCommandAsync(payload, utteranceId ?? "");
    }

    private async Task SendCommandAsync(Dictionary<string, object?> payload, string utteranceId)
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
                EmitSafe(new TtsWorkerEvent("error", utteranceId, -1, 0, 0, "", 0, 0, 0, 0.0, 0.0, false, "", "worker_unavailable", "", "", 0, 0, false));
                return;
            }
            string json = JsonSerializer.Serialize(payload);
            await process.StandardInput.WriteLineAsync(json).ConfigureAwait(false);
            await process.StandardInput.FlushAsync().ConfigureAwait(false);
        }
        catch
        {
            if (!_disposed)
                EmitSafe(new TtsWorkerEvent("error", utteranceId, -1, 0, 0, "", 0, 0, 0, 0.0, 0.0, false, "", "worker_unavailable", "", "", 0, 0, false));
        }
        finally
        {
            _writeLock.Release();
        }
    }

    internal static TtsWorkerEvent? TryParseEvent(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            string ev = ReadStr(root, "event");
            if (string.IsNullOrEmpty(ev))
                return null;

            string uttId = ReadStr(root, "utterance_id");
            int bubbleIdx = root.TryGetProperty("bubble_index", out var biEl) && biEl.TryGetInt32(out int biVal) ? biVal : -1;
            int clauseIdx = ReadInt(root, "clause_index");
            int clauseCount = ReadInt(root, "clause_count");
            string mood = ReadStr(root, "mood");
            int sr = ReadInt(root, "sample_rate");
            int ch = ReadInt(root, "channels");
            int durMs = ReadInt(root, "duration_ms");
            double synthMs = ReadDouble(root, "synth_ms");
            double bweMs = ReadDouble(root, "bwe_ms");
            bool bweApplied = ReadBool(root, "bwe_applied") || ReadBool(root, "bwe_48k");
            string pcmB64 = ReadStr(root, "pcm16_b64");
            string code = ReadStr(root, "code");
            string modelId = ReadStr(root, "model_id");
            string backend = ReadStr(root, "backend");
            int loraTensors = ReadInt(root, "lora_merged_tensors");
            int moodCount = ReadInt(root, "mood_count");
            bool skipped = ReadBool(root, "skipped");
            string bweProvider = ReadStr(root, "bwe_provider");

            return new TtsWorkerEvent(
                ev, uttId, bubbleIdx, clauseIdx, clauseCount, mood, sr, ch, durMs,
                synthMs, bweMs, bweApplied, pcmB64, code, modelId, backend,
                loraTensors, moodCount, skipped, bweProvider);
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

    private static int ReadInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.TryGetInt32(out int v) ? v : 0;

    private static double ReadDouble(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.TryGetDouble(out double v) ? v : 0.0;

    private static bool ReadBool(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.True;

    private void EmitSafe(TtsWorkerEvent ev)
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
        if (p == null)
            return;
        try
        {
            if (!p.HasExited)
            {
                try
                {
                    p.StandardInput.WriteLine("{\"op\":\"shutdown\",\"request_id\":\"dispose\"}");
                    p.StandardInput.Flush();
                    p.WaitForExit(700);
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
            try { p.Dispose(); } catch { }
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
