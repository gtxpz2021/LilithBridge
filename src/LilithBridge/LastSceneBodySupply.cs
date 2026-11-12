using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace LilithBridge;

// Formatting over already revalidated material only. Unknown/legacy snapshots
// remain snapshots; text equality alone never upgrades their source identity.
internal static class LastSceneBodySupply
{
    internal static string Render(LastSceneSummary scene, string resolvedReply,
        IReadOnlyList<MemoryTurn> supplied, IReadOnlyList<MemoryTurn> selectedHistory, string userAttribution = "",
        bool sourceEnvelope = false, bool groupReplies = false, Action<RequestSourceBody>? onBodyReference = null)
    {
        var builder = new StringBuilder();
        var hostBodies = new List<RequestSourceBody>();
        if (sourceEnvelope && onBodyReference != null)
        {
            var history = HistoryProjection.Build(selectedHistory, Math.Max(1, selectedHistory.Count), groupReplies, false);
            foreach (var entry in history.Turns)
            {
                // A locator must address the actual carrier text. Normalization,
                // separators or unknown provenance are reasons to retain inline.
                if (entry.Text != string.Concat(entry.Records.Select(record => record.Text))) continue;
                int start = 0;
                foreach (var record in entry.Records)
                {
                    hostBodies.Add(new("utterance", record.EventId, record.Text,
                        "history[entry_with_sources.event_id=" + record.EventId + "].text",
                        record.Role, record.Origin, record.DeliveryState, start, record.Text.Length));
                    start += record.Text.Length;
                }
            }
        }
        var sourceIds = scene.SourceEventIds.ToHashSet(StringComparer.Ordinal);
        bool Eligible(MemoryTurn t) => new RequestSourceBody("utterance", t.EventId, t.Text,
            "selected_request_history_source:" + t.EventId, t.Role, t.Origin, t.DeliveryState).Qualified;
        var userMatches = scene.SchemaVersion >= 3 ? supplied.Where(t => sourceIds.Contains(t.EventId)
            && t.Role == "user" && t.Text == scene.UserText && Eligible(t)).ToArray() : Array.Empty<MemoryTurn>();
        MemoryTurn? user = userMatches.Length == 1 ? userMatches[0] : null;
        MemoryTurn? replyEnd = null;
        IReadOnlyList<MemoryTurn> replyRecords = Array.Empty<MemoryTurn>();
        if (scene.SchemaVersion >= 3)
        {
            var projected = HistoryProjection.Build(supplied, Math.Max(1, supplied.Count), true, true);
            var matches = projected.Turns.Where(t => t.Role == "assistant" && t.Records.Count > 0
                && sourceIds.Contains(t.Records[^1].EventId)
                && string.Concat(t.Records.Select(record => record.Text)) == resolvedReply
                // v3 snapshots exist in both forms: a final display fragment,
                // or the complete delivered run. Neither form grants one ID the
                // body of another fragment; verified output remains per record.
                && (scene.AssistantText == t.Records[^1].Text || scene.AssistantText == resolvedReply)
                && t.Records.All(record => record.Role == "assistant" && Eligible(record))
                && (t.Records.Count == 1 || t.Records[^1].SessionId.Length > 0 && t.Records[^1].SourceEventId.Length > 0
                    && t.Records.All(record => record.SessionId == t.Records[^1].SessionId
                        && record.SourceEventId == t.Records[^1].SourceEventId && record.Origin == t.Records[^1].Origin)
                    && t.Records.Zip(t.Records.Skip(1), (a, b) => a.Sequence > 0 && b.Sequence == a.Sequence + 1).All(adjacent => adjacent)))
                .ToArray();
            if (matches.Length == 1)
            {
                replyRecords = matches[0].Records;
                replyEnd = replyRecords[^1];
            }
        }
        string Identity(MemoryTurn t) => "utterance:" + t.EventId + ";sha256=" + RequestSourceBody.Hash(t.Text);
        string Body(MemoryTurn t)
        {
            // Same identity AND exact version, never same wording from another turn.
            var matches = hostBodies.Where(h => h.Qualified && h.EventId == t.EventId && h.Text == t.Text
                && h.Role == t.Role && h.Origin == t.Origin && h.DeliveryState == t.DeliveryState).Distinct().ToArray();
            if (matches.Length == 1)
            {
                onBodyReference!(matches[0]);
                return "[正文引用 " + Identity(t) + ";body=" + matches[0].Location
                    + ";start_char=" + matches[0].TextStart + ";char_count=" + t.Text.Length + "]";
            }
            return "[来源 " + Identity(t) + "] " + t.Text;
        }
        builder.AppendLine("[上一幕的历史线索]");
        builder.AppendLine("- 原记录时间=" + scene.Timestamp.ToString("O") + "; 来源="
            + string.Join(",", scene.SourceEventIds.Concat(supplied.Select(t => t.EventId)).Distinct(StringComparer.Ordinal)));
        if (scene.SchemaVersion < 3)
            builder.AppendLine("- 旧格式摘要；来源与时间仅为当时快照所载，未单独核实话题原始事件，不视为未完成事项或逐字原话。");
        if (scene.TopicText.Length > 0)
            builder.AppendLine(user != null && scene.TopicText == SnapshotPrefix(user.Text, 80)
                ? "- 当时的话题线索：同一玩家发言的前缀摘录；来源=" + Identity(user) + ";正文见该发言。"
                : "- 当时的话题线索：" + scene.TopicText);
        if (scene.UserText.Length > 0)
            builder.AppendLine(user == null
                ? "- 当时快照记录的玩家发言（来源未能重新核实）：" + userAttribution + scene.UserText
                : "- 当时玩家提交的原文：" + userAttribution + Body(user));
        if (scene.AssistantText.Length > 0)
        {
            if (replyRecords.Count > 0)
            {
                builder.AppendLine("- 此后实际交付的莉莉丝回复（已核片段，不代表整次生成）：");
                foreach (var fragment in replyRecords)
                    builder.AppendLine("  - " + fragment.Timestamp.ToString("O") + " " + Body(fragment));
            }
            else builder.AppendLine("- 当时快照记录的莉莉丝回复片段（来源未能重新核实）（"
                + scene.AssistantTimestamp?.ToString("O") + "）：" + resolvedReply);
        }
        if (scene.NotableEvent.Length > 0)
        {
            var notableSources = new List<MemoryTurn>();
            if (replyEnd != null && replyRecords.Count > 0 && new[] { "莉莉丝（AI）说：", "莉莉丝（AI）注意到：", "莉莉丝（原生）说：" }
                .Any(prefix => scene.NotableEvent == SnapshotPrefix(prefix + scene.AssistantText, 60)))
                notableSources.AddRange(scene.AssistantText == resolvedReply ? replyRecords : new[] { replyEnd });
            if (user != null && replyEnd == null && new[] { "玩家说：", "玩家选择了：" }
                .Any(prefix => scene.NotableEvent == SnapshotPrefix(prefix + user.Text, 60))) notableSources.Add(user);
            builder.AppendLine(notableSources.Count > 0
                ? "- 当时的事件摘录：同一发言的事件标签及前缀；逐片来源=" + string.Join(",", notableSources.Select(Identity)) + ";正文见各片段。"
                : "- 当时的事件摘录：" + scene.NotableEvent);
        }
        if (scene.BoundaryHint.Length > 0) builder.AppendLine("- 当时的边界线索：" + scene.BoundaryHint);
        builder.AppendLine("这些记录不表示话题尚未结束，也不要求继续；按原话、已给的回复和当前情况判断是否值得提起，可以保持安静。");
        return builder.ToString();
    }

    private static string SnapshotPrefix(string text, int max)
    {
        text = text.Trim();
        return text.Length > max ? text[..max] + "…" : text;
    }
}
