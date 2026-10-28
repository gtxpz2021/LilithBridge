using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

namespace LilithBridge;

internal static class ActionExecutor
{
    private static readonly Regex NumericVoice = new(@"^\d+$", RegexOptions.Compiled);
    private static readonly HashSet<LilithActionType> LieSafeActions = new()
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
        LilithActionType.DrowsySlump,
        LilithActionType.PillowLie,
        LilithActionType.ShakeLeg,
        LilithActionType.RubFace,
        LilithActionType.RubEyes,
        LilithActionType.RubHead,
        LilithActionType.CoverBlanket,
        LilithActionType.TakeBlanket,
        LilithActionType.TwitchBare,
        LilithActionType.FakeCry,
        LilithActionType.FakeAngry,
        LilithActionType.FakeWronged,
        LilithActionType.Pucker,
        LilithActionType.Snap,
        LilithActionType.Flirt
    };

    private static readonly HashSet<LilithActionType> SofaSafeActions = new()
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
        LilithActionType.Flirt
    };

    private static readonly LilithActionType[] LieFallbacks =
    {
        LilithActionType.Flirt,
        LilithActionType.Snap,
        LilithActionType.ShakeLeg,
        LilithActionType.Mumble,
        LilithActionType.RubEyes,
        LilithActionType.CoverBlanket
    };

    private static readonly LilithActionType[] SofaFallbacks =
    {
        LilithActionType.Flirt,
        LilithActionType.Snap,
        LilithActionType.ShakeLeg,
        LilithActionType.Think,
        LilithActionType.Pucker
    };

    private static readonly List<ActionStep> PendingSteps = new();
    private static readonly List<DeferredExecution> DeferredExecutions = new();
    private static float _nextStepAt;
    private static LilithActionType _lastReplayAction;
    private static float _lastReplayActionAt;
    private static readonly System.Random WaitingRng = new();
    private static float _nextWaitingAt;
    private static bool _waitingRequested;
    private static bool _waitingActive;
    private const float ReplayWindowSeconds = 20f;

    private sealed record DeferredExecution(
        ApiResult Reply,
        LilithActionType? Action,
        bool AlreadyHandledByNativeSay);

    public static void Execute(
        ApiResult reply,
        LilithActionType? action,
        bool alreadyHandledByNativeSay,
        bool deferWhileNativeBusy = true)
    {
        ActionPathDiagnostic.TraceExecutor(action, reply);
        if (deferWhileNativeBusy
            && NativeBusyNow()
            && (action.HasValue
                || (reply.Actions is { Count: > 0 })
                || !string.IsNullOrWhiteSpace(reply.Animation)))
        {
            DeferredExecutions.Add(new DeferredExecution(reply, action, alreadyHandledByNativeSay));
            return;
        }
        if (NativePoseGate.IsPoseBlocked)
        {
            LilithActionType? resolved = action;
            bool isPoseTransition = resolved.HasValue
                && resolved.Value is LilithActionType.LieStandUp
                    or LilithActionType.SofaStandUp
                    or LilithActionType.SofaDoubt
                    or LilithActionType.PillowLie;
            if (resolved.HasValue
                && resolved.Value is not (LilithActionType.None or LilithActionType.Idle)
                && !isPoseTransition
                && !IsPoseSafe(resolved.Value))
                resolved = FindPoseFallback(resolved.Value);
            if (resolved.HasValue
                && (isPoseTransition || IsPoseSafe(resolved.Value))
                && HasNativeAnimation(resolved.Value))
            {
                if (ShouldSkipReplay(resolved.Value))
                {
                    NativeEventLog.Trace("action.replay_skipped", "mod", "lilith",
                        resolved.Value.ToString(), "pose_safe");
                }
                else
                {
                    try
                    {
                        CharacterController? controller =
                            UnityEngine.Object.FindObjectOfType<CharacterController>(true);
                        controller?.PlayLilithAction(resolved.Value, true);
                        RecordNativeAction(resolved.Value);
                        LogActionExecuted(resolved.Value, "pose_safe");
                        if (!string.IsNullOrWhiteSpace(reply.Emotion))
                            controller?.PlayEmotion(reply.Emotion, true, true, true);
                        NativeEventLog.Trace("action.pose_safe", "mod", "lilith",
                            "pose-safe animation played",
                            "requested=" + (action?.ToString() ?? reply.Animation)
                            + ";played=" + resolved.Value);
                    }
                    catch (Exception error)
                    {
                        NativeEventLog.Trace("action.pose_safe_error", "mod", "lilith",
                            resolved.Value.ToString(), error.Message);
                    }
                    if (!string.IsNullOrWhiteSpace(reply.Voice))
                        PlayVoice(reply.Voice);
                }
            }
            else
            {
                // Keep the current pose while Lilith talks. The original game
                // can still show her bubble; only the requested animation is
                // skipped. Sequences are still queued below when they are
                // pose-safe.
                if (!string.IsNullOrWhiteSpace(reply.Voice))
                    PlayVoice(reply.Voice);
                NativeEventLog.Trace("action.pose_kept", "mod", "lilith",
                    "animation skipped while lying/sitting",
                    "action=" + (action?.ToString() ?? reply.Animation));
            }
        }
        else
        {
            LilithActionType? playAction = action;
            if (playAction.HasValue && !HasNativeAnimation(playAction.Value))
            {
                NativeEventLog.Trace("action.capability_skip", "mod", "lilith",
                    playAction.Value.ToString(),
                    "not available in current pose/clothing");
                playAction = null;
            }
            try
            {
                CharacterController? controller = UnityEngine.Object.FindObjectOfType<CharacterController>(true);
                if (!alreadyHandledByNativeSay && playAction.HasValue && controller != null)
                {
                    if (playAction.Value is not (LilithActionType.None or LilithActionType.Idle))
                    {
                        if (ShouldSkipReplay(playAction.Value))
                        {
                            NativeEventLog.Trace("action.replay_skipped", "mod", "lilith",
                                playAction.Value.ToString(), "model_reply");
                            return;
                        }
                        if (playAction.Value == LilithActionType.ScreenPeek)
                        {
                            if (!NativeWindowPeekDirector.TryPeek(controller, playAction.Value.ToString()))
                            {
                                if (!NativeWindowPeekDirector.IsSuppressed)
                                {
                                    controller.PlayLilithAction(LilithActionType.ScreenPeek, true);
                                    RecordNativeAction(LilithActionType.ScreenPeek);
                                    NativeEventLog.Trace("action.animation", "mod", "lilith", "ScreenPeek");
                                }
                            }
                            else
                            {
                                RecordNativeAction(LilithActionType.ScreenPeek);
                            }
                        }
                        else
                        {
                            controller.PlayLilithAction(playAction.Value, true);
                            RecordNativeAction(playAction.Value);
                            LogActionExecuted(playAction.Value, "model");
                            NativeEventLog.Trace("action.animation", "mod", "lilith", playAction.Value.ToString());
                        }
                    }
                }
                else if (playAction == null && !string.IsNullOrWhiteSpace(reply.Animation) && controller != null)
                {
                    string animation = reply.Animation.Trim();
                    if (GameHasAnimation(animation))
                    {
                        controller.PlayAnimation(animation, false, 0);
                        NativeEventLog.Trace("action.animation", "mod", "lilith", animation);
                    }
                    else
                    {
                        NativeEventLog.Trace("action.rejected", "mod", "lilith", animation, "unknown animation");
                    }
                }

                if (!alreadyHandledByNativeSay && !string.IsNullOrWhiteSpace(reply.Emotion) && controller != null)
                {
                    try
                    {
                        controller.PlayEmotion(reply.Emotion, true, true, true);
                    }
                    catch
                    {
                        // Emotion is cosmetic.
                    }
                }
            }
            catch (Exception error)
            {
                NativeEventLog.Trace("action.error", "mod", "lilith", action?.ToString() ?? reply.Animation, error.Message);
            }

            if (!string.IsNullOrWhiteSpace(reply.Voice))
                PlayVoice(reply.Voice);
            else if (!alreadyHandledByNativeSay && playAction.HasValue)
                PlayActionVoice(playAction.Value);
        }

        if (!alreadyHandledByNativeSay)
            QueueSteps(reply.Actions);
    }

    public static void QueueSequence(List<ActionStep>? steps)
    {
        if (steps == null || steps.Count == 0)
            return;
        var safeSteps = new List<ActionStep>();
        int totalDelayMs = 0;
        foreach (ActionStep step in steps)
        {
            string name = string.IsNullOrWhiteSpace(step.Type) ? step.Animation : step.Type;
            if (!ActionCatalog.TryParseAllowedAction(name, out LilithActionType parsed)
                || !ActionCapability.IsExposed(parsed))
            {
                NativeEventLog.Trace("action.sequence_skip", "mod", "lilith",
                    name, "not in capability set");
                continue;
            }
            if (safeSteps.Count >= 8)
                break;
            totalDelayMs += Math.Clamp(step.DelayMs, 0, 5000)
                + Math.Clamp(step.DurationMs, 300, 15000);
            if (totalDelayMs > 20000)
                break;
            step.Type = parsed.ToString();
            step.Animation = "";
            step.DelayMs = Math.Clamp(step.DelayMs, 0, 5000);
            step.DurationMs = Math.Clamp(step.DurationMs, 300, 15000);
            safeSteps.Add(step);
        }
        if (safeSteps.Count > 0)
            QueueSteps(safeSteps);
    }

    private static LilithActionType? FindPoseFallback(LilithActionType requested)
    {
        try
        {
            LilithStateManager? state =
                UnityEngine.Object.FindObjectOfType<LilithStateManager>(true);
            if (state == null)
                return null;
            LilithActionType[] pool = state.IsSofaSit ? SofaFallbacks : LieFallbacks;
            foreach (LilithActionType candidate in pool)
            {
                if (IsPoseSafe(candidate) && HasNativeAnimation(candidate))
                    return candidate;
            }
        }
        catch
        {
            // Fallback is cosmetic; never break the reply.
        }
        return null;
    }

    private static bool IsPoseSafe(LilithActionType action)
    {
        try
        {
            LilithStateManager? state =
                UnityEngine.Object.FindObjectOfType<LilithStateManager>(true);
            if (state == null)
                return false;
            if (state.IsLieDown)
                return LieSafeActions.Contains(action);
            if (state.IsSofaSit)
                return SofaSafeActions.Contains(action);
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static void PlayActionVoice(LilithActionType action)
    {
        if (action is LilithActionType.None or LilithActionType.Idle)
            return;
        try
        {
            AudioManager.PlayActionSEOrVoice(action, action.ToString());
            NativeEventLog.Trace("action.voice_auto", "mod", "lilith", action.ToString());
        }
        catch (Exception error)
        {
            NativeEventLog.Trace("action.voice_error", "mod", "lilith", action.ToString(), error.Message);
        }
    }

    public static void RecordNativeAction(LilithActionType action)
    {
        if (action is LilithActionType.None or LilithActionType.Idle)
            return;
        _lastReplayAction = action;
        _lastReplayActionAt = Time.unscaledTime;
    }

    private static void LogActionExecuted(
        LilithActionType action,
        string source)
    {
        try
        {
            NativeEventLog.Trace(
                "action.executed",
                "mod",
                "lilith",
                action.ToString(),
                "source=" + source
                + ";pose=" + DescribeNativePose()
                + ";clothing=" + DescribeNativeClothing());
        }
        catch
        {
            // Logging must never break an animation.
        }
    }

    public static bool HasAmbientWaitingAnimation(LilithActionType action)
    {
        return action is
            LilithActionType.Think
            or LilithActionType.RubEyes
            or LilithActionType.TiltHead
            or LilithActionType.SmallYawn
            or LilithActionType.Blink;
    }

    public static bool HasNativeAnimation(LilithActionType action)
    {
        if (action is LilithActionType.None or LilithActionType.Idle)
            return true;
        try
        {
            RuntimeActionCapability capability =
                RuntimeActionCapabilityResolver.Resolve(
                    action,
                    DescribeNativePose(),
                    DescribeNativeClothing());
            return capability.Availability != ActionAvailability.Unavailable;
        }
        catch
        {
            return false;
        }
    }

    private static string DescribeNativePose()
    {
        try
        {
            LilithStateManager? state =
                UnityEngine.Object.FindObjectOfType<LilithStateManager>(true);
            if (state == null)
                return "idle_or_standing";
            if (state.IsSleep)
                return "sleeping";
            if (state.IsLieDown)
                return "lying_down";
            if (state.IsSofaSit)
                return "sofa_sit";
            if (state.IsSit)
                return "sitting";
            return "idle_or_standing";
        }
        catch
        {
            return "idle_or_standing";
        }
    }

    private static string DescribeNativeClothing()
    {
        try
        {
            LilithStateManager? state =
                UnityEngine.Object.FindObjectOfType<LilithStateManager>(true);
            return state is null ? "casual" : state.ClothingState.ToString();
        }
        catch
        {
            return "casual";
        }
    }

    public static LilithActionType? ResolveActionForPose(LilithActionType? requested)
    {
        if (!requested.HasValue || !NativePoseGate.IsPoseBlocked)
            return requested;
        if (IsPoseSafe(requested.Value))
            return requested;
        return FindPoseFallback(requested.Value);
    }

    private static bool ShouldSkipReplay(LilithActionType action)
    {
        if (action is LilithActionType.None or LilithActionType.Idle or LilithActionType.Blink)
            return false;
        return action == _lastReplayAction
            && Time.unscaledTime - _lastReplayActionAt <= ReplayWindowSeconds;
    }

    public static void Tick(float now)
    {
        TryApplyWaiting();
        if (NativeBusyNow())
            return;
        if (DeferredExecutions.Count > 0)
        {
            DeferredExecution deferred = DeferredExecutions[0];
            DeferredExecutions.RemoveAt(0);
            Execute(deferred.Reply, deferred.Action, deferred.AlreadyHandledByNativeSay);
            return;
        }
        if (PendingSteps.Count == 0 || now < _nextStepAt)
            return;
        ActionStep step = PendingSteps[0];
        PendingSteps.RemoveAt(0);
        ExecuteStep(step);
        _nextStepAt = now + Math.Max(0.1f, Math.Min(step.DelayMs / 1000f, 5f));
    }

    public static void Clear()
    {
        PendingSteps.Clear();
        DeferredExecutions.Clear();
        _nextStepAt = 0f;
        SetWaiting(false);
    }

    public static void SetWaiting(bool requested, SessionState? state = null)
    {
        if (requested && !_waitingRequested)
            _nextWaitingAt = Time.unscaledTime + 0.85f;
        _waitingRequested = requested;
        TryApplyWaiting(state);
    }

    private static void TryApplyWaiting(SessionState? state = null)
    {
        try
        {
            CharacterController? controller = UnityEngine.Object.FindObjectOfType<CharacterController>(true);
            if (_waitingRequested
                && !_waitingActive
                && !NativeBusyNow()
                && !NativePoseGate.IsPoseBlocked
                && Time.unscaledTime >= _nextWaitingAt
                && controller != null)
            {
                LilithActionType waitingAction = PickWaitingAction(state);
                controller.PlayLilithAction(waitingAction, true);
                RecordNativeAction(waitingAction);
                _waitingActive = true;
                NativeEventLog.Trace("action.waiting_start", "mod", "lilith", waitingAction.ToString());
            }
            else if (!_waitingRequested && _waitingActive && controller != null)
            {
                controller.PlayLilithAction(LilithActionType.Idle, true);
                _waitingActive = false;
                NativeEventLog.Trace("action.waiting_end", "mod", "lilith", "Idle");
            }
        }
        catch (Exception error)
        {
            NativeEventLog.Trace("action.waiting_error", "mod", "lilith", error.Message);
            _waitingActive = false;
        }
    }

    private static LilithActionType PickWaitingAction(SessionState? state)
    {
        if (state == null)
            return LilithActionType.Think;
        double roll = WaitingRng.NextDouble();
        if (state.Emotions.Tension > 0.62 && roll < 0.48)
            return LilithActionType.RubEyes;
        if (state.Emotions.Warmth > 0.64
            && state.Emotions.Closeness > 0.54
            && roll < 0.35)
            return LilithActionType.TiltHead;
        if (state.Mood == MoodKind.Shy && roll < 0.35)
            return LilithActionType.Blink;
        return LilithActionType.Think;
    }

    private static bool NativeBusyNow()
    {
        try
        {
            bool quietForExternal = NativeStateInspector.IsGamingHidden
                || NativeStateInspector.IsDisappearPending
                || NativeStateInspector.IsDissipatePending
                || NativeStateInspector.IsNeglectHidden
                || NativeStateInspector.IsForegroundFullscreen;
            return quietForExternal
                || (BridgeController.Instance?.IsNativeBusyForActions() == true)
                || (BridgeController.Instance?.IsAiBubbleActive == true);
        }
        catch
        {
            return true;
        }
    }

    private static void QueueSteps(List<ActionStep> steps)
    {
        if (steps == null || steps.Count == 0)
            return;
        PendingSteps.AddRange(steps);
        if (_nextStepAt <= 0f)
            _nextStepAt = 0f;
    }

    private static void ExecuteStep(ActionStep step)
    {
        string nativeName = string.IsNullOrWhiteSpace(step.Type) ? step.Animation : step.Type;
        if (ActionCatalog.TryParseAllowedAction(nativeName, out LilithActionType nativeAction)
            && ActionCapability.IsExposed(nativeAction)
            && HasNativeAnimation(nativeAction))
        {
            try
            {
                CharacterController? controller =
                    UnityEngine.Object.FindObjectOfType<CharacterController>(true);
                if (controller != null)
                {
                    controller.PlayLilithAction(nativeAction, true);
                    RecordNativeAction(nativeAction);
                    LogActionExecuted(nativeAction, "sequence");
                    NativeEventLog.Trace("action.sequence_step", "mod", "lilith",
                        nativeAction.ToString() + ";delay=" + step.DelayMs);
                }
            }
            catch
            {
                // Ignore a malformed action step.
            }
        }
        else if (!string.IsNullOrWhiteSpace(step.Animation))
        {
            try
            {
                CharacterController? controller = UnityEngine.Object.FindObjectOfType<CharacterController>(true);
                string animation = step.Animation.Trim();
                if (controller != null && GameHasAnimation(animation))
                    controller.PlayAnimation(animation, false, 0);
            }
            catch
            {
                // Ignore a malformed action step.
            }
        }
        if (!string.IsNullOrWhiteSpace(step.Voice))
            PlayVoice(step.Voice);
    }

    private static bool GameHasAnimation(string name)
    {
        try
        {
            LilithStateManager? state = UnityEngine.Object.FindObjectOfType<LilithStateManager>(true);
            return state != null && state.HasAnimation(name);
        }
        catch
        {
            return false;
        }
    }

    private static void PlayVoice(string voice)
    {
        string value = voice.Trim();
        if (value.Length == 0)
            return;
        try
        {
            if (value.StartsWith("voice:", StringComparison.OrdinalIgnoreCase))
                value = value[6..].Trim();

            LilithActionType? actionVoice = ActionCatalog.TryParseAction(value);
            if (NumericVoice.IsMatch(value))
            {
                AudioManager.PlayVoiceBySoundId(value, true);
                NativeEventLog.Trace("action.voice", "mod", "lilith", value);
            }
            else if (actionVoice.HasValue)
            {
                AudioManager.PlayActionSEOrVoice(actionVoice.Value, value);
                NativeEventLog.Trace("action.voice", "mod", "lilith", value);
            }
            else if (value.StartsWith("se:", StringComparison.OrdinalIgnoreCase))
            {
                AudioManager.PlaySound(value[3..].Trim());
                NativeEventLog.Trace("action.se", "mod", "lilith", value);
            }
            else
            {
                AudioManager.PlaySound(value);
                NativeEventLog.Trace("action.se", "mod", "lilith", value);
            }
        }
        catch (Exception error)
        {
            NativeEventLog.Trace("action.voice_error", "mod", "lilith", value, error.Message);
        }
    }
}
