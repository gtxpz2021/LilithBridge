using System;
using UnityEngine;

namespace LilithBridge;

internal static class MicroReactionDirector
{
    private static float _nextAllowedAt;
    private static bool _initialized;
    private static readonly System.Random Rng = new();

    public static void Tick(SessionState state, float now, BridgeConfig config)
    {
        try
        {
            if (!_initialized)
            {
                _initialized = true;
                _nextAllowedAt = now + Math.Max(6f, config.MicroGestureCooldownSeconds * 0.45f);
            }
            if (!config.MicroGesturesEnabled)
                return;
            if (BridgeController.Instance?.IsNativeBusyForActions() == true)
                return;
            if (state.MomentListening
                || state.MomentAboutToSpeak
                || state.NativeDialogueActive
                || state.PlayerOptionsShowing
                || state.BusyOrAwaitingResponse
                || state.Native.IsInteracting
                || state.Native.IsDragging)
                return;
            if (now < _nextAllowedAt)
                return;
            if (NativePoseGate.IsPoseBlocked)
                return;
            if (NativeStateInspector.IsNativeQuietForMod())
                return;
            if (NativeWindowPeekDirector.IsSuppressed)
                return;
            if (HasRecentEvent(state, SceneEventKind.DialogueEnd, TimeSpan.FromSeconds(8)))
                return;
            if (Rng.NextDouble() > config.MicroReactionRandomChance)
                return;

            if (!TryPickAction(state, out LilithActionType? action, out string reason))
            {
                _nextAllowedAt = now + Math.Max(6f, config.MicroGestureCooldownSeconds * 0.55f);
                return;
            }

            CharacterController? controller = UnityEngine.Object.FindObjectOfType<CharacterController>(true);
            if (controller == null || !action.HasValue)
                return;
            if (!ActionCapability.IsPoseSafe(action.Value, state))
            {
                NativeEventLog.Trace("micro.capability_skip", "mod", "lilith",
                    action.Value.ToString(), "not usable for current pose");
                return;
            }
            if (action.Value == LilithActionType.ScreenPeek)
            {
                if (!NativeWindowPeekDirector.TryPeek(controller, reason))
                {
                    controller.PlayLilithAction(LilithActionType.ScreenPeek, true);
                    ActionExecutor.RecordNativeAction(LilithActionType.ScreenPeek);
                }
                else
                {
                    ActionExecutor.RecordNativeAction(LilithActionType.ScreenPeek);
                }
            }
            else
            {
                controller.PlayLilithAction(action.Value, true);
                ActionExecutor.RecordNativeAction(action.Value);
            }
            NativeEventLog.Trace("micro.reaction", "mod", "lilith",
                action.Value.ToString(), reason);
            _nextAllowedAt = now + NextCooldown(config);
        }
        catch (Exception error)
        {
            NativeEventLog.Trace("micro.reaction_error", "mod", "lilith", error.Message);
        }
    }

    private static bool TryPickAction(
        SessionState state,
        out LilithActionType? action,
        out string reason)
    {
        action = null;
        reason = "";

        if (HasRecentEvent(state, SceneEventKind.AppForeground, TimeSpan.FromSeconds(8)))
        {
            action = Rng.NextDouble() < 0.6
                ? LilithActionType.ScreenPeek
                : LilithActionType.Turnhead;
            reason = "app_changed";
            return true;
        }
        if (state.PlayerRecentlyReturned)
        {
            action = Rng.NextDouble() < 0.65
                ? LilithActionType.LazyWave
                : LilithActionType.Greet;
            reason = "player_returned";
            state.PlayerRecentlyReturned = false;
            return true;
        }
        if (state.TimePhase == TimePhase.LateNight)
        {
            action = LilithActionType.Blink;
            reason = "late_night";
            return true;
        }
        if (state.TimePhase == TimePhase.Morning)
        {
            action = LilithActionType.Stretch;
            reason = "morning";
            return true;
        }
        if (state.Emotions.Tension > 0.62)
        {
            action = Rng.NextDouble() < 0.5
                ? LilithActionType.RubEyes
                : LilithActionType.Tense;
            reason = "emotional_tension_residue";
            return true;
        }
        if (state.Emotions.Warmth > 0.62 && state.Emotions.Closeness > 0.54)
        {
            action = Rng.Next(3) switch
            {
                0 => LilithActionType.TiltHead,
                1 => LilithActionType.Blink,
                _ => LilithActionType.Blush
            };
            reason = "warm_residue";
            return true;
        }
        if (state.Emotions.Curiosity > 0.66)
        {
            double roll = Rng.NextDouble();
            action = roll switch
            {
                < 0.45 => LilithActionType.TiltHead,
                < 0.80 => LilithActionType.Turnhead,
                _ => LilithActionType.Blink
            };
            reason = "curious_residue";
            return true;
        }
        if (state.Mood == MoodKind.Shy)
        {
            action = LilithActionType.Blink;
            reason = "shy";
            return true;
        }

        action = Rng.Next(4) switch
        {
            0 => LilithActionType.TiltHead,
            1 => LilithActionType.LookAround,
            2 => LilithActionType.Turnhead,
            _ => LilithActionType.Blink
        };
        reason = "idle_attention";
        return true;
    }

    private static float NextCooldown(BridgeConfig config)
    {
        float baseSeconds = Math.Max(8f, config.MicroGestureCooldownSeconds);
        return baseSeconds * (0.75f + (float)Rng.NextDouble() * 0.75f);
    }

    private static bool HasRecentEvent(
        SessionState state,
        SceneEventKind kind,
        TimeSpan window)
    {
        for (int i = state.RecentEvents.Count - 1; i >= 0; i--)
        {
            LilithSceneEvent sceneEvent = state.RecentEvents[i];
            if (sceneEvent.Kind == kind)
                return DateTimeOffset.UtcNow - sceneEvent.Timestamp <= window;
        }
        return false;
    }
}
