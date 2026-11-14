using System.Text.Json;

namespace LilithBridge;

// Bounded volatile next-user-turn positions and optional complete inline entries.
// No persistence, write reference, interpretation or cross-request tool-result ref.
internal sealed record WorkReadPosition(string entry_id, string locator, string query,
    int first_row, int last_row);
internal sealed record WorkPositionRead(string Root, string ConfigRoot, string Session,
    string OriginEvent, string ArchiveDigest, WorkReadPosition[] Positions, Func<bool> Permitted)
{
    internal JsonElement[] InlineEntries { get; init; } = Array.Empty<JsonElement>();
    internal string OriginRequest { get; init; } = "";
    internal DateTimeOffset? LocalDispatchAt { get; init; }
}

internal sealed partial class DialogueRequest
{
    private sealed record StagedWorkPosition(string Hash, WorkPositionRead Read);
    private readonly Dictionary<string, StagedWorkPosition> _workPositionStaged = new(StringComparer.Ordinal);
    private readonly object _workPositionGate = new();
    internal WorkPositionRead? WorkReferencePreviousPositions { get; set; }

    internal void StageWorkReferencePositions(string callId, string result, Func<bool> permitted)
    {
        try
        {
            using var outer = JsonDocument.Parse(result);
            var value = outer.RootElement;
            if (!value.TryGetProperty("status", out var status) || status.GetString() != "ok"
                || !value.TryGetProperty("material", out var material) || material.ValueKind != JsonValueKind.String) return;
            string[] framed = material.GetString()!.Split('\n');
            if (framed.Length != 3 || framed[0] != "[作品资料·局部节选]" || framed[2] != "[作品资料结束]") return;
            using var page = JsonDocument.Parse(framed[1]);
            var root = page.RootElement;
            if (root.GetProperty("schema_version").GetInt32() is not (1 or 2)
                || root.GetProperty("kind").GetString() != "work_reference_data") return;
            string digest = root.GetProperty("archive_sha256").GetString()!;
            if (digest.Length != 64 || value.GetProperty("archive_sha256").GetString() != digest) return;
            var positions = new List<WorkReadPosition>();
            var inlineEntries = new List<JsonElement>();
            int inlineBytes = 0;
            foreach (var entry in root.GetProperty("entries").EnumerateArray().Take(2))
            {
                // Entry references have no new body here. An earlier inline
                // result may still match this same actual outgoing transcript.
                if (!entry.TryGetProperty("lines", out var lines) || lines.ValueKind != JsonValueKind.Array
                    || lines.GetArrayLength() == 0) continue;
                var first = lines[0]; var last = lines[lines.GetArrayLength() - 1];
                string query = first.GetProperty("script").GetString() + "#" + first.GetProperty("line_id").GetString();
                string id = entry.GetProperty("id").GetString()!, locator = entry.GetProperty("locator").GetString()!;
                if (query.Length is < 2 or > 201 || id.Length > 220 || locator.Length > 302) continue;
                positions.Add(new(id, locator, query, first.GetProperty("corpus_row").GetInt32(), last.GetProperty("corpus_row").GetInt32()));
                int bytes = System.Text.Encoding.UTF8.GetByteCount(entry.GetRawText());
                if (!entry.TryGetProperty("entry_ref", out _) && bytes <= WorkMaterialMaxBytes - inlineBytes)
                { inlineEntries.Add(entry.Clone()); inlineBytes += bytes; }
            }
            if (positions.Count == 0) return; // Short-form archives have no equivalent exact line query.
            var read = new WorkPositionRead(ReviewDataRoot, ReviewConfigRoot, ReviewSessionIdentity,
                UserEventId, digest, positions.ToArray(), permitted)
                { InlineEntries = inlineEntries.ToArray(), OriginRequest = Id };
            lock (_workPositionGate)
            {
                if (_workPositionStaged.Count >= 16 && !_workPositionStaged.ContainsKey(callId))
                    _workPositionStaged.Remove(_workPositionStaged.Keys.First());
                _workPositionStaged[callId] = new(MemoryStore.B1Hash(result), read);
            }
        }
        catch (Exception) { /* Optional position capture never blocks a valid result. */ }
    }

    internal void ObserveWorkReferencePositionDispatch(string wire)
    {
        try
        {
            lock (_workPositionGate)
            {
                if (_workPositionStaged.Count == 0 || IsCancelled) return;
                using var doc = JsonDocument.Parse(wire);
                var root = doc.RootElement;
                bool chat = root.TryGetProperty("messages", out var messages);
                if (!chat && !root.TryGetProperty("input", out messages)) return;
                if (messages.ValueKind != JsonValueKind.Array) return;
                WorkPositionRead? latest = null;
                foreach (var message in messages.EnumerateArray())
                {
                    if (message.ValueKind != JsonValueKind.Object
                        || !message.TryGetProperty(chat ? "role" : "type", out var tag)
                        || tag.GetString() != (chat ? "tool" : "function_call_output")
                        || !message.TryGetProperty(chat ? "tool_call_id" : "call_id", out var id)
                        || !message.TryGetProperty(chat ? "content" : "output", out var body)
                        || id.ValueKind != JsonValueKind.String || body.ValueKind != JsonValueKind.String) continue;
                    if (_workPositionStaged.TryGetValue(id.GetString()!, out var staged)
                        && staged.Hash == MemoryStore.B1Hash(body.GetString()!)) latest = staged.Read;
                }
                // Dispatch is an attempted supply, not acknowledgement or use.
                if (latest != null) ReviewHandoff?.PublishWorkReferencePositions(this,
                    latest with { LocalDispatchAt = DateTimeOffset.UtcNow });
            }
        }
        catch (Exception) { /* Keep the existing dispatch result unchanged. */ }
    }

    internal string WorkReferencePositionOffer()
    {
        try
        {
            var read = AvailableWorkReferenceRead();
            if (read == null) return "";
            // Re-reading still uses the current ordinary source/consent guards,
            // selection and budget. The position offer is not a source grant.
            return "\n上个用户轮请求里的原作定位（此处只有定位，无正文）："
                + ReviewContext.Serialize(new { archive_sha256 = read.ArchiveDigest, positions = read.Positions })
                + "。需要时可将 query 原样用于本工具；按当前资料和预算重新查询，不保证返回原片段。";
        }
        catch (Exception) { return ""; }
    }
}

internal sealed partial class ReviewHandoff
{
    private string _workPositionOwner = "", _workPositionEvent = "", _workPositionRoot = "",
        _workPositionConfigRoot = "", _workPositionSession = "";
    private WorkPositionRead? _workPositionLatest, _workPositionIncoming;

    // Called by the existing controller owner transition, even when the next
    // request does not offer tools. Merely offering a pointer never renews it.
    internal void BeginWorkReferenceTurn(DialogueRequest request)
    {
        lock (_gate)
        {
            if (_workPositionRoot != request.ReviewDataRoot || _workPositionConfigRoot != request.ReviewConfigRoot
                || _workPositionSession != request.ReviewSessionIdentity)
                _workPositionLatest = _workPositionIncoming = null;
            if (_workPositionEvent != request.UserEventId)
            {
                _workPositionIncoming = _workPositionLatest;
                _workPositionLatest = null;
            }
            _workPositionOwner = request.Id; _workPositionEvent = request.UserEventId;
            _workPositionRoot = request.ReviewDataRoot; _workPositionConfigRoot = request.ReviewConfigRoot;
            _workPositionSession = request.ReviewSessionIdentity;
            request.WorkReferencePreviousPositions = _workPositionIncoming;
        }
    }

    internal void PublishWorkReferencePositions(DialogueRequest request, WorkPositionRead read)
    {
        lock (_gate)
            if (_workPositionOwner == request.Id && !request.IsCancelled) _workPositionLatest = read;
    }
}
