using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace LilithBridge;

internal sealed partial class BridgeController
{
    // Reversible comparison baseline: per Work, two recorded local entry
    // permissions in thirty minutes, at least one minute after the last return
    // (or declaration when no return is known). Existing waits still apply.
    private static readonly ReflectionRetryPolicy ReflectionRetryBaseline = new(2, 1800, 60);
    private string _reflectionRetryWaitStamp = "";

    private bool WaitForReflectionRetryBudget()
    {
        var plan = _memory.InspectReflectionRetry(_reflectionWindow, ReflectionRetryBaseline, DateTimeOffset.UtcNow);
        string workId = _reflectionWindow.Snapshot.Work?.Request.RequestId ?? "";
        string stamp = workId + ":" + plan.Kind + ":" + plan.NextAtUtc?.ToString("O");
        if (stamp != _reflectionRetryWaitStamp)
        {
            _reflectionRetryWaitStamp = stamp;
            TraceReflectionRetry(workId, "probe", plan.Kind + ";next_at=" + plan.NextAtUtc?.ToString("O")
                + ";last_return=" + plan.LastReturn + ";earlier_attempts_unknown=" + plan.EarlierAttemptsUnknown);
        }
        if (plan.Kind != ReflectionRetryPlanKind.Wait) return false;
        // Yield the owner and avoid a per-frame probe. UTC is recomputed on the
        // next short probe; do not turn a wall-clock jump into a long Unity wait.
        _nextReflectionPreparationAt = ReflectionClock() + ReflectionPreparationRetrySeconds;
        return true;
    }

    private ReflectionRetryAttemptStep DispatchOrdinaryReflectionWithBudget(ReflectionRetryAttempt attempt,
        ReflectionPreparation owner, MemoryStore memory, ReflectionRequestSnapshot snapshot,
        ReflectionHistorySelection selection, ReflectionWork? recovery, long coveredFactActivity,
        long coveredModelRequest, bool progressiveService, CancellationToken token, Func<Task<HttpResponseMessage>> send)
    {
        // Called only inside the original main-thread HoldReflectionStep and
        // TryUseReflectionRequest/selection lock. No idle or safety gate is relaxed.
        return attempt.Step(() => CurrentReflectionPreparation(owner, memory), ReflectionMayPrepare, () =>
        {
            if (recovery == null) memory.StageReflectionWindow(_reflectionWindow, selection, coveredFactActivity, coveredModelRequest);
            else memory.StageRecoveredReflection(_reflectionWindow);
            if (!memory.BeginReflectionWork(_reflectionWindow, snapshot, selection))
            { _reflectionWindow.EndPass(null); return ReflectionRetryWriteKind.PersistencePending; }
            return memory.RecordReflectionRetryClaim(_reflectionWindow, owner, snapshot, ReflectionRetryBaseline, DateTimeOffset.UtcNow);
        }, token, () =>
        {
            owner.Staged = true;
            RefreshReflectionCadence();
            _reflectionCadence.Started(snapshot.RequestId);
            _reflectionProgressiveCadence.Started(snapshot.RequestId, progressiveService);
            _reflectionDialogueMayBypassCooldown = false;
            _nextReflectionAt = ReflectionClock() + EffectiveReflectionGapSeconds();
            ReflectionIntentTrace.Emit(() => ReflectionIntentTrace.Dispatch(snapshot, owner.IntentTrace));
        }, send);
    }

    private void DeferOrdinaryReflectionRetry(ReflectionPreparation owner, MemoryStore memory,
        ReflectionRequestSnapshot snapshot, ReflectionRetryAttemptStep step)
    {
        if (!ReferenceEquals(owner, _reflectionPreparation)) { owner.Dispose(); return; }
        // No semantic failure and no consumption. EndPass(null) restores the
        // pre-pass coverage while retaining this Work and later pending requests.
        if (_reflectionWindow.Snapshot.Work?.Request.RequestId == snapshot.RequestId)
            _reflectionWindow.EndPass(null);
        _reflectionPreparation = null; _reflectionContinuation = null; _reflectionInProgress = false;
        _nextReflectionPreparationAt = ReflectionClock() + ReflectionPreparationRetrySeconds;
        TraceReflectionRetry(snapshot.RequestId, "deferred_not_invoked", step.Kind + ";fact=" + step.Fact);
        owner.Dispose(); // Cancels the awaiting client; a late callback has no owner.
    }

    private static void TraceReflectionRetry(string workId, string stage, string detail)
    {
        try { NativeEventLog.Trace("memory.reflection.retry_budget", "mod", "system", "local retry facts",
            "work=" + workId + ";stage=" + stage + ";" + detail); }
        catch { } // Diagnostics cannot change dispatch, persistence or coverage.
    }
}
