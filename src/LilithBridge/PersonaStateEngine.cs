using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace LilithBridge;

internal readonly record struct PersonaDecisionHint(
    bool ShouldSpeak,
    bool ShouldWait,
    bool ShouldDeferProactive,
    string MomentFocus,
    string SafetyReason)
{
    public static PersonaDecisionHint Neutral() =>
        new(true, false, false, "等待", "natural");
}

internal static class PersonaStateEngine
{
    private static readonly HashSet<LilithActionType> SleepSafeActions = new()
    {
        LilithActionType.None,
        LilithActionType.Idle,
        LilithActionType.Sleep,
        LilithActionType.Drowsy,
        LilithActionType.DrowsySlump,
        LilithActionType.HalfAwake,
        LilithActionType.Mumble,
        LilithActionType.Blink,
        LilithActionType.SmallYawn,
        LilithActionType.PerkYawn
    };

    private static readonly HashSet<LilithActionType> PoseBlockingActions = new()
    {
        LilithActionType.StandUp,
        LilithActionType.Walk,
        LilithActionType.WalkEnd,
        LilithActionType.Drag,
        LilithActionType.Intro
    };

    private static readonly Dictionary<LilithActionType, string> TouchContextActions = new()
    {
        [LilithActionType.RubHead] = "head",
        [LilithActionType.RubFace] = "face",
        [LilithActionType.BesidePat] = "hand"
    };

    public static PersonaDecisionHint Evaluate(SessionState state, BridgeConfig config)
    {
        if (state == null)
            return PersonaDecisionHint.Neutral();

        bool nativeStory = state.NativeDialogueActive
            || state.PlayerOptionsShowing
            || state.BusyOrAwaitingResponse;
        bool sleeping = state.Native.IsSleep;
        bool beingDragged = state.Native.IsDragging || state.Native.IsInteracting;
        bool hasPlayerWords = state.LastUserText.Length > 0;
        bool aboutToSpeak = state.MomentAboutToSpeak;
        bool listening = state.MomentListening;

        string focus = sleeping
            ? "睡觉"
            : beingDragged
                ? "被玩家带离桌面"
                : nativeStory
                    ? "原生剧情"
                    : listening
                        ? "听玩家说话"
                        : aboutToSpeak
                            ? "准备开口"
                            : state.Attention.ToString();
        string safety = sleeping
            ? "native_sleep"
            : nativeStory
                ? "native_story"
                : beingDragged
                    ? "being_dragged"
                    : "natural";
        bool shouldWait = nativeStory || sleeping || beingDragged;
        bool shouldDeferProactive = shouldWait
            || (config != null && config.ReflectionOnlyWhenIdle && !state.MomentListening && !hasPlayerWords);
        bool shouldSpeak = !shouldWait && (hasPlayerWords || aboutToSpeak || state.Attention == AttentionTarget.Player);
        return new PersonaDecisionHint(
            shouldSpeak,
            shouldWait,
            shouldDeferProactive,
            focus,
            safety);
    }

    public static string DescribeForPrompt(PersonaDecisionHint hint, SessionState state)
    {
        // Emotional residue has its own single projection in SessionState.
        // Do not turn the same numbers into another set of length, follow-up,
        // silence or mood instructions. Only surface an active runtime boundary.
        if (!hint.ShouldWait && !hint.ShouldDeferProactive)
            return "";
        var builder = new StringBuilder();
        builder.AppendLine("[莉莉丝此刻的决策·软建议（程序倾向，不是必须执行的指令）]");
        builder.AppendLine("等待=" + (hint.ShouldWait ? "是" : "否")
            + "；关注=" + hint.MomentFocus
            + "；原因=" + hint.SafetyReason
            + "；暂缓主动开口=" + (hint.ShouldDeferProactive ? "是" : "否"));
        builder.AppendLine("能力契约与原生状态决定可执行边界；原生剧情、选项等待、睡眠或拖拽期间不要抢话。");
        return builder.ToString();
    }

    public static void ApplyMoment(SessionState state, PersonaDecisionHint hint)
    {
        if (state == null)
            return;
        state.MomentFocus = hint.MomentFocus;
        state.MomentWaitingForPlayer = hint.ShouldWait;
        state.MomentListening = state.MomentListening || hint.MomentFocus == "听玩家说话";
        state.MomentAboutToSpeak = hint.ShouldSpeak && !hint.ShouldWait;
        state.MomentUncertain = hint.SafetyReason is "native_story" or "native_sleep";
        state.MomentReason = hint.SafetyReason;
    }

    public static bool RepairsAction(ApiResult reply, SessionState state, BridgeConfig config, out string reason)
    {
        reason = "";
        if (reply == null || state == null || config == null || !config.CoherenceGuardEnabled)
            return false;
        if (string.IsNullOrWhiteSpace(reply.Action))
            return false;
        LilithActionType? action = ActionCatalog.TryParseAction(reply.Action);
        if (!action.HasValue)
            return false;

        if (action.Value == LilithActionType.StandUp)
        {
            if (state.Native.IsLieDown)
            {
                action = LilithActionType.LieStandUp;
                reply.Action = action.Value.ToString();
                reason = "stand_up_mapped_to_lie_stand_up";
            }
            else if (state.Native.IsSofaSit)
            {
                action = LilithActionType.SofaStandUp;
                reply.Action = action.Value.ToString();
                reason = "stand_up_mapped_to_sofa_stand_up";
            }
        }

        if (HasRecentCharacterAction(state, action.Value, TimeSpan.FromSeconds(25)))
        {
            reply.Action = "";
            reply.Animation = "";
            reason = "native_action_recently_played_" + action.Value;
            return true;
        }

        if (TouchContextActions.TryGetValue(action.Value, out string? requiredArea)
            && requiredArea != null
            && !HasRecentTouch(state, requiredArea, TimeSpan.FromSeconds(25)))
        {
            reply.Action = "";
            reply.Animation = "";
            reason = "touch_action_without_matching_area_" + requiredArea;
            return true;
        }

        if (state.Native.IsSleep && !SleepSafeActions.Contains(action.Value))
        {
            reply.Action = "drowsy";
            reply.Animation = "";
            reason = "sleep_action_replaced";
            return true;
        }
        if ((state.Native.IsLieDown || state.Native.IsSit || state.Native.IsSofaSit)
            && PoseBlockingActions.Contains(action.Value))
        {
            reply.Action = "";
            reply.Animation = "";
            reason = "pose_action_blocked";
            return true;
        }
        if (action.Value == LilithActionType.ScreenPeek
            && (state.NativeDialogueActive || state.PlayerOptionsShowing || state.Native.IsSleep))
        {
            reply.Action = "";
            reason = "peek_blocked_by_native";
            return true;
        }
        return false;
    }

    private static bool HasRecentCharacterAction(
        SessionState state,
        LilithActionType action,
        TimeSpan window)
    {
        string expected = action.ToString();
        DateTimeOffset cutoff = DateTimeOffset.UtcNow - window;
        foreach (LilithSceneEvent sceneEvent in state.RecentEvents)
        {
            if (sceneEvent.Kind != SceneEventKind.CharacterAction)
                continue;
            if (sceneEvent.Timestamp < cutoff)
                continue;
            if (string.Equals(
                    sceneEvent.Area,
                    expected,
                    StringComparison.OrdinalIgnoreCase)
                || sceneEvent.Details.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }
        return false;
    }

    private static bool HasRecentTouch(
        SessionState state,
        string requiredArea,
        TimeSpan window)
    {
        DateTimeOffset cutoff = DateTimeOffset.UtcNow - window;
        foreach (LilithSceneEvent sceneEvent in state.RecentEvents)
        {
            if (sceneEvent.Kind is not (SceneEventKind.Touch or SceneEventKind.NativeHitArea))
                continue;
            if (sceneEvent.Timestamp < cutoff)
                continue;
            if (NormalizeArea(sceneEvent.Area) == requiredArea)
                return true;
        }
        return false;
    }

    public static bool DetectBodyPartMismatch(ApiResult reply, SessionState state, out string mismatch)
    {
        mismatch = "";
        if (reply == null || state == null)
            return false;
        var recent = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        DateTimeOffset cutoff = DateTimeOffset.UtcNow.AddSeconds(-240);
        foreach (LilithSceneEvent sceneEvent in state.RecentEvents)
        {
            if (sceneEvent.Kind is not (SceneEventKind.Touch or SceneEventKind.NativeHitArea))
                continue;
            if (sceneEvent.Timestamp < cutoff || string.IsNullOrWhiteSpace(sceneEvent.Area))
                continue;
            recent.Add(NormalizeArea(sceneEvent.Area));
        }
        if (recent.Count == 0)
            return false;

        foreach ((string claimed, string normalized) in ClaimedTouchAreas(reply.Text))
        {
            if (!recent.Contains(normalized))
            {
                mismatch = claimed;
                return true;
            }
        }
        return false;
    }

    private static IEnumerable<(string Display, string Normalized)> ClaimedTouchAreas(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            yield break;
        var matches = Regex.Matches(
            text,
            @"(?:摸|碰|戳|捏|拉|按|碰到|摸到|戳到)[^。！？]{0,10}(头|脸|身体|胸口|胸|肩膀|手|裙子|腿|脚)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        foreach (Match match in matches)
        {
            string raw = match.Groups[1].Value;
            string normalized = NormalizeArea(raw);
            yield return (raw, normalized);
        }
    }

    private static string NormalizeArea(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "头" or "head" => "head",
            "脸" or "face" => "face",
            "身体" or "body" or "character" => "body",
            "胸口" or "胸" or "chest" => "chest",
            "肩膀" or "shoulder" => "shoulder",
            "手" or "hand" => "hand",
            "裙子" or "skirt" => "skirt",
            "腿" or "leg" => "leg",
            "脚" or "foot" => "foot",
            _ => value.Trim().ToLowerInvariant()
        };
    }
}
