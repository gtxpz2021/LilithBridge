using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using UnityEngine;

namespace LilithBridge;

internal sealed partial class BridgeController
{
    private readonly SensorySidecarClient _sensoryClient = new();
    private long _lastSensoryActionTicks;
    private string _pendingSilentNoticeAction = "";
    private string _pendingSilentNoticeReason = "";

    private void TickSensoryRuntime()
    {
        if (!_config.SensoryEnabled)
            return;

        string? notifyAction = null;
        string? notifyReason = null;
        if (_pendingSilentNoticeAction.Length > 0)
        {
            notifyAction = _pendingSilentNoticeAction;
            notifyReason = _pendingSilentNoticeReason;
            _pendingSilentNoticeAction = "";
            _pendingSilentNoticeReason = "";
        }

        var report = new SensoryHostReport(
            Environment.ProcessId,
            TryCaptureLilithScreenRect(),
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
    }

    private void TryExecuteSensorySilentAction(SensoryPendingAction pending)
    {
        if (!_config.SensorySilentActingEnabled) return;
        if (_requestPending || _aiSpeechActive || NativeBubblePresenter.HasPendingReply) return;
        if (IsNativeBusyForActions() || NativePoseGate.IsPoseBlocked) return;
        if (NativeStateInspector.IsNativeQuietForMod() || NativeWindowPeekDirector.IsActive) return;

        long nowTicks = Stopwatch.GetTimestamp();
        if (_lastSensoryActionTicks > 0 && (double)(nowTicks - _lastSensoryActionTicks) / Stopwatch.Frequency < 10.0)
            return;

        if (!ActionCatalog.TryParseAllowedAction(pending.Action, out LilithActionType action))
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

    private static int[]? TryCaptureLilithScreenRect()
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

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref Win32Point lpPoint);

    private static Win32Point TryGetWindowClientOrigin()
    {
        try
        {
            IntPtr hwnd = Process.GetCurrentProcess().MainWindowHandle;
            if (hwnd == IntPtr.Zero) return default;
            var pt = default(Win32Point);
            return ClientToScreen(hwnd, ref pt) ? pt : default;
        }
        catch
        {
            return default;
        }
    }
}
