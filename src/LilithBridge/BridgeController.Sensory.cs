using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using UnityEngine;

namespace LilithBridge;

internal sealed partial class BridgeController
{
    private readonly SensorySidecarClient _sensoryClient = new();
    private long _lastSensoryTickTicks;
    private long _lastRectCaptureTicks;
    private int[]? _cachedLilithRect;
    private IntPtr _cachedMainHwnd;
    private long _lastHwndLookupTicks;
    private long _lastSensoryActionTicks;
    private string _pendingSilentNoticeAction = "";
    private string _pendingSilentNoticeReason = "";

    private void TickSensoryRuntime()
    {
        if (!_config.SensoryEnabled)
            return;

        long nowTicks = Stopwatch.GetTimestamp();
        bool hasNotice = _pendingSilentNoticeAction.Length > 0;
        if (!hasNotice && _lastSensoryTickTicks > 0
            && (double)(nowTicks - _lastSensoryTickTicks) / Stopwatch.Frequency < 0.20)
        {
            return;
        }
        _lastSensoryTickTicks = nowTicks;

        string? notifyAction = null;
        string? notifyReason = null;
        if (hasNotice)
        {
            notifyAction = _pendingSilentNoticeAction;
            notifyReason = _pendingSilentNoticeReason;
            _pendingSilentNoticeAction = "";
            _pendingSilentNoticeReason = "";
        }

        if (_lastRectCaptureTicks == 0
            || (double)(nowTicks - _lastRectCaptureTicks) / Stopwatch.Frequency >= 0.40)
        {
            _lastRectCaptureTicks = nowTicks;
            _cachedLilithRect = TryCaptureLilithScreenRect();
        }

        var report = new SensoryHostReport(
            Environment.ProcessId,
            _cachedLilithRect,
            NativeStateInspector.IsSleeping,
            NativeStateInspector.IsNativeQuietForMod(),
            _aiSpeechActive || NativeBubblePresenter.HasPendingReply,
            _listening && _inputField.IsOpen,
            _listening && _inputField.IsOpen ? _inputField.Text.Length : 0,
            notifyAction,
            notifyReason,
            _config.MediaContextEnabled ? _config.MediaAllowedSources : Array.Empty<string>());

        _sensoryClient.Tick(_config, report);

        SensorySnapshot snap = _sensoryClient.Latest;
        if (snap.Available && snap.PendingAction is { } pendingAction)
            TryExecuteSensorySilentAction(pendingAction);
        if (snap.Available && snap.PendingFocusNote is { } focusNote)
            TryHandleSensoryFocusNote(focusNote);
    }

    private void TryHandleSensoryFocusNote(SensoryPendingFocusNote focusNote)
    {
        if (TryTriggerSensoryFocusNote())
        {
            _sensoryClient.AcknowledgeConsumed(_config, 0, null, false, true);
            NativeEventLog.Trace("sensory.focus_note", "mod", "system",
                "quiet focus marathon note scheduled",
                $"kind={focusNote.Kind};mins={focusNote.FocusMinutes:F1}");
        }
    }

    private void TryExecuteSensorySilentAction(SensoryPendingAction pending)
    {
        if (!_config.SensorySilentActingEnabled) return;
        if (_requestPending || _aiSpeechActive || NativeBubblePresenter.HasPendingReply) return;
        if (IsNativeBusyForActions() || NativePoseGate.IsPoseBlocked) return;
        if (NativeWindowPeekDirector.IsActive) return;

        if (!ActionCatalog.TryParseAllowedAction(pending.Action, out LilithActionType action))
            return;
        bool sleepWake = action is LilithActionType.HalfAwake or LilithActionType.SmallYawn;
        if (sleepWake && NativeStateInspector.IsSleeping)
        {
            if (NativeStateInspector.IsGamingHidden || NativeStateInspector.IsDisappearPending
                || NativeStateInspector.IsDissipatePending || NativeStateInspector.IsNeglectHidden
                || NativeStateInspector.IsForegroundFullscreen || NativeStateInspector.IsPoseBlocked)
                return;
        }
        else if (NativeStateInspector.IsNativeQuietForMod())
        {
            return;
        }

        long nowTicks = Stopwatch.GetTimestamp();
        if (_lastSensoryActionTicks > 0 && (double)(nowTicks - _lastSensoryActionTicks) / Stopwatch.Frequency < 10.0)
            return;

        if (!ActionCapability.IsPoseSafe(action, _sessionState))
            return;

        CharacterController? controller = UnityEngine.Object.FindObjectOfType<CharacterController>(true);
        if (controller == null)
            return;

        try
        {
            if (action == LilithActionType.ScreenPeek)
            {
                if (!NativeWindowPeekDirector.TryPeek(controller, "sensory:" + pending.Reason))
                    controller.PlayLilithAction(LilithActionType.ScreenPeek, true);
                ActionExecutor.RecordNativeAction(LilithActionType.ScreenPeek);
            }
            else
            {
                controller.PlayLilithAction(action, true);
                ActionExecutor.RecordNativeAction(action);
            }

            _lastSensoryActionTicks = nowTicks;
            _pendingSilentNoticeAction = pending.Action;
            _pendingSilentNoticeReason = pending.Reason;
            _sensoryClient.AcknowledgeConsumed(_config, 0, consumedAction: pending.Action);
            NativeEventLog.Trace("sensory.silent_action", "mod", "lilith",
                action.ToString(), "reason=" + pending.Reason);
        }
        catch (Exception error)
        {
            NativeEventLog.Trace("sensory.silent_action_error", "mod", "lilith", error.Message);
        }
    }

    private int[]? TryCaptureLilithScreenRect()
    {
        try
        {
            CharacterController? character = CharacterController.instance
                ?? UnityEngine.Object.FindObjectOfType<CharacterController>(true);
            Camera? cam = Camera.main;
            if (character == null || cam == null)
                return null;
            Vector3 sp = cam.WorldToScreenPoint(character.transform.position);
            if (sp.z <= 0f || !float.IsFinite(sp.x) || !float.IsFinite(sp.y))
                return null;
            int sh = Screen.height;
            var origin = TryGetWindowClientOrigin();
            int cx = origin.X + (int)Math.Round(sp.x);
            int cyTop = origin.Y + (int)Math.Round(sh - sp.y);
            return new[] { cx - 95, cyTop - 220, cx + 95, cyTop + 45 };
        }
        catch
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Win32Point
    {
        public int X;
        public int Y;
    }

    private delegate bool EnumThreadWndProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref Win32Point lpPoint);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool EnumThreadWindows(uint dwThreadId, EnumThreadWndProc lpfn, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private Win32Point TryGetWindowClientOrigin()
    {
        try
        {
            long nowTicks = Stopwatch.GetTimestamp();
            if (_cachedMainHwnd != IntPtr.Zero && !IsWindow(_cachedMainHwnd))
                _cachedMainHwnd = IntPtr.Zero;

            if (_cachedMainHwnd == IntPtr.Zero
                && (_lastHwndLookupTicks == 0 || (double)(nowTicks - _lastHwndLookupTicks) / Stopwatch.Frequency >= 5.0))
            {
                _lastHwndLookupTicks = nowTicks;
                IntPtr found = IntPtr.Zero;
                EnumThreadWindows(GetCurrentThreadId(), (hwnd, _) =>
                {
                    if (!IsWindowVisible(hwnd)) return true;
                    found = hwnd;
                    return false;
                }, IntPtr.Zero);
                _cachedMainHwnd = found;
            }

            if (_cachedMainHwnd == IntPtr.Zero) return default;
            var pt = default(Win32Point);
            return ClientToScreen(_cachedMainHwnd, ref pt) ? pt : default;
        }
        catch
        {
            return default;
        }
    }
}
