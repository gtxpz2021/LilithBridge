using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Il2Action = Il2CppSystem.Action;

namespace LilithBridge;

/// <summary>
/// Uses the original game's peek lifecycle instead of replaying a generic
/// ScreenPeek animation. The original flow hides Lilith, peeks from the
/// screen edge, then returns to idle; this director schedules the same
/// CharacterController methods while keeping the mod's event flow intact.
/// </summary>
internal static class NativeWindowPeekDirector
{
    private const float HideDelaySeconds = 0.7f;
    private const float PeekHoldSeconds = 2.2f;
    private const float FinalizeDelaySeconds = 4.0f;

    private static readonly List<object> KeptDelegates = new();
    private static CharacterController? _controller;
    private static ActiveWindowCollider? _windowColliders;
    private static bool _windowCollidersWereEnabled;
    private static LilithStateManager? _poseState;
    private static LilithClothingState _savedClothing = LilithClothingState.Casual;
    private static bool _wasLieDown;
    private static bool _wasSofaSit;
    private static Phase _phase;
    private static float _nextAt;
    private static float _suppressUntil;
    private static string _reason = "";
    private static bool _comebackStarted;

    private enum Phase
    {
        Idle,
        Entering,
        Holding,
        Ending,
        Finalizing
    }

    public static bool IsActive => _phase != Phase.Idle;

    public static bool IsSuppressed =>
        Time.unscaledTime < _suppressUntil;

    public static void SuppressFor(float seconds)
    {
        float until = Time.unscaledTime + Math.Max(0f, seconds);
        _suppressUntil = Math.Max(_suppressUntil, until);
    }

    public static bool TryPeek(CharacterController controller, string reason)
    {
        if (_phase != Phase.Idle || controller == null)
            return false;
        if (IsSuppressed)
        {
            NativeEventLog.Trace("window_peek.suppressed", "mod", "lilith",
                "screen peek suppressed by state-transition cooldown",
                "reason=" + reason
                + ";left=" + Math.Max(0f, _suppressUntil - Time.unscaledTime).ToString("0.0"));
            return false;
        }
        if (controller.IsScreenPeeking || controller.IsNeglectHidden)
            return false;

        LilithStateManager? poseState =
            UnityEngine.Object.FindObjectOfType<LilithStateManager>(true);
        _poseState = poseState;
        _savedClothing = poseState?.ClothingState ?? LilithClothingState.Casual;
        _wasLieDown = poseState?.IsLieDown ?? false;
        _wasSofaSit = poseState?.IsSofaSit ?? false;

        if (NativePoseGate.IsPoseBlocked)
        {
            NativePoseGate.Request(() => TryPeek(controller, reason));
            return true;
        }
        if (NativeStateInspector.IsNativeQuietForMod())
        {
            NativeEventLog.Trace("window_peek.native_quiet", "mod", "lilith",
                "native quiet state skipped screen peek", "reason=" + reason);
            _poseState = null;
            return false;
        }

        try
        {
            // Let the native window feeder refresh before the peek uses it.
            ActiveWindowCollider? windows =
                UnityEngine.Object.FindObjectOfType<ActiveWindowCollider>(true);
            if (windows != null)
            {
                _windowColliders = windows;
                _windowCollidersWereEnabled = windows.WindowCollidersEnabled;
                windows.EnableForegroundWindowCollider();
                windows.RefreshTopEdgeExposure();
                bool foundSeat = windows.TryGetUnobstructedWindowBoxTopCenterWorld(
                    out Vector3 windowTop,
                    out bool blockedByHeadroom,
                    0.15f,
                    2f,
                    80f);
                NativeEventLog.Trace("window_peek.native_windows", "mod", "system",
                    "native window feeder refreshed",
                    "foundSeat=" + foundSeat
                    + ";blockedByHeadroom=" + blockedByHeadroom
                    + ";top=" + (foundSeat ? windowTop.ToString("F2") : "none"));
            }

            _controller = controller;
            _phase = Phase.Entering;
            _nextAt = Time.unscaledTime + HideDelaySeconds;
            _reason = reason;
            _comebackStarted = false;

            // EnterNeglectHide records the return position and makes the
            // native peek sequence start from the hidden side.
            controller.EnterNeglectHide();
            NativeEventLog.Trace("window_peek.queued", "mod", "lilith",
                "native window peek queued", "reason=" + reason);
            return true;
        }
        catch (Exception error)
        {
            NativeEventLog.Trace("window_peek.queued_error", "mod", "lilith",
                "native window peek queue failed", error.Message);
            _phase = Phase.Idle;
            _controller = null;
            RestoreWindowColliders();
            RestoreNativeOutfit();
            return false;
        }
    }

    public static void Tick(float now)
    {
        if (_phase == Phase.Idle || _controller == null)
            return;

        try
        {
            switch (_phase)
            {
                case Phase.Entering:
                    if (now < _nextAt)
                        return;
                    _phase = Phase.Holding;
                    if (!_controller.PlayScreenPeekFromLeft(ToIl2Action(OnPeekEntered)))
                    {
                        FallbackToClassic();
                        return;
                    }
                    _nextAt = now + PeekHoldSeconds;
                    NativeEventLog.Trace("window_peek.entered", "mod", "lilith",
                        "native screen peek started", "reason=" + _reason);
                    break;

                case Phase.Holding:
                    if (now < _nextAt)
                        return;
                    _phase = Phase.Ending;
                    _controller.PlayPeekEnd(ToIl2Action(OnPeekEnded));
                    NativeEventLog.Trace("window_peek.ending", "mod", "lilith",
                        "native screen peek end requested", "reason=" + _reason);
                    break;

                case Phase.Ending:
                    // PlayPeekEnd invokes OnPeekEnded when its exit animation
                    // has finished. Nothing else to do while waiting.
                    break;

                case Phase.Finalizing:
                    if (now < _nextAt)
                        return;
                    FinishAndReturn();
                    break;
            }
        }
        catch (Exception error)
        {
            NativeEventLog.Trace("window_peek.tick_error", "mod", "lilith",
                "native window peek failed", error.Message);
            CancelSilently();
        }
    }

    public static void CancelForPlayer()
    {
        if (_phase == Phase.Idle || _controller == null)
            return;
        try
        {
            NativeEventLog.Trace("window_peek.player_cancel", "mod", "player",
                "player interrupted screen peek", "reason=" + _reason);
            FinishAndReturn();
        }
        catch
        {
            CancelSilently();
        }
    }

    public static void Clear()
    {
        CancelSilently();
        _controller = null;
        _phase = Phase.Idle;
        _reason = "";
        KeptDelegates.Clear();
    }

    private static void OnPeekEntered()
    {
        try
        {
            if (_phase != Phase.Holding || _controller == null)
                return;
            _nextAt = Time.unscaledTime + PeekHoldSeconds;
        }
        catch (Exception error)
        {
            NativeEventLog.Trace("window_peek.enter_callback_error", "mod", "lilith",
                "screen peek enter callback failed", error.Message);
            CancelSilently();
        }
    }

    private static void OnPeekEnded()
    {
        if (_phase != Phase.Ending || _controller == null)
            return;
        _phase = Phase.Finalizing;
        _nextAt = Time.unscaledTime + FinalizeDelaySeconds;
        NativeEventLog.Trace("window_peek.exit_done", "mod", "lilith",
            "native screen peek exit animation finished", "reason=" + _reason);
        try
        {
            _controller.PlayNeglectComeback(ToIl2Action(OnComebackDone));
            _comebackStarted = true;
        }
        catch (Exception error)
        {
            NativeEventLog.Trace("window_peek.comeback_error", "mod", "lilith",
                "natural comeback failed", error.Message);
            _comebackStarted = false;
            FinishAndReturn();
        }
    }

    private static void OnComebackDone()
    {
        try
        {
            NativeEventLog.Trace("window_peek.comeback_done", "mod", "lilith",
                "native peek returned to the desktop", "reason=" + _reason);
            _comebackStarted = false;
            RestoreNativeOutfit();
            CancelSilently();
        }
        catch (Exception error)
        {
            NativeEventLog.Trace("window_peek.comeback_callback_error", "mod", "lilith",
                "screen peek comeback callback failed", error.Message);
            RestoreNativeOutfit();
            CancelSilently();
        }
    }

    private static void FallbackToClassic()
    {
        try
        {
            if (_controller != null)
            {
                _controller.CancelNeglectHide();
                _controller.PlayLilithAction(LilithActionType.ScreenPeek, true);
                NativeEventLog.Trace("window_peek.fallback", "mod", "lilith",
                    "native peek unavailable; fell back to classic ScreenPeek",
                    "reason=" + _reason);
            }
        }
        catch (Exception error)
        {
            NativeEventLog.Trace("window_peek.fallback_error", "mod", "lilith",
                "classic ScreenPeek fallback failed", error.Message);
        }
        RestoreNativeOutfit();
        CancelSilently();
    }

    private static void FinishAndReturn()
    {
        try
        {
            if (_controller != null && !_comebackStarted)
            {
                _controller.PlayNeglectComeback(null);
                _comebackStarted = true;
            }
        }
        catch (Exception error)
        {
            NativeEventLog.Trace("window_peek.return_error", "mod", "lilith",
                "returning from native peek failed", error.Message);
            try
            {
                _controller?.CancelNeglectHide();
            }
            catch
            {
                // Final fallback.
            }
        }
        RestoreNativeOutfit();
        CancelSilently();
    }

    private static void CancelSilently()
    {
        _phase = Phase.Idle;
        _nextAt = 0f;
        _reason = "";
        _controller = null;
        _comebackStarted = false;
        RestoreWindowColliders();
        RestoreNativeOutfit();
    }

    private static void RestoreNativeOutfit()
    {
        try
        {
            if (_poseState != null)
            {
                _poseState.SetClothingState(_savedClothing, true);
            }
            if (_poseState != null && _wasLieDown)
            {
                _poseState.EnterLieDownState(false);
            }
            else if (_poseState != null && _wasSofaSit)
            {
                _poseState.RequestSofaSit();
            }
        }
        catch
        {
            // Native clothing restore is best-effort.
        }
        _poseState = null;
        _savedClothing = LilithClothingState.Casual;
        _wasLieDown = false;
        _wasSofaSit = false;
    }

    private static void RestoreWindowColliders()
    {
        try
        {
            if (_windowColliders != null && !_windowCollidersWereEnabled)
                _windowColliders.SetWindowCollidersEnabled(false);
        }
        catch
        {
            // Restoring the native collider switch is best-effort.
        }
        _windowColliders = null;
        _windowCollidersWereEnabled = false;
    }

    private static Il2Action ToIl2Action(Action callback)
    {
        Type? il2ActionType = Type.GetType("Il2CppSystem.Action, Il2Cppmscorlib");
        Type? supportType = Type.GetType(
            "Il2CppInterop.Runtime.DelegateSupport, Il2CppInterop.Runtime");
        MethodInfo? convert = supportType?.GetMethod(
            "ConvertDelegate",
            BindingFlags.Public | BindingFlags.Static);
        if (il2ActionType == null || convert == null)
            throw new InvalidOperationException("Il2Cpp delegate support is unavailable");

        convert = convert.MakeGenericMethod(il2ActionType);
        var converted = (Il2Action)(convert.Invoke(null, new object[] { callback })
            ?? throw new InvalidOperationException("Il2Cpp delegate conversion failed"));
        KeptDelegates.Add(converted);
        return converted;
    }
}
