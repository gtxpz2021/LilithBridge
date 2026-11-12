using UnityEngine;

namespace LilithBridge;

internal static partial class ActionExecutor
{
    private sealed class WaitingAnimationLease
    {
        internal readonly IntPtr State;
        internal readonly Spine.TrackEntry Entry;
        internal readonly LilithActionType Action;
        internal bool Expired;
        private Spine.AnimationState.TrackEntryDelegate? _ended;
        internal WaitingAnimationLease(IntPtr state, Spine.TrackEntry entry, LilithActionType action)
        {
            State = state; Entry = entry; Action = action;
            try
            {
                _ended = Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<Spine.AnimationState.TrackEntryDelegate>(
                    (Action<Spine.TrackEntry>)(_ => Expired = true));
                entry.add_Interrupt(_ended); entry.add_End(_ended); entry.add_Dispose(_ended);
            }
            catch { Expired = true; Detach(); }
        }
        internal void Detach()
        {
            var ended = _ended; _ended = null;
            if (ended == null) return;
            try { Entry.remove_Interrupt(ended); } catch { }
            try { Entry.remove_End(ended); } catch { }
            try { Entry.remove_Dispose(ended); } catch { }
        }
    }
    private static WaitingAnimationLease? _waitingLease;

    // PlayAction reserves an action-state request. Another PlayAction(Idle)
    // may be rejected while IsActionAnimationPlaying remains true. Program
    // waiting gestures belong to the native ambient-animation path instead.
    private static bool TryBeginAmbientWaiting(CharacterController controller, LilithActionType action)
    {
        _waitingLease?.Detach(); _waitingLease = null;
        if (!HasAmbientWaitingAnimation(action) || controller.IsActionAnimationPlaying) return false;
        var state = UnityEngine.Object.FindObjectOfType<LilithStateManager>(true);
        var animation = state?.SkeletonAnimation?.AnimationState;
        if (state == null || animation == null) return false;
        var before = animation.GetCurrent(0);
        var priorAction = controller.ActionType;
        if (!NativeIdleAnimation.TryPlay(controller, action)) return false;
        var after = animation.GetCurrent(0);
        if (after == null || controller.ActionType != action || controller.IsActionAnimationPlaying
            || before?.Pointer == after.Pointer && priorAction == action) return false;
        _waitingLease = new(state.Pointer, after, action);
        return true;
    }

    private static void EndAmbientWaiting(CharacterController controller)
    {
        var lease = _waitingLease; _waitingLease = null;
        if (lease == null) return;
        bool expired = lease.Expired;
        lease.Detach();
        if (expired) return;
        var state = UnityEngine.Object.FindObjectOfType<LilithStateManager>(true);
        var current = state?.SkeletonAnimation?.AnimationState?.GetCurrent(0);
        // Lifecycle invalidation also prevents a pooled/reused TrackEntry from
        // making an older wait appear to own a later native animation.
        if (state == null || state.Pointer != lease.State || current?.Pointer != lease.Entry.Pointer
            || controller.ActionType != lease.Action || controller.IsActionAnimationPlaying
            || NativePoseGate.IsPoseBlocked) return;
        NativeIdleAnimation.TryPlay(controller, LilithActionType.Idle);
    }
}
