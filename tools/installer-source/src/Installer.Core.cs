using System;
using System.IO;
using System.Text;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace LilithSetup
{
    // The installer owns exactly these two destination paths. It never manages
    // config, memories, native saves, game files, shared loaders, or models.
    public sealed class InstallManifest
    {
        public string Id, PluginSha256, PackageRoot, ManifestDigest;
        public readonly Dictionary<string, string> TargetHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> PreviousPluginHashes = new List<string>();
        public const string Plugin = @"BepInEx\plugins\LilithBridge.dll";
        public const string Marker = @"BepInEx\plugins\LilithBridge.data-dir.txt";
        public static readonly string[] Required = {
            "Lilith.exe", "GameAssembly.dll", @"Lilith_Data\il2cpp_data\Metadata\global-metadata.dat", "winhttp.dll", "doorstop_config.ini",
            @"dotnet\coreclr.dll", @"dotnet\hostpolicy.dll", @"dotnet\Microsoft.NETCore.App.deps.json", @"dotnet\Microsoft.NETCore.App.runtimeconfig.json",
            @"BepInEx\core\BepInEx.Core.dll", @"BepInEx\core\BepInEx.Unity.Common.dll", @"BepInEx\core\BepInEx.Unity.IL2CPP.dll",
            @"BepInEx\core\Il2CppInterop.Runtime.dll", @"BepInEx\core\Il2CppInterop.Common.dll", @"BepInEx\core\Il2CppInterop.HarmonySupport.dll", @"BepInEx\core\0Harmony.dll",
            @"BepInEx\interop\Assembly-CSharp.dll", @"BepInEx\interop\Il2Cppmscorlib.dll", @"BepInEx\interop\Il2CppSystem.dll",
            @"BepInEx\interop\UnityEngine.dll", @"BepInEx\interop\UnityEngine.CoreModule.dll", @"BepInEx\interop\UnityEngine.UI.dll",
            @"BepInEx\interop\Unity.TextMeshPro.dll", @"BepInEx\interop\UnityEngine.UIModule.dll", @"BepInEx\interop\UnityEngine.IMGUIModule.dll", @"BepInEx\interop\UnityEngine.TextRenderingModule.dll",
            @"BepInEx\interop\UnityEngine.InputLegacyModule.dll", @"BepInEx\interop\UnityEngine.ImageConversionModule.dll", @"BepInEx\interop\UnityEngine.Physics2DModule.dll",
            @"BepInEx\interop\spine-csharp.dll", @"BepInEx\interop\spine-unity.dll", @"BepInEx\interop\BehaviorDesigner.Runtime.dll"
        };
        public static InstallManifest Load(string packageRoot)
        {
            string root = SetupCore.SafePath(packageRoot, null); SetupCore.NoReparse(root);
            string path = Path.Combine(root, "install-manifest.json"); SetupCore.NoReparse(path);
            SetupCore.Require(File.Exists(path) && new FileInfo(path).Length < 65536, "LBINSTALL_MANIFEST_MISSING_OR_TOO_LARGE");
            byte[] bytes = File.ReadAllBytes(path); var doc = DataJson.Read(new UTF8Encoding(false, true).GetString(bytes));
            SetupCore.Require(Convert.ToInt32(doc["schema"]) == 1 && (string)doc["product"] == "LilithBridge", "LBINSTALL_MANIFEST_INVALID");
            var m = new InstallManifest { Id = (string)doc["id"], PluginSha256 = Digest((string)doc["plugin_sha256"]), PackageRoot = root, ManifestDigest = SetupCore.Hash(bytes) };
            SetupCore.Require(Regex.IsMatch(m.Id, @"^[a-zA-Z0-9._-]{1,64}$"), "LBINSTALL_MANIFEST_ID_INVALID");
            var targets = doc["target_hashes"] as Dictionary<string, object>;
            SetupCore.Require(targets != null && targets.Keys.All(k => Required.Contains(k, StringComparer.OrdinalIgnoreCase)), "LBINSTALL_MANIFEST_TARGET_INVALID");
            foreach (var p in targets) m.TargetHashes[p.Key] = Digest((string)p.Value);
            foreach (object p in (object[])doc["previous_plugin_hashes"]) m.PreviousPluginHashes.Add(Digest((string)p));
            return m;
        }
        internal static string Digest(string value) { SetupCore.Require(value != null && Regex.IsMatch(value, "^[0-9a-f]{64}$"), "LBINSTALL_INVALID_DIGEST"); return value; }
    }
    public sealed class InstallPlan
    {
        public string GameRoot, DataRoot, OldPlugin, OldMarker, PayloadHash, ManifestDigest, RetainedDataStamp;
        public bool CreateMarker, NoChange;
        public readonly List<string> Changes = new List<string>();
    }
    public sealed class InstallResult
    {
        public bool Committed, NoChange, Recovered;
        public string Transaction;
    }
    public sealed class RecoveryPlan
    {
        public readonly string GameRoot, Fingerprint;
        public readonly bool PendingOnly;
        public readonly string[] Journals, Changes;
        internal RecoveryPlan(string game, bool pending, string fingerprint, string[] journals, string[] changes)
        { GameRoot = game; PendingOnly = pending; Fingerprint = fingerprint; Journals = journals; Changes = changes; }
    }
    public static class InstallerCore
    {
        const string StateDir = ".LilithBridge-installer";
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        static string Join(string root, string relative) { string path = SetupCore.SafePath(relative, root); SetupCore.Require(SetupCore.Inside(root, path, false), "LBINSTALL_PATH_ESCAPES_ROOT"); SetupCore.NoReparse(path); return path; }
        public static string FileState(string path) { SetupCore.NoReparse(path); SetupCore.Require(!Directory.Exists(path), "LBINSTALL_FILE_PATH_IS_DIRECTORY"); return File.Exists(path) ? SetupCore.FileHash(path) : "MISSING"; }
        public static void RequireStopped()
        {
            Process[] processes = Process.GetProcessesByName("Lilith");
            try { SetupCore.Require(processes.Length == 0, "LBINSTALL_GAME_RUNNING_CLOSE_IT_FIRST"); }
            finally { foreach (var p in processes) p.Dispose(); }
        }
        static void CheckPe64(string path)
        {
            using (var f = File.OpenRead(path)) using (var r = new BinaryReader(f))
            {
                SetupCore.Require(f.Length >= 128 && r.ReadUInt16() == 0x5a4d, "LBINSTALL_TARGET_NOT_WINDOWS_X64");
                f.Position = 60; int offset = r.ReadInt32(); SetupCore.Require(offset >= 64 && offset <= f.Length - 24, "LBINSTALL_TARGET_NOT_WINDOWS_X64");
                f.Position = offset; SetupCore.Require(r.ReadUInt32() == 0x4550 && r.ReadUInt16() == 0x8664, "LBINSTALL_TARGET_NOT_WINDOWS_X64");
            }
        }
        static string MarkerRoot(string game, string marker)
        {
            SetupCore.Require(new FileInfo(marker).Length <= 8192, "LBINSTALL_MARKER_INVALID");
            var lines = File.ReadAllLines(marker, Utf8).Where(s => !String.IsNullOrWhiteSpace(s) && !s.TrimStart().StartsWith("#")).ToArray();
            SetupCore.Require(lines.Length == 1, "LBINSTALL_MARKER_INVALID");
            string value = lines[0].Trim(); if (value.StartsWith("\"") && value.EndsWith("\"") && value.Length >= 2) value = value.Substring(1, value.Length - 2);
            string data = SetupCore.SafePath(value, game); SetupCore.NoReparse(data);
            SetupCore.Require(SetupCore.Inside(game, data, false), "LBINSTALL_EXISTING_DATA_ROOT_OUTSIDE_GAME_UNSUPPORTED");
            string production = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LilithAI");
            SetupCore.Require(!SetupCore.Inside(production, data, true) && !SetupCore.Inside(data, production, true), "LBINSTALL_PRODUCTION_DATA_REJECTED");
            return data;
        }
        public static InstallPlan Inspect(InstallManifest m, string gameRoot)
        {
            RequireStopped(); string game = SetupCore.SafePath(gameRoot, null); SetupCore.NoReparse(game);
            SetupCore.Require(game.Length <= 160, "LBINSTALL_GAME_PATH_TOO_LONG_USE_SHORTER_COPY");
            SetupCore.Require(Directory.Exists(game) && File.Exists(Join(game, "Lilith.exe")), "LBINSTALL_SELECT_GAME_FOLDER_WITH_LILITH_EXE");
            SetupCore.Require(Pending(game).Count == 0, "LBINSTALL_INTERRUPTED_TRANSACTION_RECOVER_FIRST");
            SetupCore.Require(FileState(Join(m.PackageRoot, "install-manifest.json")) == m.ManifestDigest, "LBINSTALL_MANIFEST_CHANGED_RELOAD");
            SetupCore.Require(FileState(Join(m.PackageRoot, @"payload\LilithBridge.dll")) == m.PluginSha256, "LBINSTALL_PAYLOAD_HASH_MISMATCH"); var capabilities = HostCapabilities.Inspect(game, Join(m.PackageRoot, @"payload\LilithBridge.dll")); SetupCore.Require(capabilities.CanInstall, "LBINSTALL_HOST_" + capabilities.State);
            CheckPe64(Join(game, "Lilith.exe")); CheckPe64(Join(game, "GameAssembly.dll"));
            string payload = Join(m.PackageRoot, @"payload\LilithBridge.dll");
            SetupCore.Require(FileState(payload) == m.PluginSha256, "LBINSTALL_PAYLOAD_HASH_MISMATCH");
            string plugin = Join(game, InstallManifest.Plugin), marker = Join(game, InstallManifest.Marker);
            SetupCore.Require(Directory.Exists(Path.GetDirectoryName(plugin)), "LBINSTALL_DEPENDENCY_MISSING:BepInEx\\plugins");
            string old = FileState(plugin), oldMarker = FileState(marker);
            SetupCore.Require(old == "MISSING" || old == m.PluginSha256 || m.PreviousPluginHashes.Contains(old) || HostCapabilities.IsNamedBridge(plugin), "LBINSTALL_UNKNOWN_EXISTING_PLUGIN_BACK_UP_MANUALLY");
            // Avoid silently switching an existing APPDATA-based installation.
            SetupCore.Require(old == "MISSING" || oldMarker != "MISSING", "LBINSTALL_EXISTING_INSTALL_NEEDS_EXPLICIT_DATA_MIGRATION");
            string data = oldMarker == "MISSING" ? Join(game, "LilithBridgeData") : MarkerRoot(game, marker);
            RetainedDataProof retained = null;
            if (oldMarker == "MISSING" && (Directory.Exists(data) || File.Exists(data)))
            { retained = RetainedData(game); SetupCore.Require(old == "MISSING" && retained != null && SetupCore.Same(retained.Root, data), "LBINSTALL_EXISTING_DATA_FOLDER_REQUIRES_REVIEW"); }
            string env = Environment.GetEnvironmentVariable("LILITH_BRIDGE_DATA_DIR");
            if (env != null) SetupCore.Require(SetupCore.Same(SetupCore.SafePath(env, game), data), "LBINSTALL_ENVIRONMENT_ROOT_CONFLICT");
            CheckDuplicates(Path.GetDirectoryName(plugin), plugin);
            var plan = new InstallPlan { GameRoot = game, DataRoot = data, OldPlugin = old, OldMarker = oldMarker, PayloadHash = m.PluginSha256, ManifestDigest = m.ManifestDigest, RetainedDataStamp = retained == null ? "" : retained.Stamp, CreateMarker = oldMarker == "MISSING", NoChange = old == m.PluginSha256 && oldMarker != "MISSING" };
            if (retained != null) plan.Changes.Add("继续使用已回退事务保留的配置和记忆目录：" + data);
            if (plan.NoChange) plan.Changes.Add("版本相同：无需覆盖文件。可直接打开配置器。");
            else { plan.Changes.Add((old == "MISSING" ? "新增 " : "备份并替换 ") + InstallManifest.Plugin); if (plan.CreateMarker) plan.Changes.Add("新增 " + InstallManifest.Marker + " → LilithBridgeData"); plan.Changes.Add("在 .LilithBridge-installer 保留事务记录与旧插件备份"); }
            return plan;
        }
        static string Transactions(string game) { return Join(game, StateDir + @"\transactions"); }
        sealed class RetainedDataProof { public string Root, Stamp; }
        static RetainedDataProof RetainedData(string game)
        {
            string expected = Join(game, "LilithBridgeData"); if (!Directory.Exists(expected) || File.Exists(expected)) return null;
            var records = Records(game).Select(path => new { Path = path, Journal = ReadJournal(game, path) }).Where(x => (string)x.Journal["phase"] == "RolledBack").OrderByDescending(x => (string)x.Journal["utc"], StringComparer.Ordinal);
            foreach (var record in records)
            {
                var files = (object[])record.Journal["files"]; if (files.Length != 2) continue;
                int markerIndex = Array.FindIndex(files, row => (string)((Dictionary<string, object>)row)["path"] == InstallManifest.Marker), pluginIndex = Array.FindIndex(files, row => (string)((Dictionary<string, object>)row)["path"] == InstallManifest.Plugin);
                if (markerIndex < 0 || pluginIndex < 0) continue;
                var marker = (Dictionary<string, object>)files[markerIndex]; var plugin = (Dictionary<string, object>)files[pluginIndex];
                if ((string)marker["before"] != "MISSING" || (string)plugin["before"] != "MISSING") continue;
                string folder = Path.GetDirectoryName(record.Path), markerCopy = Join(folder, markerIndex + ".after"), pluginCopy = Join(folder, pluginIndex + ".after");
                SetupCore.Require(FileState(markerCopy) == (string)marker["after"] && FileState(pluginCopy) == (string)plugin["after"] && HostCapabilities.IsNamedBridge(pluginCopy), "LBINSTALL_RETAINED_DATA_RECORD_CHANGED");
                string data = MarkerRoot(game, markerCopy); SetupCore.Require(SetupCore.Same(data, expected), "LBINSTALL_RETAINED_DATA_RECORD_CHANGED");
                string identity = FileIdentity.Directory(data);
                if (record.Journal.ContainsKey("retained_data_root")) SetupCore.Require(SetupCore.Same((string)record.Journal["retained_data_root"], data), "LBINSTALL_RETAINED_DATA_RECORD_CHANGED");
                if (record.Journal.ContainsKey("retained_data_identity")) SetupCore.Require((string)record.Journal["retained_data_identity"] == identity, "LBINSTALL_RETAINED_DATA_DIRECTORY_CHANGED");
                return new RetainedDataProof { Root = data, Stamp = SetupCore.Hash(Utf8.GetBytes(FileState(record.Path) + "|" + identity + "|" + (string)marker["after"])) };
            }
            return null;
        }
        public static string FindRetainedDataRoot(string gameRoot)
        { string game = SetupCore.SafePath(gameRoot, null); SetupCore.NoReparse(game); var proof = RetainedData(game); return proof == null ? null : proof.Root; }
        static void CheckDuplicates(string pluginDirectory, string ownedPlugin)
        {
            var queue = new Queue<string>(); queue.Enqueue(pluginDirectory); int count = 0;
            while (queue.Count != 0)
            {
                string directory = queue.Dequeue(); SetupCore.NoReparse(directory);
                foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    SetupCore.Require(++count < 10000, "LBINSTALL_PLUGIN_TREE_TOO_LARGE_REVIEW_REQUIRED"); SetupCore.NoReparse(entry);
                    if (Directory.Exists(entry)) queue.Enqueue(entry);
                    else SetupCore.Require(!(Path.GetFileName(entry).IndexOf("LilithBridge", StringComparison.OrdinalIgnoreCase) >= 0 && Path.GetExtension(entry).Equals(".dll", StringComparison.OrdinalIgnoreCase)) || SetupCore.Same(entry, ownedPlugin), "LBINSTALL_DUPLICATE_PLUGIN_REVIEW_REQUIRED");
                }
            }
        }
        static List<string> Records(string game)
        {
            string dir = Transactions(game); var result = new List<string>();
            if (Directory.Exists(dir)) foreach (string child in Directory.GetDirectories(dir))
            { SetupCore.NoReparse(child); SetupCore.Require(Regex.IsMatch(Path.GetFileName(child), "^[0-9a-f]{32}$"), "LBINSTALL_UNKNOWN_TRANSACTION_FOLDER"); string j = Join(child, "journal.json"); SetupCore.Require(File.Exists(j), "LBINSTALL_INCOMPLETE_RECORD_REQUIRES_REVIEW"); result.Add(j); }
            return result;
        }
        static Dictionary<string, object> ReadJournal(string game, string path)
        {
            SetupCore.Require(SetupCore.Inside(Transactions(game), path, false) && Path.GetFileName(path) == "journal.json", "LBINSTALL_INVALID_JOURNAL_PATH");
            SetupCore.NoReparse(path); SetupCore.Require(new FileInfo(path).Length < 65536, "LBINSTALL_JOURNAL_TOO_LARGE");
            var j = DataJson.Read(File.ReadAllText(path, Utf8));
            SetupCore.Require(Convert.ToInt32(j["schema"]) == 1 && SetupCore.Same((string)j["game"], game), "LBINSTALL_JOURNAL_IDENTITY_MISMATCH");
            SetupCore.Require(new[] { "Prepared", "Applying", "Committed", "RolledBack", "RecoveryBlocked" }.Contains((string)j["phase"]), "LBINSTALL_JOURNAL_PHASE_INVALID");
            var files = j["files"] as object[]; SetupCore.Require(files != null && files.Length >= 1 && files.Length <= 2, "LBINSTALL_JOURNAL_FILES_INVALID");
            var unique = new HashSet<string>();
            for (int i = 0; i < files.Length; i++)
            {
                var f = (Dictionary<string, object>)files[i]; string relative = (string)f["path"];
                SetupCore.Require((relative == InstallManifest.Plugin || relative == InstallManifest.Marker) && unique.Add(relative), "LBINSTALL_JOURNAL_OWNERSHIP_INVALID");
                string before = (string)f["before"], after = (string)f["after"];
                if (before != "MISSING") InstallManifest.Digest(before); InstallManifest.Digest(after);
            }
            SetupCore.Require(unique.Contains(InstallManifest.Plugin), "LBINSTALL_JOURNAL_PLUGIN_REQUIRED");
            if (unique.Contains(InstallManifest.Marker))
            {
                var plugin = files.Cast<Dictionary<string, object>>().Single(f => (string)f["path"] == InstallManifest.Plugin);
                var marker = files.Cast<Dictionary<string, object>>().Single(f => (string)f["path"] == InstallManifest.Marker);
                SetupCore.Require((string)plugin["before"] == "MISSING" && (string)marker["before"] == "MISSING", "LBINSTALL_JOURNAL_MARKER_ONLY_FOR_FIRST_INSTALL");
            }
            return j;
        }
        // Shared pure ordering: never publish the new DLL before its isolation
        // marker, and never remove that marker before the new DLL is gone.
        internal static int[] OwnedFileOrder(object[] files, bool installing)
        {
            return Enumerable.Range(0, files.Length).OrderBy(i =>
            {
                bool marker = (string)((Dictionary<string, object>)files[i])["path"] == InstallManifest.Marker;
                return installing ? (marker ? 0 : 1) : (marker ? 1 : 0);
            }).ToArray();
        }
        public static List<string> Pending(string gameRoot)
        {
            string game = SetupCore.SafePath(gameRoot, null); var result = new List<string>();
            foreach (string path in Records(game)) { var j = ReadJournal(game, path); if ((string)j["phase"] != "Committed" && (string)j["phase"] != "RolledBack") result.Add(path); }
            return result;
        }
        static void WriteNew(string path, byte[] bytes) { SetupCore.NoReparse(path); using (var f = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { f.Write(bytes, 0, bytes.Length); f.Flush(true); } }
        static void Atomic(string path, byte[] bytes)
        {
            SetupCore.NoReparse(path); string temp = Path.Combine(Path.GetDirectoryName(path), ".lb-" + Guid.NewGuid().ToString("N").Substring(0, 16) + ".tmp");
            WriteNew(temp, bytes); if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
        }
        static void WriteJournal(string path, Dictionary<string, object> j) { Atomic(path, Utf8.GetBytes(DataJson.Write(j))); }
        static FileStream Lock(string game)
        {
            string dir = Join(game, StateDir); Directory.CreateDirectory(dir); string path = Join(dir, "lock");
            var f = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (f.Length != 0) { f.Dispose(); throw new SetupException("LBINSTALL_UNKNOWN_LOCK_FILE"); } return f;
        }
        static bool SamePlan(InstallPlan a, InstallPlan b) { return SetupCore.Same(a.GameRoot, b.GameRoot) && a.OldPlugin == b.OldPlugin && a.OldMarker == b.OldMarker && a.PayloadHash == b.PayloadHash && a.ManifestDigest == b.ManifestDigest && a.RetainedDataStamp == b.RetainedDataStamp && SetupCore.Same(a.DataRoot, b.DataRoot); }
        public static InstallResult Install(InstallManifest manifest, InstallPlan approved, bool confirm)
        { return InstallInternal(manifest, approved, confirm, null, false); }
        // Test hook stays internal: injected faults cannot be requested by user CLI.
        internal static InstallResult InstallInternal(InstallManifest manifest, InstallPlan approved, bool confirm, Action<string> checkpoint, bool leaveInterrupted)
        {
            SetupCore.Require(confirm && approved != null, "LBINSTALL_REVIEW_AND_CONFIRM_FIRST");
            SetupCore.Require(SamePlan(approved, Inspect(manifest, approved.GameRoot)), "LBINSTALL_TARGET_CHANGED_RECHECK");
            if (approved.NoChange) return new InstallResult { NoChange = true };
            string journal = null; bool promoted = false;
            using (Lock(approved.GameRoot))
            {
                SetupCore.Require(SamePlan(approved, Inspect(manifest, approved.GameRoot)), "LBINSTALL_TARGET_CHANGED_RECHECK");
                string id = Guid.NewGuid().ToString("N"), destination = Join(Transactions(approved.GameRoot), id);
                string staging = Join(approved.GameRoot, StateDir + @"\staging"); Directory.CreateDirectory(staging); Directory.CreateDirectory(Transactions(approved.GameRoot));
                string folder = Join(staging, id); Directory.CreateDirectory(folder);
                journal = Join(folder, "journal.json"); var files = new List<object>();
                try
                {
                    var payloads = new List<byte[]> { File.ReadAllBytes(Join(manifest.PackageRoot, @"payload\LilithBridge.dll")) };
                    SetupCore.Require(SetupCore.Hash(payloads[0]) == approved.PayloadHash, "LBINSTALL_PAYLOAD_CHANGED_RECHECK");
                    if (approved.CreateMarker) payloads.Add(Utf8.GetBytes("LilithBridgeData\r\n"));
                    for (int i = 0; i < payloads.Count; i++)
                    {
                        string relative = i == 0 ? InstallManifest.Plugin : InstallManifest.Marker;
                        string target = Join(approved.GameRoot, relative), before = FileState(target);
                        if (before != "MISSING") { byte[] old = File.ReadAllBytes(target); SetupCore.Require(SetupCore.Hash(old) == before, "LBINSTALL_TARGET_CHANGED_RECHECK"); WriteNew(Join(folder, i + ".before"), old); }
                        WriteNew(Join(folder, i + ".after"), payloads[i]);
                        files.Add(new Dictionary<string, object> { { "path", relative }, { "before", before }, { "after", SetupCore.Hash(payloads[i]) } });
                    }
                    var j = new Dictionary<string, object> { { "schema", 1 }, { "game", approved.GameRoot }, { "data_root", approved.DataRoot }, { "package", manifest.Id }, { "utc", DateTime.UtcNow.ToString("o") }, { "phase", "Prepared" }, { "files", files.ToArray() } };
                    WriteJournal(journal, j); if (checkpoint != null) checkpoint("staged");
                    // The ready record is promoted on the same volume before any target write.
                    // A crash during preparation leaves only inert staging; no half-record can
                    // prevent later installs or pretend that a target was already modified.
                    SetupCore.NoReparse(folder); SetupCore.NoReparse(destination);
                    SetupCore.Require(SetupCore.Inside(staging, folder, false) && SetupCore.Inside(Transactions(approved.GameRoot), destination, false), "LBINSTALL_STAGE_ESCAPES_ROOT");
                    Directory.Move(folder, destination); folder = destination; journal = Join(folder, "journal.json"); promoted = true;
                    if (checkpoint != null) checkpoint("prepared");
                    j["phase"] = "Applying"; WriteJournal(journal, j);
                    foreach (int i in OwnedFileOrder(files.ToArray(), true))
                    {
                        RequireStopped(); var f = (Dictionary<string, object>)files[i]; string target = Join(approved.GameRoot, (string)f["path"]);
                        SetupCore.Require(FileState(target) == (string)f["before"], "LBINSTALL_TARGET_CHANGED_DURING_INSTALL");
                        Atomic(target, payloads[i]); SetupCore.Require(FileState(target) == (string)f["after"], "LBINSTALL_COPY_VERIFY_FAILED");
                        if (checkpoint != null) checkpoint("applied-" + i);
                    }
                    j["phase"] = "Committed"; WriteJournal(journal, j);
                    return new InstallResult { Committed = true, Transaction = journal };
                }
                catch
                {
                    if (promoted && journal != null && File.Exists(journal) && !leaveInterrupted)
                    { try { RestoreJournal(approved.GameRoot, journal); } catch { throw new SetupException("LBINSTALL_RECOVERY_NEEDED_NO_OTHER_FILES_OVERWRITTEN"); } }
                    throw;
                }
            }
        }
        static void RestoreJournal(string game, string path)
        {
            RequireStopped(); var j = ReadJournal(game, path); string folder = Path.GetDirectoryName(path); var files = (object[])j["files"];
            // Validate every target and backup first; refuse to overwrite later user edits.
            for (int i = 0; i < files.Length; i++)
            {
                var f = (Dictionary<string, object>)files[i]; string before = (string)f["before"], now = FileState(Join(game, (string)f["path"]));
                SetupCore.Require(now == before || now == (string)f["after"], "LBINSTALL_RECOVERY_CONFLICT_PRESERVED");
                if (before != "MISSING") SetupCore.Require(FileState(Join(folder, i + ".before")) == before, "LBINSTALL_BACKUP_HASH_MISMATCH");
            }
            foreach (Dictionary<string, object> file in files)
                if ((string)file["path"] == InstallManifest.Marker && (string)file["before"] == "MISSING" && File.Exists(Join(game, InstallManifest.Marker)))
                { string data = MarkerRoot(game, Join(game, InstallManifest.Marker)); if (Directory.Exists(data)) { j["retained_data_root"] = data; j["retained_data_identity"] = FileIdentity.Directory(data); } }
            j["phase"] = "RecoveryBlocked"; WriteJournal(path, j);
            foreach (int i in OwnedFileOrder(files, false))
            {
                RequireStopped(); var f = (Dictionary<string, object>)files[i]; string target = Join(game, (string)f["path"]), before = (string)f["before"];
                string now = FileState(target); if (now == before) continue;
                SetupCore.Require(now == (string)f["after"], "LBINSTALL_RECOVERY_CONFLICT_PRESERVED");
                if ((string)f["path"] == InstallManifest.Marker)
                    SetupCore.Require(FileState(Join(game, InstallManifest.Plugin)) == "MISSING", "LBINSTALL_KEEP_ISOLATION_MARKER_UNTIL_PLUGIN_REMOVED");
                if (before == "MISSING") File.Delete(target); else Atomic(target, File.ReadAllBytes(Join(folder, i + ".before")));
                SetupCore.Require(FileState(target) == before, "LBINSTALL_RESTORE_VERIFY_FAILED");
            }
            j["phase"] = "RolledBack"; WriteJournal(path, j);
        }
        public static int Recover(string gameRoot, bool confirm)
        {
            SetupCore.Require(confirm, "LBINSTALL_REVIEW_AND_CONFIRM_FIRST"); RequireStopped();
            return ExecuteRecovery(PreviewRecovery(gameRoot, true), true);
        }
        public static string LatestCommitted(string gameRoot)
        {
            string game = SetupCore.SafePath(gameRoot, null);
            return Records(game).Where(p => (string)ReadJournal(game, p)["phase"] == "Committed").OrderByDescending(p => (string)ReadJournal(game, p)["utc"], StringComparer.Ordinal).FirstOrDefault();
        }
        public static void RestoreLast(string gameRoot, bool confirm)
        {
            SetupCore.Require(confirm, "LBINSTALL_REVIEW_AND_CONFIRM_FIRST"); RequireStopped();
            ExecuteRecovery(PreviewRecovery(gameRoot, false), true);
        }
        // Read-only: may describe a selected transaction while another game is
        // running. All mutation entry points retain RequireStopped unchanged.
        public static RecoveryPlan PreviewRecovery(string gameRoot, bool pendingOnly)
        {
            string game = SetupCore.SafePath(gameRoot, null); SetupCore.NoReparse(game);
            string[] selected;
            if (pendingOnly) selected = Pending(game).OrderBy(p => p, StringComparer.Ordinal).ToArray();
            else
            {
                SetupCore.Require(Pending(game).Count == 0, "LBINSTALL_INTERRUPTED_TRANSACTION_RECOVER_FIRST");
                string latest = LatestCommitted(game); SetupCore.Require(latest != null, "LBINSTALL_NO_COMMITTED_BACKUP"); selected = new[] { latest };
            }
            SetupCore.Require(selected.Length > 0, "LBINSTALL_NO_PENDING_TRANSACTION");
            var stamp = new StringBuilder(game + "|" + pendingOnly + "\n"); var changes = new List<string>();
            foreach (string path in selected)
            {
                var j = ReadJournal(game, path); var files = (object[])j["files"]; string folder = Path.GetDirectoryName(path);
                stamp.Append(path).Append('|').Append(FileState(path)).Append('\n');
                changes.Add("事务：" + Path.GetFileName(folder) + "；包版本：" + (string)j["package"] + "；日期：" + (string)j["utc"]);
                foreach (int i in OwnedFileOrder(files, false))
                {
                    var f = (Dictionary<string, object>)files[i]; string relative = (string)f["path"], target = Join(game, relative), before = (string)f["before"], now = FileState(target);
                    SetupCore.Require(now == before || now == (string)f["after"], "LBINSTALL_RECOVERY_CONFLICT_PRESERVED");
                    stamp.Append(relative).Append('|').Append(now).Append('|').Append(before).Append('\n');
                    if (before != "MISSING")
                    {
                        string backup = FileState(Join(folder, i + ".before")); stamp.Append(backup).Append('\n');
                        SetupCore.Require(backup == before, "LBINSTALL_BACKUP_HASH_MISMATCH");
                    }
                    changes.Add((now == before ? "保持（已是安装前状态） " : before == "MISSING" ? "删除本次新增文件 " : "恢复安装前备份 ") + target + "；目标摘要：" + (before == "MISSING" ? "不存在" : before));
                }
            }
            return new RecoveryPlan(game, pendingOnly, SetupCore.Hash(Utf8.GetBytes(stamp.ToString())), selected, changes.ToArray());
        }
        internal static void RequireSameRecoveryPlan(RecoveryPlan approved, RecoveryPlan fresh)
        {
            SetupCore.Require(approved != null && fresh != null && SetupCore.Same(approved.GameRoot, fresh.GameRoot) && approved.PendingOnly == fresh.PendingOnly && approved.Fingerprint == fresh.Fingerprint && approved.Journals.SequenceEqual(fresh.Journals, StringComparer.OrdinalIgnoreCase), "LBINSTALL_RECOVERY_PREVIEW_CHANGED_RECHECK");
        }
        public static int ExecuteRecovery(RecoveryPlan approved, bool confirm)
        {
            SetupCore.Require(confirm && approved != null, "LBINSTALL_REVIEW_AND_CONFIRM_FIRST"); RequireStopped();
            using (Lock(approved.GameRoot))
            {
                var fresh = PreviewRecovery(approved.GameRoot, approved.PendingOnly); RequireSameRecoveryPlan(approved, fresh);
                foreach (string path in fresh.Journals) RestoreJournal(fresh.GameRoot, path);
                return fresh.Journals.Length;
            }
        }
        // Steam discovery reads library metadata only; no drive walk or game launch.
        public static List<string> Discover(string packageRoot)
        {
            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var games = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddCandidate(games, packageRoot); string parent = Path.GetDirectoryName(packageRoot); if (parent != null) AddCandidate(games, parent);
            try { using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")) if (key != null) { string steam = key.GetValue("SteamPath") as string; if (!String.IsNullOrEmpty(steam)) roots.Add(steam); } } catch { }
            string standard = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86); if (!String.IsNullOrEmpty(standard)) roots.Add(Path.Combine(standard, "Steam"));
            foreach (string steam in roots.ToArray())
            {
                try
                {
                    string vdf = Path.Combine(steam, @"steamapps\libraryfolders.vdf"); SetupCore.NoReparse(vdf);
                    if (File.Exists(vdf) && new FileInfo(vdf).Length < 1048576) foreach (Match match in Regex.Matches(File.ReadAllText(vdf), "\\\"path\\\"\\s+\\\"([^\\\"]+)\\\"")) roots.Add(match.Groups[1].Value.Replace(@"\\", @"\"));
                } catch { }
            }
            foreach (string root in roots) foreach (string folder in new[] { "The NOexistenceN of Lilith", "The NOexistenceN of you AND me" }) AddCandidate(games, Path.Combine(root, "steamapps", "common", folder));
            return games.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        }
        static void AddCandidate(HashSet<string> list, string path) { try { string root = SetupCore.SafePath(path, null); SetupCore.NoReparse(root); if (File.Exists(Path.Combine(root, "Lilith.exe"))) list.Add(root); } catch { } }
        public static string Explain(Exception error)
        {
            string code = SetupCore.SafeError(error);
            if (code.Contains("COMPATIBILITY_NOT_VERIFIED")) return "当前包尚无经核对的游戏/依赖指纹，安装已禁用。维护者需先完成隔离兼容验证。";
            if (code.Contains("DEPENDENCY_MISSING")) return "缺少依赖：" + code.Split(':').Last() + "。请按该游戏经验证的 BepInEx IL2CPP 安装说明补齐，并生成匹配的 interop 后重检；此原型不下载或分发依赖。";
            if (code.Contains("GAME_RUNNING")) return "请先正常退出 Lilith 游戏，再重新检查。";
            if (code.Contains("ACCESS_DENIED")) return "当前目录没有写入权限。请选择有权限的游戏副本或检查目录权限；本工具不会自动提权。";
            if (code.Contains("RECOVERY_CONFLICT")) return "发现安装后被改动的文件，已保留现状。请对照事务备份人工核对，未强制覆盖。";
            if (code.Contains("DATA_MIGRATION")) return "旧插件没有独立数据标记。请先明确迁移方案；为避免切换/覆盖原数据，本次不安装。";
            if (code.Contains("GAME_PATH_TOO_LONG")) return "游戏目录路径过长，无法保证旧版 Windows 文件操作与备份可靠。请选择路径较短的游戏副本（根目录不超过 160 字符）。";
            if (code.Contains("SELECT_GAME_FOLDER")) return "未找到 Lilith.exe。请在 Steam 中选择“管理 → 浏览本地文件”，再选择该目录。";
            if (code.Contains("UNSUPPORTED_TARGET_OR_DEPENDENCY")) return "游戏或依赖版本与此包的已记录指纹不匹配：" + code.Split(':').Last() + "。请使用明确支持此版本的包；不要覆盖尝试。";
            if (code.Contains("UNKNOWN_EXISTING_PLUGIN") || code.Contains("DUPLICATE_PLUGIN")) return "检测到未识别或重复的 LilithBridge 插件。请先人工核对并备份旧插件，再重新检查；本次未覆盖。";
            if (code.Contains("RECOVER_FIRST")) return "检测到未完成的安装。恢复只涉及 LilithBridge.dll 与可能新增的数据标记；核对目录并勾选确认后，点“恢复中断安装”，再重新检查。";
            if (code.Contains("REVIEW_AND_CONFIRM")) return "请先核对目录及将改动的文件，再勾选确认。";
            if (code.Contains("MANIFEST_MISSING")) return "安装清单缺失或过大。请重新取得完整候选目录，不要单独移动 EXE。";
            if (code.Contains("PAYLOAD_HASH_MISMATCH")) return "插件文件缺失或校验失败。请重新取得经核对的完整插件包；当前原型未附带插件载荷。";
            if (code.Contains("FILE_OPERATION_FAILED_OR_LOCKED")) return "文件操作失败或文件被占用。请关闭占用程序并检查目录写入权限；若有中断事务，先恢复。";
            return code;
        }
    }
}
