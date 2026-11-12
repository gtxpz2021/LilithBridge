using System.Text.Json;
using System.Text.Json.Nodes;

namespace LilithBridge;

internal static partial class ConfigStore
{
    // A preference for subsequent turns, not a source-data permission. Preserve
    // every provider/library/reflection option and the original native-tools flag.
    internal static BridgeConfig SetTemporaryExpressions(bool enabled)
    {
        lock (LoadLock)
        {
            string root = DataRoot(), path = Path.Combine(root, "config.json");
            string? before = File.Exists(path) ? File.ReadAllText(path) : null;
            var document = before == null ? new JsonObject() : JsonNode.Parse(before) as JsonObject
                ?? throw new InvalidDataException("Config root must be an object");
            if (document["dialogue"] == null) document["dialogue"] = new JsonObject();
            var dialogue = document["dialogue"] as JsonObject
                ?? throw new InvalidDataException("Dialogue config must be an object");
            dialogue["temporary_expressions_trial"] = enabled;
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
