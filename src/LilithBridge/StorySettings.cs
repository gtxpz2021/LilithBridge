using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LilithBridge;

internal static partial class ConfigStore
{
    // Existing configuration owner, preserving unrelated settings and snapshots.
    internal static BridgeConfig SetStoryOption(string name, string value)
    {
        if (name is not ("file" or "allow_model_data" or "auto_recall_enabled"))
            throw new ArgumentException("Unknown story setting");
        lock (LoadLock)
        {
            string root = DataRoot(), path = Path.Combine(root, "config.json");
            string? before = File.Exists(path) ? File.ReadAllText(path) : null;
            var document = before == null ? new JsonObject() : JsonNode.Parse(before) as JsonObject
                ?? throw new InvalidDataException("Config root must be an object");
            if (document["story"] == null) document["story"] = new JsonObject();
            var options = document["story"]!.AsObject();
            if (name == "file")
            {
                if (value.Length > 512) throw new ArgumentException("Story path too long");
                string previousFile = options["file"]?.GetValue<string>() ?? "";
                options[name] = value.Trim();
                // A changed source needs a fresh deliberate grant.
                if (value.Trim() != previousFile) options["allow_model_data"] = false;
            }
            else
            {
                if (value is not ("true" or "false")) throw new ArgumentException("Invalid story option");
                if (name == "allow_model_data" && value == "true")
                {
                    var candidate = new BridgeConfig { DataDirectory = root,
                        PreviousStoryFile = options["file"]?.GetValue<string>() ?? "" };
                    if (!PreviousStoryArchive.IsAvailable(candidate)) throw new InvalidDataException("Local story data unavailable");
                    options["enabled"] = true;
                    options["on_demand"] = true;
                }
                options[name] = value == "true";
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
