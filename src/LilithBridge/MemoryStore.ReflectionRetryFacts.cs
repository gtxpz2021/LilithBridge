using System;
using System.Linq;

namespace LilithBridge;

internal sealed partial class MemoryStore
{
    // The budgeted caller preserves the original owner/source/native/provider gates.
    // Waiting and persistence failure retain the Work without completing coverage.
    internal ReflectionRetryPlan InspectReflectionRetry(ReflectionWindowCursor cursor,
        ReflectionRetryPolicy policy, DateTimeOffset now)
    {
        lock (_experienceLock) return ReflectionRetryFacts.Evaluate(cursor.Snapshot.Work, policy, now);
    }

    internal ReflectionRetryWriteKind RecordReflectionRetryClaim(ReflectionWindowCursor cursor,
        ReflectionPreparation owner, ReflectionRequestSnapshot request, ReflectionRetryPolicy proposed, DateTimeOffset now)
    {
        lock (_experienceLock)
        {
            if (cursor.Snapshot.Work is not { Purpose: ReflectionWorkPurpose.Dialogue } work
                || work.Request.RequestId != request.RequestId || !ReflectionPreparationCurrent(owner)
                || !ReflectionMaterialsCurrent(request, recovering: true)) return ReflectionRetryWriteKind.Unavailable;
            if (work.Result != null) return ReflectionRetryWriteKind.LocalResult;
            if (work.RetryHistory is {} prior && !prior.Valid(work)) return ReflectionRetryWriteKind.InvalidHistory;
            // This is not another permission to invoke send. The live dispatcher
            // must keep its own entered task / cancellation state as before.
            if (work.RetryHistory?.Claims.Any(c => c.OwnerId == owner.Id) == true) return ReflectionRetryWriteKind.AlreadyRecorded;
            var plan = ReflectionRetryFacts.Evaluate(work, proposed, now);
            if (plan.Kind != ReflectionRetryPlanKind.Ready) return plan.Kind switch {
                ReflectionRetryPlanKind.Wait => ReflectionRetryWriteKind.Waiting,
                ReflectionRetryPlanKind.ClockUncertain => ReflectionRetryWriteKind.ClockUncertain,
                ReflectionRetryPlanKind.InvalidHistory => ReflectionRetryWriteKind.InvalidHistory,
                _ => ReflectionRetryWriteKind.Unavailable };
            var policy = work.RetryHistory?.Policy ?? proposed;
            if (work.RetryHistory?.RecordedClaims == long.MaxValue) return ReflectionRetryWriteKind.Unavailable;
            var claims = (work.RetryHistory?.Claims ?? Array.Empty<ReflectionRetryClaim>())
                .Where(c => now - c.DeclaredAtUtc < TimeSpan.FromSeconds(policy.WindowSeconds))
                .Append(new ReflectionRetryClaim(owner.Id, now)).ToArray();
            var history = new ReflectionRetryHistory(work.Request.RequestId, policy,
                work.RetryHistory?.FirstRecordedAtUtc ?? now, (work.RetryHistory?.RecordedClaims ?? 0) + 1, claims)
                { EarlierAttemptsUnknown = work.RetryHistory?.EarlierAttemptsUnknown ?? true };
            var updated = work with { RetryHistory = history };
            if (!SaveReflectionWindow(cursor.DurableSnapshot with { Work = updated })) return ReflectionRetryWriteKind.PersistencePending;
            cursor.SetWork(updated);
            return ReflectionRetryWriteKind.Stored;
        }
    }

    internal ReflectionRetryWriteKind RecordReflectionRetryReturn(ReflectionWindowCursor cursor,
        ReflectionPreparation owner, ReflectionRequestSnapshot request, ReflectionLocalOutcome outcome, DateTimeOffset now)
    {
        lock (_experienceLock)
        {
            if (cursor.Snapshot.Work is not { Purpose: ReflectionWorkPurpose.Dialogue, RetryHistory: {} history } work
                || work.Request.RequestId != request.RequestId || !ReflectionPreparationCurrent(owner)
                || !ReflectionMaterialsCurrent(request, recovering: true)) return ReflectionRetryWriteKind.Unavailable;
            if (!history.Valid(work)) return ReflectionRetryWriteKind.InvalidHistory;
            if (history.Claims[^1].OwnerId != owner.Id) return ReflectionRetryWriteKind.Unavailable;
            if (history.Claims[^1].Return != null) return ReflectionRetryWriteKind.AlreadyRecorded;
            var returned = ReflectionRetryReturn.From(outcome, now);
            if (!returned.Valid) return ReflectionRetryWriteKind.Unavailable;
            // Keep the first return fact. A late former attempt cannot replace a
            // newer claim, and metadata cannot claim durable Result or coverage.
            var claims = history.Claims.ToArray();claims[^1] = claims[^1] with { Return = returned };
            var updated = work with { RetryHistory = history with { Claims = claims } };
            if (!SaveReflectionWindow(cursor.DurableSnapshot with { Work = updated })) return ReflectionRetryWriteKind.PersistencePending;
            cursor.SetWork(updated);
            return ReflectionRetryWriteKind.Stored;
        }
    }
    // The first usable value stays with the existing live ResultSave owner.
    // Save its return metadata and value in the same Work write, including a
    // legal empty result. Invalid/missing optional metadata cannot block value recovery.
    internal bool SaveReflectionResponseWithRetryReturn(ReflectionWindowCursor cursor,
        ReflectionPreparation owner, ReflectionRequestSnapshot request, ReflectionResult result, ReflectionRetryReturn? returned)
    {
        lock (_experienceLock)
        {
            if (cursor.Snapshot.Work is not { Purpose: ReflectionWorkPurpose.Dialogue } work
                || work.Request.RequestId != request.RequestId || !ReflectionPreparationCurrent(owner)
                || !ReflectionMaterialsCurrent(request, recovering: true)) return false;
            var accepted = work with { Result = result };
            if (returned?.Valid == true && returned.Kind == ReflectionLocalOutcomeKind.AcceptedResult
                && work.RetryHistory is {} history && history.Valid(work)
                && history.Claims[^1].OwnerId == owner.Id && history.Claims[^1].Return == null)
            {
                var claims = history.Claims.ToArray();
                claims[^1] = claims[^1] with { Return = returned };
                accepted = accepted with { RetryHistory = history with { Claims = claims } };
            }
            return SaveAcceptedReflectionResponse(cursor, work, accepted);
        }
    }

}
