using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace LilithBridge;

// Request-bound read provenance, never a synthetic history event or a global
// "last selection". The counts start at the permitted recent-store read, not
// at the full archive or the earlier on-disk compaction history.
internal sealed record HistoryWindowProvenance(
    int RecentReadRecords, int OrdinaryEligibleRecords, int OrdinaryBudget,
    int ReservedRecallRecords, int OrdinarySelectedRecords,
    int InitiallyReturnedRecords, int CurrentRecords, string WindowReason,
    bool SourceRootOverflow, string Reasons,
    int RevalidationRemovedRecords = 0, int FinalSelectionRemovedRecords = 0)
{
    public string Stage => "native_aware_recent_selection";
    public string Scope => "permitted_recent_store_only;not_archive_or_prior_compaction";
}

internal sealed class HistoryWindowRecords : IReadOnlyList<MemoryTurn>
{
    private readonly MemoryTurn[] _records;
    public HistoryWindowProvenance Provenance { get; }
    public HistoryWindowRecords(IEnumerable<MemoryTurn> records, HistoryWindowProvenance provenance)
    { _records = records.ToArray(); Provenance = provenance; }
    public int Count => _records.Length;
    public MemoryTurn this[int index] => _records[index];
    public IEnumerator<MemoryTurn> GetEnumerator() => ((IEnumerable<MemoryTurn>)_records).GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    internal HistoryWindowRecords Revalidated(IReadOnlyList<MemoryTurn> records)
        => new(records, Provenance with {
            CurrentRecords = records.Count,
            RevalidationRemovedRecords = Provenance.RevalidationRemovedRecords + Count - records.Count,
            Reasons = Count == records.Count || Provenance.Reasons.Contains("revalidation_filter", StringComparison.Ordinal)
                ? Provenance.Reasons : Provenance.Reasons + ";revalidation_filter"
        });
    internal HistoryWindowRecords Selected(IReadOnlyList<MemoryTurn> records)
        => new(records, Provenance with {
            CurrentRecords = records.Count,
            FinalSelectionRemovedRecords = Provenance.FinalSelectionRemovedRecords + Count - records.Count
        });
}

// One request/display identity, not a textual-similarity or timing heuristic.
// Storage retention and request projection must use the same message boundary.
internal static class HistoryBoundary
{
    // Advisory only: larger runs are retained whole, never silently clipped.
    internal const int OversizeRunRecords = 160;
    internal const int OversizeRunChars = 24000;

    internal static bool DisplayedOrLegacyRecord(MemoryTurn turn) => turn.Role switch
    {
        "assistant" => turn.DeliveryState is "delivered" or "legacy_unknown",
        "user" => turn.DeliveryState is "received" or "legacy_unknown",
        _ => true
    };

    internal static bool TryReplyBubble(MemoryTurn turn, out string requestId, out int chunkIndex)
    {
        requestId = "";
        chunkIndex = -1;
        if (turn.SchemaVersion != 1 || turn.Role != "assistant" || turn.Kind != "chat"
            || (turn.Origin != "model" && turn.Origin != MemoryStore.LocalFallbackOrigin)
            || turn.DeliveryState != "delivered" || turn.SessionId.Length == 0 || turn.Sequence <= 0)
            return false;
        string id = turn.EventId;
        int separator = id.LastIndexOf(':');
        if (separator != 32 || id.Length <= separator + 1) return false;
        for (int i = 0; i < separator; i++) if (!Uri.IsHexDigit(id[i])) return false;
        if (!int.TryParse(id.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out chunkIndex))
            return false;
        requestId = id[..separator];
        return true;
    }

    internal static bool SameReplyRun(MemoryTurn before, MemoryTurn after)
        => TryReplyBubble(before, out string left, out int leftChunk)
            && TryReplyBubble(after, out string right, out int rightChunk)
            && left == right && leftChunk < int.MaxValue && rightChunk == leftChunk + 1
            && before.SessionId == after.SessionId && before.Origin == after.Origin
            && before.SourceEventId == after.SourceEventId
            && before.Sequence < long.MaxValue && after.Sequence == before.Sequence + 1;

    internal static int CountMessages(IReadOnlyList<MemoryTurn> records)
    {
        int count = 0;
        for (int i = 0; i < records.Count; i++)
            if (i == 0 || !SameReplyRun(records[i - 1], records[i])) count++;
        return count;
    }

    internal static int OversizeReplyRunCount(IReadOnlyList<MemoryTurn> records)
    {
        int result = 0, members = 0; long chars = 0; bool identified = false;
        for (int i = 0; i <= records.Count; i++)
        {
            if (i == records.Count || i == 0 || !SameReplyRun(records[i - 1], records[i]))
            {
                if (identified && (members > OversizeRunRecords || chars > OversizeRunChars)) result++;
                if (i == records.Count) break;
                identified = TryReplyBubble(records[i], out _, out _); members = 0; chars = 0;
            }
            members++; chars += records[i].Text.Length;
        }
        return result;
    }

    // maxMessages counts independent records and proven, delivered reply runs.
    // It never invents the missing head or the unshown tail of a reply.
    internal static int WindowStart(IReadOnlyList<MemoryTurn> records, int maxMessages,
        bool keepAdjacentUserRoot = false)
    {
        if (maxMessages <= 0) return records.Count;
        int start = records.Count, remaining = maxMessages;
        for (int index = records.Count - 1; index >= 0 && remaining > 0; remaining--)
        {
            int first = index;
            while (first > 0 && SameReplyRun(records[first - 1], records[first])) first--;
            start = first;
            index = first - 1;
        }
        // At most one extra slot. An exact, immediately preceding user source is
        // a real owner, unlike a guessed neighbour with similar wording.
        if (keepAdjacentUserRoot && start > 0 && start < records.Count
            && TryReplyBubble(records[start], out _, out int chunk) && chunk == 0)
        {
            var root = records[start - 1]; var first = records[start];
            if (root.SchemaVersion == 1 && root.Role == "user" && root.Origin == "user"
                && root.Kind == "chat" && root.DeliveryState == "received"
                && root.EventId.Length > 0 && root.EventId == first.SourceEventId
                && root.SessionId == first.SessionId && root.Sequence > 0
                && root.Sequence < long.MaxValue && first.Sequence == root.Sequence + 1)
                start--;
        }
        return start;
    }
}
