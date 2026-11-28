using System;
using System.Collections.Generic;
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
    private long _lastSensoryBeatTicks;
    private int _sensoryBeatStep;
    private IntPtr _activeBeatTrackPtr;
    private string _pendingSilentNoticeAction = "";
    private string _pendingSilentNoticeReason = "";
    private readonly Queue<SensoryHostEvent> _pendingHostEvents = new();
    private readonly object _hostEventGate = new();

    internal void NotifySensoryHostEvent(string kind, string detail = "")
    {
        if (!_config.SensoryEnabled || string.IsNullOrWhiteSpace(kind))
            return;
        lock (_hostEventGate)
        {
            if (_pendingHostEvents.Count >= 8)
                _pendingHostEvents.Dequeue();
            _pendingHostEvents.Enqueue(new SensoryHostEvent(
                kind.Trim(),
                detail ?? "",
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        }
    }

    internal bool HasRecentSensoryAction(double seconds)
    {
        long nowTicks = Stopwatch.GetTimestamp();
        if (_lastSensoryActionTicks > 0 && (double)(nowTicks - _lastSensoryActionTicks) / Stopwatch.Frequency < seconds)
            return true;
        if (_lastSensoryBeatTicks > 0 && (double)(nowTicks - _lastSensoryBeatTicks) / Stopwatch.Frequency < seconds)
            return true;
        return false;
    }

    private void TickSensoryRuntime()
    {
        if (!_config.SensoryEnabled)
            return;

        SensoryHostEvent[]? hostEvents = null;
        lock (_hostEventGate)
        {
            if (_pendingHostEvents.Count > 0)
            {
                hostEvents = _pendingHostEvents.ToArray();
                _pendingHostEvents.Clear();
            }
        }

        long nowTicks = Stopwatch.GetTimestamp();
        bool hasActionNotice = _pendingSilentNoticeAction.Length > 0;
        bool hasNotice = hasActionNotice || hostEvents is { Length: > 0 };
        if (!hasNotice && _lastSensoryTickTicks > 0
            && (double)(nowTicks - _lastSensoryTickTicks) / Stopwatch.Frequency < 0.20)
        {
            if (hostEvents is { Length: > 0 })
            {
                // Keep unreachable guard cleanly structured
            }
            return;
        }
        _lastSensoryTickTicks = nowTicks;

        string? notifyAction = null;
        string? notifyReason = null;
        if (hasActionNotice)
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
            _config.MediaContextEnabled ? _config.MediaAllowedSources : Array.Empty<string>(),
            hostEvents);

        _sensoryClient.Tick(_config, report);

        SensorySnapshot snap = _sensoryClient.Latest;
        SyncBeatTimeScale(snap);
        if (snap.Available && snap.PendingAction is { } pendingAction)
            TryExecuteSensorySilentAction(pendingAction, snap);
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

    private void SyncBeatTimeScale(SensorySnapshot snap)
    {
        if (_activeBeatTrackPtr == IntPtr.Zero)
            return;
        try
        {
            LilithStateManager? state = UnityEngine.Object.FindObjectOfType<LilithStateManager>(true);
            Spine.TrackEntry? current = state?.SkeletonAnimation?.AnimationState?.GetCurrent(0);
            if (current == null || current.Pointer != _activeBeatTrackPtr)
            {
                _activeBeatTrackPtr = IntPtr.Zero;
                return;
            }
            bool keepScaled = snap.Available
                && snap.BeatSyncEnabled
                && _config.SensoryBeatGrooveEnabled
                && snap.BeatBpm >= 60.0
                && !NativeStateInspector.IsSleeping
                && !IsNativeBusyForActions();
            if (!keepScaled)
            {
                current.TimeScale = 1f;
                _activeBeatTrackPtr = IntPtr.Zero;
                return;
            }
            current.TimeScale = SensoryActionPacing.ComputeBeatTimeScale(snap.BeatBpm);
        }
        catch
        {
            _activeBeatTrackPtr = IntPtr.Zero;
        }
    }

    private void TryExecuteSensorySilentAction(SensoryPendingAction pending, SensorySnapshot snap)
    {
        if (!_config.SensorySilentActingEnabled) return;
        if (_requestPending || _aiSpeechActive || NativeBubblePresenter.HasPendingReply) return;
        if (IsNativeBusyForActions() || NativePoseGate.IsPoseBlocked) return;
        if (NativeWindowPeekDirector.IsActive) return;

        if (!ActionCatalog.TryParseAllowedAction(pending.Action, out LilithActionType requested))
            return;

        bool isBeatCue = string.Equals(pending.Reason, "music_beat_pulse", StringComparison.OrdinalIgnoreCase);
        if (isBeatCue && (!snap.BeatSyncEnabled || !_config.SensoryBeatGrooveEnabled))
            return;

        bool isSleeping = NativeStateInspector.IsSleeping;
        bool sleepWake = isSleeping || requested is LilithActionType.HalfAwake or LilithActionType.SmallYawn;
        if (sleepWake && isSleeping)
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
        if (isBeatCue)
        {
            if (_lastSensoryBeatTicks > 0 && (double)(nowTicks - _lastSensoryBeatTicks) / Stopwatch.Frequency < 7.0)
                return;
            if (_lastSensoryActionTicks > 0 && (double)(nowTicks - _lastSensoryActionTicks) / Stopwatch.Frequency < 4.5)
                return;
        }
        else if (_lastSensoryActionTicks > 0 && (double)(nowTicks - _lastSensoryActionTicks) / Stopwatch.Frequency < 4.5)
        {
            return;
        }

        bool beatAccent = isBeatCue && ((_sensoryBeatStep % 4) == 3);
        LilithActionType? resolved = SensoryActionPacing.ResolveAction(
            requested,
            pending.Reason,
            isSleeping,
            beatAccent,
            a => ActionCapability.IsPoseSafe(a, _sessionState),
            ActionExecutor.HasNativeAnimation,
            !NativeWindowPeekDirector.IsSuppressed);

        if (!resolved.HasValue)
        {
            _sensoryClient.AcknowledgeConsumed(_config, 0, consumedAction: pending.Action);
            return;
        }

        LilithActionType action = resolved.Value;
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

            if (isBeatCue)
            {
                _lastSensoryBeatTicks = nowTicks;
                _sensoryBeatStep++;
                try
                {
                    Spine.TrackEntry? entry = UnityEngine.Object.FindObjectOfType<LilithStateManager>(true)
                        ?.SkeletonAnimation?.AnimationState?.GetCurrent(0);
                    if (entry != null && snap.BeatBpm >= 60.0)
                    {
                        _activeBeatTrackPtr = entry.Pointer;
                        entry.TimeScale = SensoryActionPacing.ComputeBeatTimeScale(snap.BeatBpm);
                    }
                }
                catch
                {
                    _activeBeatTrackPtr = IntPtr.Zero;
                }
            }
            else
            {
                _lastSensoryActionTicks = nowTicks;
                _pendingSilentNoticeAction = pending.Action;
                _pendingSilentNoticeReason = pending.Reason;
            }

            // Always acknowledge with the exact original sidecar string so PendingAction clears cleanly.
            _sensoryClient.AcknowledgeConsumed(_config, 0, consumedAction: pending.Action);
            NativeEventLog.Trace("sensory.silent_action", "mod", "lilith",
                action.ToString(), "requested=" + requested + ";reason=" + pending.Reason);
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

internal static class SensoryActionPacing
{
    public static float ComputeBeatTimeScale(double beatBpm)
    {
        if (beatBpm < 60.0 || double.IsNaN(beatBpm) || double.IsInfinity(beatBpm))
            return 1.0f;
        return (float)Math.Clamp(beatBpm / 108.0, 0.80, 1.40);
    }

    public static LilithActionType? ResolveAction(
        LilithActionType requested,
        string? reason,
        bool isSleeping,
        bool beatAccentStep,
        Func<LilithActionType, bool> isPoseSafe,
        Func<LilithActionType, bool> hasNativeAnimation,
        bool allowWindowPeek)
    {
        if (requested == LilithActionType.RubHead)
            requested = LilithActionType.TiltHead;

        if (requested == LilithActionType.ScreenPeek)
        {
            if (allowWindowPeek && isPoseSafe(LilithActionType.ScreenPeek))
                return LilithActionType.ScreenPeek;
            return PickFirstSafe(
                new[] { LilithActionType.Tsundere, LilithActionType.Confuse, LilithActionType.Snap, LilithActionType.Blink },
                isPoseSafe,
                hasNativeAnimation);
        }

        if (isSleeping)
        {
            return PickFirstSafe(
                new[] { requested, LilithActionType.Mumble, LilithActionType.RubEyes, LilithActionType.TwitchBare, LilithActionType.Blink },
                isPoseSafe,
                hasNativeAnimation);
        }

        bool isBeatCue = string.Equals(reason, "music_beat_pulse", StringComparison.OrdinalIgnoreCase)
            || requested is LilithActionType.Music or LilithActionType.ShakeLeg;
        if (isBeatCue)
        {
            LilithActionType[] beatPool = beatAccentStep
                ? new[] { LilithActionType.Snap, LilithActionType.Music, LilithActionType.ShakeLeg }
                : new[] { LilithActionType.Music, LilithActionType.ShakeLeg, LilithActionType.Snap };
            return PickFirstSafe(beatPool, isPoseSafe, hasNativeAnimation);
        }

        LilithActionType[] pool = requested switch
        {
            LilithActionType.Hide or LilithActionType.TwitchBare => new[]
            {
                requested, LilithActionType.TwitchBare, LilithActionType.DrowsySlump, LilithActionType.Drowsy, LilithActionType.Hide, LilithActionType.Tsundere, LilithActionType.Blink
            },
            LilithActionType.Squint => new[]
            {
                LilithActionType.Squint, LilithActionType.DrowsySlump, LilithActionType.Drowsy, LilithActionType.Tsundere, LilithActionType.Blink
            },
            LilithActionType.Turnhead or LilithActionType.TiltHead => new[]
            {
                requested, LilithActionType.UpLeft, LilithActionType.LookAround, LilithActionType.MagicCast, LilithActionType.Tsundere, LilithActionType.Snap, LilithActionType.Blink
            },
            LilithActionType.Yawn => new[]
            {
                LilithActionType.Yawn, LilithActionType.SmallYawn, LilithActionType.DrowsySlump, LilithActionType.Stretch, LilithActionType.Blink
            },
            _ => new[]
            {
                requested, LilithActionType.Snap, LilithActionType.Tsundere, LilithActionType.Blink
            }
        };
        return PickFirstSafe(pool, isPoseSafe, hasNativeAnimation);
    }

    private static LilithActionType? PickFirstSafe(
        LilithActionType[] candidates,
        Func<LilithActionType, bool> isPoseSafe,
        Func<LilithActionType, bool> hasNativeAnimation)
    {
        foreach (LilithActionType candidate in candidates)
        {
            if (candidate != LilithActionType.RubHead && isPoseSafe(candidate) && hasNativeAnimation(candidate))
                return candidate;
        }
        return null;
    }
}
