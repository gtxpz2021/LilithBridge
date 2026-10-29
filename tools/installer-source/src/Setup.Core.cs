using System;
using System.IO;
using System.Text;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace LilithSetup
{
    public sealed class SetupException : Exception
    {
        public SetupException(string code) : base(code) { }
    }

    public static class DataJson
    {
        public static JavaScriptSerializer Serializer()
        {
            return new JavaScriptSerializer { MaxJsonLength = 4194304, RecursionLimit = 100 };
        }
        public static Dictionary<string, object> Read(string text)
        {
            try
            {
                text = text.TrimStart('\uFEFF');
                new StrictJson(text).Validate();
                var value = Serializer().DeserializeObject(text) as Dictionary<string, object>;
                if (value == null) throw new SetupException("LBSETUP_CONFIG_OBJECT_REQUIRED");
                ValidateKnown(value);
                return value;
            }
            catch (SetupException) { throw; }
            catch { throw new SetupException("LBSETUP_CONFIG_INVALID_JSON"); }
        }
        public static string Write(Dictionary<string, object> doc)
        {
            ValidateKnown(doc);
            try { return Serializer().Serialize(doc); }
            catch { throw new SetupException("LBSETUP_JSON_SERIALIZATION_FAILED"); }
        }
        public static object Get(Dictionary<string, object> map, string key)
        {
            object value; return map != null && map.TryGetValue(key, out value) ? value : null;
        }
        public static Dictionary<string, object> Map(Dictionary<string, object> map, string key, bool create)
        {
            object value;
            if (map.TryGetValue(key, out value))
            {
                var child = value as Dictionary<string, object>;
                if (child == null) throw new SetupException("LBSETUP_CONFIG_BAD_TYPE");
                return child;
            }
            if (!create) return null;
            var next = new Dictionary<string, object>(StringComparer.Ordinal); map[key] = next; return next;
        }
        public static string String(Dictionary<string, object> map, string key, string fallback)
        {
            object value = Get(map, key); return value == null ? fallback : (string)value;
        }
        static void Strings(Dictionary<string, object> map, params string[] keys)
        {
            if (map == null) return;
            foreach (string key in keys) if (map.ContainsKey(key) && !(map[key] is string)) throw new SetupException("LBSETUP_CONFIG_BAD_TYPE");
        }
        public static void ValidateKnown(Dictionary<string, object> doc)
        {
            if (doc == null) throw new SetupException("LBSETUP_CONFIG_OBJECT_REQUIRED");
            Strings(doc, "active_provider");
            Strings(Map(doc, "provider", false), "api_key", "base_url", "model");
            var profiles = Map(doc, "provider_profiles", false);
            if (profiles != null) foreach (var item in profiles)
            {
                var profile = item.Value as Dictionary<string, object>;
                if (profile == null) throw new SetupException("LBSETUP_CONFIG_BAD_TYPE");
                Strings(profile, "api_key", "base_url", "model");
            }
            var semantic = Map(doc, "semantic_recall", false);
            Strings(semantic, "python_path", "worker_script", "model_path", "index_directory");
            if (semantic != null && semantic.ContainsKey("enabled") && !(semantic["enabled"] is bool)) throw new SetupException("LBSETUP_CONFIG_BAD_TYPE");
        }
        public static Dictionary<string, object> EffectiveProvider(Dictionary<string, object> doc)
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            var profiles = Map(doc, "provider_profiles", false);
            string active = String(doc, "active_provider", "");
            var profile = profiles != null ? Get(profiles, active) as Dictionary<string, object> : null;
            foreach (var source in new[] { profile, Map(doc, "provider", false) })
                if (source != null) foreach (string key in new[] { "api_key", "base_url", "model" })
                    if (source.ContainsKey(key)) result[key] = source[key];
            return result;
        }
    }

    // Syntax gate: JavaScriptSerializer alone accepts several JavaScript extensions.
    // Never include the rejected input or parser excerpts in errors.
    sealed class StrictJson
    {
        readonly string text; int at, nodes;
        public StrictJson(string value) { text = value; }
        void Bad() { throw new SetupException("LBSETUP_CONFIG_INVALID_JSON"); }
        void Space() { while (at < text.Length && (text[at] == ' ' || text[at] == '\t' || text[at] == '\r' || text[at] == '\n')) at++; }
        bool Take(char c) { Space(); if (at < text.Length && text[at] == c) { at++; return true; } return false; }
        public void Validate() { Value(0); Space(); if (at != text.Length) Bad(); }
        void Value(int depth)
        {
            if (depth > 90 || ++nodes > 200000) Bad();
            Space(); if (at == text.Length) Bad(); char c = text[at];
            if (c == '{')
            {
                at++; var names = new HashSet<string>(StringComparer.Ordinal); if (Take('}')) return;
                do { Space(); string name = Quoted(); if (!names.Add(name) || !Take(':')) Bad(); Value(depth + 1); if (Take('}')) return; } while (Take(',')); Bad();
            }
            else if (c == '[')
            {
                at++; if (Take(']')) return;
                do { Value(depth + 1); if (Take(']')) return; } while (Take(',')); Bad();
            }
            else if (c == '"') Quoted();
            else if (c == 't') Literal("true"); else if (c == 'f') Literal("false"); else if (c == 'n') Literal("null");
            else Number();
        }
        void Literal(string token) { if (at + token.Length > text.Length || text.Substring(at, token.Length) != token) Bad(); at += token.Length; }
        bool Digit() { return at < text.Length && text[at] >= '0' && text[at] <= '9'; }
        void Number()
        {
            if (at < text.Length && text[at] == '-') at++;
            if (!Digit()) Bad();
            if (text[at] == '0') at++; else while (Digit()) at++;
            if (at < text.Length && text[at] == '.') { at++; if (!Digit()) Bad(); while (Digit()) at++; }
            if (at < text.Length && (text[at] == 'e' || text[at] == 'E')) { at++; if (at < text.Length && (text[at] == '+' || text[at] == '-')) at++; if (!Digit()) Bad(); while (Digit()) at++; }
        }
        string Quoted()
        {
            if (at == text.Length || text[at++] != '"') { Bad(); return null; }
            var value = new StringBuilder();
            while (at < text.Length)
            {
                char c = text[at++]; if (c == '"') return value.ToString(); if (c < 32) Bad();
                if (c != '\\') { value.Append(c); continue; }
                if (at == text.Length) Bad(); c = text[at++];
                switch (c)
                {
                    case '"': case '\\': case '/': value.Append(c); break;
                    case 'b': value.Append('\b'); break; case 'f': value.Append('\f'); break;
                    case 'n': value.Append('\n'); break; case 'r': value.Append('\r'); break; case 't': value.Append('\t'); break;
                    case 'u':
                        if (at + 4 > text.Length) Bad();
                        int code; if (!Int32.TryParse(text.Substring(at, 4), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out code)) Bad();
                        value.Append((char)code); at += 4; break;
                    default: Bad(); break;
                }
            }
            Bad(); return null;
        }
    }

    public sealed class CopyContext
    {
        public string GameRoot, DataRoot, ConfigPath, PluginPath, PluginHash, Marker, MarkerHash;
    }
    public sealed class Snapshot
    {
        public bool Exists; public string Digest; public Dictionary<string, object> Document;
    }
    public enum RecallAction { Preserve, Disable, Enable }
    public sealed class EditRequest
    {
        public string ApiKey = "", BaseUrl = "", ModelName = "", ModelOverride = "", PythonOverride = "", WorkerOverride = "";
        public bool ClearKey, ConfirmCopy;
        public RecallAction Recall = RecallAction.Preserve;
        public LocalCheck CheckedLocal;
    }
    public sealed class LocalCheck
    {
        public string GameRoot, ConfigDigest, Python, Worker, Model, Index, Stamp;
        public bool FilesVerified;
    }
    public sealed class SaveResult
    {
        public bool Committed, ReadbackVerified; public string BackupPath;
        public readonly List<string> Warnings = new List<string>();
    }

    public static class SetupCore
    {
        public const string PluginCandidate = "47a9493ae11e34afa8b3b1bb4028308bfa00b029052b00ad1631984f88b9ee44";
        public const string Plugin062 = "0b3d10b4dcea1d35365afb06e77bd2184592c26efa7a4114530ea77a417df353";
        public const string Plugin061 = "1df82a2620b2a62af17458d087c090660c8af690a0e2ae37f0eba2c86301a0f0";
        public const string ModelRevision = "7999e1d3359715c523056ef9478215996d62a620";
        public const string ManifestHash = "c0f597c0cd4a19a54e08b3d589633b94154ea6f76df9c23365a9ff212eb07c17";
        public const string DefaultPython = "semantic-runtime-v1/python/python.exe";
        public const string DefaultWorker = "semantic-runtime-v1/semantic_worker.py";
        public const string DefaultModel = "semantic-runtime-v1/models/bge-small-zh-v1.5";
        public const string DefaultIndex = "semantic-recall-index";
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        public static void Require(bool condition, string code) { if (!condition) throw new SetupException(code); }
        public static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }
        public static string FileHash(string path)
        {
            using (var stream = File.OpenRead(path)) using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
        public static string SafePath(string path, string basis)
        {
            Require(!String.IsNullOrWhiteSpace(path) && !Regex.IsMatch(path, @"^[a-zA-Z]+://") && !path.StartsWith(@"\\"), "LBSETUP_LOCAL_PATH_REQUIRED");
            path = path.Replace('/', '\\');
            Require(!Regex.IsMatch(path, @"^[A-Za-z]:[^\\]|^\\|[\x00-\x1f""<>|?*]"), "LBSETUP_AMBIGUOUS_PATH");
            string prefix = Path.GetPathRoot(path);
            foreach (string part in path.Substring(prefix.Length).Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries))
                Require(part != "." && part != ".." && !part.EndsWith(".") && !part.EndsWith(" ") && !Regex.IsMatch(part, @"[:]|~[0-9]|^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\..*)?$"), "LBSETUP_UNSAFE_PATH_SEGMENT");
            if (!Path.IsPathRooted(path)) { Require(!String.IsNullOrEmpty(basis), "LBSETUP_ABSOLUTE_PATH_REQUIRED"); path = Path.Combine(basis, path); }
            string full = Path.GetFullPath(path); return full.Length == Path.GetPathRoot(full).Length ? full : full.TrimEnd('\\');
        }
        public static bool Same(string a, string b) { return String.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        public static bool Inside(string parent, string child, bool equal)
        {
            parent = parent.TrimEnd('\\'); child = child.TrimEnd('\\');
            return (equal && Same(parent, child)) || child.StartsWith(parent + "\\", StringComparison.OrdinalIgnoreCase);
        }
        public static void NoReparse(string path)
        {
            for (string current = path; !String.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
                if (File.Exists(current) || Directory.Exists(current)) Require((File.GetAttributes(current) & FileAttributes.ReparsePoint) == 0, "LBSETUP_REPARSE_POINT_REJECTED");
        }
        public static CopyContext Inspect(string gameRoot) { return Inspect(gameRoot, Environment.GetEnvironmentVariable("LILITH_BRIDGE_DATA_DIR")); }
        public static CopyContext Inspect(string gameRoot, string environmentOverride)
        {
            string game = SafePath(gameRoot, null); NoReparse(game);
            Require(File.Exists(Path.Combine(game, "Lilith.exe")), "LBSETUP_GAME_EXE_MISSING");
            string plugin = Path.Combine(game, @"BepInEx\plugins\LilithBridge.dll");
            string marker = Path.Combine(Path.GetDirectoryName(plugin), "LilithBridge.data-dir.txt");
            NoReparse(plugin); NoReparse(marker);
            Require(File.Exists(plugin), "LBSETUP_PLUGIN_MISSING");
            string pluginHash = FileHash(plugin);
            Require(pluginHash == PluginCandidate || pluginHash == Plugin062 || pluginHash == Plugin061 || HostCapabilities.IsNamedBridge(plugin), "LBSETUP_UNSUPPORTED_PLUGIN_IDENTITY");
            Require(File.Exists(marker) && new FileInfo(marker).Length <= 8192, "LBSETUP_ISOLATION_MARKER_REQUIRED");
            byte[] markerBytes = File.ReadAllBytes(marker);
            var lines = new List<string>();
            foreach (string line in Utf8.GetString(markerBytes).TrimStart('\uFEFF').Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                if (!String.IsNullOrWhiteSpace(line) && !line.TrimStart().StartsWith("#")) lines.Add(line.Trim());
            Require(lines.Count == 1, "LBSETUP_MARKER_INVALID"); string candidate = lines[0];
            if (candidate.Length >= 2 && candidate.StartsWith("\"") && candidate.EndsWith("\"")) candidate = candidate.Substring(1, candidate.Length - 2);
            string data = SafePath(candidate, game);
            if (environmentOverride != null) Require(Same(SafePath(environmentOverride, game), data), "LBSETUP_ENVIRONMENT_ROOT_CONFLICT");
            Require(Inside(game, data, false), "LBSETUP_DATA_ROOT_MUST_BE_IN_COPY");
            string production = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LilithAI");
            Require(!Inside(production, data, true) && !Inside(data, production, true), "LBSETUP_PRODUCTION_ROOT_REJECTED");
            string config = Path.Combine(data, "config.json"); NoReparse(data); NoReparse(config);
            return new CopyContext { GameRoot = game, DataRoot = data, ConfigPath = config, PluginPath = plugin, PluginHash = pluginHash, Marker = marker, MarkerHash = Hash(markerBytes) };
        }
        public static Snapshot ReadSnapshot(string path)
        {
            NoReparse(path);
            if (!File.Exists(path)) return new Snapshot { Exists = false, Digest = "MISSING", Document = new Dictionary<string, object>(StringComparer.Ordinal) };
            Require(new FileInfo(path).Length <= 4194304, "LBSETUP_CONFIG_TOO_LARGE");
            byte[] bytes = File.ReadAllBytes(path); string text;
            try { text = Utf8.GetString(bytes); } catch { throw new SetupException("LBSETUP_CONFIG_INVALID_UTF8"); }
            return new Snapshot { Exists = true, Digest = Hash(bytes), Document = DataJson.Read(text) };
        }
        public static Snapshot Current(CopyContext context, string expectedDigest)
        {
            Process[] games = Process.GetProcessesByName("Lilith");
            try { Require(games.Length == 0, "LBSETUP_GAME_RUNNING_CLOSE_IT_FIRST"); } finally { foreach (var game in games) game.Dispose(); }
            CopyContext fresh = Inspect(context.GameRoot);
            Require(Same(fresh.DataRoot, context.DataRoot) && fresh.PluginHash == context.PluginHash && fresh.MarkerHash == context.MarkerHash, "LBSETUP_COPY_CHANGED_RECHECK");
            Snapshot current = ReadSnapshot(context.ConfigPath);
            Require(current.Digest == expectedDigest, "LBSETUP_CONFIG_CHANGED_RECHECK"); return current;
        }
        public static void ApplyChangedProviderText(EditRequest edit, Snapshot snapshot, string urlText, string modelText)
        {
            var original = DataJson.EffectiveProvider(snapshot.Document);
            edit.BaseUrl = String.Equals(urlText, DataJson.String(original, "base_url", ""), StringComparison.Ordinal) ? "" : urlText;
            edit.ModelName = String.Equals(modelText, DataJson.String(original, "model", ""), StringComparison.Ordinal) ? "" : modelText;
        }
        public static Dictionary<string, object> Patch(Snapshot snapshot, EditRequest edit)
        {
            var doc = DataJson.Read(DataJson.Write(snapshot.Document));
            var provider = DataJson.Map(doc, "provider", true);
            Require(!(edit.ClearKey && edit.ApiKey.Length != 0), "LBSETUP_KEY_CLEAR_AND_REPLACE_CONFLICT");
            var originalProvider = DataJson.EffectiveProvider(snapshot.Document);
            if (!String.IsNullOrWhiteSpace(edit.BaseUrl) && !ProviderPresets.SameEndpoint(DataJson.String(originalProvider, "base_url", ""), edit.BaseUrl.Trim()) && !String.IsNullOrEmpty(DataJson.String(originalProvider, "api_key", "")))
                Require(edit.ApiKey.Length != 0 || edit.ClearKey, "LBSETUP_NEW_SERVICE_REQUIRES_OWN_KEY_OR_EXPLICIT_CLEAR");
            if (edit.ApiKey.Length != 0)
            {
                Require(edit.ApiKey.Length <= 4096 && !Regex.IsMatch(edit.ApiKey, @"[\x00-\x1f\x7f]"), "LBSETUP_INVALID_KEY_FORMAT");
                provider["api_key"] = edit.ApiKey;
            }
            else if (edit.ClearKey) provider["api_key"] = "";
            if (!String.IsNullOrWhiteSpace(edit.BaseUrl))
            {
                Uri url; string input = edit.BaseUrl.Trim();
                Require(Uri.TryCreate(input, UriKind.Absolute, out url) && url.Scheme == "https" && String.IsNullOrEmpty(url.UserInfo) && String.IsNullOrEmpty(url.Query) && String.IsNullOrEmpty(url.Fragment), "LBSETUP_HTTPS_PROVIDER_URL_REQUIRED");
                provider["base_url"] = input.TrimEnd('/');
            }
            if (!String.IsNullOrWhiteSpace(edit.ModelName))
            {
                Require(edit.ModelName.Length <= 256 && !Regex.IsMatch(edit.ModelName, @"[\x00-\x1f\x7f]"), "LBSETUP_INVALID_MODEL_NAME");
                provider["model"] = edit.ModelName.Trim();
            }
            // File existence does not establish a configured provider. Validate
            // the result after profile fallback and the user's edits are merged.
            if (edit.ApiKey.Length > 0 || !String.IsNullOrWhiteSpace(edit.BaseUrl) || !String.IsNullOrWhiteSpace(edit.ModelName))
            {
                var effective = DataJson.EffectiveProvider(doc);
                Require(!String.IsNullOrWhiteSpace(DataJson.String(effective, "base_url", "")) && !String.IsNullOrWhiteSpace(DataJson.String(effective, "model", "")), "LBSETUP_FIRST_PROVIDER_URL_AND_MODEL_REQUIRED");
            }
            bool changeModel = !String.IsNullOrEmpty(edit.ModelOverride);
            bool changeRuntime = !String.IsNullOrEmpty(edit.PythonOverride) || !String.IsNullOrEmpty(edit.WorkerOverride);
            bool preparedEnable = edit.Recall == RecallAction.Enable && edit.CheckedLocal != null && edit.CheckedLocal.FilesVerified;
            Require(!changeModel || edit.Recall == RecallAction.Disable || preparedEnable, "LBSETUP_MODEL_CHANGE_REQUIRES_EXPLICIT_RECALL_OFF");
            Require(!changeRuntime || edit.Recall == RecallAction.Disable || preparedEnable, "LBSETUP_RUNTIME_PATH_CHANGE_REQUIRES_EXPLICIT_RECALL_OFF");
            if (changeModel || changeRuntime || edit.Recall != RecallAction.Preserve)
            {
                var semantic = DataJson.Map(doc, "semantic_recall", true);
                if (edit.Recall == RecallAction.Disable) semantic["enabled"] = false;
                if (changeModel) { semantic["model_path"] = edit.ModelOverride; if (String.IsNullOrWhiteSpace(DataJson.String(semantic, "index_directory", ""))) semantic["index_directory"] = DefaultIndex; }
                if (!String.IsNullOrEmpty(edit.PythonOverride)) semantic["python_path"] = edit.PythonOverride;
                if (!String.IsNullOrEmpty(edit.WorkerOverride)) semantic["worker_script"] = edit.WorkerOverride;
                if (edit.Recall == RecallAction.Enable)
                {
                    semantic["enabled"] = true;
                    foreach (var pair in new[] { new[] { "python_path", DefaultPython }, new[] { "worker_script", DefaultWorker }, new[] { "model_path", DefaultModel }, new[] { "index_directory", DefaultIndex } })
                        if (!semantic.ContainsKey(pair[0]) || String.IsNullOrWhiteSpace((string)semantic[pair[0]])) semantic[pair[0]] = pair[1];
                }
            }
            return doc;
        }
        public static LocalCheck Locate(CopyContext context, Snapshot snapshot, string modelOverride, string pythonOverride = "", string workerOverride = "")
        {
            var semantic = DataJson.Map(snapshot.Document, "semantic_recall", false);
            Func<string, string, string> resolve = (key, fallback) => { string value = DataJson.String(semantic, key, fallback); return SafePath(String.IsNullOrWhiteSpace(value) ? fallback : value, context.DataRoot); };
            var paths = new LocalCheck { GameRoot = context.GameRoot, ConfigDigest = snapshot.Digest, Python = String.IsNullOrEmpty(pythonOverride) ? resolve("python_path", DefaultPython) : SafePath(pythonOverride, context.DataRoot), Worker = String.IsNullOrEmpty(workerOverride) ? resolve("worker_script", DefaultWorker) : SafePath(workerOverride, context.DataRoot), Model = String.IsNullOrEmpty(modelOverride) ? resolve("model_path", DefaultModel) : SafePath(modelOverride, context.DataRoot), Index = resolve("index_directory", DefaultIndex) };
            Require(Inside(context.DataRoot, paths.Index, false), "LBSETUP_INDEX_MUST_STAY_IN_DATA_ROOT");
            Require(!Inside(paths.Model, paths.Index, true) && !Inside(paths.Index, paths.Model, true), "LBSETUP_MODEL_INDEX_OVERLAP");
            NoReparse(paths.Python); NoReparse(paths.Worker); NoReparse(paths.Model); NoReparse(paths.Index); return paths;
        }
        public static void ValidateRuntimeFiles(LocalCheck paths)
        {
            NoReparse(paths.Python); NoReparse(paths.Worker);
            Require(File.Exists(paths.Python) && Path.GetExtension(paths.Python).Equals(".exe", StringComparison.OrdinalIgnoreCase), "LBSETUP_PYTHON_EXE_REQUIRED");
            Require(File.Exists(paths.Worker) && Path.GetExtension(paths.Worker).Equals(".py", StringComparison.OrdinalIgnoreCase), "LBSETUP_WORKER_SCRIPT_REQUIRED");
            string manifest = Path.Combine(Path.GetDirectoryName(paths.Worker), "model-manifest.json"); NoReparse(manifest);
            Require(File.Exists(manifest) && FileHash(manifest) == ManifestHash, "LBSETUP_WORKER_MODEL_MANIFEST_MISSING_OR_MISMATCH");
        }
        public static LocalCheck CheckLocal(CopyContext context, Snapshot snapshot, string modelOverride, ModelValidator validator, string pythonOverride = "", string workerOverride = "")
        {
            Current(context, snapshot.Digest); LocalCheck check = Locate(context, snapshot, modelOverride, pythonOverride, workerOverride);
            ValidateRuntimeFiles(check);
            validator.Verify(check.Model);
            check.Stamp = LocalStamp(check, validator); check.FilesVerified = true; return check;
        }
        static string LocalStamp(LocalCheck check, ModelValidator validator)
        {
            ValidateRuntimeFiles(check);
            var b = new StringBuilder();
            foreach (string path in new[] { check.Python, check.Worker })
            {
                NoReparse(path); Require(File.Exists(path), "LBSETUP_BUNDLE_MISSING"); var f = new FileInfo(path);
                b.Append(path).Append('|').Append(f.Length).Append('|').Append(f.LastWriteTimeUtc.Ticks).Append('\n');
            }
            b.Append(validator.Fingerprint(check.Model)); return Hash(Utf8.GetBytes(b.ToString()));
        }
        public static SaveResult Save(CopyContext context, Snapshot snapshot, EditRequest edit, ModelValidator validator)
        { return SaveWithCommitBoundary(context, snapshot, edit, validator, null); }
        internal static SaveResult SaveWithCommitBoundary(CopyContext context, Snapshot snapshot, EditRequest edit, ModelValidator validator, Action enterCommit)
        {
            Require(edit.ConfirmCopy, "LBSETUP_EXPLICIT_COPY_CONFIRMATION_REQUIRED");
            Current(context, snapshot.Digest); var doc = Patch(snapshot, edit);
            if (!String.IsNullOrEmpty(edit.PythonOverride) || !String.IsNullOrEmpty(edit.WorkerOverride))
                ValidateRuntimeFiles(Locate(context, snapshot, edit.ModelOverride, edit.PythonOverride, edit.WorkerOverride));
            if (!String.IsNullOrEmpty(edit.ModelOverride))
            {
                LocalCheck selected = Locate(context, snapshot, edit.ModelOverride);
                Require(!Inside(selected.Model, selected.Index, true) && !Inside(selected.Index, selected.Model, true), "LBSETUP_MODEL_INDEX_OVERLAP");
                validator.Verify(selected.Model);
            }
            if (edit.Recall == RecallAction.Enable)
            {
                LocalCheck expected = Locate(context, snapshot, edit.ModelOverride, edit.PythonOverride, edit.WorkerOverride); LocalCheck check = edit.CheckedLocal;
                Require(check != null && check.FilesVerified && Same(check.GameRoot, context.GameRoot) && check.ConfigDigest == snapshot.Digest && Same(check.Python, expected.Python) && Same(check.Worker, expected.Worker) && Same(check.Model, expected.Model) && Same(check.Index, expected.Index), "LBSETUP_EXPLICIT_LOCAL_CHECK_REQUIRED");
                Require(check.Stamp == LocalStamp(expected, validator), "LBSETUP_LOCAL_FILES_CHANGED_RECHECK");
            }
            byte[] bytes = Utf8.GetBytes(DataJson.Write(doc));
            string temp = null, lockPath = Path.Combine(context.DataRoot, ".clean-setup.write.lock"); FileStream held = null, guardedOriginal = null;
            var result = new SaveResult();
            try
            {
                NoReparse(context.DataRoot); Directory.CreateDirectory(context.DataRoot); NoReparse(lockPath);
                held = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                Require(held.Length == 0, "LBSETUP_UNKNOWN_NONEMPTY_LOCK_FILE_REJECTED");
                Snapshot current = Current(context, snapshot.Digest);
                temp = context.ConfigPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { output.Write(bytes, 0, bytes.Length); output.Flush(true); }
                Current(context, snapshot.Digest);
                if (current.Exists)
                {
                    result.BackupPath = context.ConfigPath + ".before-setup." + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "." + Guid.NewGuid().ToString("N") + ".bak";
                    // Guard the actual source file while checking its bytes after the temp write.
                    // Ordinary external writers cannot write through this handle. ShareDelete permits
                    // atomic replacement, so recheck the path identity immediately before our replace.
                    guardedOriginal = new FileStream(context.ConfigPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                    string identity = FileIdentity.Of(guardedOriginal.SafeFileHandle);
                    using (var sha = SHA256.Create()) Require(BitConverter.ToString(sha.ComputeHash(guardedOriginal)).Replace("-", "").ToLowerInvariant() == snapshot.Digest, "LBSETUP_CONFIG_CHANGED_RECHECK");
                    if (enterCommit != null) enterCommit();
                    using (var currentPath = new FileStream(context.ConfigPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                        Require(FileIdentity.Of(currentPath.SafeFileHandle) == identity, "LBSETUP_CONFIG_CHANGED_RECHECK");
                    // Windows ReplaceFile provides atomic replacement, not a file-ID compare-and-swap.
                    // A noncooperating atomic rename in the tiny final OS call gap cannot be excluded.
                    File.Replace(temp, context.ConfigPath, result.BackupPath, false);
                }
                else { if (enterCommit != null) enterCommit(); File.Move(temp, context.ConfigPath); }
                result.Committed = true;
                try { result.ReadbackVerified = ReadSnapshot(context.ConfigPath).Digest == Hash(bytes) && (!current.Exists || FileHash(result.BackupPath) == snapshot.Digest); if (!result.ReadbackVerified) result.Warnings.Add("LBSETUP_COMMITTED_READBACK_OR_BACKUP_CHANGED"); }
                catch { result.Warnings.Add("LBSETUP_COMMITTED_READBACK_UNAVAILABLE"); }
            }
            finally
            {
                if (temp != null && File.Exists(temp)) try { File.Delete(temp); } catch { result.Warnings.Add("LBSETUP_TEMP_CLEANUP_FAILED"); }
                if (guardedOriginal != null) guardedOriginal.Dispose();
                if (held != null) try { held.Dispose(); } catch { result.Warnings.Add("LBSETUP_LOCK_CLEANUP_FAILED"); }
            }
            return result;
        }
        public static string SafeError(Exception error)
        {
            if (error is SetupException) return error.Message;
            if (error is UnauthorizedAccessException) return "LBSETUP_ACCESS_DENIED";
            if (error is IOException) return "LBSETUP_FILE_OPERATION_FAILED_OR_LOCKED";
            return "LBSETUP_OPERATION_FAILED";
        }
    }

    public sealed class ModelValidator
    {
        readonly Dictionary<string, object> manifest;
        readonly Dictionary<string, string> cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public ModelValidator(string manifestPath)
        {
            SetupCore.NoReparse(manifestPath);
            SetupCore.Require(SetupCore.FileHash(manifestPath) == SetupCore.ManifestHash, "LBSETUP_TRUSTED_MANIFEST_HASH_MISMATCH");
            manifest = DataJson.Read(File.ReadAllText(manifestPath, Encoding.UTF8));
            SetupCore.Require((string)manifest["revision"] == SetupCore.ModelRevision && ((object[])manifest["files"]).Length == 11, "LBSETUP_TRUSTED_MANIFEST_INVALID");
        }
        public string Fingerprint(string root)
        {
            root = SetupCore.SafePath(root, null); SetupCore.NoReparse(root);
            SetupCore.Require(Directory.Exists(root), "LBSETUP_MODEL_DIRECTORY_MISSING");
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var allowedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var b = new StringBuilder();
            foreach (Dictionary<string, object> spec in (object[])manifest["files"])
            {
                string file = SetupCore.SafePath((string)spec["file"], root); SetupCore.NoReparse(file); allowed.Add(file);
                SetupCore.Require(File.Exists(file) && new FileInfo(file).Length == Convert.ToInt64(spec["bytes"]), "LBSETUP_MODEL_FILE_MISSING_OR_SIZE_MISMATCH");
                var f = new FileInfo(file); b.Append(file).Append('|').Append(f.Length).Append('|').Append(f.LastWriteTimeUtc.Ticks).Append('\n');
                for (string dir = Path.GetDirectoryName(file); SetupCore.Inside(root, dir, false); dir = Path.GetDirectoryName(dir)) allowedDirs.Add(dir);
            }
            var pending = new Queue<string>(); pending.Enqueue(root);
            while (pending.Count != 0)
            {
                string dir = pending.Dequeue(); SetupCore.NoReparse(dir);
                foreach (string entry in Directory.EnumerateFileSystemEntries(dir))
                {
                    SetupCore.NoReparse(entry);
                    if (Directory.Exists(entry)) { SetupCore.Require(allowedDirs.Contains(entry), "LBSETUP_MODEL_EXTRA_DIRECTORY_REJECTED"); pending.Enqueue(entry); }
                    else SetupCore.Require(allowed.Contains(entry), "LBSETUP_MODEL_EXTRA_FILE_REJECTED");
                }
            }
            return SetupCore.Hash(Encoding.UTF8.GetBytes(b.ToString()));
        }
        public void Verify(string root)
        {
            string stamp = Fingerprint(root), prior;
            if (cache.TryGetValue(root, out prior) && prior == stamp) return;
            foreach (Dictionary<string, object> spec in (object[])manifest["files"])
                SetupCore.Require(SetupCore.FileHash(SetupCore.SafePath((string)spec["file"], root)) == (string)spec["sha256"], "LBSETUP_MODEL_HASH_MISMATCH");
            SetupCore.Require(stamp == Fingerprint(root), "LBSETUP_LOCAL_FILES_CHANGED_RECHECK"); cache[root] = stamp;
        }
    }
}
