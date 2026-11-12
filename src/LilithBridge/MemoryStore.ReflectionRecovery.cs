using System;
using System.Linq;

namespace LilithBridge;

internal sealed partial class MemoryStore
{
    // Called after staging, before sending. Failure means no outbound attempt.
    internal bool BeginReflectionWork(ReflectionWindowCursor cursor, ReflectionRequestSnapshot request,
        ReflectionHistorySelection selection)
    {
        lock (_experienceLock)
        {
            _reflectionCursors.Add(cursor);
            if (cursor.Snapshot.Work is { } existing) return existing.Request.RequestId == request.RequestId;
            var work = new ReflectionWork(request, selection.History.Select(t => t.Copy()).ToArray(),
                cursor.Snapshot with { Work = null, RequestOrigin = null, PendingIntents = null });
            if (!SaveReflectionWindow(cursor.DurableSnapshot with { Work = work })) return false;
            cursor.SetWork(work);
            return true;
        }
    }

    internal bool SaveReflectionResponse(ReflectionWindowCursor cursor, ReflectionRequestSnapshot request,
        ReflectionResult result)
    {
        lock (_experienceLock)
        {
            if (cursor.Snapshot.Work is not { } work || work.Request.RequestId != request.RequestId) return false;
            var accepted = work with { Result = result };
            if (!SaveReflectionWindow(cursor.DurableSnapshot with { Work = accepted })) return false;
            cursor.SetWork(accepted);
            return true;
        }
    }

    internal void StageRecoveredReflection(ReflectionWindowCursor cursor)
    {
        if (!cursor.PassActive && cursor.Snapshot.Work is { } work)
            cursor.BeginPass(work.Target with { Work = work,
                // A request arriving after the original capture remains pending.
                ModelRequested = work.Target.ModelRequested || work.RequestedAgain,
                RequestOrigin = cursor.Snapshot.RequestOrigin,
                PendingIntents = cursor.Snapshot.PendingIntents?.Where(i => !work.Request.PurposeContexts.Any(c => c.Intent.Serial == i.Serial && c.Intent.TagIndex == i.TagIndex)).ToArray() });
    }

    // True means the persisted response was handled without another model call.
    // A missing response may retry transport with the same work and snapshot.
    internal bool ResumeReflectionWork(ReflectionWindowCursor cursor, out string reason,
        out ReflectionChainOutcome? chains)
    {
        reason = "no_pending_response"; chains = null;
        lock (_experienceLock)
        {
            if (cursor.Snapshot.Work is not { Result: { } result } work) return false;
            StageRecoveredReflection(cursor);
            bool finished = ChainFinished(work, "note") && ChainFinished(work, "lesson") && ChainFinished(work, "reading");
            // Completed artifacts remain in their original chains. A changed or
            // withdrawn read footprint cancels only the unfinished work; its
            // input stays pending for a fresh capture, under current permissions.
            if (!finished && !ReflectionMaterialsCurrent(work.Request, recovering: true))
            {
                reason = "source_or_version_changed";
                CancelReflectionWork(cursor);
                string observedReason = reason;
                ReflectionIntentTrace.Emit(() => ReflectionIntentTrace.Outcome(work.Request, result, null, observedReason, cursor.Snapshot.Work != null));
                return true;
            }
            ApplyReflectionNote(result, work.Request, work.Progress, out reason, out var outcome);
            chains = outcome;
            string completion = ReflectionWindow.CompletionReason(reason, outcome);
            if (!ReflectionWindow.RetainsForRetry(completion))
            {
                CompleteReflectionWindow(cursor, reason, outcome, out string cursorReason);
                ReflectionIntentTrace.Emit(() => ReflectionIntentTrace.Outcome(work.Request, result, outcome, cursorReason, cursor.Snapshot.Work != null));
                return true;
            }
            bool saveFailed = outcome.NoteReason == "persistence_failed" || outcome.LessonReason == "persistence_failed"
                || outcome.ReadingReason == "persistence_failed";
            // Persistence retries replay the already accepted proposals. A
            // malformed unfinished chain may ask the model again at the normal
            // cadence; completed chains retain their identities and outcomes.
            var progress = work with { Progress = outcome, Result = saveFailed ? result : null };
            if (SaveReflectionWindow(cursor.DurableSnapshot with { Work = progress })) cursor.SetWork(progress);
            cursor.EndPass(null);
            ReflectionIntentTrace.Emit(() => ReflectionIntentTrace.Outcome(work.Request, result, outcome, completion, cursor.Snapshot.Work != null));
            return true;
        }
    }

    private bool ChainFinished(ReflectionWork work, string chain) => chain switch
    {
        "note" => CompletedChain(work.Progress?.NoteReason) != null || work.Result?.Note == null
            || _reflectionNotes.Any(t => t.OperationId == work.Request.RequestId || t.ReflectionSettlementOperationId == work.Request.RequestId),
        "lesson" => CompletedChain(work.Progress?.LessonReason) != null || work.Result?.Lesson == null
            || _reflectionLessons.Any(t => t.OperationId == work.Request.RequestId),
        _ => CompletedChain(work.Progress?.ReadingReason) != null || work.Result?.Relationship == null
            || _relationshipReadings.Any(t => t.OperationId == work.Request.RequestId)
    };

    internal bool CancelReflectionWork(ReflectionWindowCursor cursor)
    {
        lock (_experienceLock)
        {
            if (!SaveReflectionWindow(cursor.DurableSnapshot with { Work = null })) { cursor.EndPass(null); return false; }
            cursor.SetWork(null);
            cursor.EndPass(null);
            return true;
        }
    }
}
