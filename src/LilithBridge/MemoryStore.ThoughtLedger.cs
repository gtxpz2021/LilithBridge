using System;
using System.Collections.Generic;
using System.Linq;

namespace LilithBridge;

/// <summary>
/// In-memory, session-scoped ledger of provider reasoning that belonged to a
/// completed user turn whose reply was fully delivered. It is created with the
/// MemoryStore instance, is never persisted, and a restart therefore starts
/// empty. Only full deliveries qualify; tool preludes, review notes, sub-model
/// reasoning and cancelled/partial replies are never entries here.
/// </summary>
internal sealed class SessionThoughtLedger
{
    internal const int MaxEntries = 32;

    internal sealed record Entry(string RequestId, string AttemptId, string UserEventId,
        string Reasoning, string DeliveryState, int DeliveredChars, DateTimeOffset RecordedAt);

    private readonly List<Entry> _entries = new();

    internal int Count => _entries.Count;

    internal void Record(ApiResult reply)
    {
        if (reply.DialogueRequest == null || reply.Error.Length > 0) return;
        if (reply.Delivery is not { IsFullyDelivered: true }) return;
        if (string.IsNullOrWhiteSpace(reply.ReasoningContent)) return;
        if (string.IsNullOrEmpty(reply.SourceUserEventId)) return;
        _entries.RemoveAll(e => e.RequestId == reply.RequestId && e.AttemptId == reply.Response.AttemptId);
        _entries.Add(new Entry(reply.RequestId, reply.Response.AttemptId, reply.SourceUserEventId,
            reply.ReasoningContent, "completed_full", reply.DeliveredText.Length, DateTimeOffset.UtcNow));
        while (_entries.Count > MaxEntries) _entries.RemoveAt(0);
    }

    /// <summary>
    /// All completed-full entries. Source eligibility, mention rules, scope and
    /// relevance ordering all belong to MemoryStore; taking a top-N here before
    /// those checks would drop eligible older entries.
    /// </summary>
    internal IReadOnlyList<Entry> CompletedEntries() =>
        _entries.Where(e => e.DeliveryState == "completed_full").ToList();
}

internal sealed partial class MemoryStore
{
    internal SessionThoughtLedger ThoughtLedger { get; } = new();
}
