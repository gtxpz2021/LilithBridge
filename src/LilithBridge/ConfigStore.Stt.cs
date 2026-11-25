using System;
using System.Collections.Generic;
using System.Text.Json;
using UnityEngine;

namespace LilithBridge;

internal sealed partial class BridgeConfig
{
    public bool SttEnabled { get; set; } = false;
    public string SttPythonPath { get; set; } = "stt-runtime/.venv/Scripts/python.exe";
    public string SttWorkerScript { get; set; } = "stt-runtime/stt_worker.py";
    public string SttModelPath { get; set; } = "stt-runtime/models/sherpa-onnx-sense-voice-zh-en-ja-ko-yue-int8-2024-07-17";
    public string SttVadModelPath { get; set; } = "stt-runtime/models/silero_vad.onnx";
    public string SttHotkey { get; set; } = "F8";
    public string SttLanguage { get; set; } = "zh";
    public bool SttUseItn { get; set; } = true;
    public int SttNumThreads { get; set; } = 2;
    public int SttPartialIntervalMs { get; set; } = 850;
    public int SttMaxDurationSeconds { get; set; } = 120;
    public bool SttBuiltinLexicon { get; set; } = true;
    public Dictionary<string, string> SttReplacements { get; set; } = new(StringComparer.Ordinal);
}

internal static partial class ConfigStore
{
    internal static bool TryParseSttHotkey(string? raw, out KeyCode key)
    {
        key = KeyCode.None;
        if (string.IsNullOrWhiteSpace(raw))
            return false;
        string trimmed = raw.Trim();
        if (string.Equals(trimmed, "none", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "off", StringComparison.OrdinalIgnoreCase))
            return false;
        if (Enum.TryParse<KeyCode>(trimmed, ignoreCase: true, out KeyCode parsed) && parsed != KeyCode.None)
        {
            // Never allow Enter or Escape to be hijacked as the STT toggle hotkey.
            if (parsed is KeyCode.Return or KeyCode.KeypadEnter or KeyCode.Escape)
                return false;
            key = parsed;
            return true;
        }
        return false;
    }

    private static void ReadSttConfig(BridgeConfig config, JsonElement root)
    {
        if (!root.TryGetProperty("stt", out JsonElement stt) || stt.ValueKind != JsonValueKind.Object)
            return;

        config.SttEnabled = ReadBoolean(stt, "enabled", config.SttEnabled);
        string? pythonPath = ReadString(stt, "python_path");
        if (!string.IsNullOrWhiteSpace(pythonPath))
            config.SttPythonPath = pythonPath.Trim();
        string? workerScript = ReadString(stt, "worker_script");
        if (!string.IsNullOrWhiteSpace(workerScript))
            config.SttWorkerScript = workerScript.Trim();
        string? modelPath = ReadString(stt, "model_path");
        if (!string.IsNullOrWhiteSpace(modelPath))
            config.SttModelPath = modelPath.Trim();
        string? vadModelPath = ReadString(stt, "vad_model_path");
        if (vadModelPath != null)
            config.SttVadModelPath = vadModelPath.Trim();
        string? hotkey = ReadString(stt, "hotkey");
        if (!string.IsNullOrWhiteSpace(hotkey))
            config.SttHotkey = hotkey.Trim();
        string? language = ReadString(stt, "language");
        if (!string.IsNullOrWhiteSpace(language))
        {
            string normalized = language.Trim().ToLowerInvariant();
            if (normalized is "zh" or "auto" or "en" or "ja" or "ko" or "yue")
                config.SttLanguage = normalized;
        }
        config.SttUseItn = ReadBoolean(stt, "use_itn", config.SttUseItn);
        if (stt.TryGetProperty("num_threads", out JsonElement threadsEl) && threadsEl.TryGetInt32(out int threads))
            config.SttNumThreads = Math.Clamp(threads, 1, 4);
        if (stt.TryGetProperty("partial_interval_ms", out JsonElement partialEl) && partialEl.TryGetInt32(out int partialMs))
            config.SttPartialIntervalMs = Math.Clamp(partialMs, 200, 2000);
        if (stt.TryGetProperty("max_duration_seconds", out JsonElement maxDurEl) && maxDurEl.TryGetInt32(out int maxDur))
            config.SttMaxDurationSeconds = Math.Clamp(maxDur, 10, 300);
        config.SttBuiltinLexicon = ReadBoolean(stt, "builtin_lexicon", config.SttBuiltinLexicon);
        if (stt.TryGetProperty("replacements", out JsonElement replEl) && replEl.ValueKind == JsonValueKind.Object)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (JsonProperty prop in replEl.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.String)
                    continue;
                string k = (prop.Name ?? "").Trim();
                string v = (prop.Value.GetString() ?? "").Trim();
                if (k.Length > 0 && v.Length > 0)
                    map[k] = v;
            }
            config.SttReplacements = map;
        }
    }
}
