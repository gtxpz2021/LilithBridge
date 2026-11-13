using System.Text.Json;
using System.Text.Json.Nodes;

namespace LilithBridge;

internal sealed partial class BridgeConfig
{
    // Bind edits to the write target AND active profile, without storing key text here.
    internal string DeepSeekKeyOwner { get; set; } = "provider";
}

internal static partial class ConfigStore
{
    // Keep the installer's existing DeepSeek storage format. Only an explicit
    // password-field save/clear writes this value; no key enters a status message.
    internal static BridgeConfig SetDeepSeekApiKey(BridgeConfig selected, string key, bool clear = false)
    {
        if (selected.IsOpenAI || clear && key.Length != 0
            || !clear && (string.IsNullOrWhiteSpace(key) || key.Length > 4096 || key.Any(char.IsControl)))
            throw new ArgumentException("Invalid DeepSeek key action");
        lock (LoadLock)
        {
            string root = DataRoot(), path = Path.Combine(root, "config.json");
            if (!string.Equals(root, selected.DataDirectory, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Data root changed; reopen settings");
            string? before = File.Exists(path) ? File.ReadAllText(path) : null;
            var document = before == null ? new JsonObject() : JsonNode.Parse(before) as JsonObject
                ?? throw new InvalidDataException("Config root must be an object");
            var current = ReadUncached();
            if (current.IsOpenAI || current.BaseUrl != selected.BaseUrl || current.ApiKey != selected.ApiKey
                || current.DeepSeekKeyOwner != selected.DeepSeekKeyOwner)
                throw new InvalidOperationException("Service changed; reopen settings");
            if (!clear && (!Uri.TryCreate(current.BaseUrl, UriKind.Absolute, out var endpoint)
                || endpoint.Scheme != "https" || endpoint.UserInfo.Length != 0
                || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0))
                throw new InvalidOperationException("A valid HTTPS service is required");

            JsonObject owner;
            if (document["provider"] != null)
                owner = document["provider"] as JsonObject ?? throw new InvalidDataException("Invalid provider settings");
            else if (document["active_provider"] is JsonValue active
                && document["provider_profiles"]?[active.GetValue<string>()] is JsonObject profile)
                owner = profile;
            else { owner = new JsonObject(); document["provider"] = owner; }
            // An explicit empty value also masks a legacy profile fallback key.
            owner["api_key"] = clear ? "" : key;
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
