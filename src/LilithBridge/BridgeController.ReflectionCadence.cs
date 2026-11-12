namespace LilithBridge;
internal sealed partial class BridgeController
{
    private readonly ReflectionIdleCadence _reflectionCadence = new();
    private readonly ReflectionProgressiveCadence _reflectionProgressiveCadence = new();
    // Only a durably completed normal pass may yield its ordinary cooldown to
    // a different, current dialogue opportunity. Frozen work and failures may not.
    private bool _reflectionDialogueMayBypassCooldown;
    private string _lastReflectionScheduleWait = "";
    private float _lastReflectionScheduleWaitUntil;
    private bool _reflectionScheduleWaitProgressive;
    private long _reflectionScheduleWaitMaterial = -1;
    private long _reflectionScheduleWaitEpoch = -1;
    private void RefreshReflectionCadence()
    {
        if (_memory == null) return;
        _reflectionCadence.Observe(_memory.SemanticDataRoot + "/" + _memory.ReviewSessionIdentity,
            _conversationGeneration, _memory.ReflectionMaterialGeneration,
            _memory.MeasureMemoryActivity(), _reflectionWindow.ModelRequestVersion);
        // Own note/lesson writes change facts. They must not reset successful
        // old-material pacing and turn every pass back into a first interval.
        _reflectionProgressiveCadence.Observe(_memory.SemanticDataRoot + "/" + _memory.ReviewSessionIdentity,
            _conversationGeneration, _reflectionWindow.ModelRequestVersion);
    }
    // Same production selection step, isolated from Unity/provider gates for tests.
    // The caller still owns every original admission and resource check.
    private ReflectionHistorySelection? InspectReflectionCadence(bool testTrigger)
    {
        if (!testTrigger && !_reflectionCadence.IdleProbeDue(ReflectionClock())) return null;
        var selection = _memory.SelectCurrentReflectionHistory(_reflectionWindow.Snapshot);
        if (selection.History.Count == 0 && selection.PendingReason.Length == 0 && !selection.BudgetLimited)
            selection = _memory.SelectPendingReflectionHistory(_reflectionWindow.Snapshot);
        if (selection.History.Count == 0)
        {
            // A blocked group/open delivery is pending, not an empty idle queue.
            if (selection.AvailableDialogueLines == 0 && !selection.BudgetLimited && selection.PendingReason.Length == 0)
                _reflectionCadence.EmptyProbe(ReflectionClock());
            else _reflectionCadence.MaterialPending();
            return null;
        }
        _reflectionCadence.MaterialPending();
        return selection;
    }
    private void CompleteReflectionCadenceAttempt(bool coverageAdvanced, bool retryableFailure)
    {
        RefreshReflectionCadence();
        _reflectionCadence.Completed(ReflectionClock(), coverageAdvanced, retryableFailure);
        _reflectionProgressiveCadence.Completed(ReflectionClock(), coverageAdvanced);
        _reflectionDialogueMayBypassCooldown = coverageAdvanced;
    }

    // Resume can also clear Work by cancelling an invalid unfinished response.
    // Applied chains and a legal consumption reason distinguish durable
    // coverage from that cancellation; an accepted pending save keeps its attempt.
    private void CompleteResumedReflectionCadence(string workId, string reason,
        ReflectionChainOutcome? chains, bool localRecovery)
    {
        bool pending = _reflectionWindow.Snapshot.Work != null;
        bool covered = !pending && chains != null
            && !ReflectionWindow.RetainsForRetry(ReflectionWindow.CompletionReason(reason, chains));
        if (localRecovery)
        {
            // Preserve the original local-recovery cooldown/bypass policy.
            if (covered)
            {
                _reflectionCadence.Covered();
                RefreshReflectionCadence();
                _reflectionProgressiveCadence.Completed(ReflectionClock(), true);
            }
        }
        else
            CompleteReflectionCadenceAttempt(covered, _reflectionWindow.Snapshot.Work is { Result: null });
        if (!pending && !covered) _reflectionProgressiveCadence.Abandon(workId);
    }

    private bool CurrentDialogueReflection(ReflectionHistorySelection selection) =>
        selection.Recent && selection.RecentGroups is { Length: > 0 } groups
        && groups.All(g => g.SessionId == _memory.ReviewSessionIdentity);

    // A pure clock seam; provider/resource/source/owner admission stays with the
    // caller. It is never consulted as a way around frozen-work retry handling.
    private bool ReflectionOpportunityDue(bool currentDialogue, bool requestedByModel) =>
        ReflectionClock() >= _nextReflectionPreparationAt
        && (ReflectionClock() >= _nextReflectionAt
            || _reflectionDialogueMayBypassCooldown && (currentDialogue || requestedByModel));

    // A known old-lane wait need not rescan the archive on every Unity frame.
    // New dialogue/request resets NextAt; actual new raw material wakes a fresh
    // selection. No source eligibility or body is cached by this local shortcut.
    private bool ReflectionProgressiveProbeDue() =>
        !_reflectionScheduleWaitProgressive || _reflectionWindow.ModelRequested
        || _reflectionProgressiveCadence.Epoch != _reflectionScheduleWaitEpoch
        || _memory.ReflectionMaterialGeneration != _reflectionScheduleWaitMaterial
        || ReflectionClock() >= Math.Max(_reflectionProgressiveCadence.NextAt, _lastReflectionScheduleWaitUntil);

    private void TraceReflectionScheduleWait(string reason, float until, ReflectionHistorySelection selection)
    {
        _reflectionScheduleWaitProgressive = !CurrentDialogueReflection(selection) && !_reflectionWindow.ModelRequested;
        _reflectionScheduleWaitMaterial = _memory.ReflectionMaterialGeneration;
        _reflectionScheduleWaitEpoch = _reflectionProgressiveCadence.Epoch;
        if (_lastReflectionScheduleWait == reason && _lastReflectionScheduleWaitUntil == until) return;
        _lastReflectionScheduleWait = reason; _lastReflectionScheduleWaitUntil = until;
        NativeEventLog.Trace("memory.reflection.schedule_wait", "mod", "system", "selected material has a soft scheduling wait; source admission has not run",
            "soft_wait=true;reason=" + reason + ";remaining_seconds=" + Math.Max(0f, until - ReflectionClock()).ToString("F3", System.Globalization.CultureInfo.InvariantCulture)
            + ";progressive_step=" + _reflectionProgressiveCadence.Step + ";selection_lane=" + (selection.Recent ? "recent" : "backlog")
            + ";current_dialogue=" + CurrentDialogueReflection(selection));
    }
}
