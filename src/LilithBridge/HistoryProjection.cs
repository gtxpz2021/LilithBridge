using System;
using System.Collections.Generic;
using System.Linq;

namespace LilithBridge;

/// <summary>
/// One entry of the request history projection: either a single stored record
/// or the bubbles of one model reply joined for this request only.
/// </summary>
internal sealed class ProjectedHistoryTurn
{
    public string Role { get; init; } = "";
    public string Text { get; init; } = "";
    /// <summary>The stored record when this entry is not a joined reply.</summary>
    public MemoryTurn? Record { get; init; }
    /// <summary>Stored records this entry represents, newest last.</summary>
    public IReadOnlyList<MemoryTurn> Records { get; init; } = Array.Empty<MemoryTurn>();
    /// <summary>Index of the last stored record this entry represents.</summary>
    public int LastRecordIndex { get; init; } = -1;
    public bool IsGrouped => Records.Count > 1;
    // A proven displayed run is not proof of the full generation's delivery or
    // of a known user source (e.g. a proactive run may have no SourceEventId).
    public bool IdentityGuaranteed { get; init; }
    public bool TruncatedHeadOrUnknown { get; init; } = true;
    public int ActualFragmentCount => Records.Count;
    public int? FirstChunkIndex { get; init; }
    public int? LastChunkIndex { get; init; }
    public string Coverage { get; init; } = "independent_record_identity_unknown";
}

internal sealed class HistoryProjectionResult
{
    public IReadOnlyList<ProjectedHistoryTurn> Turns { get; init; } = Array.Empty<ProjectedHistoryTurn>();
    /// <summary>Stored records the projection was allowed to read.</summary>
    public int AvailableRecords { get; init; }
    /// <summary>Stored records represented by the selected turns.</summary>
    public int SelectedRecords { get; init; }
    public int GroupedReplies { get; init; }
    public bool GroupReplies { get; init; }
    public bool WindowInMessages { get; init; }
    public int OmittedRecords { get; init; }
    public string OmissionReason { get; init; } = "";
    public int OversizeReplyRuns { get; init; }
    public bool RetainedSourceRootOverflow { get; init; }
    public string PriorWindowCoverage { get; init; } = "unknown";
    public HistoryWindowProvenance? PriorWindow { get; init; }
    public string Mode => !GroupReplies ? "legacy" : WindowInMessages ? "grouped_window" : "grouped";
}

/// <summary>
/// Per-request record of what the history projection did, in the units the
/// experiment must report separately: records read, records represented,
/// projected messages and characters.  Characters are not provider tokens.
/// </summary>
internal sealed class HistoryProjectionSummary
{
    public string Mode { get; init; } = "legacy";
    public int AvailableRecords { get; init; }
    public int SelectedRecords { get; init; }
    public int GroupedReplies { get; init; }
    public int ProjectedMessages { get; init; }
    /// <summary>Selected entries, including the current user if supplied in history;
    /// distinct from wire messages when several entries travel in a data envelope.</summary>
    public int ProjectedEntries { get; init; }
    public int TextChars { get; init; }
}

/// <summary>
/// Request-only projection of the stored chat history.
///
/// Every displayed bubble is stored as its own MemoryTurn, so one long reply
/// can occupy most of ConversationMaxMessages. This projection may join
/// consecutive bubbles of one reply in the request messages, and — only when
/// the experiment asks for it — spend that same budget on more real records.
///
/// It is deliberately a read-only view:
///  - stored records, ids, sessions, the experience archive and native lines
///    are untouched; nothing is written back as a merged "original" record;
///  - only bubbles that provably belong to one reply are joined: consecutive
///    stored records, same request id, chunk index +1, sequence +1, same source/session, and a
///    delivered chat record with the same model or local-fallback origin;
///  - unknown provenance (other requests, native lines, other speakers, gaps,
///    out-of-order or legacy records) keeps each record separate;
///  - the window may only use records the store still holds; compression may
///    already have replaced older records with a summary.
/// </summary>
internal static class HistoryProjection
{
    public static HistoryProjectionResult Build(
        IReadOnlyList<MemoryTurn>? available, int maxMessages, bool groupReplies, bool windowInMessages)
        => BuildCore(available, maxMessages, groupReplies, windowInMessages, preserveStoredText: false);

    // The request carrier uses already stored characters verbatim. Keep the
    // existing projection entry point for other consumers such as compression.
    internal static HistoryProjectionResult BuildForRequest(
        IReadOnlyList<MemoryTurn>? available, int maxMessages, bool groupReplies, bool windowInMessages)
        => BuildCore(available, maxMessages, groupReplies, windowInMessages, preserveStoredText: true);

    private static HistoryProjectionResult BuildCore(
        IReadOnlyList<MemoryTurn>? available,
        int maxMessages,
        bool groupReplies,
        bool windowInMessages,
        bool preserveStoredText)
    {
        IReadOnlyList<MemoryTurn> records = available ?? Array.Empty<MemoryTurn>();
        int budget = Math.Max(1, maxMessages);
        bool expandWindow = groupReplies && windowInMessages;
        int boundedStart = expandWindow ? HistoryBoundary.WindowStart(records, budget) : 0;
        int start = expandWindow
            ? HistoryBoundary.WindowStart(records, budget, keepAdjacentUserRoot: true)
            : Math.Max(0, records.Count - budget);
        List<ProjectedHistoryTurn> turns = BuildTurns(records, start, groupReplies, preserveStoredText);
        int selected = 0;
        foreach (ProjectedHistoryTurn turn in turns)
            selected += Math.Max(1, turn.Records.Count);
        return new HistoryProjectionResult
        {
            Turns = turns,
            AvailableRecords = records.Count,
            SelectedRecords = selected,
            GroupedReplies = turns.Count(turn => turn.IsGrouped),
            GroupReplies = groupReplies,
            WindowInMessages = windowInMessages,
            OmittedRecords = records.Count - selected,
            OmissionReason = start == 0 ? "" : expandWindow ? "message_window" : "legacy_record_window",
            OversizeReplyRuns = turns.Count(t => t.IdentityGuaranteed
                && (t.ActualFragmentCount > HistoryBoundary.OversizeRunRecords || t.Text.Length > HistoryBoundary.OversizeRunChars)),
            RetainedSourceRootOverflow = expandWindow && start < boundedStart,
            PriorWindowCoverage = records is HistoryWindowRecords ? "recorded_native_aware_selection" : "unknown",
            PriorWindow = (records as HistoryWindowRecords)?.Provenance
        };
    }

    private static List<ProjectedHistoryTurn> BuildTurns(
        IReadOnlyList<MemoryTurn> records,
        int start,
        bool groupReplies,
        bool preserveStoredText)
    {
        var turns = new List<ProjectedHistoryTurn>();
        int index = Math.Max(0, start);
        while (index < records.Count)
        {
            MemoryTurn first = records[index];
            if (groupReplies && HistoryBoundary.TryReplyBubble(first, out _, out int chunk))
            {
                var members = new List<MemoryTurn> { first };
                int next = index + 1;
                while (next < records.Count
                    && HistoryBoundary.SameReplyRun(records[next - 1], records[next]))
                {
                    members.Add(records[next]);
                    next++;
                }
                if (members.Count > 1)
                {
                    turns.Add(new ProjectedHistoryTurn
                    {
                        Role = "assistant",
                        Text = string.Concat(members.Select(member => preserveStoredText ? member.Text : MemoryStore.Clean(member.Text))),
                        Records = members,
                        LastRecordIndex = next - 1,
                        IdentityGuaranteed = true,
                        TruncatedHeadOrUnknown = chunk != 0,
                        FirstChunkIndex = chunk,
                        LastChunkIndex = chunk + members.Count - 1,
                        Coverage = chunk == 0 ? "qualified_delivered_prefix_tail_unknown"
                            : "qualified_delivered_run_head_missing_tail_unknown"
                    });
                    index = next;
                    continue;
                }
            }
            bool identified = HistoryBoundary.TryReplyBubble(first, out _, out int firstChunk);
            turns.Add(new ProjectedHistoryTurn
            {
                Role = first.Role,
                Text = preserveStoredText ? first.Text ?? "" : MemoryStore.Clean(first.Text),
                Record = first,
                Records = new[] { first },
                LastRecordIndex = index,
                IdentityGuaranteed = identified,
                TruncatedHeadOrUnknown = !identified || firstChunk != 0,
                FirstChunkIndex = identified ? firstChunk : null,
                LastChunkIndex = identified ? firstChunk : null,
                Coverage = !identified ? "independent_record_identity_unknown"
                    : firstChunk == 0 ? "qualified_delivered_prefix_tail_unknown"
                    : "qualified_delivered_run_head_missing_tail_unknown"
            });
            index++;
        }
        return turns;
    }

}
