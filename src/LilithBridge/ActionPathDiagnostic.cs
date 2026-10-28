using System;
using System.Text.RegularExpressions;

namespace LilithBridge;

/// <summary>
/// Diagnostic-only action path tracing. It does not change behavior and does
/// not inspect or modify the player-facing reply; it only records what the
/// model produced and what each downstream stage decided.
/// </summary>
internal static class ActionPathDiagnostic
{
    private static readonly Regex ActionTagRegex = new(
        @"(?is)<\s*action\s*:?\s*([^>]+)>|""action""\s*:\s*""([^""]+)""",
        RegexOptions.Compiled);

    private static readonly string[] IntentPhrases =
    {
        "做动作", "动一下", "活动", "挥手", "转圈", "跳舞", "跳一小段",
        "给你跳", "变个魔术", "变魔术", "魔法", "魔术", "懒腰", "伸手",
        "抱抱", "拥抱", "唱歌", "唱一段", "亲亲", "吻", "打个招呼",
        "招呼", "拜拜", "招手", "秀", "表演", "来一个", "看好",
        "你看好", "接住", "展示", "做个", "来一段", "再来一个",
        "动给你看", "给你看"
    };

    public static void TraceParsed(ApiResult reply, string rawText)
    {
        if (reply == null)
            return;
        string raw = string.IsNullOrWhiteSpace(rawText) ? "" : rawText;
        bool rawHasActionTag = ActionTagRegex.IsMatch(raw);
        bool modelRequestedAction = !string.IsNullOrWhiteSpace(reply.Action)
            || (reply.Actions != null && reply.Actions.Count > 0);
        bool intentWithoutTag = !rawHasActionTag
            && !modelRequestedAction
            && ContainsIntent(raw);

        NativeEventLog.Trace(
            "action.diagnostic.parsed",
            "mod",
            "system",
            "model reply parsed for action path",
            "raw_has_action_tag=" + rawHasActionTag
            + ";model_requested_action=" + modelRequestedAction
            + ";action_intent_without_tag=" + intentWithoutTag
            + ";parsed_action=" + (reply.Action ?? "")
            + ";parsed_sequence=" + (reply.Actions?.Count ?? 0)
            + ";raw_length=" + raw.Length
            + ";raw_sample=" + Truncate(raw, 400));
    }

    public static void TraceFallback(string beforeAction, ApiResult? reply)
    {
        if (reply == null)
            return;
        bool fallbackUsed = string.IsNullOrWhiteSpace(beforeAction)
            && !string.IsNullOrWhiteSpace(reply.Action);
        bool allowed = !string.IsNullOrWhiteSpace(reply.Action)
            && ActionCatalog.TryParseAllowedAction(reply.Action, out _);
        NativeEventLog.Trace(
            "action.diagnostic.fallback",
            "mod",
            "system",
            "action fallback path evaluated",
            "fallback_used=" + fallbackUsed
            + ";fallback_before=" + (beforeAction ?? "")
            + ";fallback_after=" + (reply.Action ?? "")
            + ";capability_allowed=" + allowed
            + ";sequence_after=" + (reply.Actions?.Count ?? 0));
    }

    public static void TraceExecutor(LilithActionType? action, ApiResult? reply)
    {
        NativeEventLog.Trace(
            "action.diagnostic.executor",
            "mod",
            "system",
            "action executor entered",
            "requested=" + (action?.ToString() ?? "")
            + ";model_parsed_action=" + (reply?.Action ?? "")
            + ";sequence_count=" + (reply?.Actions?.Count ?? 0));
    }

    public static void TracePlayback(LilithActionType? nativeAction)
    {
        NativeEventLog.Trace(
            "action.diagnostic.playback",
            "mod",
            "system",
            "native say action playback started",
            "native_action=" + (nativeAction?.ToString() ?? "None"));
    }

    private static bool ContainsIntent(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        foreach (string phrase in IntentPhrases)
        {
            if (text.Contains(phrase, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static string Truncate(string value, int max)
    {
        value = value?.Trim() ?? "";
        return value.Length <= max ? value : value[..max] + "...";
    }
}
