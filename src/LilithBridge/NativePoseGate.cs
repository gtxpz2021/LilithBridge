using System;
using System.Collections.Generic;
using UnityEngine;

namespace LilithBridge;

/// <summary>
/// The original interaction handler refuses to enter an interaction state
/// while Lilith is lying down or sitting on the sofa. This gate wakes those
/// native poses first, then releases queued mod work so Enter, clicks and AI
/// actions are not silently swallowed.
/// </summary>
internal static class NativePoseGate
{
    private const float WakeTimeoutSeconds = 3.0f;

    private static readonly Queue<Action> Pending = new();
    private static LilithStateManager? _state;
    private static float _deadline;
    private static bool _waking;

    public static bool IsPoseBlocked
    {
        get
        {
            LilithStateManager? state = FindState();
            return state != null && IsBlockingPose(state);
        }
    }

    public static void WakeForInput()
    {
        BeginWake(null);
    }

    public static void Request(Action onReady)
    {
        if (onReady == null)
            return;
        if (!IsPoseBlocked)
        {
            onReady();
            return;
        }
        Pending.Enqueue(onReady);
        BeginWake(onReady);
    }

    public static void Tick(float now)
    {
        if (!_waking)
            return;

        LilithStateManager? state = FindState();
        if (state == null || !IsBlockingPose(state) || now >= _deadline)
            CompleteWake();
    }

    public static void Clear()
    {
        while (Pending.Count > 0)
        {
            try
            {
                Pending.Dequeue()?.Invoke();
            }
            catch
            {
                // Drain best-effort on shutdown.
            }
        }
        _state = null;
        _waking = false;
        _deadline = 0f;
    }

    private static void BeginWake(Action? onReady)
    {
        if (_waking)
            return;

        LilithStateManager? state = FindState();
        if (state == null || !IsBlockingPose(state))
        {
            if (onReady != null)
                onReady();
            return;
        }

        _state = state;
        _waking = true;
        _deadline = Time.unscaledTime + WakeTimeoutSeconds;
        try
        {
            if (state.IsSofaSit)
            {
                state.ReleaseSofaSit();
                NativeEventLog.Trace("pose.wake", "mod", "system",
                    "sofa sit released for interaction");
            }
            else
            {
                state.ExitLieDownState(false);
                NativeEventLog.Trace("pose.wake", "mod", "system",
                    "lying state exited for interaction");
            }
        }
        catch (Exception error)
        {
            NativeEventLog.Trace("pose.wake_error", "mod", "system",
                "native pose wake failed", error.Message);
            CompleteWake();
        }
    }

    private static void CompleteWake()
    {
        _waking = false;
        _state = null;
        _deadline = 0f;
        while (Pending.Count > 0)
        {
            Action? action = Pending.Dequeue();
            try
            {
                action?.Invoke();
            }
            catch (Exception error)
            {
                NativeEventLog.Trace("pose.wake_callback_error", "mod", "system",
                    "queued pose-gate work failed", error.Message);
            }
        }
    }

    private static bool IsBlockingPose(LilithStateManager state)
    {
        return state.IsLieDown || state.IsSofaSit;
    }

    private static LilithStateManager? FindState()
    {
        try
        {
            return UnityEngine.Object.FindObjectOfType<LilithStateManager>(true);
        }
        catch
        {
            return null;
        }
    }
}
