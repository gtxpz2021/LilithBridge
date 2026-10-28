using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace LilithBridge;

// Spans refer to Source, the command-normalized real utterance. Evidence remains
// the complete unmodified user input, never a generated explicit command.
internal sealed record NaturalCorrectionIntent(string Source, string Entity,
    string OldValue, string NewValue, int OldStart, int NewStart, bool Supported);
internal sealed record NaturalCorrectionBinding(string SourceEventId,
    NaturalCorrectionIntent Intent, string TargetId, int ExpectedRevision,
    string OldText, string NewText);

internal sealed partial class MemoryStore
{
    private static UserMemoryCommand? ParseNaturalCorrection(string source)
    {
        // This is only a syntactic intent, not yet a long-term memory operation.
        // The resolver requires an existing stored target before propagating an
        // outcome to the reply. Keep the frame subject even when its tail is
        // unsupported so a real target's tentative correction can fail honestly.
        const string entitySlot = @"[\p{IsCJKUnifiedIdeographs}A-Za-z0-9]{2,24}";
        Match frame = Regex.Match(source, "^(?<entity>" + entitySlot + ")"
                + @"(?:我改主意了|更正一下|不是也许[，,]我确定改主意了)[，,]不是[^，,。！？!?；;]+[，,](?:而)?是.+");
        if (!frame.Success)
            frame = Regex.Match(source, "^(?:关于)?(?<entity>" + entitySlot + ")"
                + @"[，,](?:不是也许[，,])?我确定把?[^，,。！？!?；;]+(?:改成|改为|换成).+");
        if (!frame.Success) return null;
        Match match = Regex.Match(source,
            @"^(?<entity>.+?)(?:我改主意了|更正一下|不是也许[，,]我确定改主意了)[，,]不是(?<old>[^，,。！？!?；;]+)[，,](?:而)?是(?<new>[^，,。！？!?；;]+)[。！!]?$",
            RegexOptions.CultureInvariant);
        if (!match.Success)
            match = Regex.Match(source,
                @"^(?:关于)?(?<entity>.+?)[，,](?:不是也许[，,])?我确定把?(?<old>[^，,。！？!?；;]+?)(?:改成|改为|换成)(?<new>[^，,。！？!?；;]+)[。！!]?$",
                RegexOptions.CultureInvariant);
        string entity = (match.Success ? match : frame).Groups["entity"].Value.Trim();
        string oldValue = match.Groups["old"].Value;
        string newValue = match.Groups["new"].Value;
        // Ownership is part of the supported entity grammar. Internal possessives,
        // lists and whole clauses are deliberately not resolved from context.
        string entityCore = Regex.Replace(entity, @"^(?:我家的|我的)", "");
        bool supported = match.Success && Regex.IsMatch(entityCore, @"^[\p{IsCJKUnifiedIdeographs}A-Za-z0-9]{2,18}$")
            && !Regex.IsMatch(entityCore, @"的|和|与|或|以及|他|她|你|如果|假如|不是|不想")
            && (NaturalCorrectionValueDomain(oldValue) & NaturalCorrectionValueDomain(newValue)) != 0
            && oldValue != newValue;
        return new("correct", entity, "", Natural: true,
            Correction: new(source, entity, oldValue, newValue,
                match.Groups["old"].Index, match.Groups["new"].Index, supported));
    }

    // Bounded literal value domains prevent an unpunctuated clause from being
    // mistaken for a noun/value (“书架但没定”, “书架才怪”, etc.). Unsupported
    // atoms use the existing explicit full-record command. No entity is special.
    private static int NaturalCorrectionValueDomain(string value) => value switch
    {
        "窗边" or "书架" or "阳台" or "壁柜" or "桌上" or "桌边" or "桌面"
            or "窗台" or "床头" or "门口" or "玄关" or "客厅" or "卧室"
            or "厨房" or "书房" or "储物间" or "办公室" => 1,
        "海边" or "森林" or "城市" or "夜景" or "星空" or "山谷" or "灯塔" => 2,
        "加糖" or "不加糖" or "无糖" or "加奶" or "不加奶" or "黑咖啡" => 4,
        "红色" or "蓝色" or "绿色" or "黄色" or "白色" or "黑色" or "紫色" or "粉色" => 8,
        _ => 0
    };

    private (NaturalCorrectionBinding? Binding, UserMemoryOperationResult Result)
        BindNaturalCorrection(MemoryTurn input, NaturalCorrectionIntent intent)
    {
        UserMemoryOperationResult Fail(string reason) => new(false, reason,
            "这句话还不能可靠地更新长期记录，原记录没有改动。", ContinueDialogue: true);
        if (!intent.Supported) return (null, Fail("natural_scope_unsupported"));
        // No experience targets, inferred material or identical-text grouping.
        var candidates = _autonomous.Where(t => IsActiveLifecycle(t)
            && IsNaturalCorrectionTarget(t, intent.Entity))
            .ToList();
        if (candidates.Count != 1)
            return (null, Fail(candidates.Count == 0 ? "natural_target_not_found" : "target_ambiguous")
                with { Candidates = candidates.Count });
        MemoryTurn target = candidates[0];
        string remainder = target.Text[intent.Entity.Length..];
        Match record = Regex.Match(remainder,
            @"^(?<predicate>放在|摆在|住在|位于|安排在|定在|颜色是|主题是|时间是|在|是)(?<value>[\p{IsCJKUnifiedIdeographs}A-Za-z0-9]{1,18})(?<end>[。.！!]?)$");
        if (!record.Success || NaturalCorrectionValueDomain(record.Groups["value"].Value) == 0)
            return (null, Fail("natural_reconstruction_unsupported"));
        string predicate = record.Groups["predicate"].Value;
        if (predicate is "放在" or "摆在" or "住在" or "位于" or "在"
            && NaturalCorrectionValueDomain(intent.OldValue) != 1)
            return (null, Fail("natural_reconstruction_unsupported"));
        string current = record.Groups["value"].Value;
        if (current == intent.NewValue)
            return (null, new(true, "already_current", "当前长期记录已经是这个值，这次没有新增版本。",
                target.RecallId, target.Revision, Match: "natural_slot", Candidates: 1, ContinueDialogue: true));
        if (current != intent.OldValue)
            return (null, Fail("natural_old_value_conflict"));
        int valueStart = intent.Entity.Length + record.Groups["value"].Index;
        string fullText = target.Text[..valueStart] + intent.NewValue
            + target.Text[(valueStart + current.Length)..];
        var binding = new NaturalCorrectionBinding(input.EventId, intent, target.RecallId,
            target.Revision, target.Text, fullText);
        return (binding, new(false, "natural_bound", "", ContinueDialogue: true));
    }

    private static bool IsNaturalCorrectionTarget(MemoryTurn turn, string entity) =>
        entity.Length > 0 && turn.Kind != MentionBoundaryKind && turn.Source == "explicit_user"
        && turn.Role == "user" && turn.Origin == "user"
        && turn.Text.StartsWith(entity, StringComparison.Ordinal)
        && Regex.IsMatch(turn.Text[entity.Length..],
            @"^(?:放在|摆在|住在|位于|安排在|定在|颜色是|主题是|时间是|在|是)");

    private UserMemoryOperationResult? ResolveNaturalCorrection(MemoryTurn input, NaturalCorrectionIntent intent)
    {
        lock (_experienceLock)
        {
            // Syntax alone is not permission to turn ordinary speech into an
            // operation failure. No stored long-term referent means ordinary
            // dialogue: no result, no operation flag, no reply replacement.
            // Retained withdrawn/history records still qualify as known targets,
            // but the unchanged active/version checks below forbid revival.
            if (!_autonomous.Any(t => IsNaturalCorrectionTarget(t, intent.Entity)))
                return null;
            if (!_longTermEnabled || !VerifyUserEvidence(input.EventId, input.Text))
                return new(false, "source_unavailable", "这次没有保存成功，原记录没有改动。", ContinueDialogue: true);
            MemoryTurn? receipt = _autonomous.LastOrDefault(t => input.EventId.Length > 0 && t.OperationId == input.EventId);
            if (receipt != null)
                return new(receipt.LifecycleState is not ("deleting" or "deleted" or "forgotten"),
                    "already_applied", "这条更正以前已经处理过了，当前内容以现存记录为准。",
                    receipt.RecallId, receipt.Revision, ContinueDialogue: true);
            var (binding, result) = BindNaturalCorrection(input, intent);
            if (binding == null) return result;
            return ApplyUserMemoryOperation(input, "correct", binding.TargetId,
                binding.ExpectedRevision, binding.NewText, binding)
                with { ContinueDialogue = true, Match = "natural_slot", Candidates = 1 };
        }
    }
}
