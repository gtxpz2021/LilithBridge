using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LilithBridge;

internal sealed partial class BridgeConfig
{
    public bool TtsEnabled { get; set; } = false;
    public string TtsPythonPath { get; set; } = "tts-runtime/.venv/Scripts/python.exe";
    public string TtsWorkerScript { get; set; } = "tts-runtime/tts_worker.py";
    public string TtsServerExePath { get; set; } = "tts-runtime/bin/audiocpp_server.exe";
    public string TtsModelPath { get; set; } = "tts-runtime/models/qwen3-tts-12hz-1.7b-lilith-e6-s07-q8_0.gguf";
    public string TtsLoraPath { get; set; } = "";
    public float TtsLoraScale { get; set; } = 0.7f;
    public string TtsRefsManifestPath { get; set; } = "tts-runtime/refs/refs_manifest.json";
    public bool TtsBweEnabled { get; set; } = true;
    public string TtsBweModelPath { get; set; } = "tts-runtime/models/flashsr_onnx/model.onnx";
    public int TtsBweThreads { get; set; } = 4;
    public string TtsBweDevice { get; set; } = "auto";
    public string TtsBackend { get; set; } = "auto";
    public int TtsDevice { get; set; } = 0;
    public int TtsThreads { get; set; } = 6;
    public int TtsVoicePromptCacheSlots { get; set; } = 8;
    public float TtsVolume { get; set; } = 1.0f;
    public bool TtsSyncGameVoiceVolume { get; set; } = true;
    public int TtsMaxClauseChars { get; set; } = 36;
    public bool TtsSyncBubblePacing { get; set; } = true;
    public bool TtsWarmupEnabled { get; set; } = true;
    public bool TtsInterruptOnStt { get; set; } = true;
    public bool TtsInterruptOnPlayerInput { get; set; } = true;
    public string TtsLanguage { get; set; } = "auto";
    public float TtsTemperature { get; set; } = 0.60f;
    public float TtsTopP { get; set; } = 0.85f;
    public int TtsTopK { get; set; } = 20;
    public float TtsRepetitionPenalty { get; set; } = 1.05f;
    public bool TtsSubtalkerDoSample { get; set; } = false;
    public float TtsSubtalkerTemperature { get; set; } = 0.60f;
    public float TtsSubtalkerTopP { get; set; } = 0.85f;
    public int TtsSubtalkerTopK { get; set; } = 20;
    public bool TtsSingleVoiceAnchor { get; set; } = true;
    public Dictionary<string, string> TtsPronunciationOverrides { get; set; } = new(StringComparer.Ordinal);
}

internal static partial class ConfigStore
{
    private static void ReadTtsConfig(BridgeConfig config, JsonElement root)
    {
        if (!root.TryGetProperty("tts", out JsonElement tts) || tts.ValueKind != JsonValueKind.Object)
            return;

        config.TtsEnabled = ReadBoolean(tts, "enabled", config.TtsEnabled);
        string? pythonPath = ReadString(tts, "python_path");
        if (!string.IsNullOrWhiteSpace(pythonPath))
            config.TtsPythonPath = pythonPath.Trim();
        string? workerScript = ReadString(tts, "worker_script");
        if (!string.IsNullOrWhiteSpace(workerScript))
            config.TtsWorkerScript = workerScript.Trim();
        string? serverExe = ReadString(tts, "server_exe");
        if (!string.IsNullOrWhiteSpace(serverExe))
            config.TtsServerExePath = serverExe.Trim();
        string? modelPath = ReadString(tts, "model_path");
        if (!string.IsNullOrWhiteSpace(modelPath))
            config.TtsModelPath = modelPath.Trim();
        string? loraPath = ReadString(tts, "lora_path");
        if (loraPath != null)
            config.TtsLoraPath = loraPath.Trim();
        if (tts.TryGetProperty("lora_scale", out JsonElement scaleEl) && scaleEl.TryGetDouble(out double scale))
            config.TtsLoraScale = (float)Math.Clamp(scale, 0.0, 1.5);
        string? refsManifest = ReadString(tts, "refs_manifest");
        if (!string.IsNullOrWhiteSpace(refsManifest))
            config.TtsRefsManifestPath = refsManifest.Trim();

        config.TtsBweEnabled = ReadBoolean(tts, "bwe_48k", config.TtsBweEnabled);
        string? bweModel = ReadString(tts, "bwe_model_path");
        if (bweModel != null)
            config.TtsBweModelPath = bweModel.Trim();
        if (tts.TryGetProperty("bwe_threads", out JsonElement bweThreadsEl) && bweThreadsEl.TryGetInt32(out int bweThreads))
            config.TtsBweThreads = Math.Clamp(bweThreads, 1, 8);
        string? bweDevice = ReadString(tts, "bwe_device");
        if (!string.IsNullOrWhiteSpace(bweDevice))
        {
            string normBwe = bweDevice.Trim().ToLowerInvariant();
            if (normBwe is "auto" or "gpu" or "cpu")
                config.TtsBweDevice = normBwe;
        }

        string? backend = ReadString(tts, "backend");
        if (!string.IsNullOrWhiteSpace(backend))
        {
            string normalized = backend.Trim().ToLowerInvariant();
            if (normalized is "auto" or "vulkan" or "cpu" or "cuda" or "hip" or "rocm" or "metal" or "webgpu" or "sycl")
                config.TtsBackend = normalized;
        }
        if (tts.TryGetProperty("device", out JsonElement deviceEl) && deviceEl.TryGetInt32(out int device))
            config.TtsDevice = Math.Clamp(device, 0, 8);
        if (tts.TryGetProperty("threads", out JsonElement threadsEl) && threadsEl.TryGetInt32(out int threads))
            config.TtsThreads = Math.Clamp(threads, 1, 16);
        if (tts.TryGetProperty("voice_prompt_cache_slots", out JsonElement slotsEl) && slotsEl.TryGetInt32(out int slots))
            config.TtsVoicePromptCacheSlots = Math.Clamp(slots, 1, 16);
        if (tts.TryGetProperty("volume", out JsonElement volEl) && volEl.TryGetDouble(out double vol))
            config.TtsVolume = (float)Math.Clamp(vol, 0.0, 1.5);
        config.TtsSyncGameVoiceVolume = ReadBoolean(tts, "sync_game_voice_volume", config.TtsSyncGameVoiceVolume);
        if (tts.TryGetProperty("max_clause_chars", out JsonElement clauseEl) && clauseEl.TryGetInt32(out int clauseChars))
            config.TtsMaxClauseChars = Math.Clamp(clauseChars, 16, 120);

        config.TtsSyncBubblePacing = ReadBoolean(tts, "sync_bubble_pacing", config.TtsSyncBubblePacing);
        config.TtsWarmupEnabled = ReadBoolean(tts, "warmup_enabled", config.TtsWarmupEnabled);
        config.TtsInterruptOnStt = ReadBoolean(tts, "interrupt_on_stt", config.TtsInterruptOnStt);
        config.TtsInterruptOnPlayerInput = ReadBoolean(tts, "interrupt_on_player_input", config.TtsInterruptOnPlayerInput);

        string? lang = ReadString(tts, "language");
        if (!string.IsNullOrWhiteSpace(lang))
            config.TtsLanguage = lang.Trim();
        if (tts.TryGetProperty("temperature", out JsonElement tempEl) && tempEl.TryGetDouble(out double temp))
            config.TtsTemperature = (float)Math.Clamp(temp, 0.05, 2.0);
        if (tts.TryGetProperty("top_p", out JsonElement topPEl) && topPEl.TryGetDouble(out double topP))
            config.TtsTopP = (float)Math.Clamp(topP, 0.1, 1.0);
        if (tts.TryGetProperty("top_k", out JsonElement topKEl) && topKEl.TryGetInt32(out int topK))
            config.TtsTopK = Math.Clamp(topK, 0, 200);
        if (tts.TryGetProperty("repetition_penalty", out JsonElement repEl) && repEl.TryGetDouble(out double rep))
            config.TtsRepetitionPenalty = (float)Math.Clamp(rep, 0.8, 2.0);
        config.TtsSubtalkerDoSample = ReadBoolean(tts, "subtalker_do_sample", config.TtsSubtalkerDoSample);
        if (tts.TryGetProperty("subtalker_temperature", out JsonElement subTempEl) && subTempEl.TryGetDouble(out double subTemp))
            config.TtsSubtalkerTemperature = (float)Math.Clamp(subTemp, 0.05, 2.0);
        if (tts.TryGetProperty("subtalker_top_p", out JsonElement subTopPEl) && subTopPEl.TryGetDouble(out double subTopP))
            config.TtsSubtalkerTopP = (float)Math.Clamp(subTopP, 0.1, 1.0);
        if (tts.TryGetProperty("subtalker_top_k", out JsonElement subTopKEl) && subTopKEl.TryGetInt32(out int subTopK))
            config.TtsSubtalkerTopK = Math.Clamp(subTopK, 0, 200);
        config.TtsSingleVoiceAnchor = ReadBoolean(tts, "single_voice_anchor", config.TtsSingleVoiceAnchor);
        if (tts.TryGetProperty("pronunciation_overrides", out JsonElement ovEl) && ovEl.ValueKind == JsonValueKind.Object)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (JsonProperty prop in ovEl.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.String)
                    continue;
                string k = (prop.Name ?? "").Trim();
                string v = (prop.Value.GetString() ?? "").Trim();
                if (k.Length > 0 && v.Length > 0)
                    map[k] = v;
            }
            config.TtsPronunciationOverrides = map;
        }
    }

    // Atomic update of tts.enabled only; preserves every other config key (including persona/provider).
    public static BridgeConfig SetTtsEnabled(bool enabled)
    {
        lock (LoadLock)
        {
            SetTtsEnabledInRoot(DataRoot(), enabled);
            return Publish(ReadUncached());
        }
    }

    internal static void SetTtsEnabledInRoot(string root, bool enabled)
    {
        string path = Path.Combine(root, "config.json");
        string? before = File.Exists(path) ? File.ReadAllText(path) : null;
        JsonObject document = before == null ? new JsonObject()
            : JsonNode.Parse(before) as JsonObject
                ?? throw new InvalidDataException("Config root must be an object.");
        JsonObject tts;
        if (document["tts"] == null)
        {
            tts = new JsonObject();
            document["tts"] = tts;
        }
        else
        {
            tts = document["tts"] as JsonObject
                ?? throw new InvalidDataException("TTS config must be an object.");
        }
        tts["enabled"] = enabled;
        Directory.CreateDirectory(root);
        string pending = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(pending, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            if (before != null)
            {
                if (File.ReadAllText(path) != before)
                    throw new IOException("Config changed; retry the selection.");
                File.Replace(pending, path, null);
            }
            else
            {
                File.Move(pending, path);
            }
        }
        finally
        {
            if (File.Exists(pending))
                File.Delete(pending);
        }
    }
}
