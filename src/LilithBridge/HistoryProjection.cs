using System;
using System.Collections.Generic;
using System.Globalization;
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
///    stored records, same request id, chunk index +1, same session, and a
///    delivered chat record with the same model or local-fallback origin;
///  - unknown provenance (other requests, native lines, other speakers, gaps,
///    out-of-order or legacy records) keeps each record separate;
///  - the window may only use records the store still holds; compression may
///    already have replaced older records with a summary.
/// </summary>
internal static class HistoryProjection
{
    public static HistoryProjectionResult Build(
        IReadOnlyList<MemoryTurn>? available,
        int maxMessages,
        bool groupReplies,
        bool windowInMessages)
    {
        IReadOnlyList<MemoryTurn> records = available ?? Array.Empty<MemoryTurn>();
        int budget = Math.Max(1, maxMessages);
        bool expandWindow = groupReplies && windowInMessages;
        int start = expandWindow
            ? WindowStartByMessages(records, budget)
            : Math.Max(0, records.Count - budget);
        List<ProjectedHistoryTurn> turns = BuildTurns(records, start, groupReplies);
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
            WindowInMessages = windowInMessages
        };
    }

    private static List<ProjectedHistoryTurn> BuildTurns(
        IReadOnlyList<MemoryTurn> records,
        int start,
        bool groupReplies)
    {
        var turns = new List<ProjectedHistoryTurn>();
        int index = Math.Max(0, start);
        while (index < records.Count)
        {
            MemoryTurn first = records[index];
            if (groupReplies && TryReplyBubble(first, out string key, out int chunk))
            {
                var members = new List<MemoryTurn> { first };
                int next = index + 1;
                int expected = chunk + 1;
                while (next < records.Count
                    && TryReplyBubble(records[next], out string nextKey, out int nextChunk)
                    && nextKey == key
                    && nextChunk == expected
                    && records[next].Origin == first.Origin
                    && records[next].SessionId == first.SessionId)
                {
                    members.Add(records[next]);
                    expected = nextChunk + 1;
                    next++;
                }
                if (members.Count > 1)
                {
                    turns.Add(new ProjectedHistoryTurn
                    {
                        Role = "assistant",
                        Text = string.Join("\n", members.Select(member => MemoryStore.Clean(member.Text))),
                        Records = members,
                        LastRecordIndex = next - 1
                    });
                    index = next;
                    continue;
                }
            }
            turns.Add(new ProjectedHistoryTurn
            {
                Role = first.Role,
                Text = MemoryStore.Clean(first.Text),
                Record = first,
                Records = new[] { first },
                LastRecordIndex = index
            });
            index++;
        }
        return turns;
    }

    /// <summary>
    /// Chooses the oldest record that still fits the message budget, counting
    /// one message per reply instead of one per displayed bubble. Whole replies
    /// stay together, so the oldest included reply is never split.
    /// </summary>
    private static int WindowStartByMessages(IReadOnlyList<MemoryTurn> records, int budget)
    {
        int start = records.Count;
        int index = records.Count - 1;
        int messages = 0;
        while (index >= 0 && messages < budget)
        {
            int groupStart = index;
            if (TryReplyBubble(records[index], out string key, out int chunk))
            {
                while (groupStart > 0
                    && TryReplyBubble(records[groupStart - 1], out string previousKey, out int previousChunk)
                    && previousKey == key
                    && previousChunk == chunk - 1
                    && records[groupStart - 1].Origin == records[index].Origin
                    && records[groupStart - 1].SessionId == records[index].SessionId)
                {
                    groupStart--;
                    chunk = previousChunk;
                }
            }
            messages++;
            start = groupStart;
            index = groupStart - 1;
        }
        return Math.Max(0, start);
    }

    /// <summary>
    /// True when the record is a displayed bubble of a model or local reply whose store
    /// id still carries the request id and chunk index written when it was
    /// shown. Everything else keeps its own message.
    /// </summary>
    private static bool TryReplyBubble(MemoryTurn turn, out string requestId, out int chunkIndex)
    {
        requestId = "";
        chunkIndex = -1;
        if (turn.Role != "assistant"
            || turn.Kind != "chat"
            || (turn.Origin != "model" && turn.Origin != MemoryStore.LocalFallbackOrigin)
            || turn.DeliveryState != "delivered")
        {
            return false;
        }
        string id = turn.EventId;
        int separator = id.LastIndexOf(':');
        // Bubble ids are written as "{requestId}:{chunkIndex}" with a Guid "N"
        // request id; anything else is unknown provenance and stays separate.
        if (separator != 32 || id.Length <= separator + 1)
            return false;
        for (int i = 0; i < separator; i++)
        {
            if (!Uri.IsHexDigit(id[i]))
                return false;
        }
        if (!int.TryParse(id.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture,
                out chunkIndex))
        {
            return false;
        }
        requestId = id[..separator];
        return true;
    }
}
