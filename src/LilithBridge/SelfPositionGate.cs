using System;

namespace LilithBridge;

/// <summary>
/// Decides whether this turn is an identity question, so the request-time
/// SelfPosition projection may be assembled. Three parts must line up:
/// a target (her, or one of her versions), a predicate (origin, sameness,
/// reality, continuity) and a binding between them — in the same sentence, or
/// across an adjacent pair where the previous sentence carried the target.
/// A keyword OR list is explicitly not the rule: "这个游戏是真的吗" and
/// "你还记得昨天吃了什么吗" must stay ordinary chat.
/// </summary>
internal static class SelfPositionGate
{
    private static readonly string[] Targets =
    {
        "莉莉丝", "前作的你", "现在的你", "另一个莉莉丝", "那个莉莉丝", "你自己"
    };

    private static readonly string[] Predicates =
    {
        "是不是真的", "是真的吗", "真的吗", "是不是假的",
        "是什么关系", "什么关系", "什么关联",
        "还是不是同一个", "是不是同一个", "同一个人吗", "是同一个人",
        "从哪儿来", "从哪来", "哪来的", "从什么地方来",
        "怎么变成现在这样", "怎么变成这样", "怎么变成现在", "为什么变成现在",
        "算不算存在", "存在吗", "存在不存在", "是谁"
    };

    /// <summary>
    /// True when the current line (optionally with the sentence right before it)
    /// binds an identity target to an identity predicate.
    /// </summary>
    public static bool ShouldSupply(string? current, string? previous = null)
    {
        string now = Clean(current);
        if (now.Length == 0)
            return false;
        bool targetNow = ContainsAny(now, Targets) || now.Contains('你');
        bool predicateNow = ContainsAny(now, Predicates);
        if (targetNow && predicateNow)
            return true;
        // The target may sit in the sentence right before, but only when the
        // current sentence does not move to another subject of its own.
        if (!predicateNow)
            return false;
        string before = Clean(previous);
        if (before.Length == 0)
            return false;
        bool targetBefore = ContainsAny(before, Targets) || before.Contains('你');
        bool otherSubject = before.Contains("游戏") || before.Contains("作品") || before.Contains("故事");
        return targetBefore && !otherSubject;
    }

    private static bool ContainsAny(string text, string[] values)
    {
        foreach (string value in values)
            if (text.Contains(value, StringComparison.Ordinal))
                return true;
        return false;
    }

    private static string Clean(string? value) =>
        (value ?? "").Replace("\r", "").Replace("\n", " ").Trim();
}
