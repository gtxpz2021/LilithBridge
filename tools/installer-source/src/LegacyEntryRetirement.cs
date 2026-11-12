using System;
using System.IO;
using System.Text;
using System.Collections.Generic;

namespace LilithSetup
{
    // Only exact known, post-migration launch-only scripts. Never native-save
    // switchers or arbitrary same-name user files. Scope is selected game root.
    internal static class LegacyEntryRetirement
    {
        static readonly Dictionary<string,string> Known = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase) {
            {"play-lilith.cmd", "b4c13f70c59c5a5d17bfdbee442dc01c817bd6c793ab60356f8b8ccdaf92b041"},
            {"Lilith-RC3.cmd", "7b56612c373597be629bfef63078c107312a7e158dddfb6fb8a58df8673d7428"},
            {"play-vanguard.cmd", "7b56612c373597be629bfef63078c107312a7e158dddfb6fb8a58df8673d7428"},
            {"play-vanguard.ps1", "7abf24fe74abfd9b97fe0a97ce17d7ecf4d516aadf0b0f498c9f78c74a56ba6b"}
        };
        internal static string Inspect(string game)
        {
            game = SetupCore.SafePath(game, null); SetupCore.NoReparse(game);
            var stamp = new StringBuilder();
            foreach (var entry in Known) {
                string path = Path.Combine(game, entry.Key); SetupCore.NoReparse(path);
                SetupCore.Require(!Directory.Exists(path), "LBINSTALL_LEGACY_ENTRY_DIRECTORY_REVIEW:" + entry.Key);
                if (!File.Exists(path)) continue;
                SetupCore.Require(SetupCore.FileHash(path) == entry.Value, "LBINSTALL_UNKNOWN_LEGACY_ENTRY_PRESERVED_REVIEW:" + entry.Key);
                stamp.Append(entry.Key).Append('|').Append(entry.Value).Append('\n');
            }
            return stamp.ToString();
        }
        // Called by InstallerCore only while its installation lock and stopped
        // image handle are held. Pure file seam is also used by offline tests.
        internal static void Retire(string game, string approvedStamp, Action<string> checkpoint)
        {
            SetupCore.Require(Inspect(game) == approvedStamp, "LBINSTALL_LEGACY_ENTRY_CHANGED_RECHECK");
            if (approvedStamp.Length == 0) return;
            string archive = Path.Combine(game, @".LilithBridge-installer\retired-launchers", Guid.NewGuid().ToString("N"));
            SetupCore.NoReparse(archive); Directory.CreateDirectory(archive);
            // Durable intent written before moves. Source/destination/hash let
            // a failure remain recoverable without auto-restoring live entries.
            File.WriteAllText(Path.Combine(archive, "retirement-intent.txt"),
                "game=" + game + "\nAutomatic DLL rollback must not restore these launchers.\n" + approvedStamp, new UTF8Encoding(false));
            foreach (var entry in Known) {
                string source = Path.Combine(game, entry.Key), target = Path.Combine(archive, entry.Key);
                SetupCore.NoReparse(source); SetupCore.NoReparse(target);
                if (!File.Exists(source)) continue;
                SetupCore.Require(SetupCore.FileHash(source) == entry.Value, "LBINSTALL_LEGACY_ENTRY_CHANGED_RECHECK");
                File.Move(source, target);
                SetupCore.Require(SetupCore.FileHash(target) == entry.Value && !File.Exists(source), "LBINSTALL_LEGACY_RETIRE_VERIFY_FAILED");
                if (checkpoint != null) checkpoint(entry.Key);
            }
        }
    }
}
