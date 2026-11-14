namespace LilithBridge;

internal sealed partial class BridgeController
{
    private void CompleteOrdinaryReflectionResponse(ReflectionPreparation owner, MemoryStore memory,
        ReflectionRequestSnapshot snapshot, ReflectionResult? response, bool recovering, ReflectionLocalOutcome? outcome = null)
    {
        if (!ReferenceEquals(owner, _reflectionPreparation)) { owner.Dispose(); return; }
        if (!CurrentReflectionPreparation(owner, memory))
        { CancelReflectionPreparation("owner_changed_before_result"); return; }
        outcome?.Trace(snapshot.RequestId, owner.Id);
        if (owner.RetryBudgetEnabled && outcome != null)
            owner.RetryReturn ??= ReflectionRetryReturn.From(outcome, DateTimeOffset.UtcNow);
        if (owner.Staged)
        {
            var state = ReflectionResultSaveKind.AlreadyHandled;
            // Recheck the full frozen footprint on every local save step. New
            // append-only exchanges do not change this Work's coverage.
            bool allowed = memory.TryUseReflectionRequest(owner, snapshot, () =>
            {
                bool CurrentWork() => CurrentReflectionPreparation(owner, memory)
                    && _reflectionWindow.Snapshot.Work is { Purpose: ReflectionWorkPurpose.Dialogue } work
                    && work.Request.RequestId == snapshot.RequestId;
                if (!CurrentWork()) { state = ReflectionResultSaveKind.Rejected; return true; }
                if (response == null && !owner.ResultSave.HasReceived)
                {
                    if (owner.RetryBudgetEnabled && outcome != null)
                    {
                        // Best effort metadata only. A failed write leaves the durable
                        // entry unresolved; do not hold the owner just to save diagnostics.
                        var recorded = memory.RecordReflectionRetryReturn(_reflectionWindow, owner, snapshot,
                            outcome, owner.RetryReturn!.ObservedAtUtc);
                        TraceReflectionRetry(snapshot.RequestId, "returned", recorded.ToString());
                    }
                    OnReflectionResult(null, snapshot); // Original no-usable-result failure policy.
                    return true;
                }
                var saved = owner.ResultSave.Step(snapshot.RequestId, response, owner.Token, CurrentWork,
                    received => owner.RetryBudgetEnabled
                        ? memory.SaveReflectionResponseWithRetryReturn(_reflectionWindow, owner, snapshot, received, owner.RetryReturn)
                        : memory.SaveReflectionResponse(_reflectionWindow, snapshot, received));
                state = saved.Kind;
                if (state == ReflectionResultSaveKind.Stored) OnReflectionResult(saved.Result!, snapshot);
                return true;
            }, recovering);
            if (!allowed) { CancelReflectionPreparation("source_or_version_changed_before_apply"); return; }
            if (state is ReflectionResultSaveKind.Cancelled or ReflectionResultSaveKind.Rejected)
            { CancelReflectionPreparation("response_owner_or_work_changed"); return; }
            if (state == ReflectionResultSaveKind.PersistencePending)
            {
                _nextReflectionPreparationAt = ReflectionClock() + ReflectionPreparationRetrySeconds;
                HoldReflectionStep(owner, memory,
                    () => ReflectionClock() >= _nextReflectionPreparationAt && ReflectionMayApply(owner.ResultSave.Pending),
                    () => CompleteOrdinaryReflectionResponse(owner, memory, snapshot, null, recovering));
                return;
            }
        }
        else
        {
            _nextReflectionPreparationAt = ReflectionClock() + ReflectionPreparationRetrySeconds;
            NativeEventLog.Trace("memory.reflection.not_sent", "mod", "system", "reflection window remains unconsumed", "owner=" + owner.Id);
        }
        _reflectionPreparation = null; _reflectionInProgress = false;
        owner.Dispose();
    }
}
