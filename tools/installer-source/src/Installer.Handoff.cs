using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Diagnostics;
using System.Collections.Generic;

namespace LilithSetup
{
    public sealed class InstallerHandoffPlan
    {
        public string Path, Cache, Archive, ArchiveHash, NextRoot, NextExeHash, Session, SessionHash;
        public int MinimumInstaller, RequiredConfig, OldProcessId; public long OldProcessStart;
        public bool LostDraft;
    }
    public static class InstallerHandoff
    {
        public const int Protocol = 1;
        static string Inside(string root, string relative) { return WizardProvisioning.PathIn(root, relative); }
        static void Write(string path, Dictionary<string, object> document)
        {
            SetupCore.NoReparse(path); string temp = path + ".tmp"; SetupCore.NoReparse(temp);
            File.WriteAllText(temp, DataJson.Write(document), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
        }
        static Dictionary<string, object> Read(string path, int max)
        {
            SetupCore.NoReparse(path); SetupCore.Require(File.Exists(path) && new FileInfo(path).Length <= max, "LBWIZ_HANDOFF_FILE_MISSING_OR_TOO_LARGE"); return DataJson.Read(File.ReadAllText(path));
        }
        static byte[] TrustedManifest(string archive, string expectedHash)
        {
            SetupCore.NoReparse(archive);
            using (var stream = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                using (var sha = System.Security.Cryptography.SHA256.Create()) SetupCore.Require(BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant() == expectedHash, "LBWIZ_RELEASE_ARCHIVE_CHANGED");
                stream.Position = 0;
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Read, true))
                {
                    var entries = zip.Entries.Where(entry => entry.FullName == "PACKAGE-FILES.json").ToArray();
                    SetupCore.Require(entries.Length == 1 && entries[0].Length > 0 && entries[0].Length <= 1048576, "LBWIZ_HANDOFF_PACKAGE_MANIFEST_INVALID");
                    using (var input = entries[0].Open()) using (var output = new MemoryStream())
                    { byte[] buffer = new byte[81920]; int count; while ((count = input.Read(buffer, 0, buffer.Length)) > 0) { SetupCore.Require(output.Length + count <= 1048576, "LBWIZ_HANDOFF_PACKAGE_MANIFEST_INVALID"); output.Write(buffer, 0, count); } return output.ToArray(); }
                }
            }
        }
        static Dictionary<string, object> VerifyPackage(string root, string archive, string expectedHash)
        {
            root = SetupCore.SafePath(root, null); SetupCore.NoReparse(root);
            byte[] trusted = TrustedManifest(archive, expectedHash); string cachedManifest = Inside(root, "PACKAGE-FILES.json");
            SetupCore.Require(File.Exists(cachedManifest) && new FileInfo(cachedManifest).Length == trusted.Length && SetupCore.FileHash(cachedManifest) == SetupCore.Hash(trusted), "LBWIZ_HANDOFF_ARCHIVE_BINDING_MISMATCH");
            // Validate roles using records from the verified ZIP, never a cache's self-written claims.
            var manifest = DataJson.Read(Encoding.UTF8.GetString(trusted)); var rows = DataJson.Get(manifest, "files") as object[];
            SetupCore.Require(rows != null && rows.Length > 0 && rows.Length <= 1000, "LBWIZ_HANDOFF_PACKAGE_MANIFEST_INVALID");
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0;
            foreach (Dictionary<string, object> row in rows)
            {
                string relative = (string)row["path"], file = Inside(root, relative); long length = Convert.ToInt64(row["bytes"]); string hash = InstallManifest.Digest((string)row["sha256"]);
                SetupCore.Require(allowed.Add(file) && length >= 0 && length <= 52428800 && File.Exists(file) && new FileInfo(file).Length == length && SetupCore.FileHash(file) == hash, "LBWIZ_HANDOFF_PACKAGE_INTEGRITY_FAILED");
                total += length; SetupCore.Require(total <= 134217728, "LBWIZ_HANDOFF_PACKAGE_TOO_LARGE");
            }
            allowed.Add(Inside(root, "PACKAGE-FILES.json"));
            var queue = new Queue<string>(); queue.Enqueue(root); int count = 0;
            while (queue.Count > 0)
            {
                string dir = queue.Dequeue(); SetupCore.NoReparse(dir);
                foreach (string entry in Directory.EnumerateFileSystemEntries(dir)) { SetupCore.NoReparse(entry); SetupCore.Require(++count <= 3000, "LBWIZ_HANDOFF_PACKAGE_TOO_MANY_FILES"); if (Directory.Exists(entry)) queue.Enqueue(entry); else SetupCore.Require(allowed.Contains(entry), "LBWIZ_HANDOFF_UNLISTED_PACKAGE_FILE"); }
            }
            SetupCore.Require(allowed.Contains(Inside(root, "setup/LilithBridge-Installer.exe")) && allowed.Contains(Inside(root, "setup/install-manifest.json")) && allowed.Contains(Inside(root, "CANDIDATE.json")), "LBWIZ_HANDOFF_REQUIRED_ROLE_MISSING");
            return Read(Inside(root, "CANDIDATE.json"), 65536);
        }
        static string ExtractVerified(string archive, DownloadSpec spec, string cache)
        {
            SetupCore.Require(TrustedDownloads.Matches(archive, spec), "LBWIZ_RELEASE_ARCHIVE_CHANGED");
            string target = Inside(cache, "installer-" + spec.Hash);
            if (Directory.Exists(target)) { VerifyPackage(target, archive, spec.Hash); return target; }
            string stage = Inside(cache, "installer-stage-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stage);
            using (var zip = ZipFile.OpenRead(archive))
            {
                var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0;
                SetupCore.Require(zip.Entries.Count <= 1000, "LBWIZ_HANDOFF_PACKAGE_TOO_MANY_FILES");
                foreach (var entry in zip.Entries)
                {
                    string relative = entry.FullName.Replace('/', '\\'); if (relative.EndsWith("\\")) continue;
                    SetupCore.Require(unique.Add(relative) && (entry.ExternalAttributes & unchecked((int)0xF0000000)) != unchecked((int)0xA0000000), "LBWIZ_HANDOFF_UNSAFE_ARCHIVE");
                    total += entry.Length; SetupCore.Require(entry.Length >= 0 && entry.Length <= 52428800 && total <= 134217728, "LBWIZ_HANDOFF_PACKAGE_TOO_LARGE");
                    string file = Inside(stage, relative); Directory.CreateDirectory(Path.GetDirectoryName(file));
                    using (var input = entry.Open()) using (var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    { byte[] buffer = new byte[81920]; long written = 0; int count; while ((count = input.Read(buffer, 0, buffer.Length)) > 0) { written += count; SetupCore.Require(written <= entry.Length, "LBWIZ_HANDOFF_PACKAGE_TOO_LARGE"); output.Write(buffer, 0, count); } SetupCore.Require(written == entry.Length, "LBWIZ_HANDOFF_PACKAGE_INTEGRITY_FAILED"); }
                }
            }
            VerifyPackage(stage, archive, spec.Hash); Directory.Move(stage, target); return target;
        }
        public static string Prepare(string archive, DownloadSpec spec, WizardOptions options)
        {
            SetupCore.Require(options.Confirm, "LBWIZ_CONFIRM_SELECTION_FIRST"); string cache = SetupCore.SafePath(options.CacheRoot, null); SetupCore.NoReparse(cache); Directory.CreateDirectory(cache);
            // The verified source ZIP may come from an offline bundle. Copy it under the owned cache
            // so a later helper never relies on removable media or a changing offline directory.
            string cached = Inside(cache, spec.Name); if (!SetupCore.Same(cached, archive)) { if (File.Exists(cached) && !TrustedDownloads.Matches(cached, spec)) File.Move(cached, cached + ".invalid-" + Guid.NewGuid().ToString("N")); WizardProvisioning.CopyVerified(archive, cached, spec.Hash); }
            string root = ExtractVerified(cached, spec, cache); var package = VerifyPackage(root, cached, spec.Hash); var install = Read(Inside(root, "setup/install-manifest.json"), 65536);
            int minimum = install.ContainsKey("min_installer_schema") ? Convert.ToInt32(install["min_installer_schema"]) : 1, config = install.ContainsKey("config_schema") ? Convert.ToInt32(install["config_schema"]) : 1;
            SetupCore.Require(minimum >= 1 && minimum <= 1000 && config >= 1 && config <= 1000, "LBWIZ_HANDOFF_PROTOCOL_UNSUPPORTED");
            SetupCore.Require(package.ContainsKey("handoff_protocol") && Convert.ToInt32(package["handoff_protocol"]) == Protocol && package.ContainsKey("installer_schema") && Convert.ToInt32(package["installer_schema"]) >= minimum && package.ContainsKey("supported_config_schema") && Convert.ToInt32(package["supported_config_schema"]) >= config, "LBWIZ_HANDOFF_PROTOCOL_UNSUPPORTED");
            // A downloaded manifest cannot choose a program, URL, command or system destination.
            string next = Inside(root, "setup/LilithBridge-Installer.exe");
            string folder = Inside(cache, "handoffs/" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
            string session = Inside(folder, "session.json"); WizardSession.SaveCopy(options, session, "InstallerHandoffPrepared");
            using (var process = Process.GetCurrentProcess())
            {
                var document = new Dictionary<string, object> { { "schema", Protocol }, { "cache", cache }, { "archive", cached }, { "archive_sha256", spec.Hash }, { "archive_bytes", spec.Bytes }, { "next_root", root }, { "next_exe_sha256", SetupCore.FileHash(next) }, { "session", session }, { "session_sha256", SetupCore.FileHash(session) }, { "minimum_installer_schema", minimum }, { "required_config_schema", config }, { "old_pid", process.Id }, { "old_start_utc_ticks", process.StartTime.ToUniversalTime().Ticks }, { "lost_unsaved_draft", options.ApiKey.Length > 0 || options.ProviderEdited }, { "state", "Prepared" } };
                string path = Inside(folder, "handoff.json"); Write(path, document); return path;
            }
        }
        public static InstallerHandoffPlan Validate(string path)
        {
            path = SetupCore.SafePath(path, null); var document = Read(path, 32768);
            SetupCore.Require(Convert.ToInt32(document["schema"]) == Protocol && Path.GetFileName(path) == "handoff.json", "LBWIZ_HANDOFF_PROTOCOL_UNSUPPORTED");
            string cache = SetupCore.SafePath((string)document["cache"], null), hash = InstallManifest.Digest((string)document["archive_sha256"]), archive = SetupCore.SafePath((string)document["archive"], null), root = SetupCore.SafePath((string)document["next_root"], null), session = SetupCore.SafePath((string)document["session"], null);
            SetupCore.NoReparse(cache); SetupCore.NoReparse(archive); SetupCore.NoReparse(root); SetupCore.NoReparse(session);
            SetupCore.Require(SetupCore.Inside(Inside(cache, "handoffs"), path, false) && SetupCore.Same(session, Inside(Path.GetDirectoryName(path), "session.json")) && SetupCore.Same(root, Inside(cache, "installer-" + hash)) && SetupCore.Same(Path.GetDirectoryName(archive), cache), "LBWIZ_HANDOFF_TARGET_REJECTED");
            SetupCore.Require(File.Exists(archive) && new FileInfo(archive).Length == Convert.ToInt64(document["archive_bytes"]) && SetupCore.FileHash(archive) == hash, "LBWIZ_RELEASE_ARCHIVE_CHANGED");
            string exeHash = InstallManifest.Digest((string)document["next_exe_sha256"]), sessionHash = InstallManifest.Digest((string)document["session_sha256"]);
            var package = VerifyPackage(root, archive, hash); int minimum = Convert.ToInt32(document["minimum_installer_schema"]), config = Convert.ToInt32(document["required_config_schema"]);
            SetupCore.Require(Convert.ToInt32(package["handoff_protocol"]) == Protocol && Convert.ToInt32(package["installer_schema"]) >= minimum && Convert.ToInt32(package["supported_config_schema"]) >= config && SetupCore.FileHash(Inside(root, "setup/LilithBridge-Installer.exe")) == exeHash && SetupCore.FileHash(session) == sessionHash, "LBWIZ_HANDOFF_PACKAGE_INTEGRITY_FAILED");
            return new InstallerHandoffPlan { Path = path, Cache = cache, Archive = archive, ArchiveHash = hash, NextRoot = root, NextExeHash = exeHash, Session = session, SessionHash = sessionHash, MinimumInstaller = minimum, RequiredConfig = config, OldProcessId = Convert.ToInt32(document["old_pid"]), OldProcessStart = Convert.ToInt64(document["old_start_utc_ticks"]), LostDraft = (bool)document["lost_unsaved_draft"] };
        }
        public static ProcessStartInfo NextStart(InstallerHandoffPlan plan)
        {
            return new ProcessStartInfo(Inside(plan.NextRoot, "setup/LilithBridge-Installer.exe"), "--resume-handoff " + WizardProvisioning.Quote(plan.Path)) { WorkingDirectory = Inside(plan.NextRoot, "setup"), UseShellExecute = false, CreateNoWindow = true };
        }
        public static void LaunchHelper(string packageRoot, string planPath)
        {
            Validate(planPath); string helper = Inside(packageRoot, "LilithBridge-Handoff.exe"); SetupCore.Require(File.Exists(helper), "LBWIZ_HANDOFF_HELPER_MISSING");
            using (var process = Process.Start(new ProcessStartInfo(helper, "--handoff " + WizardProvisioning.Quote(planPath)) { WorkingDirectory = packageRoot, UseShellExecute = false, CreateNoWindow = true })) SetupCore.Require(process != null, "LBWIZ_HANDOFF_HELPER_NOT_STARTED");
        }
        static void State(string path, string state) { var document = Read(path, 32768); document["state"] = state; Write(path, document); }
        public static void RunHelper(string path)
        {
            var plan = Validate(path); string gate = Inside(Path.GetDirectoryName(path), ".helper.lock");
            using (var locked = new FileStream(gate, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                var document = Read(path, 32768); if ((string)document["state"] == "Accepted") return;
                State(path, "WaitingForOldInstaller");
                try
                {
                    try
                    {
                        using (var old = Process.GetProcessById(plan.OldProcessId))
                        {
                            if (old.StartTime.ToUniversalTime().Ticks == plan.OldProcessStart) SetupCore.Require(old.WaitForExit(120000), "LBWIZ_HANDOFF_OLD_INSTALLER_STILL_RUNNING");
                        }
                    }
                    catch (ArgumentException) { }
                    plan = Validate(path); State(path, "LaunchingNewInstaller"); using (var next = Process.Start(NextStart(plan))) SetupCore.Require(next != null, "LBWIZ_HANDOFF_NEXT_NOT_STARTED");
                }
                catch { State(path, "LaunchFailed"); throw; }
            }
        }
        public static WizardOptions Accept(string path, string ownBase, int installerSchema, int configSchema)
        {
            var plan = Validate(path); SetupCore.Require(SetupCore.Same(SetupCore.SafePath(ownBase, null), Inside(plan.NextRoot, "setup")) && SetupCore.FileHash(Inside(ownBase, "LilithBridge-Installer.exe")) == plan.NextExeHash && installerSchema >= plan.MinimumInstaller && configSchema >= plan.RequiredConfig, "LBWIZ_HANDOFF_NEXT_CAPABILITY_MISMATCH");
            WizardOptions options = WizardSession.ReadFile(plan.Cache, plan.Session); SetupCore.Require(options != null, "LBWIZ_HANDOFF_SESSION_INVALID");
            options.FromHandoff = true; options.LostHandoffDraft = plan.LostDraft; options.Confirm = true; State(path, "Accepted"); return options;
        }
    }
}
