using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace LilithBridge;

internal sealed partial class BridgeConfig
{
    public bool SensoryEnabled { get; set; } = false;
    public string SensoryEndpoint { get; set; } = "http://127.0.0.1:6188";
    public bool SensoryAutoStart { get; set; } = false;
    public string SensoryPythonPath { get; set; } = "";
    public string SensoryScriptPath { get; set; } = "";
    public int SensoryTimeoutMs { get; set; } = 25;
    public bool SensorySilentActingEnabled { get; set; } = true;
    public bool SensoryBeatGrooveEnabled { get; set; } = true;
    public bool SensoryProactiveGateEnabled { get; set; } = true;
    public bool SensoryProactiveReleaseEnabled { get; set; } = true;
    public bool SensoryAutoThinkingNudgeEnabled { get; set; } = true;

    public bool MediaContextEnabled { get; set; } = true;
    public string[] MediaAllowedSources { get; set; } = new[]
    {
        "cloudmusic", "netease", "qqmusic", "spotify", "music.ui",
        "applemusic", "appleinc.applemusicwin", "amazonmusic", "amazon music",
        "amazonmobilellc.amazonmusic"
    };
}

internal static partial class ConfigStore
{
    private static void ReadSensoryConfig(BridgeConfig config, JsonElement root)
    {
        if (root.TryGetProperty("media", out JsonElement media) && media.ValueKind == JsonValueKind.Object)
        {
            config.MediaContextEnabled = ReadBoolean(media, "enabled", config.MediaContextEnabled);
            if (media.TryGetProperty("allowed_sources", out JsonElement sources) && sources.ValueKind == JsonValueKind.Array)
            {
                var list = new List<string>();
                foreach (JsonElement item in sources.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        string val = (item.GetString() ?? "").Trim();
                        if (val.Length > 0) list.Add(val);
                    }
                }
                if (list.Count > 0) config.MediaAllowedSources = list.ToArray();
            }
        }
        if (root.TryGetProperty("local_context", out JsonElement localContext) && localContext.ValueKind == JsonValueKind.Object)
        {
            if (localContext.TryGetProperty("media_enabled", out _))
                config.MediaContextEnabled = ReadBoolean(localContext, "media_enabled", config.MediaContextEnabled);
        }

        if (root.TryGetProperty("sensory", out JsonElement sensory) && sensory.ValueKind == JsonValueKind.Object)
        {
            config.SensoryEnabled = ReadBoolean(sensory, "enabled", config.SensoryEnabled);
            string? endpoint = ReadString(sensory, "endpoint");
            if (!string.IsNullOrWhiteSpace(endpoint))
                config.SensoryEndpoint = endpoint.Trim().TrimEnd('/');
            config.SensoryAutoStart = ReadBoolean(sensory, "auto_start", config.SensoryAutoStart);
            config.SensoryPythonPath = ReadString(sensory, "python_path") ?? config.SensoryPythonPath;
            config.SensoryScriptPath = ReadString(sensory, "script_path") ?? config.SensoryScriptPath;
            if (sensory.TryGetProperty("timeout_ms", out JsonElement timeoutEl) && timeoutEl.TryGetInt32(out int ms))
                config.SensoryTimeoutMs = Math.Clamp(ms, 5, 250);
            config.SensorySilentActingEnabled = ReadBoolean(sensory, "silent_acting_first", config.SensorySilentActingEnabled);
            config.SensoryBeatGrooveEnabled = ReadBoolean(sensory, "beat_groove_enabled", config.SensoryBeatGrooveEnabled);
            config.SensoryProactiveGateEnabled = ReadBoolean(sensory, "proactive_interruption_gate", config.SensoryProactiveGateEnabled);
            config.SensoryProactiveReleaseEnabled = ReadBoolean(sensory, "proactive_release_trigger", config.SensoryProactiveReleaseEnabled);
            config.SensoryAutoThinkingNudgeEnabled = ReadBoolean(sensory, "auto_v2_nudge", config.SensoryAutoThinkingNudgeEnabled);
        }
    }
}
