using System.Text.Json;
using System.Text.Json.Serialization;

namespace LilithBridge;

// Body-free pointers to already retained, exact source versions. Neither a
// selectable source grant nor a declaration that the model used a material.
internal sealed record B1ReadLink(string Carrier, string OwnerVersion, B1Ref Source,
    string BodyHash, string VersionFingerprint, int Offset, int Chars, int TotalChars,
    string SliceHash, string[] ToolCallIds);
internal sealed record B1ReadRepeat(string CallId, string OriginalCallId);
internal sealed record B1ReadIndex(int Schema, string RequestId, string StepId, string AttemptId,
    DateTimeOffset DispatchObservedAt, B1ReadLink[] Links, B1ReadRepeat[] Repeats, int UnindexedRows)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public B1WorkReadLink[]? WorkLinks { get; init; }
}

internal sealed partial record B1Receipt
{
    // Opaque optional metadata in the existing receipt row. Unknown/malformed
    // index versions must not prevent loading or using the retained thought.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? ReadIndex { get; init; }
}

internal sealed partial class DialogueRequest
{
    private sealed record StagedB1Read(string CallId, string JsonHash, B1ReadLink[] Links,
        string OriginalCallId, int UnindexedRows, B1WorkReadLink[]? WorkLinks = null);
    private readonly object _b1ReadIndexGate = new();
    private readonly Dictionary<string, StagedB1Read> _b1ReadIndexStaged = new(StringComparer.Ordinal);
    private readonly Dictionary<string, B1ReadIndex> _b1ReadIndexDispatched = new(StringComparer.Ordinal);

    internal void StageB1ReadIndex(string callId, ReviewToolOutcome outcome)
    {
        // Called after fitting and receipt projection. Capture failures are
        // historical gaps, never a new condition on a legitimate conversation.
        try
        {
            if (outcome.ExactB1Read == null && outcome.DayArtifacts == null) return;
            using var json = JsonDocument.Parse(outcome.Json);
            var root = json.RootElement;
            bool repeat = root.TryGetProperty("status", out var status) && status.GetString() == "already_supplied";
            string original = repeat ? root.GetProperty("original_tool_call_id").GetString() ?? "" : "";
            B1ReadLink[] links = repeat ? Array.Empty<B1ReadLink>() : outcome.ExactB1Read?.ReadLinks.ToArray()
                ?? outcome.DayArtifacts!.Items.Select(i => new B1ReadLink("artifact", i.OwnerId,
                    new(i.SourceKind, i.Turn.EventId, MemoryStore.B1Hash(i.Turn.Text), "history"),
                    MemoryStore.B1Hash(i.Turn.Text), i.Version, i.Offset, i.Text.Length, i.Turn.Text.Length,
                    MemoryStore.B1Hash(i.Text), Array.Empty<string>())).ToArray();
            var staged = new StagedB1Read(callId, MemoryStore.B1Hash(outcome.Json), links,
                original, repeat ? 0 : outcome.ExactB1Read?.UnindexedRows ?? 0);
            lock (_b1ReadIndexGate) _b1ReadIndexStaged[callId] = staged;
        }
        catch (Exception) { /* optional observation only */ }
    }

    internal void ObserveB1ReadIndexDispatch(string wire, string attemptId)
    {
        try
        {
            lock (_b1ReadIndexGate)
            {
                if (_b1ReadIndexStaged.Count == 0 || _b1ReadIndexDispatched.ContainsKey(attemptId)) return;
                using var doc = JsonDocument.Parse(wire);
                var root = doc.RootElement;
                bool chat = root.TryGetProperty("messages", out var messages);
                if (!chat && !root.TryGetProperty("input", out messages)) return;
                if (messages.ValueKind != JsonValueKind.Array) return;
                var present = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var m in messages.EnumerateArray())
                {
                    if (m.ValueKind != JsonValueKind.Object) continue;
                    string tag = chat ? "role" : "type", id = chat ? "tool_call_id" : "call_id";
                    string body = chat ? "content" : "output";
                    if (!m.TryGetProperty(tag, out var t) || t.GetString() != (chat ? "tool" : "function_call_output")
                        || !m.TryGetProperty(id, out var c) || !m.TryGetProperty(body, out var b)
                        || c.ValueKind != JsonValueKind.String || b.ValueKind != JsonValueKind.String) continue;
                    present[c.GetString()!] = MemoryStore.B1Hash(b.GetString()!);
                }
                var actual = _b1ReadIndexStaged.Values.Where(s => present.GetValueOrDefault(s.CallId) == s.JsonHash).ToArray();
                // Equality is source/version/range identity, not text equality
                // across authors. Overlapping but unequal slices remain distinct.
                var links = actual.SelectMany(s => s.Links.Select(l => l with { ToolCallIds = new[] { s.CallId } }))
                    .GroupBy(l => JsonSerializer.Serialize(l with { ToolCallIds = Array.Empty<string>() }))
                    .Select(g => g.First() with { ToolCallIds = g.SelectMany(l => l.ToolCallIds).Distinct().ToArray() }).ToArray();
                var repeats = actual.Where(s => s.OriginalCallId.Length > 0
                    && actual.Any(o => o.CallId == s.OriginalCallId && o.OriginalCallId.Length == 0))
                    .Select(s => new B1ReadRepeat(s.CallId, s.OriginalCallId)).ToArray();
                var work = actual.SelectMany(s => (s.WorkLinks ?? Array.Empty<B1WorkReadLink>())
                        .Select(l => l with { ToolCallIds = new[] { s.CallId } }))
                    .GroupBy(l => JsonSerializer.Serialize(l with { ToolCallIds = Array.Empty<string>() }))
                    .Select(g => g.First() with { ToolCallIds = g.SelectMany(l => l.ToolCallIds).Distinct().ToArray() }).ToArray();
                _b1ReadIndexDispatched[attemptId] = new(1, Id, Id + "/b1", attemptId, DateTimeOffset.UtcNow,
                    links, repeats, actual.Sum(s => s.UnindexedRows)) { WorkLinks = work.Length == 0 ? null : work };
            }
        }
        catch (Exception) { /* keep dispatch independent of optional recording */ }
    }

    internal B1ReadIndex? B1ReadIndexForAttempt(string attemptId)
    {
        lock (_b1ReadIndexGate) return _b1ReadIndexDispatched.GetValueOrDefault(attemptId);
    }
}
