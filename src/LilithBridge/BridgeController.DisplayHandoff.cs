using Il2CppInterop.Runtime.Attributes;

namespace LilithBridge;

internal sealed partial class BridgeController
{
    // UI-independent tail of Update's display step: once the playback window is
    // free, the queued reply is handed to the actual presenter. The gate that
    // decides when this may run stays in Update (native state); the harness
    // enters this same state transfer instead of duplicating the controller.
    [HideFromIl2Cpp]
    internal bool TryBeginQueuedReplyDisplay()
    {
        ApiResult? reply = _queuedReply;
        if (reply == null)
            return false;
        if (reply.DeliveryOutcome.Length > 0 || _memoResultContinuation != null
            && ReferenceEquals(_memoResultContinuation.DialogueRequest, reply.DialogueRequest))
        { _queuedReply = null; return false; }
        if (!NativeLinkageAllows(reply)) return false;
        if (reply.MemoResultFinal && !_memory.PaperSourcesPermitted(reply.PaperSources))
        { CancelPendingDialogue(); return false; }
        if (reply.MemoWriteOutcome == null)
        {
            try { reply.MemoWriteOutcome = NativeMemoWriter.Apply(reply, _config); }
            catch { reply.MemoWriteOutcome = new NativeMemoWriteOutcome(0, 0) { Known = false }; }
        }
        if (TryStartMemoResultContinuation(reply)) return false;
        reply.MemoWriteOutcome.ApplyToReply(reply);
        NativeBubblePresenter.SyncGeneratedDelivery(reply);
        _queuedReply = null;
        NativeBubblePresenter.Show(reply);
        return true;
    }
}
