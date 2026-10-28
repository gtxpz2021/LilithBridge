using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace LilithBridge;

internal sealed class MemoryPatch
{
    public string Action { get; set; } = "save";
    public string Text { get; set; } = "";
    public string Target { get; set; } = "";
    public string Category { get; set; } = "event";
    public double Importance { get; set; } = 0.5;
    public double Strength { get; set; } = 0.5;
    public string EmotionalSignal { get; set; } = "";
    public string Evidence { get; set; } = "";
    public string Validity { get; set; } = "confirmed";
    public string Origin { get; set; } = "reflection";
    public string Reflection { get; set; } = "";
}

internal sealed class ReflectionResult
{
    public ReflectionNoteProposal? Note { get; set; }
    public ReflectionLessonProposal? Lesson { get; set; }
    public RelationshipReadingProposal? Relationship { get; set; }
    public string Summary { get; set; } = "";
    public string Thought { get; set; } = "";
    public List<MemoryPatch> Patches { get; set; } = new();
}

internal static class MemoryLifecycle
{
    public const double MinStrength = 0.02;
    public const double MaxStrength = 1.0;

    private static readonly string[] KnownCategories =
    {
        "identity", "preference", "relationship", "event", "boundary",
        "goal", "promise", "vulnerability", "unknown"
    };

    public static string NormalizeCategory(string? value)
    {
        string normalized = (value ?? "event").Trim().ToLowerInvariant();
        return KnownCategories.Contains(normalized) ? normalized : "event";
    }

    public static string NormalizeValidity(string? value)
    {
        return (value ?? "confirmed").Trim().ToLowerInvariant() switch
        {
            "inferred" => "inferred",
            "user_reported" => "user_reported",
            "corrected" => "corrected",
            "unknown" or "unconfirmed" => "unknown",
            _ => "confirmed"
        };
    }

    public static string NormalizeLifecycleState(string? value)
    {
        return (value ?? "active").Trim().ToLowerInvariant() switch
        {
            "active" => "active",
            "superseded" => "superseded",
            // Her own decision that a thought is finished for now. It stays in
            // the archive and can be reopened by a later revision; it is simply
            // no longer one of the thoughts she is still carrying.
            "settled" => "settled",
            "forgotten" => "forgotten",
            "merged" => "merged",
            "deleting" => "deleting",
            "deleted" => "deleted",
            _ => "unknown"
        };
    }

    public static string NormalizeOrigin(string? value)
    {
        return (value ?? "model").Trim().ToLowerInvariant() switch
        {
            "extractor" => "extractor",
            "reflection" => "reflection",
            "user" => "user",
            "authority_mapping" => "authority_mapping",
            "heuristic" => "heuristic",
            _ => "model"
        };
    }

    public static string NormalizeTier(string? value)
    {
        return (value ?? "recall").Trim().ToLowerInvariant() switch
        {
            "core" => "core",
            "archival" or "archive" => "archival",
            "compressed" or "summary" => "archival",
            _ => "recall"
        };
    }

    public static double Clamp01(double value)
    {
        return Math.Clamp(value, 0.0, 1.0);
    }

    // An explicit strength update represents the new value at operation time,
    // or at the already represented later time while the clock is rolled back.
    public static void MarkStrengthUpdated(MemoryTurn turn, DateTimeOffset now)
    {
        if (now > turn.LastDecayAt)
            turn.LastDecayAt = now;
    }

    public static MemoryTurn Normalize(MemoryTurn turn, DateTimeOffset now, double decayPerDay = 0.03)
    {
        if (turn == null)
            throw new ArgumentNullException(nameof(turn));
        if (turn.CreatedAt == default)
            turn.CreatedAt = turn.Timestamp == default ? now : turn.Timestamp;
        if (turn.UpdatedAt == default)
            turn.UpdatedAt = turn.Timestamp == default ? now : turn.Timestamp;
        if (turn.LastReinforcedAt == default)
            turn.LastReinforcedAt = turn.UpdatedAt;
        if (turn.Timestamp == default)
            turn.Timestamp = turn.UpdatedAt;

        turn.Category = NormalizeCategory(turn.Category);
        turn.Validity = NormalizeValidity(turn.Validity);
        turn.LifecycleState = NormalizeLifecycleState(turn.LifecycleState);
        turn.Origin = NormalizeOrigin(turn.Origin);
        turn.Tier = NormalizeTier(turn.Tier);
        turn.Tags = Clean(turn.Tags);
        turn.Source = Clean(turn.Source);
        turn.ConflictGroup = Clean(turn.ConflictGroup);
        turn.Importance = Clamp01(turn.Importance);
        turn.Strength = Clamp01(turn.Strength);
        turn.Text = Clean(turn.Text);
        turn.EmotionalSignal = Clean(turn.EmotionalSignal);
        turn.Evidence = Clean(turn.Evidence);

        // Legacy Strength is an already accumulated value. Accept it once at
        // adoption; its unknown prior decay cannot be reconstructed from content time.
        if (turn.LastDecayAt == default)
            turn.LastDecayAt = now;
        double days = Math.Max(0d, (now - turn.LastDecayAt).TotalDays);
        double decay = Math.Clamp(decayPerDay, 0d, 0.5d);
        if (decay > 0d && days > 0d)
            turn.Strength = Math.Max(MinStrength, turn.Strength * Math.Pow(1d - decay, days));
        // Keep the high-water mark through clock rollback, including d=0.
        if (now > turn.LastDecayAt)
            turn.LastDecayAt = now;
        return turn;
    }

    public static MemoryTurn Create(
        string text,
        MemoryPatch? patch,
        DateTimeOffset now,
        int maxLength,
        string origin = "model")
    {
        text = Clean(text);
        if (text.Length > maxLength)
            text = text[..maxLength];
        var turn = new MemoryTurn
        {
            Role = "lilith",
            Text = text,
            Kind = "autonomous",
            Category = NormalizeCategory(patch?.Category),
            Importance = Clamp01(patch?.Importance ?? 0.5),
            Strength = Clamp01(patch?.Strength ?? 0.5),
            EmotionalSignal = Clean(patch?.EmotionalSignal),
            Evidence = Clean(patch?.Evidence),
            Validity = NormalizeValidity(patch?.Validity),
            LifecycleState = "active",
            Origin = NormalizeOrigin(string.IsNullOrWhiteSpace(patch?.Origin) ? origin : patch.Origin),
            Tier = "archival",
            CreatedAt = now,
            UpdatedAt = now,
            LastReinforcedAt = now,
            LastDecayAt = now,
            Timestamp = now
        };
        return Normalize(turn, now);
    }

    public static double RecallScore(MemoryTurn turn, DateTimeOffset now)
    {
        if (turn == null)
            return 0d;
        double days = Math.Max(0d, (now - turn.UpdatedAt).TotalDays);
        double recency = Math.Exp(-days / 14d);
        double emotionBonus = string.IsNullOrWhiteSpace(turn.EmotionalSignal) ? 0d : 0.10;
        double evidenceBonus = string.IsNullOrWhiteSpace(turn.Evidence) ? 0d : 0.05;
        return Math.Clamp(
            turn.Strength * 0.45 + turn.Importance * 0.30 + recency * 0.15 + emotionBonus + evidenceBonus,
            0d,
            1d);
    }

    public static bool IsStale(MemoryTurn turn, DateTimeOffset now)
    {
        return turn != null
            && turn.Strength <= MinStrength
            && turn.Importance < 0.25
            && (now - turn.UpdatedAt).TotalDays >= 30d;
    }

    public static bool Merge(
        MemoryTurn target,
        MemoryTurn other,
        string mergedText,
        DateTimeOffset now,
        out string reason)
    {
        reason = "";
        if (target == null || other == null)
        {
            reason = "missing";
            return false;
        }
        if (string.Equals(
                NormalizeMemory(target.Text),
                NormalizeMemory(other.Text),
                StringComparison.Ordinal))
        {
            reason = "same";
            return false;
        }
        string candidate = Clean(mergedText);
        if (candidate.Length == 0)
            candidate = target.Text + "；" + other.Text;

        target.Text = candidate;
        target.Importance = Math.Max(target.Importance, other.Importance);
        target.Strength = Math.Max(target.Strength, other.Strength);
        if (other.LastDecayAt > target.LastDecayAt)
            target.LastDecayAt = other.LastDecayAt;
        MarkStrengthUpdated(target, now);
        target.UpdatedAt = now;
        target.LastReinforcedAt = now;
        target.Validity = NormalizeValidity(
            target.Validity == "corrected" || other.Validity == "corrected"
                ? "corrected"
                : "confirmed");
        target.Evidence = Clean(target.Evidence + "；" + other.Evidence).Trim('；');
        target.EmotionalSignal = Clean(target.EmotionalSignal + "；" + other.EmotionalSignal).Trim('；');
        reason = "merged";
        return true;
    }

    public static void TrimByScore(List<MemoryTurn> list, int max, DateTimeOffset now)
    {
        if (list == null || list.Count <= max)
            return;
        list.Sort((left, right) => RecallScore(right, now).CompareTo(RecallScore(left, now)));
        list.RemoveRange(max, list.Count - max);
    }

    public static string Describe(MemoryTurn turn)
    {
        if (turn == null)
            return "";
        var parts = new List<string>
        {
            turn.Text,
            "类别=" + turn.Category,
            "重要度=" + turn.Importance.ToString("0.00"),
            "强度=" + turn.Strength.ToString("0.00"),
            "可信度=" + turn.Validity
        };
        if (!string.IsNullOrWhiteSpace(turn.EmotionalSignal))
            parts.Add("情绪=" + turn.EmotionalSignal);
        if (!string.IsNullOrWhiteSpace(turn.Evidence))
            parts.Add("来源=" + turn.Evidence);
        if (turn.UpdatedAt != default)
            parts.Add("更新=" + turn.UpdatedAt.LocalDateTime.ToString("yyyy-MM-dd"));
        return string.Join("；", parts);
    }

    public static string Clean(string? value)
    {
        return (value ?? "").Replace("\r", "").Replace("\n", " ").Trim();
    }

    public static bool ContainsSensitive(string? value)
    {
        return PrivacySafety.ContainsSensitiveMemory(value);
    }

    public static string NormalizeMemory(string value)
    {
        return Regex.Replace(
            Clean(value),
            @"[\s，。！？、；：（）【】\[\]{}]",
            "",
            RegexOptions.CultureInvariant)
            .ToLowerInvariant();
    }

    public static MemoryPatch? SuggestHighSignal(string userText, string replyText)
    {
        string source = PrivacySafety.Redact(
            Clean(userText ?? "") + " " + Clean(replyText ?? ""));
        if (source.Length == 0)
            return null;
        if (ContainsAny(source, "搬家", "搬走", "搬到哪里", "新家"))
        {
            return new MemoryPatch
            {
                Action = "save",
                Text = "玩家计划搬家，莉莉丝很在意这件事",
                Category = "event",
                Importance = 0.8,
                Strength = 0.7,
                EmotionalSignal = "牵挂",
                Evidence = "玩家主动分享",
                Origin = "heuristic"
            };
        }
        if (ContainsAny(source, "生日", "毕业", "考试", "结婚", "纪念日"))
        {
            return new MemoryPatch
            {
                Action = "save",
                Text = "玩家分享了人生中的重要日子或节点",
                Category = "event",
                Importance = 0.75,
                Strength = 0.65,
                EmotionalSignal = "在意",
                Evidence = "玩家主动分享",
                Origin = "heuristic"
            };
        }
        if (ContainsAny(source, "养猫", "养狗", "宠物", "家人", "爸爸", "妈妈", "住院", "手术", "失眠"))
        {
            return new MemoryPatch
            {
                Action = "save",
                Text = "玩家分享了自己生活或健康方面的重要信息",
                Category = "vulnerability",
                Importance = 0.7,
                Strength = 0.6,
                EmotionalSignal = "担心与关心",
                Evidence = "玩家主动分享",
                Origin = "heuristic"
            };
        }
        return null;
    }

    public static bool LooksLikeConflict(string? first, string? second)
    {
        return MemorySemantics.LooksLikeConflict(first, second);
    }

    private static bool ContainsAny(string source, params string[] terms)
    {
        foreach (string term in terms)
        {
            if (source.Contains(term, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
