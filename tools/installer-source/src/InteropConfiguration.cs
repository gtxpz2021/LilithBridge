using System;
using System.IO;
using System.Collections.Generic;

namespace LilithSetup
{
    internal static class InteropConfiguration
    {
        internal static bool Read(string game, out bool autoUpdate, out string reason)
        {
            autoUpdate = true; reason = "";
            try
            {
                string path = WizardProvisioning.PathIn(game, "BepInEx/config/BepInEx.cfg");
                var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                bool section = false, seen = false;
                if (File.Exists(path)) foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                    if (line.StartsWith("[") && line.EndsWith("]"))
                    {
                        section = line.Equals("[IL2CPP]", StringComparison.OrdinalIgnoreCase);
                        if (section) { if (seen) { reason = "duplicate_il2cpp_section"; return false; } seen = true; }
                        continue;
                    }
                    if (!section) continue;
                    int at = line.IndexOf('='); if (at < 0) continue;
                    string key = line.Substring(0, at).Trim();
                    if (!key.Equals("UpdateInteropAssemblies", StringComparison.OrdinalIgnoreCase)
                        && !key.Equals("GlobalMetadataPath", StringComparison.OrdinalIgnoreCase)
                        && !key.Equals("IL2CPPInteropAssembliesPath", StringComparison.OrdinalIgnoreCase)) continue;
                    if (values.ContainsKey(key)) { reason = "duplicate_il2cpp_setting"; return false; }
                    values.Add(key, line.Substring(at + 1).Trim());
                }
                string value;
                if (values.TryGetValue("UpdateInteropAssemblies", out value) && !Boolean.TryParse(value, out autoUpdate))
                { reason = "invalid_autogeneration_setting"; return false; }
                string bep = Path.Combine(game, "BepInEx"), data = Path.Combine(game, "Lilith_Data");
                string assemblies = values.TryGetValue("IL2CPPInteropAssembliesPath", out value) ? value : "{BepInEx}";
                string metadata = values.TryGetValue("GlobalMetadataPath", out value) ? value : "{GameDataPath}/il2cpp_data/Metadata/global-metadata.dat";
                if (!Standard(assemblies, bep, game, bep, data) || !Standard(metadata, Path.Combine(data, "il2cpp_data/Metadata/global-metadata.dat"), game, bep, data))
                { reason = "custom_or_unresolved_generation_path"; return false; }
                return true;
            }
            catch { reason = "generation_configuration_unreadable"; return false; }
        }
        static bool Standard(string value, string expected, string game, string bep, string data)
        {
            string expanded = value.Replace("{BepInEx}", bep).Replace("{GameDataPath}", data).Replace("{ProcessName}", "Lilith");
            if (expanded.IndexOf('{') >= 0 || expanded.IndexOf('}') >= 0 || !Path.IsPathRooted(expanded)) return false;
            return SetupCore.Same(Path.GetFullPath(expanded), Path.GetFullPath(expected));
        }
    }
}
