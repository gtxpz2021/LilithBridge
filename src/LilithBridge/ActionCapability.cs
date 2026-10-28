using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace LilithBridge;

/// <summary>
/// Runtime view of which actions are actually useful for Lilith's current
/// pose. The model is told about this list instead of the full static enum,
/// and every execution path consults it before calling the native character.
///
/// This is deliberately independent from UnityEngine so the same rules can be
/// exercised in offline tests.
/// </summary>
internal static class ActionCapability
{
    private static readonly HashSet<LilithActionType> SystemOnly = new()
    {
        LilithActionType.None,
        LilithActionType.Drag,
        LilithActionType.Falling,
        LilithActionType.Falled,
        LilithActionType.Walk,
        LilithActionType.WalkEnd,
        LilithActionType.Intro
    };

    // Enum values exist in the original game but have no static animation
    // string in LilithAnimName. They must never be exposed or executed.
    private static readonly HashSet<LilithActionType> NoStaticAnimation = new()
    {
        LilithActionType.IdleSide,
        LilithActionType.IdleAngle,
        LilithActionType.SoftWish,
        LilithActionType.LazyReach,
        LilithActionType.SleepyReach,
        LilithActionType.BlanketHold,
        LilithActionType.BlanketBlush,
        LilithActionType.BlanketPeek,
        LilithActionType.BlanketHug
    };

    private static readonly HashSet<LilithActionType> SittingSafe = new()
    {
        LilithActionType.Blink,
        LilithActionType.Pout,
        LilithActionType.SternSmile,
        LilithActionType.Tsundere,
        LilithActionType.ShyGiggle,
        LilithActionType.Blush,
        LilithActionType.Squint,
        LilithActionType.TiltHead,
        LilithActionType.Turnhead,
        LilithActionType.SmallYawn,
        LilithActionType.PerkYawn,
        LilithActionType.HappySigh,
        LilithActionType.LookAround,
        LilithActionType.Confuse,
        LilithActionType.Think,
        LilithActionType.Mumble,
        LilithActionType.Tense,
        LilithActionType.HalfAwake,
        LilithActionType.Drowsy,
        LilithActionType.ShakeLeg,
        LilithActionType.RubFace,
        LilithActionType.RubEyes,
        LilithActionType.RubHead,
        LilithActionType.FakeCry,
        LilithActionType.FakeAngry,
        LilithActionType.FakeWronged,
        LilithActionType.Pucker,
        LilithActionType.Snap,
        LilithActionType.Flirt,
        LilithActionType.SofaDoubt,
        LilithActionType.SofaStandUp
    };

    private static readonly HashSet<LilithActionType> SofaSafe = new(SittingSafe);

    private static readonly HashSet<LilithActionType> LyingSafe = new()
    {
        LilithActionType.Blink,
        LilithActionType.Pout,
        LilithActionType.SternSmile,
        LilithActionType.Tsundere,
        LilithActionType.ShyGiggle,
        LilithActionType.Blush,
        LilithActionType.Squint,
        LilithActionType.TiltHead,
        LilithActionType.Turnhead,
        LilithActionType.SmallYawn,
        LilithActionType.PerkYawn,
        LilithActionType.HappySigh,
        LilithActionType.LookAround,
        LilithActionType.Confuse,
        LilithActionType.Think,
        LilithActionType.Mumble,
        LilithActionType.Tense,
        LilithActionType.HalfAwake,
        LilithActionType.Drowsy,
        LilithActionType.ShakeLeg,
        LilithActionType.RubFace,
        LilithActionType.RubEyes,
        LilithActionType.RubHead,
        LilithActionType.FakeCry,
        LilithActionType.FakeAngry,
        LilithActionType.FakeWronged,
        LilithActionType.Pucker,
        LilithActionType.Snap,
        LilithActionType.Flirt,
        LilithActionType.PillowLie,
        LilithActionType.CoverBlanket,
        LilithActionType.TakeBlanket,
        LilithActionType.TwitchBare,
        LilithActionType.LieStandUp
    };

    private static readonly HashSet<LilithActionType> SleepingSafe = new()
    {
        LilithActionType.None,
        LilithActionType.Idle,
        LilithActionType.Sleep,
        LilithActionType.Drowsy,
        LilithActionType.DrowsySlump,
        LilithActionType.HalfAwake,
        LilithActionType.Mumble,
        LilithActionType.Blink,
        LilithActionType.SmallYawn
    };

    private static readonly HashSet<LilithActionType> TransitionActions = new()
    {
        LilithActionType.StandUp,
        LilithActionType.LieStandUp,
        LilithActionType.SofaStandUp,
        LilithActionType.SofaDoubt,
        LilithActionType.PillowLie
    };

    public static bool IsExposed(LilithActionType action)
    {
        return !SystemOnly.Contains(action)
            && !NoStaticAnimation.Contains(action)
            && ActionCatalog.TryParseAllowedAction(action.ToString(), out _);
    }

    public static bool IsPoseSafe(LilithActionType action, SessionState? state = null)
    {
        if (!IsExposed(action))
            return false;
        if (state == null)
            return true;

        if (state.Native.IsSleep)
            return SleepingSafe.Contains(action) && !TransitionActions.Contains(action);
        if (state.Native.IsLieDown)
            return LyingSafe.Contains(action) || TransitionActions.Contains(action);
        if (state.Native.IsSofaSit)
            return SofaSafe.Contains(action) || TransitionActions.Contains(action);
        if (state.Native.IsSit)
            return SittingSafe.Contains(action) || TransitionActions.Contains(action);
        return true;
    }

    public static bool IsPoseTransition(LilithActionType action)
    {
        return TransitionActions.Contains(action);
    }

    public static LilithActionType? ResolveForPose(
        LilithActionType? requested,
        SessionState? state = null)
    {
        if (!requested.HasValue || !IsPoseSafe(requested.Value, state))
            return null;
        return requested;
    }

    public static string[] AvailableActionNames(SessionState? state = null)
    {
        return ActionCatalog.AllowedActionNames()
            .Where(name => ActionCatalog.TryParseAction(name) is { } parsed
                && IsPoseSafe(parsed, state))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static string BuildPromptList(SessionState? state = null)
    {
        string[] names = AvailableActionNames(state);
        if (names.Length == 0)
            return "idle";
        return string.Join(", ", names);
    }

    public static string DescribePose(SessionState? state)
    {
        if (state == null)
            return "unknown";
        if (state.Native.IsSleep)
            return "sleeping";
        if (state.Native.IsLieDown)
            return "lying_down";
        if (state.Native.IsSofaSit)
            return "sofa_sit";
        if (state.Native.IsSit)
            return "sitting";
        return "idle_or_standing";
    }

    public static void StripUnavailable(ApiResult reply, SessionState? state)
    {
        if (reply == null)
            return;
        if (!string.IsNullOrWhiteSpace(reply.Action))
        {
            if (!ActionCatalog.TryParseAllowedAction(reply.Action, out LilithActionType action)
                || !IsPoseSafe(action, state))
            {
                NativeEventLog.Trace(
                    "action.capability_stripped",
                    "mod",
                    "lilith",
                    reply.Action,
                    "pose=" + DescribePose(state));
                reply.Action = "";
                reply.Animation = "";
            }
        }
        if (reply.Actions == null || reply.Actions.Count == 0)
            return;

        var kept = new List<ActionStep>();
        int steps = 0;
        foreach (ActionStep step in reply.Actions)
        {
            if (steps >= 8)
                break;
            string name = string.IsNullOrWhiteSpace(step.Type)
                ? step.Animation
                : step.Type;
            if (!ActionCatalog.TryParseAllowedAction(name, out LilithActionType action)
                || !IsPoseSafe(action, state))
            {
                NativeEventLog.Trace(
                    "action.sequence_step_stripped",
                    "mod",
                    "lilith",
                    name,
                    "pose=" + DescribePose(state));
                continue;
            }
            step.Type = action.ToString();
            step.Animation = "";
            step.DelayMs = Math.Clamp(step.DelayMs, 0, 5000);
            step.DurationMs = Math.Clamp(step.DurationMs, 300, 15000);
            kept.Add(step);
            steps++;
        }
        reply.Actions = kept;
    }

    public static LilithActionType? ParseSequenceStep(string? value, SessionState? state)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        string candidate = value.Trim();
        if (candidate.StartsWith("action:", StringComparison.OrdinalIgnoreCase))
            candidate = candidate[7..].Trim();
        int separator = candidate.IndexOfAny(new[] { ':', ',', '|', ' ' });
        if (separator > 0)
            candidate = candidate[..separator].Trim();
        if (!ActionCatalog.TryParseAllowedAction(candidate, out LilithActionType parsed))
            return null;
        return IsPoseSafe(parsed, state) ? parsed : null;
    }

    public static string DescribeAllowedHint(SessionState? state)
    {
        var builder = new StringBuilder();
        builder.Append("[当前姿态] ").Append(DescribePose(state));
        builder.Append("。本次可用动作：").Append(BuildPromptList(state));
        builder.Append("。");
        if (!IsPoseSafe(LilithActionType.Stretch, state))
            builder.Append(" 当前姿态下不要写 stretch 或会强制站起的动作。");
        return builder.ToString();
    }
}
