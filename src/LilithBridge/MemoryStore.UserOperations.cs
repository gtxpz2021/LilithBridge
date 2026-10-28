using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LilithBridge;

internal sealed record UserMemoryOperationResult(bool Succeeded, string Reason, string Reply, string TargetId = "", int Revision = 0, string[]? RemovedTexts = null, string Match = "", int Candidates = 0, bool ContinueDialogue = false)
{
    public ApiResult CreateReply(MemoryTurn input) => new()
    {
        Text = Reply,
        IsMemoryOperation = true,
        SourceUserEventId = input.EventId,
        SourceUserText = input.Text
    };
}
internal sealed record UserMemoryCommand(string Kind, string Target, string Text, bool Natural = false,
    bool AllMatches = false, NaturalCorrectionIntent? Correction = null, string Domain = "");

internal enum MemoryCommandMatch
{
    None = 0,
    Similarity = 1,
    Contains = 2,
    Normalized = 3,
    Exact = 4
}

/// <summary>
/// One candidate group: every record that carries the same text (the proposal's
/// "identical text is one candidate, suppress them together and report ×N").
/// </summary>
internal sealed record MemoryCommandCandidate(string Text, DateTimeOffset Timestamp,
    List<MemoryTurn> Targets, MemoryCommandMatch Match);

internal sealed partial class MemoryStore
{
    /// <summary>Similarity floor for the natural-language command matcher (proposal §7-2).</summary>
    public const double NaturalCommandSimilarityFloor = 0.5;

    /// <summary>
    /// Words that put a message unmistakably in memory context. "别提多高兴了" is a
    /// discourse marker, "删掉记忆里那条关于 X 的记录" is a request; only the second
    /// shape may be answered with a memory receipt when nothing matched.
    /// </summary>
    private static readonly Regex ExplicitMemoryContext =
        new(@"(?:记忆|记录|备忘)", RegexOptions.Compiled);
    // Receipt context only: no target identities or executable selection survive
    // an ambiguous command. The next user input consumes it, and reload clears it.
    private string _memoryCommandClarificationKind = "";

    private static string NormalizeUserMemoryCommand(string text) =>
        Regex.Replace(Clean(text), @"^(?:莉莉丝|Lilith)[，,：:\s]+", "", RegexOptions.IgnoreCase).Trim();

    private void InitializeMemoryOperationIdentities()
    {
        bool changed = false;
        for (int i = 0; i < _autonomous.Count; i++)
        {
            MemoryTurn turn = _autonomous[i];
            if (turn.RecallId.Length == 0)
            {
                // Deterministic fallback also remains stable if the migration save fails.
                byte[] key = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(
                    i + "|" + turn.Timestamp.ToString("O") + "|" + turn.Text));
                turn.RecallId = "legacy-" + Convert.ToHexString(key)[..24];
                changed = true;
            }
            if (turn.EventId.Length == 0) { turn.EventId = turn.RecallId + ":" + turn.Revision; changed = true; }
        }
        if (changed) SaveList("v4-native-autonomous-memory.json", _autonomous);
    }

    private HashSet<string> SuppressedExperienceIds() => new(_autonomous
        .Where(t => t.LifecycleState is "forgotten" or "deleting" or "deleted")
        .SelectMany(t => t.SourceEventIds), StringComparer.Ordinal);

    public bool IsExperienceSuppressed(string eventId) => SuppressedExperienceIds().Contains(eventId);

    private bool IsDerivedMemoryVisible(MemoryTurn turn)
    {
        if (!IsMentionAllowed(turn)) return false;
        if (turn.Kind == DayReceiptKind)
            return false; // Operational progress must never enter a model context.
        if (turn.Kind is DayConsolidationKind or DayDreamKind)
            return IsDayArtifactCurrent(turn);
        HashSet<string> ids = SuppressedExperienceIds();
        if (ids.Contains(turn.EventId) || ids.Contains(turn.SourceEventId) || turn.SourceEventIds.Any(ids.Contains))
            return false;
        return !SuppressedTextMatches(turn.Text) && !SuppressedTextMatches(turn.Evidence);
    }

    private bool SuppressedTextMatches(string text) => _autonomous
        .Where(t => t.LifecycleState is "forgotten" or "deleting")
        .SelectMany(t => new[]{t.Text,t.Evidence}).Any(q => q.Length > 0
            && (text == q || q.Length >= 4 && text.Contains(q,StringComparison.Ordinal)));

    public IReadOnlyList<MemoryTurn> GetUserRequestedMemories(string? query, int max)
    {
        var available = _autonomous.Where(t => IsActiveLifecycle(t) && t.Source == "explicit_user"
            && IsDerivedMemoryVisible(t)).ToList();
        return available.Where(IsExplicitMentionRecall).OrderByDescending(t => t.Timestamp)
            .Concat(RankWithNaturalRevisions(available, query, max))
            .GroupBy(t => t.EventId).Select(g => g.First()).Take(Math.Max(0, max)).Select(WithNaturalMemoryContext).ToList();
    }

    public UserMemoryOperationResult? TryApplyUserMemoryCommand(MemoryTurn input)
    {
        string clarificationKind = _memoryCommandClarificationKind;
        _memoryCommandClarificationKind = "";
        UserMemoryOperationResult? b1Explicit = TryApplyB1ManagementCommand(input, null, explicitOnly: true);
        if (b1Explicit != null) return b1Explicit;
        UserMemoryOperationResult? rawCorrection = TryApplyRawSourceCorrection(input);
        if (rawCorrection != null) return rawCorrection;
        if (clarificationKind.Length > 0 && Regex.IsMatch(NormalizeUserMemoryCommand(input.Text),
                @"^(?:全部|全部收起来|都收起来)[。！!\s]*$"))
            return new(false, "selection_requires_target", clarificationKind == "suppress"
                ? "还没有改动记录。请把目标写进完整命令，例如：把所有关于某个目标的记录收起来。单独说“全部”不会执行。"
                : "还没有改动记录。请完整写出要操作的原话；更正或删除需要明确目标，单独说“全部”不会执行。");
        UserMemoryCommand? parsed = ParseUserMemoryCommand(input.Text);
        if (parsed == null)
            return null;
        if (parsed.Correction != null)
            return _naturalMemoryEnabled ? null : ResolveNaturalCorrection(input, parsed.Correction);
        if (parsed.Kind == "unsupported_bulk_delete")
            return new(false, "unsupported_operation", "还没有改动记录。批量命令只支持收起；删除请写出对应记录的原话。");
        if (parsed.AllMatches && NormalizeForCommandMatch(parsed.Target).Length < 2)
            return new(false, "target_required", "还没有改动记录。请在命令中写出明确的目标，至少两个字。");
        MemoryTurn? receipt = _autonomous.LastOrDefault(t => input.EventId.Length > 0 && t.OperationId == input.EventId);
        if (receipt != null)
            return new(receipt.LifecycleState != "deleting", "already_applied", "这条操作已经处理过了。", receipt.RecallId, receipt.Revision);
        if (parsed.Kind is "mention_limit" or "mention_allow")
            return ApplyMentionBoundary(input, parsed);
        UserMemoryOperationResult? result = parsed.Kind == "remember"
            ? ApplyUserMemoryOperation(input, parsed.Kind, "", 0, parsed.Text)
                with { Match = "explicit", Candidates = 1 }
            : ResolveUserOperation(input, parsed);
        if (result?.Reason == "target_ambiguous")
            _memoryCommandClarificationKind = parsed.Kind;
        return result;
    }

    private static UserMemoryCommand? ParseUserMemoryCommand(string text)
    {
        string command = NormalizeUserMemoryCommand(text);
        if (command.Length == 0)
            return null;
        if (command.EndsWith('?') || command.EndsWith('？') || Regex.IsMatch(command,@"(?:了吗|了么|了没|没有|吗|么)[。！!\s]*$"))
            return null;
        Match remember = Regex.Match(command, @"^(?:请)?记住[：:\s]*(.+)$");
        if (remember.Success)
            return new("remember", "", Unquote(remember.Groups[1].Value));
        UserMemoryCommand? mention = ParseMentionBoundaryCommand(command);
        if (mention != null) return mention;
        // A stateless bulk withdrawal: the target must appear in the command and
        // match the record text literally. No fuzzy bulk match or bulk deletion.
        Match all = Regex.Match(command,
            @"^(?:请)?把(?:所有|全部)(?:关于|有关)(?<target>.*?)的(?:记忆|记录)(?<verb>收起来|收起|删除|删掉)[。！!]?$",
            RegexOptions.Singleline);
        if (all.Success)
        {
            string target = TrimQuoted(all.Groups["target"].Value);
            return new(all.Groups["verb"].Value is "删除" or "删掉" ? "unsupported_bulk_delete" : "suppress",
                target, "", Natural: true, AllMatches: true);
        }
        Match rawCorrect = Regex.Match(command, "^(?:请)?把(?:用户)?原话[“\"](.+?)[”\"]改成[“\"](.+?)[”\"][。！!]?$", RegexOptions.Singleline);
        if (rawCorrect.Success)
            return new("correct", rawCorrect.Groups[1].Value, rawCorrect.Groups[2].Value, Domain: "utterance");
        Match correct = Regex.Match(command, "^(?:请)?把[“\"](.+?)[”\"]改成[“\"](.+?)[”\"][。！!]?$", RegexOptions.Singleline);
        if (correct.Success)
            return new("correct", correct.Groups[1].Value, correct.Groups[2].Value);
        UserMemoryCommand? natural = ParseNaturalCorrection(command);
        if (natural != null) return natural;
        // Natural revise: "<A> 改成 / 改为 / 换成 <B>" (no quotes). The target side
        // is resolved at exact/normalized level only — see FindCommandCandidates.
        Match revise = Regex.Match(command,
            "^(?:请)?(?<a>.+?)\\s*(?:改成|改为|换成)\\s*(?<b>.+?)[。！!]?$", RegexOptions.Singleline);
        if (revise.Success)
        {
            string oldText = TrimQuoted(revise.Groups["a"].Value);
            string newText = TrimQuoted(revise.Groups["b"].Value);
            if (oldText.Length >= 2 && newText.Length >= 2)
                return new("correct", oldText, newText, Natural: true);
        }
        // Suppress / delete: the shipped exact shape ("以后别提：“<原话>”") plus the
        // natural shapes ("别提青禾了", "忘掉小满", "请删除记忆：青禾").
        Match remove = Regex.Match(command,
            "^(?:请)?(?:以后)?(?<verb>不要再提|别再提|别提|忘记|忘掉|删除记忆|删除|删掉)(?:记忆|记录)?[：:\\s]*(?<frag>.+?)[。！!]?$",
            RegexOptions.Singleline);
        if (remove.Success)
        {
            string raw = remove.Groups["frag"].Value;
            bool quoted = raw.Contains('“') || raw.Contains('”') || raw.Contains('"');
            string fragment = TrimTargetFragment(raw);
            if (fragment.Length >= 2)
                return new(remove.Groups["verb"].Value == "删除记忆"
                        || remove.Groups["verb"].Value is "删除" or "删掉" ? "delete" : "suppress",
                    fragment, "", Natural: !quoted);
        }
        return null;
    }

    /// <summary>Quotes and trailing punctuation only — used for the revise sides.</summary>
    private static string TrimQuoted(string value) =>
        Regex.Replace(Clean(value), @"^[“”""'「」『』]+|[“”""'「」『』]+$", "").TrimEnd('。', '.', '！', '!').Trim();

    /// <summary>
    /// A conversational reference ("青禾", "那盆薄荷", "了，先聊别的吧") reduced to the
    /// part worth matching. Fragments that reduce to nothing mean "not a memory
    /// command" and the caller falls back to ordinary chat.
    /// </summary>
    private static string TrimTargetFragment(string value)
    {
        string text = TrimQuoted(value);
        text = Regex.Replace(text, @"^(?:关于|那个|那件|这件事|那件事|事情|的|了)+", "");
        text = Regex.Replace(text, @"(?:的事|那件事|这件事|了|吧|呢|啊|呀|哦)+$", "");
        return text.Trim();
    }

    /// <summary>
    /// Four-level candidate resolution (proposal §3.2): exact → normalized →
    /// contains → similarity. Only the best level survives, identical texts merge
    /// into one group, and a natural (unquoted) command with no candidate falls back
    /// to ordinary chat instead of reporting "not found".
    /// </summary>
    private UserMemoryOperationResult? ResolveUserOperation(MemoryTurn input, UserMemoryCommand parsed)
    {
        UserMemoryOperationResult? b1 = TryApplyB1ManagementCommand(input, parsed);
        if (b1 != null) return b1;
        List<MemoryCommandCandidate> candidates = FindCommandCandidates(input, parsed,
            strictTarget: parsed.Kind == "correct", out double bestSimilarity);
        if (candidates.Count == 0)
        {
            // An unmistakable memory request must never melt into chat: if the user
            // says "记忆 / 记录" and nothing matched, she says so instead of silently
            // doing nothing. A bare "别提X了" stays ordinary conversation instead.
            if (!parsed.Natural || ExplicitMemoryContext.IsMatch(Clean(input.Text)))
                return new(false, "target_not_found", "我没找到对应的那条长期记录，还没有改动。");
            // A natural "别提X了" is often a discourse marker rather than a memory
            // request, so a phrasing with no matching record stays ordinary chat
            // (proposal §7). It must still be observable: the trace carries the
            // command kind, the fragment length and the best similarity scalar —
            // never the text itself.
            NativeEventLog.Trace("memory.command_natural_none", "mod", "system",
                "natural memory phrasing found no record and stayed ordinary chat",
                "kind=" + parsed.Kind + ";fragment_len=" + parsed.Target.Length
                + ";best_similarity=" + bestSimilarity.ToString("0.00"));
            return null;
        }
        if (candidates.Count > 1 && !parsed.AllMatches)
            return new(false, "target_ambiguous", DescribeCommandCandidates(candidates, parsed),
                Match: MatchName(candidates[0].Match), Candidates: candidates.Count);
        if (parsed.AllMatches)
            return ApplyUserMemoryOperationToGroup(input, parsed.Kind,
                candidates.SelectMany(c => c.Targets).ToList())
                with { Match = "all_literal", Candidates = candidates.Count };
        MemoryCommandCandidate chosen = candidates[0];
        UserMemoryOperationResult result = parsed.Kind == "correct"
            ? ApplyUserMemoryOperation(input, "correct", chosen.Targets[0].RecallId,
                chosen.Targets[0].Revision, parsed.Text)
            : ApplyUserMemoryOperationToGroup(input, parsed.Kind, chosen.Targets);
        return result with { Match = MatchName(chosen.Match), Candidates = 1 };
    }

    private static string MatchName(MemoryCommandMatch match) => match.ToString().ToLowerInvariant();

    private List<MemoryCommandCandidate> FindCommandCandidates(MemoryTurn input, UserMemoryCommand parsed,
        bool strictTarget, out double bestSimilarity)
    {
        string fragment = parsed.Target;
        double bestScore = 0d;
        var found = new List<(MemoryCommandMatch Level, MemoryTurn Turn)>();
        void Examine(MemoryTurn turn)
        {
            bestScore = Math.Max(bestScore, MemorySemantics.Similarity(fragment, turn.Text));
            if (parsed.AllMatches && !NormalizeForCommandMatch(turn.Text)
                    .Contains(NormalizeForCommandMatch(fragment), StringComparison.Ordinal))
                return;
            AddCommandCandidate(found, turn, fragment, strictTarget);
        }
        // "删除" keeps reaching records that were already suppressed (the shipped
        // semantics: suppression then physical deletion); every other kind only
        // sees the live record set.
        foreach (MemoryTurn turn in _autonomous.Where(t => t.Kind != MentionBoundaryKind
                     && (IsActiveLifecycle(t) || parsed.Kind == "delete" && t.LifecycleState == "forgotten")))
            Examine(turn);
        // Command lines themselves are not memory targets: "记住：…" would otherwise
        // compete with the record it created and make every fragment ambiguous.
        foreach (MemoryTurn turn in ReadExperiences()
                     .Where(t => t.EventId != input.EventId && t.Role == "user"
                          && ParseUserMemoryCommand(Clean(t.Text)) == null
                          && ParseB1ManagementCommand(Clean(t.Text)) == null))
            Examine(AsOperationTarget(turn));
        if (found.Count == 0)
        {
            bestSimilarity = bestScore;
            return new List<MemoryCommandCandidate>();
        }
        MemoryCommandMatch best = found.Max(item => item.Level);
        bestSimilarity = bestScore;
        var winners = found.Where(item => parsed.AllMatches || item.Level == best)
            .GroupBy(item => item.Turn.RecallId, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(item => item.Turn.Revision).First())
            .ToList();
        return winners
            .GroupBy(item => item.Turn.Text, StringComparer.Ordinal)
            .Select(group => new MemoryCommandCandidate(group.Key,
                group.Max(item => item.Turn.Timestamp), group.Select(item => item.Turn).ToList(),
                group.Max(item => item.Level)))
            .OrderByDescending(candidate => candidate.Timestamp)
            .ToList();
    }

    private static void AddCommandCandidate(List<(MemoryCommandMatch, MemoryTurn)> found,
        MemoryTurn turn, string fragment, bool strictTarget)
    {
        MemoryCommandMatch level = CommandMatchLevel(turn.Text, fragment);
        if (strictTarget && level is not (MemoryCommandMatch.Exact or MemoryCommandMatch.Normalized))
            return;
        if (level != MemoryCommandMatch.None)
            found.Add((level, turn));
    }

    private static MemoryCommandMatch CommandMatchLevel(string record, string fragment)
    {
        string text = Clean(record);
        string needle = Clean(fragment);
        if (text.Length == 0 || needle.Length == 0)
            return MemoryCommandMatch.None;
        if (SameTarget(text, needle))
            return MemoryCommandMatch.Exact;
        string a = NormalizeForCommandMatch(text);
        string b = NormalizeForCommandMatch(needle);
        if (a.Length == 0 || b.Length == 0)
            return MemoryCommandMatch.None;
        if (a == b)
            return MemoryCommandMatch.Normalized;
        string shorter = a.Length <= b.Length ? a : b;
        string longer = a.Length <= b.Length ? b : a;
        if (shorter.Length >= 2 && longer.Contains(shorter, StringComparison.Ordinal))
            return MemoryCommandMatch.Contains;
        if (MemorySemantics.Similarity(needle, text) >= NaturalCommandSimilarityFloor)
            return MemoryCommandMatch.Similarity;
        return MemoryCommandMatch.None;
    }

    private static string NormalizeForCommandMatch(string value) =>
        Regex.Replace(Clean(value), @"[\s，。！？、；：“”‘’（）()\[\]【】…—\-·,\.!\?;:'""]", "");

    private static string DescribeCommandCandidates(List<MemoryCommandCandidate> candidates, UserMemoryCommand parsed)
    {
        var lines = candidates.Take(3).Select(candidate =>
        {
            string text = candidate.Text.Length > 40 ? candidate.Text[..40] + "…" : candidate.Text;
            string copies = candidate.Targets.Count > 1 ? "（×" + candidate.Targets.Count + "）" : "";
            return "- 「" + text + "」" + copies + "（"
                + candidate.Timestamp.LocalDateTime.ToString("MM-dd") + "）";
        });
        return "我这边有 " + candidates.Count + " 组像的记录"
            + (candidates.Count > 3 ? "，这里先列三组" : "") + "：\n" + string.Join("\n", lines)
            + "\n还没有改动。你指的是哪一条？可以再说得更接近原话一点。"
            + (parsed.Kind == "suppress"
                ? "如果这些都要收起，请完整说：把所有关于“" + parsed.Target + "”的记录收起来。"
                : "更正或删除请写出对应的原话。")
            + "单独说“全部”不会执行。";
    }

    private static MemoryTurn AsOperationTarget(MemoryTurn source)
    {
        MemoryTurn target = source.Copy();
        target.RecallId = "experience-" + source.EventId;
        target.SourceEventIds = new[]{source.EventId};
        return target;
    }

    public UserMemoryOperationResult ApplyUserMemoryOperation(MemoryTurn input, string kind, string targetId, int expectedRevision, string text,
        NaturalCorrectionBinding? natural = null)
    {
        lock (_experienceLock)
        {
            if (natural != null && !VerifyUserEvidence(input.EventId, input.Text))
                return new(false, "source_unavailable", "这次没有保存成功，原记录没有改动。", ContinueDialogue: true);
            MemoryTurn? receipt = _autonomous.LastOrDefault(t => t.OperationId == input.EventId && input.EventId.Length > 0);
            if (receipt != null)
                return new(receipt.LifecycleState != "deleting", "already_applied", "这条操作已经处理过了。", receipt.RecallId, receipt.Revision, ContinueDialogue: natural != null);
            if (!_longTermEnabled || !VerifyUserEvidence(input.EventId, input.Text))
                return new(false, "source_unavailable", "这次没能可靠地保存来源，我还没有改动长期记录。");
            text = Clean(text);
            string source = Clean(input.Text);
            UserMemoryCommand? parsed = ParseUserMemoryCommand(source);
            bool explicitRequest = parsed != null && parsed.Kind == kind && string.Equals(parsed.Text,text,StringComparison.Ordinal)
                && parsed.Correction == null
                && (kind is "delete" or "suppress" || text.Length > 0);
            if (natural != null)
            {
                var (rebound, _) = parsed?.Correction == null
                    ? (null, new UserMemoryOperationResult(false, "not_explicit", ""))
                    : BindNaturalCorrection(input, parsed.Correction);
                explicitRequest = kind == "correct" && rebound == natural
                    && natural.SourceEventId == input.EventId && natural.TargetId == targetId
                    && natural.ExpectedRevision == expectedRevision && natural.NewText == text;
                if (!explicitRequest)
                    return new(false, "stale_or_invalid_binding", "那条记录或来源已经变化，这次没有覆盖它。", ContinueDialogue: true);
            }
            if (!explicitRequest)
                return new(false, "not_explicit", "我还不能确定你想修改哪条记录。");
            MemoryTurn? target = kind == "remember" ? null : _autonomous.Where(t => t.Kind != MentionBoundaryKind && t.RecallId == targetId
                && (IsActiveLifecycle(t) || kind == "delete" && t.LifecycleState == "forgotten"))
                .OrderByDescending(t=>t.Revision).FirstOrDefault();
            if (kind == "remember")
                target = _autonomous.Where(t => t.Kind != MentionBoundaryKind && IsActiveLifecycle(t) && string.Equals(t.Text,text,StringComparison.Ordinal))
                    .OrderByDescending(t => t.Revision).FirstOrDefault();
            if (target == null && kind != "remember" && natural == null)
                target = ReadExperiences().Where(t => t.Role == "user" && "experience-" + t.EventId == targetId)
                    .Select(AsOperationTarget).SingleOrDefault();
            if (kind != "remember" && (target == null || target.Revision != expectedRevision))
                return new(false, "stale_or_missing_target", "那条记录已经发生变化，这次操作没有覆盖它。");
            if (target != null && kind != "remember"
                && (natural == null ? !SameTarget(target.Text, parsed!.Target) : target.Text != natural.OldText))
                return new(false, "target_not_in_command", "我还不能确定你想修改哪条记录。");
            var backup = _autonomous.Select(t => t.Copy()).ToList();
            if (target != null)
                target.LifecycleState = kind is "correct" or "remember" ? "superseded" : "forgotten";
            string id = target?.RecallId ?? Guid.NewGuid().ToString("N");
            string[] ids = (target?.SourceEventIds ?? Array.Empty<string>())
                .Concat(target?.SourceEventId.Length > 0 ? new[]{target.SourceEventId} : Array.Empty<string>())
                .Append(input.EventId).Distinct(StringComparer.Ordinal).ToArray();
            if (kind is "suppress" or "delete")
            {
                var sourceIds = new HashSet<string>(ids, StringComparer.Ordinal);
                foreach (MemoryTurn turn in ReadExperiences(true))
                    if (sourceIds.Contains(turn.SourceEventId)) sourceIds.Add(turn.EventId);
                ids = sourceIds.ToArray();
            }
            var changed = new MemoryTurn
            {
                EventId=Guid.NewGuid().ToString("N"), RecallId=id, Revision=(target?.Revision ?? 0)+1,
                OperationId=input.EventId, SourceEventId=input.EventId, SourceEventIds=ids,
                RequestKind=kind,
                Role="user", Kind="autonomous", Origin="user", Source="explicit_user",
                LastDecayAt=DateTimeOffset.UtcNow,
                Text=kind is "remember" or "correct" ? text : target!.Text,
                Evidence=input.Text, Validity="user_reported", Tier="archival",
                LifecycleState=kind == "suppress" ? "forgotten" : kind == "delete" ? "deleting" : "active"
            };
            _autonomous.Add(changed);
            if (!SaveList("v4-native-autonomous-memory.json",_autonomous))
            {
                _autonomous.Clear(); _autonomous.AddRange(backup);
                return new(false,"persistence_failed","这次没有保存成功，还没有改动原记录。");
            }
            if (kind == "delete" && !PurgeMemoryRecord(changed))
                return new(false,"purge_incomplete","我已停止使用这条记录，但清除还没有完成。",id,changed.Revision);
            return new(true,"applied",kind switch
            {
                "remember" => "好，这条已经记下来了。",
                "correct" => "好，新的说法接在原来那条后面了。",
                "suppress" => "好，我会把这条记录收起来，不再用于后续对话。",
                _ => "好，这条记忆已经清除了。"
            },id,changed.Revision,kind is "suppress" or "delete"
                ? backup.Where(t=>t.RecallId==id).SelectMany(t=>new[]{t.Text,t.Evidence}).Append(target?.Text ?? "").Append(input.Text).Where(s=>s.Length>0).Distinct().ToArray()
                : null);
        }
    }

    /// <summary>
    /// Suppress / delete one identical-text group, or suppress the literal target
    /// groups explicitly authorized by a single-turn AllMatches command. One
    /// receipt covers the targets; persistence, revision and replay guards apply.
    /// </summary>
    private UserMemoryOperationResult ApplyUserMemoryOperationToGroup(MemoryTurn input, string kind,
        List<MemoryTurn> targets)
    {
        lock (_experienceLock)
        {
            MemoryTurn? receipt = _autonomous.LastOrDefault(t =>
                input.EventId.Length > 0 && t.OperationId == input.EventId);
            if (receipt != null)
                return new(receipt.LifecycleState != "deleting", "already_applied",
                    "这条操作已经处理过了。", receipt.RecallId, receipt.Revision);
            if (!_longTermEnabled || !VerifyUserEvidence(input.EventId, input.Text))
                return new(false, "source_unavailable", "这次没能可靠地保存来源，我还没有改动长期记录。");
            UserMemoryCommand? parsed = ParseUserMemoryCommand(Clean(input.Text));
            if (parsed == null || parsed.Kind != kind)
                return new(false, "not_explicit", "我还不能确定你想修改哪条记录。");
            // Same stale-target guard as the single-target path: resolution happened
            // outside the lock, so every target is re-checked against the live set
            // before anything is written.
            foreach (MemoryTurn target in targets)
            {
                MemoryTurn? live = _autonomous.Where(t => t.RecallId == target.RecallId
                        && (IsActiveLifecycle(t) || kind == "delete" && t.LifecycleState == "forgotten"))
                    .OrderByDescending(t => t.Revision).FirstOrDefault();
                // A plain sentence that never became a long-term record is addressed
                // through its own experience identity ("experience-<EventId>").
                live ??= ReadExperiences(true).Where(t => "experience-" + t.EventId == target.RecallId)
                    .Select(AsOperationTarget).LastOrDefault();
                if (live == null || live.Revision != target.Revision
                    || !SameTarget(live.Text, target.Text))
                    return new(false, "stale_or_missing_target",
                        "那条记录已经发生变化，这次操作没有覆盖它。");
            }
            var backup = _autonomous.Select(t => t.Copy()).ToList();
            var ids = new HashSet<string>(StringComparer.Ordinal) { input.EventId };
            foreach (MemoryTurn target in targets)
            {
                if (target.EventId.Length > 0) ids.Add(target.EventId);
                foreach (string id in target.SourceEventIds) ids.Add(id);
                if (target.SourceEventId.Length > 0) ids.Add(target.SourceEventId);
                // The target is withdrawn first; only the receipt carries the
                // "deleting" state that the restart recovery resumes from.
                target.LifecycleState = "forgotten";
            }
            // One-hop expansion over records that cite the withdrawn sources, so the
            // cascade does not depend on the reader having seen the raw target.
            foreach (MemoryTurn turn in ReadExperiences(true))
                if (ids.Contains(turn.SourceEventId)) ids.Add(turn.EventId);
            string text = targets[0].Text;
            var changed = new MemoryTurn
            {
                EventId = Guid.NewGuid().ToString("N"),
                RecallId = targets[0].RecallId,
                Revision = targets[0].Revision + 1,
                OperationId = input.EventId,
                SourceEventId = input.EventId,
                SourceEventIds = ids.ToArray(),
                RequestKind = kind,
                Role = "user",
                Kind = "autonomous",
                LastDecayAt = DateTimeOffset.UtcNow,
                Origin = "user",
                Source = "explicit_user",
                Text = text,
                Evidence = input.Text,
                Validity = "user_reported",
                Tier = "archival",
                LifecycleState = kind == "delete" ? "deleting" : "forgotten"
            };
            _autonomous.Add(changed);
            if (!SaveList("v4-native-autonomous-memory.json", _autonomous))
            {
                _autonomous.Clear();
                _autonomous.AddRange(backup);
                return new(false, "persistence_failed", "这次没有保存成功，还没有改动原记录。");
            }
            if (kind == "delete" && !PurgeMemoryRecord(changed))
                return new(false, "purge_incomplete", "我已停止使用这条记录，但清除还没有完成。",
                    changed.RecallId, changed.Revision);
            int copies = targets.Count;
            string reply = kind == "delete"
                ? copies > 1 ? "好，" + copies + " 条一样的记录都清除了。"
                             : "好，这条记忆已经清除了。"
                : copies > 1 ? "好，这样的记录我一次收起了 " + copies + " 条，都不再用于后续对话。"
                             : "好，我会把这条记录收起来，不再用于后续对话。";
            string[] removed = backup.Where(t => ids.Contains(t.EventId))
                .SelectMany(t => new[] { t.Text, t.Evidence })
                .Concat(targets.Select(t => t.Text)).Append(input.Text)
                .Where(s => s.Length > 0).Distinct().ToArray();
            return new(true, "applied", reply, changed.RecallId, changed.Revision, removed);
        }
    }

    private void RecoverPendingMemoryDeletes()
    {
        foreach (MemoryTurn receipt in _autonomous.Where(t=>t.LifecycleState == "deleting").ToList())
            PurgeMemoryRecord(receipt);
    }

    private bool PurgeMemoryRecord(MemoryTurn receipt)
    {
        lock (_experienceLock)
        {
            try
            {
                var ids = new HashSet<string>(receipt.SourceEventIds,StringComparer.Ordinal);
                // Follow recorded derivation identities as well as raw source
                // IDs. A thought may have re-read a day whose other sources
                // were not its own explicitly cited exchange.
                var derived = _reflectionNotes.Concat(_reflectionLessons).Concat(_relationshipReadings)
                    .Concat(_dreams).Concat(_summaries)
                    .Concat(_autonomous.Where(t => t.Kind == MentionBoundaryKind)).ToList();
                bool added;
                do
                {
                    added = false;
                    foreach (MemoryTurn t in derived.Where(t => t.Kind != DayReceiptKind))
                        if (ids.Contains(t.SourceEventId) || t.SourceEventIds.Any(ids.Contains)
                            || t.SourceNoteIds.Any(ids.Contains) || t.SourceDayIds.Any(ids.Contains)
                            || t.SourceDependencies.Concat(t.ContextDependencies).Any(d => ids.Contains(d.EventId)))
                        {
                            if (t.EventId.Length > 0) added |= ids.Add(t.EventId);
                            // Remove a bound preference and its command, but do not
                            // widen deletion to its other independent source facts.
                            if (t.Kind == MentionBoundaryKind && t.SourceEventId.Length > 0)
                                added |= ids.Add(t.SourceEventId);
                        }
                    foreach (MemoryTurn t in ReadExperiences(true).Where(t => ids.Contains(t.SourceEventId)))
                        added |= ids.Add(t.EventId);
                } while (added);
                if (!PurgeB1Sources(ids)) return false;
                var quotes = _autonomous.Where(t=>t.RecallId == receipt.RecallId)
                    .SelectMany(t=>new[]{t.Text,t.Evidence}).Where(s=>s.Length > 0).ToHashSet(StringComparer.Ordinal);
                foreach (MemoryTurn turn in ReadExperiences(true).Where(t=>ids.Contains(t.EventId)))
                    quotes.Add(turn.Text);
                foreach (MemoryTurn turn in derived.Where(t => ids.Contains(t.EventId) && t.Kind != MentionBoundaryKind))
                    if (turn.Text.Length > 0) quotes.Add(turn.Text);
                bool BoundSource(MemoryTurn t) => ids.Contains(t.EventId) || ids.Contains(t.SourceEventId)
                    || t.SourceEventIds.Any(ids.Contains) || t.SourceNoteIds.Any(ids.Contains)
                    || t.SourceDayIds.Any(ids.Contains);
                bool Matches(MemoryTurn t) => BoundSource(t) || t.DeliveryState == "legacy_unknown"
                    && t.SourceEventIds.Length == 0 && t.SourceEventId.Length == 0
                    && quotes.Any(q=>t.Text.Contains(q,StringComparison.Ordinal) || t.Evidence.Contains(q,StringComparison.Ordinal));
                if (Directory.Exists(ExperienceDirectory))
                {
                    foreach (string path in Directory.EnumerateFiles(ExperienceDirectory,"*.jsonl"))
                    {
                        var retained = new List<string>();
                        foreach (string line in File.ReadLines(path))
                        {
                            // Do not declare success while an unreadable record may contain the target.
                            MemoryTurn? turn = JsonSerializer.Deserialize<MemoryTurn>(line);
                            if (turn != null && !(turn.DeliveryState == "legacy_unknown" ? Matches(turn) : BoundSource(turn))) retained.Add(line);
                        }
                        ReplaceLines(path,retained);
                    }
                }
                _experienceCache = null;
                foreach (var pair in new[]{("sessions\\v4-native-chat.json",_history),("v4-native-context.json",_native),
                             ("v4-native-events.json",_events),("v4-native-profile.json",_profile),("v4-native-summaries.json",_summaries),
                             ("v4-native-dreams.json",_dreams),("v4-native-recall-ledger.json",_recallLog),
                             ("v4-reflection-notes.json",_reflectionNotes),
                             ("v4-reflection-lessons.json",_reflectionLessons),
                             ("v4-relationship-readings.json",_relationshipReadings)})
                {
                    pair.Item2.RemoveAll(t=>Matches(t));
                    if (!SaveList(pair.Item1,pair.Item2)) return false;
                }
                // Debug copies are generated by this plugin and may contain the
                // same original text, escaped inside nested JSON prompt dumps.
                string telemetry = Path.Combine(_root,"telemetry");
                if (Directory.Exists(telemetry))
                {
                    foreach (string path in Directory.EnumerateFiles(telemetry,"*",SearchOption.AllDirectories)
                                 .Where(p=>p.EndsWith(".json") || p.EndsWith(".jsonl") || p.EndsWith(".txt") || p.EndsWith(".old")))
                    {
                        string content=File.ReadAllText(path);
                        string replaced=ScrubDiagnosticText(content,quotes,allOccurrences:true);
                        if (content != replaced)
                        {
                            string temp=path+".tmp";
                            File.WriteAllText(temp,replaced,new UTF8Encoding(false));
                            File.Move(temp,path,true);
                        }
                    }
                }
                foreach (string name in new[]{"v4-native-last-scene.json","v4-native-persona.json"})
                {
                    string path = Path.Combine(_root,name);
                    if (!File.Exists(path)) continue;
                    string content = File.ReadAllText(path);
                    string scrubbed = ScrubDiagnosticText(content,quotes);
                    if (name == "v4-native-last-scene.json" && _lastSceneSummary != null && _lastSceneSummary.SourceEventIds.Any(ids.Contains))
                    {
                        _lastSceneSummary.NotableEvent = "";
                        _lastSceneSummary.UnfinishedTopic = "";
                    _lastSceneSummary.RecentTopic = "";
                    _lastSceneSummary.UserText = "";
                    _lastSceneSummary.AssistantText = "";
                    _lastSceneSummary.AssistantTimestamp = null;
                        _lastSceneSummary.SourceEventIds = Array.Empty<string>();
                        scrubbed = JsonSerializer.Serialize(_lastSceneSummary);
                    }
                    if (scrubbed == content) continue;
                    File.WriteAllText(path + ".tmp",scrubbed,new UTF8Encoding(false));
                    File.Move(path + ".tmp",path,true);
                }
                _lastSceneSummary = null;
                LoadLastSceneSummary();
                LoadPersonaState();
                _autonomous.RemoveAll(t=>!ReferenceEquals(t,receipt) && (t.RecallId == receipt.RecallId || Matches(t)));
                string oldText=receipt.Text, oldEvidence=receipt.Evidence;
                receipt.Text=""; receipt.Evidence=""; receipt.LifecycleState="deleted";
                if (!SaveList("v4-native-autonomous-memory.json",_autonomous))
                {
                    receipt.LifecycleState="deleting";
                    receipt.Text=oldText; receipt.Evidence=oldEvidence;
                    return false;
                }
                return true;
            }
            catch (Exception error)
            {
                _experienceCache = null;
                NativeEventLog.Trace("memory.delete_incomplete","mod","system","memory purge needs retry",error.GetType().Name);
                return false;
            }
        }
    }

    private static void ReplaceLines(string path,IEnumerable<string> lines)
    {
        string temp=path+".tmp";
        File.WriteAllLines(temp,lines,new UTF8Encoding(false));
        File.Move(temp,path,true);
    }
    private static string ScrubDiagnosticText(string content,HashSet<string> quotes,bool allOccurrences=false)
    {
        bool changed = false;
        string ScrubString(string value, int depth)
        {
            string original = value;
            if (quotes.Contains(value)) { changed = true; return "[removed]"; }
            if (depth < 8 && (value.TrimStart().StartsWith('{') || value.TrimStart().StartsWith('[')))
            {
                try
                {
                    bool before = changed;
                    changed = false;
                    JsonNode? nested = ScrubNode(JsonNode.Parse(value), depth + 1);
                    bool nestedChanged = changed;
                    changed |= before;
                    if (nestedChanged) return nested?.ToJsonString() ?? value;
                    return value;
                }
                catch (JsonException) { }
            }
            foreach (string quote in quotes.OrderByDescending(q=>q.Length))
                if (allOccurrences || quote.Length >= 4 || value == quote)
                    value=value.Replace(quote,"[removed]",StringComparison.Ordinal);
            changed |= value != original;
            return value;
        }
        JsonNode? ScrubNode(JsonNode? node, int depth)
        {
            if (node is JsonObject obj)
            {
                var copy = new JsonObject();
                foreach (var pair in obj) copy[pair.Key] = ScrubNode(pair.Value, depth);
                return copy;
            }
            if (node is JsonArray array)
                return new JsonArray(array.Select(item => ScrubNode(item, depth)).ToArray());
            if (node is JsonValue value && value.TryGetValue<string>(out string? s))
                return JsonValue.Create(ScrubString(s, depth));
            return node == null ? null : JsonNode.Parse(node.ToJsonString());
        }
        string ScrubJsonOrLine(string line)
        {
            changed = false;
            try
            {
                JsonNode? node = ScrubNode(JsonNode.Parse(line), 0);
                return changed ? node?.ToJsonString() ?? line : line;
            }
            catch (JsonException) { return ScrubString(line, 0); }
        }
        try
        {
            JsonNode? node = ScrubNode(JsonNode.Parse(content), 0);
            return changed ? node?.ToJsonString() ?? content : content;
        }
        catch (JsonException)
        {
            return string.Join("\n",content.Split('\n').Select(ScrubJsonOrLine));
        }
    }
    private static string Unquote(string text) => Clean(text).Trim('“','”','"');
    private static bool SameTarget(string left,string right) => string.Equals(Clean(left).TrimEnd('。','.'),Clean(right).TrimEnd('。','.'),StringComparison.OrdinalIgnoreCase);
}
