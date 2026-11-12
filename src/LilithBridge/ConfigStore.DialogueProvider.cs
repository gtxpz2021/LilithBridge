using System.Text.Json;
using System.Text.Json.Nodes;

namespace LilithBridge;

internal static partial class ConfigStore
{
    internal static BridgeConfig SetDialogueProviderOption(string expectedProvider, string name, string value, string? expectedAuth = null)
    {
        if (expectedProvider is not ("deepseek" or "openai")
            || name is not ("model" or "reasoning_effort" or "max_output_tokens" or "timeout_seconds"))
            throw new ArgumentException("Unknown dialogue option");
        lock (LoadLock)
        {
            string root = DataRoot(), path = Path.Combine(root, "config.json");
            string? before = File.Exists(path) ? File.ReadAllText(path) : null;
            var document = before == null ? new JsonObject() : JsonNode.Parse(before) as JsonObject
                ?? throw new InvalidDataException("Config root must be an object");
            string provider = document["llm_provider"]?.GetValue<string>() == "openai" ? "openai" : "deepseek";
            if (provider != expectedProvider) throw new InvalidOperationException("Provider changed; choose again");
            if (provider == "openai" && expectedAuth != null
                && (document["openai"]?["auth_mode"]?.GetValue<string>() == "chatgpt" ? "chatgpt" : "api_key") != expectedAuth)
                throw new InvalidOperationException("Authentication changed; choose again");
            if (provider == "openai") return SetOpenAIOption(name, value);
            JsonObject dialogue;
            if (document["dialogue"] == null) document["dialogue"] = new JsonObject();
            dialogue = document["dialogue"] as JsonObject ?? throw new InvalidDataException("Invalid dialogue settings");
            if (name == "reasoning_effort")
            {
                if (value is not ("off" or "low" or "high" or "auto")) throw new ArgumentException("Unsupported DeepSeek effort");
                dialogue["auto_thinking"] = value == "auto";
                if (value != "auto") dialogue["thinking_mode"] = value;
            }
            else if (name == "max_output_tokens" && (dialogue["thinking_mode"]?.GetValue<string>() is "low" or "high"))
                dialogue["thinking_max_tokens"] = Math.Clamp(int.Parse(value), 256, 8192);
            else
            {
                // Write back to the same effective legacy owner. Never copy keys
                // or change another provider profile merely by selecting a model.
                JsonObject owner;
                if (document["provider"] is JsonObject direct) owner = direct;
                else if (document["active_provider"] is JsonValue active
                    && document["provider_profiles"]?[active.GetValue<string>()] is JsonObject profile) owner = profile;
                else { owner = new JsonObject(); document["provider"] = owner; }
                if (name == "model")
                {
                    if (value.Length is < 1 or > 128 || value.Any(ch => ch < 33 || ch > 126))
                        throw new ArgumentException("Invalid model ID");
                    owner["model"] = value;
                }
                else if (name == "max_output_tokens") owner["max_tokens"] = Math.Clamp(int.Parse(value), 64, 2048);
                else owner["timeout_ms"] = Math.Clamp(int.Parse(value), 10, 180) * 1000;
            }
            Directory.CreateDirectory(root);
            string pending = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(pending, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                if (before != null)
                {
                    if (File.ReadAllText(path) != before) throw new IOException("Config changed; retry selection");
                    File.Replace(pending, path, null);
                }
                else File.Move(pending, path);
                return Publish(ReadUncached());
            }
            finally { if (File.Exists(pending)) File.Delete(pending); }
        }
    }
}
