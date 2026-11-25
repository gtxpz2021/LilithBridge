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
    public sealed class ConcatenatedReadStream : Stream
    {
        readonly FileStream[] streams;
        readonly long[] offsets;
        readonly long[] lengths;
        readonly long totalLength;
        long position;
        bool isDisposed;

        public ConcatenatedReadStream(IList<string> partPaths)
        {
            SetupCore.Require(partPaths != null && partPaths.Count > 0, "LBWIZ_VOICEPACK_NOT_FOUND");
            streams = new FileStream[partPaths.Count];
            offsets = new long[partPaths.Count];
            lengths = new long[partPaths.Count];
            long running = 0;
            try
            {
                for (int i = 0; i < partPaths.Count; i++)
                {
                    string p = SetupCore.SafePath(partPaths[i], null);
                    SetupCore.NoReparse(p);
                    var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.Read);
                    streams[i] = fs;
                    offsets[i] = running;
                    lengths[i] = fs.Length;
                    running += fs.Length;
                }
                totalLength = running;
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public override bool CanRead { get { return !isDisposed; } }
        public override bool CanSeek { get { return !isDisposed; } }
        public override bool CanWrite { get { return false; } }
        public override long Length { get { if (isDisposed) throw new ObjectDisposedException("ConcatenatedReadStream"); return totalLength; } }
        public override long Position
        {
            get { if (isDisposed) throw new ObjectDisposedException("ConcatenatedReadStream"); return position; }
            set { Seek(value, SeekOrigin.Begin); }
        }
        public override void Flush() { }
        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }

        public override long Seek(long offset, SeekOrigin origin)
        {
            if (isDisposed) throw new ObjectDisposedException("ConcatenatedReadStream");
            long target = origin == SeekOrigin.Begin ? offset : origin == SeekOrigin.Current ? position + offset : totalLength + offset;
            if (target < 0 || target > totalLength) throw new IOException("Invalid seek offset in split archive");
            position = target;
            return position;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (isDisposed) throw new ObjectDisposedException("ConcatenatedReadStream");
            if (buffer == null) throw new ArgumentNullException("buffer");
            if (offset < 0 || count < 0 || offset + count > buffer.Length) throw new ArgumentOutOfRangeException("count");
            if (count == 0 || position >= totalLength) return 0;

            int totalRead = 0;
            while (count > 0 && position < totalLength)
            {
                int idx = FindPartIndex(position);
                long localOffset = position - offsets[idx];
                long available = lengths[idx] - localOffset;
                int toRead = (int)Math.Min(count, available);
                FileStream fs = streams[idx];
                if (fs.Position != localOffset) fs.Seek(localOffset, SeekOrigin.Begin);
                int got = fs.Read(buffer, offset, toRead);
                if (got <= 0) break;
                position += got;
                offset += got;
                count -= got;
                totalRead += got;
            }
            return totalRead;
        }

        int FindPartIndex(long pos)
        {
            int lo = 0, hi = offsets.Length - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (pos < offsets[mid]) hi = mid - 1;
                else if (pos >= offsets[mid] + lengths[mid]) lo = mid + 1;
                else return mid;
            }
            return Math.Max(0, Math.Min(offsets.Length - 1, lo));
        }

        protected override void Dispose(bool disposing)
        {
            if (!isDisposed)
            {
                isDisposed = true;
                if (disposing && streams != null)
                {
                    for (int i = 0; i < streams.Length; i++)
                    {
                        if (streams[i] != null)
                        {
                            try { streams[i].Dispose(); } catch { }
                            streams[i] = null;
                        }
                    }
                }
            }
            base.Dispose(disposing);
        }
    }

    public sealed class VoicePackEntry
    {
        public string RelativePath;
        public long Bytes;
        public string Sha256;
    }

    public sealed class VoicePackSource
    {
        public string Kind;
        public string DisplayName;
        public string FolderRoot;
        public string[] ArchiveParts;
        public long TotalBytes;
        public List<VoicePackEntry> Entries;

        public Stream OpenArchiveStream()
        {
            if (Kind == "zip")
            {
                SetupCore.NoReparse(ArchiveParts[0]);
                return new FileStream(ArchiveParts[0], FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            if (Kind == "split-zip")
                return new ConcatenatedReadStream(ArchiveParts);
            throw new SetupException("LBWIZ_VOICEPACK_NOT_FOUND");
        }
    }

    public sealed class ExistingVoiceStatus
    {
        public bool Ready;
        public string PythonPath = "";
        public string TtsModelRelative = "";
    }

    public static class VoicePackProvisioning
    {
        public const string TtsWorkerHash = "43922a80eec1658bd1cd3eed6261f93763ca339f7c11801110a94a9c7f261ec4";
        public const string TtsVerifyHash = "670ad8af6dfceed227d767dd6a393a3a1740fcd9fcaa25483e5d1cddbe88a49e";
        public const string SttWorkerHash = "424ba08d9870ab5a3f7ec5e1988900be274122f1fc39de46215b1bccd18b3e54";
        public const string SttVerifyHash = "29a20db36bba867c437300a74012f2dabee9f99eead01d9c81141f2a08ddf4a7";
        public const string SttManifestHash = "628b6a8967201275a6750bc8f0e04d99cd8dd3ace4b0774494c10abda75d2acf";

        public static readonly string[] PreviousTtsWorkerHashes = new[]
        {
            "d87eea09a22aeb34580704332038c31f7d71188e31e927aece90f237b67a42c3"
        };
        public static readonly string[] PreviousSttWorkerHashes = new string[0];

        static IEnumerable<string> CandidateRoots(string packageRoot, string offlineRoot)
        {
            var roots = new List<string>();
            if (!String.IsNullOrWhiteSpace(offlineRoot))
            {
                string safeOffline = SetupCore.SafePath(offlineRoot, null);
                roots.Add(safeOffline);
                roots.Add(Path.Combine(safeOffline, "voice-pack"));
            }
            if (!String.IsNullOrWhiteSpace(TrustedDownloads.LauncherDirectory))
            {
                string launcher = SetupCore.SafePath(TrustedDownloads.LauncherDirectory, null);
                roots.Add(launcher);
                roots.Add(Path.Combine(launcher, "voice-pack"));
                roots.Add(Path.Combine(launcher, "offline-cache"));
            }
            if (!String.IsNullOrWhiteSpace(packageRoot))
            {
                string cur = SetupCore.SafePath(packageRoot, null);
                for (int i = 0; i < 3 && !String.IsNullOrEmpty(cur); i++, cur = Path.GetDirectoryName(cur))
                {
                    roots.Add(cur);
                    roots.Add(Path.Combine(cur, "voice-pack"));
                }
            }
            return roots.Distinct(StringComparer.OrdinalIgnoreCase);
        }

        static List<VoicePackEntry> ParseManifestText(string jsonText)
        {
            var doc = DataJson.Read(jsonText);
            SetupCore.Require(doc.ContainsKey("schema") && Convert.ToInt32(doc["schema"]) == 1, "LBWIZ_VOICEPACK_MANIFEST_INVALID");
            var rows = DataJson.Get(doc, "files") as object[];
            SetupCore.Require(rows != null && rows.Length > 0 && rows.Length <= 500, "LBWIZ_VOICEPACK_MANIFEST_INVALID");
            var entries = new List<VoicePackEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool hasServer = false, hasGguf = false, hasRefs = false, hasSttModel = false;
            foreach (Dictionary<string, object> row in rows)
            {
                string rel = DataJson.String(row, "path", "").Replace('\\', '/').Trim('/');
                SetupCore.Require(rel.Length > 0 && !rel.Contains(":") && !rel.Split('/').Any(seg => seg == "" || seg == "." || seg == ".."), "LBWIZ_VOICEPACK_MANIFEST_INVALID");
                SetupCore.Require(rel.StartsWith("tts-runtime/", StringComparison.Ordinal) || rel.StartsWith("stt-runtime/", StringComparison.Ordinal) || rel.StartsWith("voice-wheelhouse/", StringComparison.Ordinal), "LBWIZ_VOICEPACK_MANIFEST_INVALID");
                SetupCore.Require(seen.Add(rel), "LBWIZ_VOICEPACK_MANIFEST_INVALID");
                long bytes = Convert.ToInt64(row["bytes"]);
                string sha = InstallManifest.Digest(DataJson.String(row, "sha256", ""));
                SetupCore.Require(bytes > 0 && bytes <= 6442450944L, "LBWIZ_VOICEPACK_MANIFEST_INVALID");
                if (String.Equals(rel, "tts-runtime/bin/audiocpp_server.exe", StringComparison.OrdinalIgnoreCase)) hasServer = true;
                if (rel.StartsWith("tts-runtime/models/", StringComparison.OrdinalIgnoreCase) && rel.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)) hasGguf = true;
                if (String.Equals(rel, "tts-runtime/refs/refs_manifest.json", StringComparison.OrdinalIgnoreCase)) hasRefs = true;
                if (rel.StartsWith("stt-runtime/models/", StringComparison.OrdinalIgnoreCase) && rel.EndsWith("/model.int8.onnx", StringComparison.OrdinalIgnoreCase)) hasSttModel = true;
                entries.Add(new VoicePackEntry { RelativePath = rel, Bytes = bytes, Sha256 = sha });
            }
            SetupCore.Require(hasServer && hasGguf && hasRefs && hasSttModel, "LBWIZ_VOICEPACK_MANIFEST_INVALID");
            return entries;
        }

        static List<VoicePackEntry> ReadManifestFromZip(ZipArchive zip)
        {
            var matches = zip.Entries.Where(e => String.Equals(e.FullName, "voice-pack-manifest.json", StringComparison.OrdinalIgnoreCase) || String.Equals(e.FullName, "voice-pack/voice-pack-manifest.json", StringComparison.OrdinalIgnoreCase)).ToArray();
            SetupCore.Require(matches.Length == 1 && matches[0].Length > 0 && matches[0].Length <= 1048576, "LBWIZ_VOICEPACK_MANIFEST_INVALID");
            using (var input = matches[0].Open())
            using (var reader = new StreamReader(input, Encoding.UTF8))
                return ParseManifestText(reader.ReadToEnd());
        }

        static ZipArchiveEntry FindEntryInZip(ZipArchive zip, string relativePath, long expectedBytes)
        {
            string[] candidates = new[] { relativePath, "voice-pack/" + relativePath };
            for (int i = 0; i < candidates.Length; i++)
            {
                string want = candidates[i];
                var found = zip.Entries.Where(e => String.Equals(e.FullName, want, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (found.Length == 1 && found[0].Length == expectedBytes) return found[0];
            }
            return null;
        }

        public static VoicePackSource TryLocate(string packageRoot, string offlineRoot)
        {
            foreach (string root in CandidateRoots(packageRoot, offlineRoot))
            {
                if (!Directory.Exists(root)) continue;
                try { SetupCore.NoReparse(root); } catch { continue; }

                // 1. Pre-extracted folder containing voice-pack-manifest.json
                string folderManifest = Path.Combine(root, "voice-pack-manifest.json");
                if (File.Exists(folderManifest))
                {
                    try
                    {
                        SetupCore.NoReparse(folderManifest);
                        var entries = ParseManifestText(File.ReadAllText(folderManifest, Encoding.UTF8));
                        long sum = 0;
                        bool allPresent = true;
                        for (int i = 0; i < entries.Count; i++)
                        {
                            string fp = WizardProvisioning.PathIn(root, entries[i].RelativePath);
                            if (!File.Exists(fp) || new FileInfo(fp).Length != entries[i].Bytes) { allPresent = false; break; }
                            sum += entries[i].Bytes;
                        }
                        if (allPresent)
                            return new VoicePackSource { Kind = "folder", DisplayName = Path.GetFileName(root), FolderRoot = root, ArchiveParts = new string[0], TotalBytes = sum, Entries = entries };
                    }
                    catch { }
                }

                // 2. Split archive: LilithBridge-VoicePack*.zip.001 + .002 + ...
                string[] firstParts = new string[0];
                try { firstParts = Directory.GetFiles(root, "LilithBridge-VoicePack*.zip.001", SearchOption.TopDirectoryOnly); } catch { }
                Array.Sort(firstParts, StringComparer.OrdinalIgnoreCase);
                for (int pIdx = firstParts.Length - 1; pIdx >= 0; pIdx--)
                {
                    string part1 = firstParts[pIdx];
                    string prefix = part1.Substring(0, part1.Length - 4); // strip ".001"
                    var parts = new List<string>();
                    long totalBytes = 0;
                    for (int n = 1; n <= 99; n++)
                    {
                        string candidate = prefix + "." + n.ToString("000");
                        if (!File.Exists(candidate)) break;
                        SetupCore.NoReparse(candidate);
                        parts.Add(candidate);
                        totalBytes += new FileInfo(candidate).Length;
                    }
                    if (parts.Count == 0) continue;
                    try
                    {
                        using (var stream = new ConcatenatedReadStream(parts))
                        using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
                        {
                            var entries = ReadManifestFromZip(zip);
                            return new VoicePackSource { Kind = "split-zip", DisplayName = Path.GetFileName(prefix) + "（共 " + parts.Count + " 个分卷）", FolderRoot = "", ArchiveParts = parts.ToArray(), TotalBytes = totalBytes, Entries = entries };
                        }
                    }
                    catch (InvalidDataException)
                    {
                        throw new SetupException("LBWIZ_VOICEPACK_PART_MISSING");
                    }
                    catch (SetupException) { throw; }
                    catch { }
                }

                // 3. Single ZIP archive: LilithBridge-VoicePack*.zip
                string[] singleZips = new string[0];
                try { singleZips = Directory.GetFiles(root, "LilithBridge-VoicePack*.zip", SearchOption.TopDirectoryOnly); } catch { }
                Array.Sort(singleZips, StringComparer.OrdinalIgnoreCase);
                for (int zIdx = singleZips.Length - 1; zIdx >= 0; zIdx--)
                {
                    string zipPath = singleZips[zIdx];
                    try
                    {
                        SetupCore.NoReparse(zipPath);
                        using (var fs = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                        using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
                        {
                            var entries = ReadManifestFromZip(zip);
                            return new VoicePackSource { Kind = "zip", DisplayName = Path.GetFileName(zipPath), FolderRoot = "", ArchiveParts = new[] { zipPath }, TotalBytes = fs.Length, Entries = entries };
                        }
                    }
                    catch (SetupException) { throw; }
                    catch { }
                }
            }
            return null;
        }

        static string NormalizedPythonFileHash(string path)
        {
            SetupCore.NoReparse(path);
            byte[] raw = File.ReadAllBytes(path);
            string text = Encoding.UTF8.GetString(raw).Replace("\r\n", "\n");
            return SetupCore.Hash(Encoding.UTF8.GetBytes(text));
        }

        static bool IsKnownWorker(string path, string currentHash, string[] previousHashes)
        {
            if (String.IsNullOrEmpty(path) || !File.Exists(path)) return false;
            string direct = SetupCore.FileHash(path);
            if (direct == currentHash || previousHashes.Contains(direct, StringComparer.OrdinalIgnoreCase)) return true;
            string norm = NormalizedPythonFileHash(path);
            return norm == currentHash || previousHashes.Contains(norm, StringComparer.OrdinalIgnoreCase);
        }

        public static ExistingVoiceStatus InspectExisting(string dataRoot, Snapshot snapshot)
        {
            var status = new ExistingVoiceStatus { Ready = false };
            if (String.IsNullOrWhiteSpace(dataRoot) || !Directory.Exists(dataRoot)) return status;
            try
            {
                string serverExe = WizardProvisioning.PathIn(dataRoot, "tts-runtime/bin/audiocpp_server.exe");
                string refsJson = WizardProvisioning.PathIn(dataRoot, "tts-runtime/refs/refs_manifest.json");
                string sttModel = WizardProvisioning.PathIn(dataRoot, "stt-runtime/models/sherpa-onnx-sense-voice-zh-en-ja-ko-yue-int8-2024-07-17/model.int8.onnx");
                string sttTokens = WizardProvisioning.PathIn(dataRoot, "stt-runtime/models/sherpa-onnx-sense-voice-zh-en-ja-ko-yue-int8-2024-07-17/tokens.txt");
                if (!File.Exists(serverExe) || !File.Exists(refsJson) || !File.Exists(sttModel) || !File.Exists(sttTokens))
                    return status;

                string ttsCfgModel = "";
                string ttsCfgPython = "";
                if (snapshot != null && snapshot.Document != null)
                {
                    var ttsMap = DataJson.Map(snapshot.Document, "tts", false);
                    ttsCfgModel = DataJson.String(ttsMap, "model_path", "");
                    ttsCfgPython = DataJson.String(ttsMap, "python_path", "");
                }

                string chosenGgufRel = "";
                if (!String.IsNullOrWhiteSpace(ttsCfgModel))
                {
                    string resolvedModel = SetupCore.SafePath(ttsCfgModel, dataRoot);
                    if (File.Exists(resolvedModel)) chosenGgufRel = ttsCfgModel;
                }
                if (String.IsNullOrEmpty(chosenGgufRel))
                {
                    string defaultGguf = WizardProvisioning.PathIn(dataRoot, "tts-runtime/models/qwen3-tts-12hz-1.7b-lilith-e6-s07-q8_0.gguf");
                    if (File.Exists(defaultGguf)) chosenGgufRel = "tts-runtime/models/qwen3-tts-12hz-1.7b-lilith-e6-s07-q8_0.gguf";
                    else
                    {
                        string modelsDir = WizardProvisioning.PathIn(dataRoot, "tts-runtime/models");
                        if (Directory.Exists(modelsDir))
                        {
                            string[] ggufs = Directory.GetFiles(modelsDir, "*.gguf", SearchOption.TopDirectoryOnly);
                            if (ggufs.Length > 0) chosenGgufRel = "tts-runtime/models/" + Path.GetFileName(ggufs[0]);
                        }
                    }
                }
                if (String.IsNullOrEmpty(chosenGgufRel)) return status;

                string resolvedPy = "";
                if (!String.IsNullOrWhiteSpace(ttsCfgPython))
                {
                    try
                    {
                        string candPy = SetupCore.SafePath(ttsCfgPython, dataRoot);
                        if (File.Exists(candPy)) resolvedPy = candPy;
                    }
                    catch { }
                }
                if (String.IsNullOrEmpty(resolvedPy))
                {
                    try
                    {
                        string managed = WizardProvisioning.ManagedRuntimeBase(dataRoot, "");
                        string managedPy = Path.Combine(managed, "voice-runtime/.venv/Scripts/python.exe");
                        if (File.Exists(managedPy)) resolvedPy = managedPy;
                    }
                    catch { }
                }
                if (String.IsNullOrEmpty(resolvedPy)) return status;

                status.Ready = true;
                status.PythonPath = resolvedPy;
                status.TtsModelRelative = chosenGgufRel;
                return status;
            }
            catch { return status; }
        }

        public static bool CanAutoEnable(string packageRoot, string offlineRoot, string dataRoot, Snapshot snapshot)
        {
            if (InspectExisting(dataRoot, snapshot).Ready) return true;
            try { return TryLocate(packageRoot, offlineRoot) != null; }
            catch { return false; }
        }

        static void DeployOrUpgradeScript(string bundledSource, string targetPath, string expectedHash, string[] previousHashes, bool requireKnownIfExisting)
        {
            SetupCore.NoReparse(bundledSource);
            SetupCore.NoReparse(targetPath);
            SetupCore.Require(SetupCore.FileHash(bundledSource) == expectedHash, "LBWIZ_BUNDLED_FILE_CHANGED");
            if (File.Exists(targetPath))
            {
                if (SetupCore.FileHash(targetPath) == expectedHash) return;
                if (requireKnownIfExisting)
                    SetupCore.Require(IsKnownWorker(targetPath, expectedHash, previousHashes), "LBWIZ_EXISTING_COMPONENT_CHANGED");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath));
            string temp = targetPath + ".part";
            SetupCore.NoReparse(temp);
            using (var input = File.OpenRead(bundledSource))
            using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                input.CopyTo(output);
                output.Flush(true);
            }
            SetupCore.Require(SetupCore.FileHash(temp) == expectedHash, "LBWIZ_COPY_INTEGRITY_FAILED");
            if (File.Exists(targetPath)) File.Replace(temp, targetPath, null);
            else File.Move(temp, targetPath);
        }

        public static void DeployBundledVoiceScripts(string packageRoot, string dataRoot)
        {
            DeployOrUpgradeScript(
                WizardProvisioning.PathIn(packageRoot, "optional/tts-worker/tts_worker.py"),
                WizardProvisioning.PathIn(dataRoot, "tts-runtime/tts_worker.py"),
                TtsWorkerHash, PreviousTtsWorkerHashes, true);
            DeployOrUpgradeScript(
                WizardProvisioning.PathIn(packageRoot, "optional/tts-worker/verify_runtime_files.py"),
                WizardProvisioning.PathIn(dataRoot, "tts-runtime/verify_runtime_files.py"),
                TtsVerifyHash, new string[0], false);
            DeployOrUpgradeScript(
                WizardProvisioning.PathIn(packageRoot, "optional/stt-worker/stt_worker.py"),
                WizardProvisioning.PathIn(dataRoot, "stt-runtime/stt_worker.py"),
                SttWorkerHash, PreviousSttWorkerHashes, true);
            DeployOrUpgradeScript(
                WizardProvisioning.PathIn(packageRoot, "optional/stt-worker/verify_model_files.py"),
                WizardProvisioning.PathIn(dataRoot, "stt-runtime/verify_model_files.py"),
                SttVerifyHash, new string[0], false);
            DeployOrUpgradeScript(
                WizardProvisioning.PathIn(packageRoot, "optional/stt-worker/model-manifest.json"),
                WizardProvisioning.PathIn(dataRoot, "stt-runtime/model-manifest.json"),
                SttManifestHash, new string[0], false);
        }

        public static bool UpgradeKnownVoiceWorkersInPlace(string packageRoot, string dataRoot)
        {
            if (String.IsNullOrWhiteSpace(dataRoot) || !Directory.Exists(dataRoot)) return false;
            bool changed = false;
            string ttsDst = WizardProvisioning.PathIn(dataRoot, "tts-runtime/tts_worker.py");
            string ttsSrc = WizardProvisioning.PathIn(packageRoot, "optional/tts-worker/tts_worker.py");
            if (File.Exists(ttsDst) && File.Exists(ttsSrc) && SetupCore.FileHash(ttsDst) != TtsWorkerHash && IsKnownWorker(ttsDst, TtsWorkerHash, PreviousTtsWorkerHashes))
            {
                DeployOrUpgradeScript(ttsSrc, ttsDst, TtsWorkerHash, PreviousTtsWorkerHashes, true);
                changed = true;
            }
            string sttDst = WizardProvisioning.PathIn(dataRoot, "stt-runtime/stt_worker.py");
            string sttSrc = WizardProvisioning.PathIn(packageRoot, "optional/stt-worker/stt_worker.py");
            if (File.Exists(sttDst) && File.Exists(sttSrc) && SetupCore.FileHash(sttDst) != SttWorkerHash && IsKnownWorker(sttDst, SttWorkerHash, PreviousSttWorkerHashes))
            {
                DeployOrUpgradeScript(sttSrc, sttDst, SttWorkerHash, PreviousSttWorkerHashes, true);
                changed = true;
            }
            return changed;
        }

        static void ExtractEntryToTarget(Stream input, string targetPath, long expectedBytes, string expectedSha256, string label, Action<string> progress, CancellationToken cancel)
        {
            SetupCore.NoReparse(targetPath);
            if (File.Exists(targetPath) && new FileInfo(targetPath).Length == expectedBytes && SetupCore.FileHash(targetPath) == expectedSha256)
                return;

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath));
            string temp = targetPath + ".vpack-" + Guid.NewGuid().ToString("N") + ".part";
            SetupCore.NoReparse(temp);
            try
            {
                using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    byte[] buffer = new byte[262144];
                    long written = 0;
                    long lastReport = 0;
                    int count;
                    while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        cancel.ThrowIfCancellationRequested();
                        written += count;
                        SetupCore.Require(written <= expectedBytes, "LBWIZ_VOICEPACK_HASH_MISMATCH");
                        output.Write(buffer, 0, count);
                        if (progress != null && expectedBytes >= 8388608 && written - lastReport >= 16777216)
                        {
                            lastReport = written;
                            progress("正在解压语音组件：" + label + "（" + (written / 1048576) + " / " + Math.Max(1, expectedBytes / 1048576) + " MiB）");
                        }
                    }
                    output.Flush(true);
                    SetupCore.Require(written == expectedBytes, "LBWIZ_VOICEPACK_HASH_MISMATCH");
                }
                cancel.ThrowIfCancellationRequested();
                SetupCore.Require(SetupCore.FileHash(temp) == expectedSha256, "LBWIZ_VOICEPACK_HASH_MISMATCH");
                if (File.Exists(targetPath)) File.Replace(temp, targetPath, null);
                else File.Move(temp, targetPath);
            }
            finally
            {
                if (File.Exists(temp)) { try { File.Delete(temp); } catch { } }
            }
        }

        public static VoiceInstallPatch Install(
            string packageRoot,
            string dataRoot,
            string componentsBase,
            string cacheRoot,
            string offlineRoot,
            bool allowNetwork,
            Snapshot snapshot,
            Action<string, long, long> transfer,
            Action<string> progress,
            CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();
            VoicePackSource source = TryLocate(packageRoot, offlineRoot);
            ExistingVoiceStatus existing = InspectExisting(dataRoot, snapshot);

            if (source == null && existing.Ready)
            {
                if (progress != null) progress("检测到已有完整本地语音环境，正在同步最新语音脚本…");
                DeployBundledVoiceScripts(packageRoot, dataRoot);
                return new VoiceInstallPatch
                {
                    Enable = true,
                    PythonPath = existing.PythonPath,
                    TtsModelRelative = existing.TtsModelRelative
                };
            }

            SetupCore.Require(source != null, "LBWIZ_VOICEPACK_NOT_FOUND");
            string wheelhouse = WizardProvisioning.PathIn(cacheRoot, "voice-wheelhouse");
            Directory.CreateDirectory(wheelhouse);
            string chosenGgufRel = "tts-runtime/models/qwen3-tts-12hz-1.7b-lilith-e6-s07-q8_0.gguf";
            int wheelCount = 0;

            if (source.Kind == "folder")
            {
                for (int i = 0; i < source.Entries.Count; i++)
                {
                    cancel.ThrowIfCancellationRequested();
                    VoicePackEntry item = source.Entries[i];
                    string srcFile = WizardProvisioning.PathIn(source.FolderRoot, item.RelativePath);
                    string dstFile = item.RelativePath.StartsWith("voice-wheelhouse/", StringComparison.Ordinal)
                        ? WizardProvisioning.PathIn(wheelhouse, Path.GetFileName(item.RelativePath))
                        : WizardProvisioning.PathIn(dataRoot, item.RelativePath);
                    if (item.RelativePath.StartsWith("tts-runtime/models/", StringComparison.OrdinalIgnoreCase) && item.RelativePath.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
                        chosenGgufRel = item.RelativePath;
                    if (item.RelativePath.StartsWith("voice-wheelhouse/", StringComparison.Ordinal) && item.RelativePath.EndsWith(".whl", StringComparison.OrdinalIgnoreCase))
                        wheelCount++;
                    using (var fs = File.OpenRead(srcFile))
                        ExtractEntryToTarget(fs, dstFile, item.Bytes, item.Sha256, Path.GetFileName(item.RelativePath), progress, cancel);
                }
            }
            else
            {
                using (var archiveStream = source.OpenArchiveStream())
                using (var zip = new ZipArchive(archiveStream, ZipArchiveMode.Read))
                {
                    for (int i = 0; i < source.Entries.Count; i++)
                    {
                        cancel.ThrowIfCancellationRequested();
                        VoicePackEntry item = source.Entries[i];
                        string dstFile = item.RelativePath.StartsWith("voice-wheelhouse/", StringComparison.Ordinal)
                            ? WizardProvisioning.PathIn(wheelhouse, Path.GetFileName(item.RelativePath))
                            : WizardProvisioning.PathIn(dataRoot, item.RelativePath);
                        if (item.RelativePath.StartsWith("tts-runtime/models/", StringComparison.OrdinalIgnoreCase) && item.RelativePath.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
                            chosenGgufRel = item.RelativePath;
                        if (item.RelativePath.StartsWith("voice-wheelhouse/", StringComparison.Ordinal) && item.RelativePath.EndsWith(".whl", StringComparison.OrdinalIgnoreCase))
                            wheelCount++;
                        if (File.Exists(dstFile) && new FileInfo(dstFile).Length == item.Bytes && SetupCore.FileHash(dstFile) == item.Sha256)
                            continue;
                        ZipArchiveEntry zipEntry = FindEntryInZip(zip, item.RelativePath, item.Bytes);
                        SetupCore.Require(zipEntry != null, "LBWIZ_VOICEPACK_PART_MISSING");
                        using (var entryStream = zipEntry.Open())
                            ExtractEntryToTarget(entryStream, dstFile, item.Bytes, item.Sha256, Path.GetFileName(item.RelativePath), progress, cancel);
                    }
                }
            }

            DeployBundledVoiceScripts(packageRoot, dataRoot);

            if (progress != null) progress("正在准备隔离语音 Python 环境…");
            string pyArchive = TrustedDownloads.Acquire(TrustedDownloads.Python, cacheRoot, offlineRoot, allowNetwork, transfer, cancel, packageRoot, "");
            string basePython = WizardProvisioning.InstallPrivatePython(pyArchive, componentsBase, cancel);
            string voiceRuntime = WizardProvisioning.PathIn(componentsBase, "voice-runtime");
            bool venvReady = File.Exists(Path.Combine(voiceRuntime, "install-result.json")) && File.Exists(Path.Combine(voiceRuntime, ".venv/Scripts/python.exe"));

            var args = new List<string>
            {
                "-I", "-X", "utf8", "-B",
                Path.Combine(packageRoot, "optional/voice-runtime/voice_runtime_setup.py"),
                venvReady ? "verify" : "install",
                "--root", voiceRuntime,
                "--tts-worker", WizardProvisioning.PathIn(dataRoot, "tts-runtime/tts_worker.py"),
                "--stt-worker", WizardProvisioning.PathIn(dataRoot, "stt-runtime/stt_worker.py")
            };
            if (wheelCount > 0)
            {
                args.Add("--wheel-cache");
                args.Add(wheelhouse);
            }
            if (allowNetwork)
                args.Add("--allow-network");

            try
            {
                WizardProvisioning.RunPython(basePython, args, componentsBase, Path.Combine(componentsBase, "voice-environment-check.log"), progress, cancel);
            }
            catch (SetupException)
            {
                throw new SetupException("LBWIZ_VOICE_RUNTIME_FAILED");
            }

            string voicePython = Path.Combine(voiceRuntime, ".venv/Scripts/python.exe");
            if (progress != null) progress("正在离线核验 TTS 与 SenseVoice 语音文件完整性…");
            WizardProvisioning.RunPython(
                voicePython,
                new[]
                {
                    "-I", "-X", "utf8", "-B",
                    WizardProvisioning.PathIn(dataRoot, "tts-runtime/verify_runtime_files.py"),
                    "--server-exe", WizardProvisioning.PathIn(dataRoot, "tts-runtime/bin/audiocpp_server.exe"),
                    "--model", WizardProvisioning.PathIn(dataRoot, chosenGgufRel),
                    "--refs-manifest", WizardProvisioning.PathIn(dataRoot, "tts-runtime/refs/refs_manifest.json"),
                    "--bwe-model", WizardProvisioning.PathIn(dataRoot, "tts-runtime/models/flashsr_onnx/model.onnx")
                },
                dataRoot,
                Path.Combine(componentsBase, "tts-verify.log"),
                null,
                cancel);
            WizardProvisioning.RunPython(
                voicePython,
                new[]
                {
                    "-I", "-X", "utf8", "-B",
                    WizardProvisioning.PathIn(dataRoot, "stt-runtime/verify_model_files.py"),
                    "--model", WizardProvisioning.PathIn(dataRoot, "stt-runtime/models/sherpa-onnx-sense-voice-zh-en-ja-ko-yue-int8-2024-07-17"),
                    "--manifest", WizardProvisioning.PathIn(dataRoot, "stt-runtime/model-manifest.json")
                },
                dataRoot,
                Path.Combine(componentsBase, "stt-verify.log"),
                null,
                cancel);

            return new VoiceInstallPatch
            {
                Enable = true,
                PythonPath = voicePython,
                TtsModelRelative = chosenGgufRel
            };
        }

        public static bool WouldChange(Snapshot snapshot, VoiceInstallPatch patch)
        {
            if (patch == null) return false;
            var edit = new EditRequest { Voice = patch };
            var patched = SetupCore.Patch(snapshot, edit);
            return DataJson.Write(patched) != DataJson.Write(snapshot.Document);
        }
    }
}
