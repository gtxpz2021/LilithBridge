using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LilithBridge;

// Reflection failures only. No request/config/envelope or reasoning enters this sink.
// The startup marker is consumed once; previous sessions are never reopened.
internal static class ReflectionFailureEvidence
{
    internal const string TransformVersion = "strip_json_fences_trim/v1";
    private const int FileLimit = 65536, SampleLimit = 3, AnalysisLimit = 262144;
    private static readonly object Sync = new();
    private static string? Session;
    private static int Slots;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2255", Justification = "Consume an explicitly armed one-startup diagnostic before any response; never re-arm on restart.")]
    [ModuleInitializer]
    internal static void Initialize()
    {
        try
        {
            string dll = typeof(ReflectionFailureEvidence).Assembly.Location;
            string marker = dll + ".reflection-capture-next.json";
            if (!File.Exists(marker)) return;
            string claimed = marker + ".consumed-" + Guid.NewGuid().ToString("N");
            File.Move(marker, claimed); // Claim before validation: a bad marker cannot auto-resume.
            if (new FileInfo(claimed).Length > 4096) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(claimed));
            var root = doc.RootElement;
            if (root.GetProperty("schema").GetInt32() != 1 || !root.GetProperty("enabled").GetBoolean()) return;
            // The one-startup consent belongs to these exact bytes, not a later
            // replacement at the same installation path. Claim was already consumed.
            string expectedDll = root.GetProperty("dll_sha256").GetString() ?? "";
            if (expectedDll.Length != 64 || !expectedDll.Equals(Hash(File.ReadAllBytes(dll)), StringComparison.OrdinalIgnoreCase)) return;
            DateTimeOffset expiry = root.GetProperty("expires_utc").GetDateTimeOffset();
            if (expiry <= DateTimeOffset.UtcNow || expiry > DateTimeOffset.UtcNow.AddHours(25)) return;
            string directory = root.GetProperty("capture_directory").GetString() ?? "";
            if (!Path.IsPathFullyQualified(directory) || directory.StartsWith(@"\\", StringComparison.Ordinal)) return;
            directory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
            string assemblyDirectory = Path.GetDirectoryName(dll)! + Path.DirectorySeparatorChar;
            if (directory.StartsWith(assemblyDirectory, StringComparison.OrdinalIgnoreCase)
                || (directory + Path.DirectorySeparatorChar).Equals(assemblyDirectory, StringComparison.OrdinalIgnoreCase)
                || new DriveInfo(Path.GetPathRoot(directory)!).DriveType != DriveType.Fixed) return;
            // Parent arming defaults to the current user's LocalAppData, outside data/package/log roots.
            string session = Path.Combine(directory, "session-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(session);
            File.WriteAllText(Path.Combine(session, "enabled"), "one process; at most 3 failures; stop by moving this marker");
            Session = session;
        }
        catch { /* Missing permission/invalid marker disables capture. No retry or message text. */ }
    }

    internal static void Observe(string content, string input, string requestId, string attemptId,
        string stage, string code, string exceptionType, long? line, long? column, bool knownCredential)
    {
        try
        {
            byte[] raw = Encoding.UTF8.GetBytes(content), bytes = Encoding.UTF8.GetBytes(input);
            var metadata = new
            {
                schema = 1, transform_version = TransformVersion, request_id = requestId, attempt_id = attemptId,
                stage, code, exception_type = exceptionType, json_line = line, json_byte_in_line = column,
                content_chars = content.Length, content_utf8_bytes = raw.Length, content_sha256 = Hash(raw),
                parser_chars = input.Length, parser_utf8_bytes = bytes.Length, parser_sha256 = Hash(bytes),
                structure = Structure(bytes, line, column)
            };
            // Structural metadata persists independently of raw-capture activation.
            try { NativeEventLog.Trace("memory.reflection.structure", "mod", "system", "reflection failure structure", JsonSerializer.Serialize(metadata)); }
            catch { }
            Capture(content, metadata, knownCredential);
        }
        catch { /* Even hash/probe/allocation/sink faults cannot change the parser outcome. */ }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static object Structure(byte[] bytes, long? line, long? column)
    {
        long? absolute = null;
        if (line >= 0 && column >= 0)
        {
            long currentLine = 0; int start = 0;
            for (int i = 0; i < bytes.Length && currentLine < line; i++)
                if (bytes[i] == (byte)'\n') { currentLine++; start = i + 1; }
            if (currentLine == line && column <= bytes.Length - start) absolute = start + column;
        }
        bool bounded = bytes.Length <= AnalysisLimit, inString = false, escaped = false;
        int depth = 0; bool? firstComplete = null, trailing = null; long? firstEnd = null;
        if (bounded)
        {
            // No values/keys or exception messages are emitted by this observational parser.
            try
            {
                var reader = new Utf8JsonReader(bytes, true, default);
                using var first = JsonDocument.ParseValue(ref reader);
                firstComplete = true; firstEnd = reader.BytesConsumed;
                trailing = bytes.Skip((int)reader.BytesConsumed).Any(b => b is not (9 or 10 or 13 or 32));
            }
            catch (JsonException) { firstComplete = false; }
            foreach (byte b in bytes)
            {
                if (inString)
                {
                    if (escaped) escaped = false;
                    else if (b == (byte)'\\') escaped = true;
                    else if (b == (byte)'"') inString = false;
                }
                else if (b == (byte)'"') inString = true;
                else if (b is (byte)'{' or (byte)'[') depth++;
                else if (b is (byte)'}' or (byte)']') depth--;
            }
        }
        string byteClass = absolute == bytes.Length ? "eof" : absolute is >= 0 && absolute < bytes.Length
            ? Class(bytes[(int)absolute.Value]) : "unknown";
        return new
        {
            analysis_complete = bounded, json_absolute_utf8 = absolute,
            error_at_eof = absolute.HasValue ? absolute == bytes.Length : (bool?)null,
            error_byte_class = byteClass, first_value_complete = firstComplete, first_value_end_utf8 = firstEnd,
            trailing_nonwhitespace = trailing, eof_in_string = bounded ? inString : (bool?)null,
            eof_after_escape = bounded ? escaped : (bool?)null, eof_container_balance = bounded ? depth : (int?)null
        };
    }

    private static string Class(byte b) => b switch
    {
        (byte)'"' => "quote", (byte)'\\' => "backslash", (byte)'{' or (byte)'[' => "open_container",
        (byte)'}' or (byte)']' => "close_container", (byte)',' => "comma", (byte)':' => "colon",
        9 or 10 or 13 or 32 => "whitespace", < 32 => "control", >= 128 => "non_ascii",
        >= (byte)'0' and <= (byte)'9' => "digit", >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' => "letter", _ => "other"
    };

    private static void Capture(string content, object metadata, bool knownCredential)
    {
        lock (Sync)
        {
            if (Session == null || Slots >= SampleLimit || !File.Exists(Path.Combine(Session, "enabled"))) return;
            int slot = ++Slots; // Oversize, rejected credential, or a failed write still consumes a slot.
            try
            {
                string reason = knownCredential || CredentialLike(content) ? "credential_guard"
                    : Encoding.UTF8.GetByteCount(content) > FileLimit ? "oversize" : "complete";
                bool complete = reason == "complete";
                byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    schema = 1, captured_utc = DateTimeOffset.UtcNow, slot, complete_capture = complete,
                    capture_reason = reason, metadata, assistant_content = complete ? content : null
                });
                if (payload.Length > FileLimit)
                {
                    payload = JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        schema = 1, captured_utc = DateTimeOffset.UtcNow, slot, complete_capture = false,
                        capture_reason = "serialized_file_oversize", metadata, assistant_content = (string?)null
                    });
                }
                if (payload.Length > FileLimit) return;
                string target = Path.Combine(Session, "failure-" + slot.ToString("00") + ".json");
                string temporary = target + ".partial";
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { stream.Write(payload); stream.Flush(); }
                File.Move(temporary, target);
            }
            catch { /* No logger recursion, API, parser recovery or pending mutation. */ }
        }
    }

    private static bool CredentialLike(string content) => content.Contains("Bearer ", StringComparison.OrdinalIgnoreCase)
        || content.Contains("sk-", StringComparison.OrdinalIgnoreCase)
        || content.Contains("api_key", StringComparison.OrdinalIgnoreCase)
        || content.Contains("api-key", StringComparison.OrdinalIgnoreCase)
        || content.Contains("authorization", StringComparison.OrdinalIgnoreCase);
}
