using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Text;

namespace LilithBridge;

internal sealed record DialogueHistoryEntry(string EventId, string Speaker, string Text, DateTimeOffset? Time, string SessionId, long Sequence)
{
    // Read-only lineage; no merged record is written to the archive or request history.
    public IReadOnlyList<string> MemberEventIds { get; init; } = Array.Empty<string>();
}
internal sealed record DialogueHistorySnapshot(IReadOnlyList<DialogueHistoryEntry> Entries, string Notice, bool ReadFailed);
internal sealed record HistoryMessageRange(int Start, int Length, DialogueHistoryEntry Entry);
internal sealed record HistoryPageText(string Text, IReadOnlyList<HistoryMessageRange> Messages)
{
    public DialogueHistoryEntry? MessageAt(int offset) => Messages.FirstOrDefault(m => offset >= m.Start && offset < m.Start + m.Length)?.Entry;
}

internal sealed partial class MemoryStore
{
    // Read-only projection of the existing raw-utterance owner, never a new store.
    public DialogueHistorySnapshot CaptureDialogueHistory()
    {
        lock (_experienceLock)
        {
            // Explicit open/refresh only: disk deletion/replacement and repaired
            // reads must be visible on reopening. Paging/search use the snapshot.
            // Never reconcile/back-fill from _history while viewing an archive.
            _experienceCache = null;
            if (!_experienceArchiveEnabled)
                return new(Array.Empty<DialogueHistoryEntry>(), "经历归档未启用，无法提供长期聊天；不读取备份。", false);
            if (!Directory.Exists(ExperienceDirectory))
                return new(Array.Empty<DialogueHistoryEntry>(), "聊天归档目录不可用或不存在，不能据此判断没有历史。", true);
            try
            {
                var denied = SuppressedExperienceIds();
                var records = ReadExperiences(logErrors: false)
                    .OrderBy(t => _experienceRecordedTimes.TryGetValue(t.EventId, out var time) ? time ?? DateTimeOffset.MaxValue : DateTimeOffset.MaxValue)
                    .ThenBy(t => t.SessionId, StringComparer.Ordinal).ThenBy(t => t.Sequence).ToArray();
                bool Visible(MemoryTurn t) => _experienceExplicitDialogueMetadata.Contains(t.EventId) && t.Kind == "chat" && !string.IsNullOrWhiteSpace(t.Text)
                        && (t.Role == "user" && t.Origin == "user" && t.DeliveryState == "received"
                            || t.Role == "assistant" && t.Origin is "model" or LocalFallbackOrigin && t.DeliveryState == "delivered")
                        && !denied.Contains(t.EventId) && !denied.Contains(t.SourceEventId)
                        && !t.SourceEventIds.Any(denied.Contains) && !SuppressedTextMatches(t.Text);
                bool partial = _experienceMalformedRecords > 0 || _experienceReadFailures > 0;
                var entries = DialogueHistoryReplies.Build(records, Visible,
                    t => _experienceRecordedTimes.TryGetValue(t.EventId, out var time) ? time : null, !partial);
                string notice = "仅桥接聊天原文：我的已接收发言、莉莉丝已交付内容；可靠关联的同一回复气泡合为一条。旧记录缺标识时保留分条，不还原存储时已丢失的段落。缺失时间的记录列在末尾，不推测时序。";
                if (partial) notice += $" 读取不完整：{_experienceMalformedRecords}条坏记录，{_experienceReadFailures}个文件读取失败。";
                return new(entries, notice, partial);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or NullReferenceException)
            { return new(Array.Empty<DialogueHistoryEntry>(), "读取聊天记录失败（" + error.GetType().Name + "），不能据此判断没有历史。", true); }
        }
    }
}

// Pure delivered-reply grouping shared by the archive view and last-scene
// context. Neither consumer rewrites history, delivery receipts or the archive.
internal static class DialogueHistoryReplies
{
    public static IReadOnlyList<DialogueHistoryEntry> Build(IReadOnlyList<MemoryTurn> records,
        Func<MemoryTurn, bool> visible, Func<MemoryTurn, DateTimeOffset?> time, bool groupReplies)
    {
        var entries = new List<DialogueHistoryEntry>();
        for (int index = 0; index < records.Count;)
        {
            MemoryTurn first = records[index];
            if (!visible(first)) { index++; continue; }
            var members = new List<MemoryTurn> { first };
            int next = index + 1;
            while (groupReplies && next < records.Count && visible(records[next])
                && CanJoin(records[next - 1], records[next], time))
            { members.Add(records[next]); next++; }
            entries.Add(new DialogueHistoryEntry(first.EventId, first.Role == "user" ? "我"
                : first.Origin == MemoryStore.LocalFallbackOrigin ? "莉莉丝（本地提示）" : "莉莉丝",
                string.Concat(members.Select(t => t.Text)), time(first), first.SessionId, first.Sequence)
                { MemberEventIds = members.Select(t => t.EventId).ToArray() });
            index = next;
        }
        return entries;
    }

    private static bool CanJoin(MemoryTurn previous, MemoryTurn next, Func<MemoryTurn, DateTimeOffset?> time)
    {
        return Identity(previous, out string request, out int chunk)
            && Identity(next, out string nextRequest, out int nextChunk)
            && request == nextRequest && chunk < int.MaxValue && nextChunk == chunk + 1
            && !string.IsNullOrWhiteSpace(previous.SessionId) && previous.SessionId == next.SessionId
            && previous.Sequence > 0 && previous.Sequence < long.MaxValue && next.Sequence == previous.Sequence + 1
            && previous.Origin == next.Origin && previous.SourceEventId == next.SourceEventId
            && time(previous).HasValue && time(next).HasValue && time(next) >= time(previous);
    }

    private static bool Identity(MemoryTurn turn, out string request, out int chunk)
    {
        request = ""; chunk = -1;
        if (turn.Role != "assistant" || turn.Kind != "chat" || turn.DeliveryState != "delivered"
            || (turn.Origin != "model" && turn.Origin != MemoryStore.LocalFallbackOrigin)) return false;
        int separator = turn.EventId.LastIndexOf(':');
        if (separator != 32 || !Guid.TryParseExact(turn.EventId[..separator], "N", out _)) return false;
        string suffix = turn.EventId[(separator + 1)..];
        if (!int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out chunk)
            || chunk < 0 || suffix != chunk.ToString(CultureInfo.InvariantCulture)) return false;
        request = turn.EventId[..separator]; return true;
    }
}

internal sealed class DialogueHistoryView
{
    public const int PageSize = 8;
    public const int SegmentCharacters = 1000;
    private readonly DialogueHistorySnapshot _snapshot;
    private readonly List<(DialogueHistoryEntry Entry, string Text, int Part, int Parts)> _segments = new();
    public int Page { get; private set; }
    public int Pages => Math.Max(1, (_segments.Count + PageSize - 1) / PageSize);
    public int MatchedRecords { get; private set; }
    public int AvailableRecords => _snapshot.Entries.Count;
    public string Notice => _snapshot.Notice;
    public string Query { get; private set; } = "";
    public bool ReadFailed => _snapshot.ReadFailed;
    internal IReadOnlyList<string> CurrentTexts => _segments.Skip(Page * PageSize).Take(PageSize).Select(s => s.Text).ToArray();
    public DialogueHistoryView(DialogueHistorySnapshot snapshot) { _snapshot = snapshot; Search(""); }
    public void Search(string? query)
    {
        Query = (query ?? "").Trim(); _segments.Clear(); MatchedRecords = 0;
        foreach (var entry in _snapshot.Entries.Where(e => Query.Length == 0 || e.Text.Contains(Query, StringComparison.OrdinalIgnoreCase)))
        {
            MatchedRecords++;
            var chunks = new List<string>();
            for (int pos = 0; pos < entry.Text.Length;)
            {
                int count = Math.Min(SegmentCharacters, entry.Text.Length - pos);
                if (pos + count < entry.Text.Length && char.IsHighSurrogate(entry.Text[pos + count - 1])) count--;
                chunks.Add(entry.Text.Substring(pos, count)); pos += count;
            }
            for (int i = 0; i < chunks.Count; i++) _segments.Add((entry, chunks[i], i + 1, chunks.Count));
        }
        Page = Pages - 1;
    }
    public void Older() => Page = Math.Max(0, Page - 1);
    public void Newer() => Page = Math.Min(Pages - 1, Page + 1);
    public string PageText() => BuildPage().Text;
    public HistoryPageText BuildPage()
    {
        if (_segments.Count == 0) return new(ReadFailed ? "未能完整读取记录。" : Query.Length > 0 ? "没有匹配记录。" : "暂无记录。", Array.Empty<HistoryMessageRange>());
        var text = new StringBuilder();
        var ranges = new List<HistoryMessageRange>();
        foreach (var item in _segments.Skip(Page * PageSize).Take(PageSize))
        {
            text.Append(item.Entry.Time?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "时间未记录");
            text.Append("  ").Append(item.Entry.Speaker);
            if (item.Parts > 1) text.Append($"（片段{item.Part}/{item.Parts}）");
            text.AppendLine();
            ranges.Add(new(text.Length, item.Text.Length, item.Entry));
            text.AppendLine(item.Text).AppendLine();
        }
        return new(text.ToString(), ranges);
    }
    public string Status => $"{MatchedRecords} 条 · {Page + 1}/{Pages} 页";
}
