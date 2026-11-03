using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace LilithSetup
{
    public static class ConsoleStartupSettings
    {
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        public static string DisableConsole(string text)
        {
            string newline = text.Contains("\r\n") ? "\r\n" : "\n";
            var headers = Regex.Matches(text, @"(?m)^[ \t]*\[([^\]\r\n]+)\][ \t]*\r?$");
            int found = -1;
            for (int i = 0; i < headers.Count; i++)
                if (String.Equals(headers[i].Groups[1].Value, "Logging.Console", StringComparison.OrdinalIgnoreCase))
                { SetupCore.Require(found < 0, "LBWIZ_CONSOLE_CONFIG_AMBIGUOUS"); found = i; }
            if (found < 0) return text + (text.Length == 0 || text.EndsWith("\n") ? "" : newline) + newline + "[Logging.Console]" + newline + "Enabled = false" + newline;
            int start = headers[found].Index + headers[found].Length;
            int end = found + 1 < headers.Count ? headers[found + 1].Index : text.Length;
            string section = text.Substring(start, end - start);
            var entries = Regex.Matches(section, @"(?im)^[ \t]*Enabled[ \t]*=[ \t]*(true|false)[ \t]*\r?$");
            SetupCore.Require(entries.Count <= 1, "LBWIZ_CONSOLE_CONFIG_AMBIGUOUS");
            SetupCore.Require(Regex.Matches(section, @"(?im)^[ \t]*Enabled[ \t]*=").Count == entries.Count, "LBWIZ_CONSOLE_CONFIG_AMBIGUOUS");
            string changed;
            if (entries.Count == 0)
            {
                SetupCore.Require(!Regex.IsMatch(section, @"(?im)^[ \t]*Enabled[ \t]*="), "LBWIZ_CONSOLE_CONFIG_AMBIGUOUS");
                changed = newline + "Enabled = false" + newline + section.TrimStart('\r', '\n');
            }
            else
            {
                Match value = entries[0];
                if (value.Groups[1].Value.Equals("false", StringComparison.OrdinalIgnoreCase)) return text;
                int at = value.Groups[1].Index;
                changed = section.Substring(0, at) + "false" + section.Substring(at + value.Groups[1].Length);
            }
            return text.Substring(0, start) + changed + text.Substring(end);
        }
        public static bool Apply(string gameRoot)
        {
            InstallerCore.RequireStopped();
            string directory = WizardProvisioning.PathIn(gameRoot, "BepInEx/config");
            string path = WizardProvisioning.PathIn(gameRoot, "BepInEx/config/BepInEx.cfg");
            byte[] before = File.Exists(path) ? File.ReadAllBytes(path) : null;
            bool bom = before != null && before.Length >= 3 && before[0] == 0xef && before[1] == 0xbb && before[2] == 0xbf;
            string original = before == null ? "" : Utf8.GetString(before, bom ? 3 : 0, before.Length - (bom ? 3 : 0));
            string changed = DisableConsole(original);
            if (changed == original) return false;
            byte[] body = Utf8.GetBytes(changed), bytes = body;
            if (bom) { bytes = new byte[body.Length + 3]; bytes[0] = 0xef; bytes[1] = 0xbb; bytes[2] = 0xbf; Buffer.BlockCopy(body, 0, bytes, 3, body.Length); }
            Directory.CreateDirectory(directory);
            string nonce = Guid.NewGuid().ToString("N");
            string temp = path + ".lilithbridge-" + nonce + ".tmp";
            string backup = path + ".lilithbridge-console-" + nonce + ".bak";
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(bytes, 0, bytes.Length); file.Flush(true); }
            try
            {
                InstallerCore.RequireStopped(); SetupCore.NoReparse(path);
                if (before == null) { SetupCore.Require(!File.Exists(path), "LBWIZ_CONSOLE_CONFIG_CHANGED"); File.Move(temp, path); }
                else
                {
                    SetupCore.Require(File.Exists(path) && SetupCore.FileHash(path) == SetupCore.Hash(before), "LBWIZ_CONSOLE_CONFIG_CHANGED");
                    File.Replace(temp, path, backup);
                    SetupCore.Require(SetupCore.FileHash(backup) == SetupCore.Hash(before), "LBWIZ_CONSOLE_CONFIG_CHANGED");
                }
                SetupCore.Require(SetupCore.FileHash(path) == SetupCore.Hash(bytes), "LBWIZ_CONSOLE_CONFIG_CHANGED");
                return true;
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
}
