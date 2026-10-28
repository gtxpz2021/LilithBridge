using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace LilithBridge;

// Body-free operation history. The original received command remains an
// experience; managing an interpretation never deletes that exchange.
internal sealed record B1ManagementReceipt(string OperationId, string Kind,
    List<string> TargetVersions, string Outcome, DateTimeOffset At);

internal sealed partial class MemoryStore
{
    private sealed record B1ManagementCommand(string Kind, string Target, bool ExplicitDomain);
    private enum B1ManagementStoragePresence { Absent, Present, Uncertain }
    private bool _b1ManagementObservedIdentity;
    private static string B1ManagementState(B1Document doc, string version) =>
        doc.DeletedVersions.Contains(version) ? "deleted" : doc.Management.GetValueOrDefault(version, "");

    // Candidate discovery must not create a B database merely to learn that an
    // old-domain command has no B competitor. Absence is a filesystem result,
    // never a reinterpretation of a failed B read or a lost cached identity.
    private B1ManagementStoragePresence InspectB1ManagementStorage()
    {
        bool known = _b1ManagementObservedIdentity || B1StorageStatus != "unopened"
            || _b1 != null || _b1LegacyDocument != null || _b1IndexedEntries != null
            || _b1DatabaseStamp.Length > 0 || _b1LegacyStamp.Length > 0
            || _b1LegacyHash != "absent" || _b1DatabaseRevision != 0
            || B1StorageDatabaseLoads != 0 || B1StorageLegacyLoads != 0 || B1StorageTransactions != 0;
        try
        {
            DataPaths.EnsureNoReparseAncestors(_root);
            if ((File.GetAttributes(_root) & FileAttributes.Directory) == 0)
                throw new IOException("B management root is not a directory.");
            bool main = false, artifact = false, unknown = false;
            foreach (string path in Directory.EnumerateFileSystemEntries(_root))
            {
                string name = Path.GetFileName(path);
                if (!name.StartsWith("v4-b1-continuity-", StringComparison.OrdinalIgnoreCase)) continue;
                artifact = true;
                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                { unknown = true; continue; }
                if (name.Equals(B1File, StringComparison.OrdinalIgnoreCase)
                    || name.Equals(B1DatabaseFile, StringComparison.OrdinalIgnoreCase)) main = true;
                else if (!name.Equals(B1File + ".lock", StringComparison.OrdinalIgnoreCase)
                    && !name.StartsWith(B1File + ".tmp-", StringComparison.OrdinalIgnoreCase)
                    && !name.StartsWith(B1DatabaseFile + ".migrate-", StringComparison.OrdinalIgnoreCase)
                    && !new[] { "-journal", "-wal", "-shm" }.Any(suffix =>
                        name.Equals(B1DatabaseFile + suffix, StringComparison.OrdinalIgnoreCase))) unknown = true;
            }
            _b1ManagementObservedIdentity |= known || artifact;
            if (unknown || !main && (known || artifact)) return B1ManagementStoragePresence.Uncertain;
            return main ? B1ManagementStoragePresence.Present : B1ManagementStoragePresence.Absent;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
            _b1ManagementObservedIdentity = true;
            return B1ManagementStoragePresence.Uncertain;
        }
    }

    private static B1ManagementCommand? ParseB1ManagementCommand(string text)
    {
        string command = NormalizeUserMemoryCommand(text);
        if (command.EndsWith('?') || command.EndsWith('？')
            || Regex.IsMatch(command, @"(?:了吗|了么|了没|没有|吗|么)[。！!\s]*$")) return null;
        var match = Regex.Match(command,
            @"^(?:请)?(?<verb>收起|撤回|删除)(?:这条|那条)?(?:B1\s*)?(?:判断|解释|观察|问题)[：:\s]*(?<target>.+?)[。！!]?$",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        string target = TrimQuoted(match.Groups["target"].Value);
        if (target.Length < 2) return null;
        return new(match.Groups["verb"].Value switch
        {
            "收起" => "suppress", "撤回" => "withdraw", _ => "delete"
        }, target, true);
    }

    // Called before parsing only for explicit B-domain commands, and from the
    // existing resolver for generic memory commands. A null result preserves
    // the old command path and its existing evidence/authorization checks.
    private UserMemoryOperationResult? TryApplyB1ManagementCommand(MemoryTurn input,
        UserMemoryCommand? parsed, bool explicitOnly = false)
    {
        B1ManagementCommand? command = ParseB1ManagementCommand(input.Text);
        if (command == null)
        {
            if (parsed?.Domain == "utterance") return null;
            if (explicitOnly || parsed == null || parsed.Kind is not ("delete" or "suppress" or "correct")) return null;
            command = new(parsed.Kind, parsed.Target,
                parsed.Target.StartsWith("b1:", StringComparison.Ordinal));
        }
        lock (_experienceLock)
        {
            // The user may manage existing B records while automatic B recall
            // is off. This still requires a real received command in history.
            if (!_longTermEnabled || !VerifyUserEvidence(input.EventId, input.Text))
                return new(false, "source_unavailable", "这次没能可靠地保存操作来源，我还没有改动判断。");
            var presence = InspectB1ManagementStorage();
            if (presence == B1ManagementStoragePresence.Uncertain)
                return new(false, "b1_store_identity_unavailable",
                    "判断记录的存储身份暂时无法确认，这次没有确认完成操作。");
            if (!command.ExplicitDomain && presence == B1ManagementStoragePresence.Absent) return null;
            var doc = LoadB1(true);
            if (doc == null)
                return new(false, "b1_store_" + B1StorageStatus,
                    "判断记录暂时不可用，无法可靠地区分同文对象，这次没有确认完成操作。");
            var receipt = doc.ManagementReceipts.LastOrDefault(r => r.OperationId == input.EventId);
            if (receipt != null)
            {
                if (receipt.Kind == "delete" && receipt.Outcome is ("deleting" or "purge_incomplete"))
                    return FinishB1Deletion(input, doc, receipt);
                return new(true, "already_applied", "这条判断操作已经处理过了。",
                    receipt.TargetVersions.Count == 1 ? "b1:" + receipt.TargetVersions[0] : "");
            }

            bool exactId = command.ExplicitDomain && command.Target.StartsWith("b1:", StringComparison.Ordinal);
            string? version = exactId ? command.Target[3..] : null;
            if (exactId && command.Kind == "delete" && doc.DeletedVersions.Contains(version!))
                return new(true, "already_deleted", "这条判断已经删除，没有恢复或新增内容。", command.Target);
            var matches = doc.Entries.Where(e => !doc.DeletedVersions.Contains(e.Version))
                .Where(e => parsed?.AllMatches != true || NormalizeForCommandMatch(e.Text)
                    .Contains(NormalizeForCommandMatch(command.Target), StringComparison.Ordinal))
                .Select(e => (Entry: e, Match: exactId
                    ? e.Version == version ? MemoryCommandMatch.Exact : MemoryCommandMatch.None
                    : CommandMatchLevel(e.Text, command.Target)))
                .Where(x => x.Match != MemoryCommandMatch.None).ToList();
            if (matches.Count == 0)
                return command.ExplicitDomain
                    ? new(false, "target_not_found", "我没找到对应的保留判断，还没有改动。")
                    : null;
            if (parsed?.AllMatches == true)
                return new(false, "b1_bulk_requires_selection",
                    "还没有改动。这条批量命令也命中了保留判断，请先明确判断的版本或完整原文；我不会只处理其他记录却声称全部完成。",
                    Candidates: matches.Count);
            var best = matches.Max(x => x.Match);
            matches = matches.Where(x => x.Match == best).ToList();

            // A generic request must distinguish B interpretations from raw
            // exchanges even when their text is identical. Do not merge domains.
            List<MemoryCommandCandidate> other = command.ExplicitDomain ? new() :
                FindCommandCandidates(input, parsed!, strictTarget: command.Kind == "correct", out _);
            var otherBest = other.Count == 0 ? MemoryCommandMatch.None : other.Max(c => c.Match);
            if (otherBest > best) return null;
            if (otherBest == best && other.Count > 0)
                return new(false, "target_ambiguous",
                    "这段话同时对应保留判断和原始交流或其他记录，还没有改动。"
                    + "若要只管理判断，请明确说“删除判断：原文”或用 b1:版本号；原始交流会保留。",
                    Match: MatchName(best), Candidates: matches.Count + other.Count);
            if (matches.Count != 1 || best < MemoryCommandMatch.Normalized)
            {
                _memoryCommandClarificationKind = command.Kind;
                return new(false, "target_ambiguous",
                    "还没有改动。请写出那条判断的完整原文，或明确写 b1:版本号。"
                    + "可选版本：" + string.Join("、", matches.Take(3).Select(x => "b1:" + x.Entry.Version)),
                    Match: MatchName(best), Candidates: matches.Count);
            }
            if (command.Kind == "correct")
                return new(false, "b1_change_requires_relation",
                    "这条属于保留的解释。请在交流里说明新的限定或替代；我不会把原判断直接改写成过去就这样说过。",
                    "b1:" + matches[0].Entry.Version, Match: MatchName(best), Candidates: 1);
            return ApplyB1Management(input, command, matches[0].Entry)
                with { Match = MatchName(best), Candidates = 1 };
        }
    }

    private static B1Document CloneB1ManagementDocument(B1Document doc)
    {
        var next = new B1Document
        {
            SchemaVersion = doc.SchemaVersion, Entries = doc.Entries.ToList(),
            Current = new(doc.Current, StringComparer.Ordinal),
            Receipts = doc.Receipts.ToList(), Deliveries = doc.Deliveries.ToList()
        };
        CopyB1State(doc, next);
        return next;
    }

    private UserMemoryOperationResult ApplyB1Management(MemoryTurn input,
        B1ManagementCommand command, B1Entry selected)
    {
        if (!VerifyUserEvidence(input.EventId, input.Text))
            return new(false, "source_unavailable", "这次没有可靠的操作来源，还没有改动判断。");
        var doc = LoadB1(true);
        if (doc == null) return new(false, "b1_store_" + B1StorageStatus,
            "判断记录暂时不可用，这次没有确认完成操作。");
        // The selected immutable version is bound again at the mutation point.
        var live = doc.Entries.SingleOrDefault(e => e.Version == selected.Version);
        if (live == null || live.Text != selected.Text || live.LogicalId != selected.LogicalId
            || doc.DeletedVersions.Contains(live.Version))
            return new(false, "stale_or_missing_target", "那条判断已经变化，这次没有覆盖它。");
        var repeated = doc.ManagementReceipts.LastOrDefault(r => r.OperationId == input.EventId);
        if (repeated != null)
            return repeated.Kind == "delete" && repeated.Outcome is ("deleting" or "purge_incomplete")
                ? FinishB1Deletion(input, doc, repeated)
                : new(true, "already_applied", "这条判断操作已经处理过了。", "b1:" + selected.Version);
        var next = CloneB1ManagementDocument(doc);
        var ids = new HashSet<string>(StringComparer.Ordinal) { live.Version };
        if (command.Kind == "delete")
        {
            // Packets are retained audit copies too. Every record carrying a
            // copy or precise dependency must be removed, but parent raw
            // utterances and independent interpretations are never expanded in.
            bool changed;
            do
            {
                changed = false;
                foreach (var entry in doc.Entries)
                {
                    IEnumerable<B1Ref> refs = entry.Sources.Concat(entry.Packet.Footprint)
                        .Concat(entry.Packet.Materials.Select(m => m.Ref))
                        .Concat(entry.ObjectAnchors).Concat(entry.Packet.SemanticAnchors);
                    if (refs.Any(r => r.Kind == "b1" && ids.Contains(r.Version))
                        || entry.Target.StartsWith("b1:", StringComparison.Ordinal)
                            && ids.Contains(entry.Target[3..]))
                        changed |= ids.Add(entry.Version);
                }
            } while (changed);
        }
        string state = command.Kind == "delete" ? "deleting" :
            command.Kind == "withdraw" ? "withdrawn" : "suppressed";
        foreach (string id in ids) next.Management[id] = state;
        var receipt = new B1ManagementReceipt(input.EventId, command.Kind, ids.OrderBy(x => x).ToList(),
            command.Kind == "delete" ? "deleting" : "applied", DateTimeOffset.UtcNow);
        next.ManagementReceipts.Add(receipt);
        if (!SaveB1(next))
            return new(false, "persistence_failed_or_uncertain",
                "判断操作尚未确认，不能声称已经完成。", "b1:" + selected.Version);
        if (command.Kind == "delete") return FinishB1Deletion(input, next, receipt);
        return new(true, "applied", command.Kind == "withdraw"
            ? "好，这条判断已撤回，不再作为当前依据；原始交流和历史判断仍保留。"
            : "好，这条判断已收起，不再自动带入对话；原始交流和合法历史仍保留。",
            "b1:" + selected.Version, selected.Revision);
    }

    private UserMemoryOperationResult FinishB1Deletion(MemoryTurn input, B1Document doc,
        B1ManagementReceipt receipt)
    {
        // Resume only the exact body-free target set already authorized and
        // persisted by this received operation. No new candidates are selected.
        if (!VerifyUserEvidence(input.EventId, input.Text)
            || receipt.OperationId != input.EventId || receipt.Kind != "delete")
            return new(false, "stale_or_invalid_binding", "这次没有可靠的删除对象，还没有继续清除。");
        var ids = new HashSet<string>(receipt.TargetVersions, StringComparer.Ordinal);
        if (ids.Count == 0 || ids.Any(id => B1ManagementState(doc, id) is not ("deleting" or "deleted")))
            return new(false, "stale_or_invalid_binding", "判断状态已经变化，这次没有继续清除。");
        if (!PurgeLegacyB1Versions(ids))
        {
            var pending = CloneB1ManagementDocument(doc);
            int index = pending.ManagementReceipts.FindLastIndex(r => r.OperationId == input.EventId);
            if (index >= 0) pending.ManagementReceipts[index] = receipt with { Outcome = "purge_incomplete" };
            SaveB1(pending);
            return new(false, "purge_incomplete", "已停止使用这条判断，但内容清除还没有完成。",
                ids.Count == 1 ? "b1:" + ids.First() : "");
        }
        // The legacy scrub changes the compatibility fingerprint. Reload the
        // current transaction view rather than writing a stale base snapshot.
        doc = LoadB1(true) ?? doc;
        var next = CloneB1ManagementDocument(doc);
        var steps = doc.Entries.Where(e => ids.Contains(e.Version)).Select(e => e.StepId)
            .ToHashSet(StringComparer.Ordinal);
        next.Entries.RemoveAll(e => ids.Contains(e.Version));
        next.Current.Clear();
        foreach (var pair in doc.Current.Where(kv => !ids.Contains(kv.Value)))
            next.Current.Add(pair.Key, pair.Value);
        next.Deliveries.RemoveAll(d => steps.Contains(d.StepId));
        next.Receipts.Clear();
        next.Receipts.AddRange(doc.Receipts.Select(r => steps.Contains(r.StepId)
            ? r with { Outcome = "result_deleted" } : r));
        foreach (string id in ids)
        {
            next.Management[id] = "deleted";
            if (!next.DeletedVersions.Contains(id)) next.DeletedVersions.Add(id);
        }
        int receiptIndex = next.ManagementReceipts.FindLastIndex(r => r.OperationId == input.EventId);
        if (receiptIndex >= 0) next.ManagementReceipts[receiptIndex] = receipt with { Outcome = "deleted" };
        if (!SaveB1(next))
            return new(false, "purge_incomplete",
                "已停止使用这条判断，但内容清除结果尚未确认。", ids.Count == 1 ? "b1:" + ids.First() : "");
        return new(true, "applied", "好，指定判断及必须随它清除的派生副本已删除；原始交流保留。",
            ids.Count == 1 ? "b1:" + ids.First() : "");
    }
}
