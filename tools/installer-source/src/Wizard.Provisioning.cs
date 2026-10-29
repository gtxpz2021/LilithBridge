using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Diagnostics;
using System.Threading;
using System.Collections.Generic;

namespace LilithSetup
{
    public static class WizardProvisioning
    {
        public const string WorkerHash = "6ef8145e129c4399150367df4a6f41490349203c00eab898a0948eb6725d4036";
        public static string PathIn(string root, string relative)
        { string path = SetupCore.SafePath(relative, root); SetupCore.Require(SetupCore.Inside(root, path, false), "LBWIZ_PATH_ESCAPES_ROOT"); SetupCore.NoReparse(path); return path; }
        public static void CopyVerified(string source, string destination, string hash)
        {
            SetupCore.NoReparse(source); SetupCore.NoReparse(destination); SetupCore.Require(SetupCore.FileHash(source) == hash, "LBWIZ_BUNDLED_FILE_CHANGED");
            if (File.Exists(destination)) { SetupCore.Require(SetupCore.FileHash(destination) == hash, "LBWIZ_EXISTING_COMPONENT_CHANGED"); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)); string temp = destination + ".part"; SetupCore.NoReparse(temp);
            using (var input = File.OpenRead(source)) using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None)) { input.CopyTo(output); output.Flush(true); }
            SetupCore.Require(SetupCore.FileHash(temp) == hash, "LBWIZ_COPY_INTEGRITY_FAILED"); File.Move(temp, destination);
        }
        public static string PrivateBase(string data)
        {
            string root = PathIn(data, "optional-recall-v1"); string marker = PathIn(root, ".lilithbridge-components.json");
            if (Directory.Exists(root)) SetupCore.Require(File.Exists(marker) && File.ReadAllText(marker) == "{\"owner\":\"lilithbridge-components-v1\"}", "LBWIZ_FOREIGN_COMPONENT_DIRECTORY");
            else { Directory.CreateDirectory(root); File.WriteAllText(marker, "{\"owner\":\"lilithbridge-components-v1\"}", new UTF8Encoding(false)); }
            return root;
        }
        public static string ManagedRuntimeBase(string data, string overrideRoot)
        {
            string container = String.IsNullOrEmpty(overrideRoot) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"LilithBridge\r", SetupCore.Hash(Encoding.UTF8.GetBytes(data.ToUpperInvariant())).Substring(0, 12)) : overrideRoot;
            // Torch headers can exceed MAX_PATH under a deep game directory. Use an application-owned
            // short user-local location, recorded in config; never change Windows long-path policy.
            SetupCore.Require(container.Length + 27 <= 110, "LBWIZ_RUNTIME_PATH_TOO_LONG");
            SetupCore.NoReparse(container); Directory.CreateDirectory(container); return PrivateBase(container);
        }
        public static string InstallPrivatePython(string archive, string root, CancellationToken cancel)
        {
            SetupCore.Require(TrustedDownloads.Matches(archive, TrustedDownloads.Python), "LBWIZ_PYTHON_ARCHIVE_CHANGED");
            string target = PathIn(root, "python-3.12.10"), complete = PathIn(target, ".lilithbridge-python.json");
            if (Directory.Exists(target))
            {
                SetupCore.Require(File.Exists(complete) && File.ReadAllText(complete) == TrustedDownloads.Python.Hash, "LBWIZ_PRIVATE_PYTHON_INCOMPLETE");
                // Verify immutable archive members again before executing an existing interpreter.
                VerifyPython(archive, target, cancel); return PathIn(target, "tools/python.exe");
            }
            string staging = PathIn(root, "python-stage-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(staging);
            using (var zip = ZipFile.OpenRead(archive))
            {
                var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0;
                foreach (var entry in zip.Entries)
                {
                    cancel.ThrowIfCancellationRequested(); string relative = entry.FullName.Replace('/', '\\');
                    if (relative.EndsWith("\\")) continue;
                    SetupCore.Require(unique.Add(relative) && (entry.ExternalAttributes & 0xF0000000) != unchecked((int)0xA0000000), "LBWIZ_UNSAFE_PYTHON_ARCHIVE");
                    total += entry.Length; SetupCore.Require(total <= 45000000 && entry.Length <= 20000000, "LBWIZ_PYTHON_ARCHIVE_TOO_LARGE");
                    string file = PathIn(staging, relative); Directory.CreateDirectory(Path.GetDirectoryName(file));
                    using (var input = entry.Open()) using (var output = new FileStream(file, FileMode.CreateNew)) input.CopyTo(output);
                }
            }
            SetupCore.Require(File.Exists(PathIn(staging, "tools/python.exe")) && Directory.Exists(PathIn(staging, "tools/Lib/venv")) && Directory.Exists(PathIn(staging, "tools/Lib/ensurepip")), "LBWIZ_PYTHON_PACKAGE_INCOMPLETE");
            File.WriteAllText(PathIn(staging, ".lilithbridge-python.json"), TrustedDownloads.Python.Hash, new UTF8Encoding(false));
            cancel.ThrowIfCancellationRequested(); Directory.Move(staging, target); return PathIn(target, "tools/python.exe");
        }
        static void VerifyPython(string archive, string target, CancellationToken cancel)
        {
            using (var zip = ZipFile.OpenRead(archive)) foreach (var entry in zip.Entries)
            {
                cancel.ThrowIfCancellationRequested(); if (entry.FullName.EndsWith("/")) continue;
                string file = PathIn(target, entry.FullName); SetupCore.Require(File.Exists(file) && new FileInfo(file).Length == entry.Length, "LBWIZ_PRIVATE_PYTHON_CHANGED");
                using (var input = entry.Open()) using (var sha = System.Security.Cryptography.SHA256.Create())
                    SetupCore.Require(SetupCore.FileHash(file) == BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "").ToLowerInvariant(), "LBWIZ_PRIVATE_PYTHON_CHANGED");
            }
        }
        internal static string Quote(string argument)
        {
            var result = new StringBuilder("\""); int slash = 0;
            foreach (char c in argument) { if (c == '\\') { slash++; continue; } if (c == '"') { result.Append('\\', slash * 2 + 1).Append(c); slash = 0; continue; } result.Append('\\', slash).Append(c); slash = 0; }
            return result.Append('\\', slash * 2).Append('"').ToString();
        }
        public static void RunPython(string python, IEnumerable<string> arguments, string working, string log, Action<string> progress, CancellationToken cancel)
        {
            SetupCore.NoReparse(python); SetupCore.NoReparse(log); SetupCore.NoReparse(working); Directory.CreateDirectory(working); Directory.CreateDirectory(Path.GetDirectoryName(log));
            var start = new ProcessStartInfo(python, String.Join(" ", arguments.Select(Quote))) { WorkingDirectory = working, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
            // Only this child receives an isolated environment; never persist machine/user settings.
            var keep = new HashSet<string>(new[] { "SYSTEMROOT", "WINDIR", "COMSPEC", "PATH", "PATHEXT", "PROCESSOR_ARCHITECTURE", "NUMBER_OF_PROCESSORS" }, StringComparer.OrdinalIgnoreCase);
            foreach (string key in start.EnvironmentVariables.Keys.Cast<string>().ToArray()) if (!keep.Contains(key)) start.EnvironmentVariables.Remove(key);
            string temp = PathIn(working, "process-temp"); Directory.CreateDirectory(temp); start.EnvironmentVariables["TEMP"] = temp; start.EnvironmentVariables["TMP"] = temp;
            start.EnvironmentVariables["PYTHONDONTWRITEBYTECODE"] = "1"; start.EnvironmentVariables["PIP_CONFIG_FILE"] = "NUL"; start.EnvironmentVariables["PIP_DISABLE_PIP_VERSION_CHECK"] = "1";
            start.EnvironmentVariables["HF_HUB_OFFLINE"] = "1"; start.EnvironmentVariables["TRANSFORMERS_OFFLINE"] = "1"; start.EnvironmentVariables["HF_HUB_DISABLE_TELEMETRY"] = "1";
            using (var writer = new StreamWriter(log, false, new UTF8Encoding(false)))
            using (var process = new Process { StartInfo = start })
            {
                object gate = new object(); Action<string> line = value => { if (value == null) return; lock (gate) { writer.WriteLine(value); writer.Flush(); } if (progress != null && (value.StartsWith("Running ") || value.StartsWith("Import ") || value.StartsWith("Creating "))) progress(value); };
                process.OutputDataReceived += (s, e) => line(e.Data); process.ErrorDataReceived += (s, e) => line(e.Data);
                process.Start(); process.StandardInput.Close(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
                while (!process.WaitForExit(250))
                {
                    // Dependency scripts may have pip children: wait for their bounded operation instead of orphaning them.
                }
                process.WaitForExit(); cancel.ThrowIfCancellationRequested(); SetupCore.Require(process.ExitCode == 0, "LBWIZ_RUNTIME_INSTALL_FAILED");
            }
        }
        public static string InstallModel(string package, string components, string cache, string offline, bool network, Action<string, long, long> progress, CancellationToken cancel)
        {
            string manifest = Path.Combine(package, "model-manifest.json"), target = PathIn(components, "models/bge-small-zh-v1.5");
            var specs = TrustedDownloads.Model(manifest); var rows = (object[])DataJson.Read(File.ReadAllText(manifest))["files"];
            for (int i = 0; i < specs.Count; i++)
            {
                cancel.ThrowIfCancellationRequested(); string dest = PathIn(target, (string)((Dictionary<string, object>)rows[i])["file"]);
                if (File.Exists(dest)) { SetupCore.Require(TrustedDownloads.Matches(dest, specs[i]), "LBWIZ_EXISTING_MODEL_CHANGED"); continue; }
                string input = TrustedDownloads.Acquire(specs[i], PathIn(cache, "model"), String.IsNullOrEmpty(offline) ? "" : PathIn(offline, "model"), network, progress, cancel); CopyVerified(input, dest, specs[i].Hash);
            }
            new ModelValidator(manifest).Verify(target); return target;
        }
        public static string DeployWorker(string package, string components)
        {
            string worker = PathIn(components, "worker/semantic_worker.py");
            CopyVerified(PathIn(package, "optional/semantic-worker/semantic_worker.py"), worker, WorkerHash);
            CopyVerified(PathIn(package, "optional/semantic-worker/model-manifest.json"), Path.Combine(Path.GetDirectoryName(worker), "model-manifest.json"), SetupCore.ManifestHash); return worker;
        }
    }
}
