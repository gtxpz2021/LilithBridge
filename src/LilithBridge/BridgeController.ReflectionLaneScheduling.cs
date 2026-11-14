using System;
using System.Linq;

namespace LilithBridge;

internal sealed partial class BridgeController
{
    // Reuse the shipped empty-queue probe policy for the independently empty old
    // lane. This caches no source permission, body, eligibility or pending work.
    private readonly ReflectionIdleCadence _reflectionOlderLaneProbe = new();
    private long _reflectionIndependentWaitEpoch = -1;

    private static bool ReflectionLaneEmpty(ReflectionHistorySelection s) =>
        s.History.Count == 0 && s.AvailableDialogueLines == 0 && !s.BudgetLimited && s.PendingReason.Length == 0;

    private bool ReflectionLaneQualified(ReflectionHistorySelection s, double idle, bool changed,
        bool current, bool? windowQualified)
    {
        if (s.History.Count == 0) return false;
        return RuntimeDirector.ShouldReflect(_config, _systemReady, false, false, false,
            _annotationInProgress, _noteSupplementInProgress, _reflectionInProgress,
            idle, MemoryStore.CountReflectionDialogueLines(s.History), changed, false, false,
            _memory.HasResumedReflectionExchange(s.History),
            s.History.Any(t => t.Role == "user" && !t.ReflectionExchangeComplete)
                || s.History.FirstOrDefault()?.Role == "assistant",
            s.BudgetLimited ? s.AvailableDialogueLines : 0, current, windowQualified);
    }

    // Entered only after all original recovery/provider/native-state guards, for
    // the configured current-dialogue cadence. No work is started here
    // except through the existing QueueReflectionPreparation owner.
    private void TryScheduleIndependentReflectionLanes(double nativeIdle)
    {
        float now = ReflectionClock();
        string scope = ReflectionDialogueOpportunityScope;
        long material = _memory.ReflectionMaterialGeneration, request = _reflectionWindow.ModelRequestVersion;
        long facts = _memory.MeasureMemoryActivity();
        _reflectionOlderLaneProbe.Observe(scope, _conversationGeneration, material, facts, request);
        bool changed = facts != _reflectionWindow.FactActivity;
        double? activityIdle = _reflectionActivityObservation.DialogueIdleSeconds(now);
        double olderIdle = RuntimeDirector.ReflectionSelectionIdleSeconds(_config, false, false, nativeIdle, activityIdle);
        bool olderClockAllows = now >= _nextReflectionAt && _reflectionProgressiveCadence.Due(now)
            && RuntimeDirector.ReflectionIdleOpportunity(_config, olderIdle, changed);
        bool currentProbeDue = _reflectionIndependentWaitEpoch != _reflectionProgressiveCadence.Epoch
            || _reflectionDialogueOpportunityWait.ProbeDue(_config, scope, material, request, false, now);
        if (!ReflectionLaneArbitration.ProbeDue(currentProbeDue, olderClockAllows, _reflectionOlderLaneProbe.IdleProbeDue(now)))
            return;
        if (!_reflectionCadence.IdleProbeDue(now)) return;

        var current = _memory.SelectCurrentReflectionHistory(_reflectionWindow.Snapshot);
        ReflectionHistorySelection? older = null;
        // Cheap clock predicates may rule out the old lane, but never authorize
        // it. Resolve current sources only once those necessary clocks allow it.
        if (olderClockAllows && _reflectionOlderLaneProbe.IdleProbeDue(now))
        {
            older = _memory.SelectIndependentOlderReflectionHistory(_reflectionWindow.Snapshot, current);
            if (ReflectionLaneEmpty(older)) _reflectionOlderLaneProbe.EmptyProbe(now);
            else _reflectionOlderLaneProbe.MaterialPending();
        }
        bool currentDialogue = CurrentDialogueReflection(current);
        var plan = ReflectionDialogueOpportunity.Evaluate(_config, current, _reflectionWindow.Snapshot,
            _memory.ReviewSessionIdentity, currentDialogue, false, DateTimeOffset.UtcNow);
        bool currentReady = currentDialogue && plan.RemainingSeconds <= 0
            && ReflectionOpportunityDue(true, false)
            && ReflectionLaneQualified(current, nativeIdle, changed, true, plan.WindowQualified);
        bool olderReady = older != null && olderClockAllows
            && ReflectionOpportunityDue(false, false)
            && ReflectionLaneQualified(older, olderIdle, changed, false, null);
        var lane = ReflectionLaneArbitration.Choose(currentReady, olderReady,
            _reflectionWindow.Snapshot.RecentPassesSinceBacklog);

        if (lane == ReflectionOpportunityLane.None)
        {
            if (ReflectionLaneEmpty(current) && older != null && ReflectionLaneEmpty(older))
                _reflectionCadence.EmptyProbe(now);
            else _reflectionCadence.MaterialPending();
            // Retain only a known positive current soft wait. The cheap old-lane
            // clock check above can wake earlier; a separately empty old lane
            // uses the existing bounded empty probe rather than scanning again.
            float wait = current.History.Count > 0 ? plan.RemainingSeconds
                : Math.Max(0f, Math.Max(_nextReflectionAt, _reflectionProgressiveCadence.NextAt) - now);
            if (wait > 0)
            {
                _reflectionDialogueOpportunityWait.Wait(_config, scope, material, request, now + wait);
                _reflectionIndependentWaitEpoch = _reflectionProgressiveCadence.Epoch;
                TraceReflectionScheduleWait(current.History.Count > 0 ? plan.WaitReason : "independent_older_cooldown",
                    now + wait, current.History.Count > 0 ? current : older ?? current);
            }
            else _reflectionDialogueOpportunityWait.Clear();
            return;
        }

        var selected = lane == ReflectionOpportunityLane.Current ? current : older!;
        string latest = selected.History.LastOrDefault()?.EventId ?? "";
        if (latest.Length == 0 || !selected.Recent && latest == _reflectionWindow.InputId) return;
        // Debt only selected between eligible, disjoint windows. Do not apply
        // the original mixed-prefix fair replacement again after this choice.
        bool pickedCurrent = lane == ReflectionOpportunityLane.Current;
        // Preserve the existing B1 handoff only if this same current window
        // also meets the old pacing. The new material clock authorizes ordinary
        // dialogue alone; it must not lend that opportunity to another purpose.
        bool ordinaryCurrentOnly = pickedCurrent && plan.AgeKnown
            && !ReflectionLaneQualified(current, nativeIdle, changed, true, null);
        if (!QueueReflectionPreparation(selected, null, ordinaryCurrentOnly)) return;
        _reflectionDialogueOpportunityWait.Clear();
        _lastReflectionScheduleWait = "";
        _reflectionScheduleWaitProgressive = false;
        double selectedIdle = pickedCurrent ? nativeIdle : olderIdle;
        NativeEventLog.Trace("memory.reflection.start", "mod", "system", "Lilith started a quiet reflection",
            "entries=" + _memory.CountAutonomousMemoriesReadOnly()
            + ";test=false;trigger=" + RuntimeDirector.DescribeReflectionTrigger(_config, selectedIdle, changed, false, false)
            + ";new_turns=" + MemoryStore.CountReflectionDialogueLines(selected.History)
            + ";source_fragments=" + selected.History.Count
            + ";selection_lane=" + (selected.Recent ? "recent" : "backlog")
            + ";scheduling_reason=independent_lane_admission;current_dialogue=" + pickedCurrent
            + ";current_eligible=" + currentReady + ";older_eligible=" + olderReady
            + ";dialogue_cadence=true;ordinary_current_only=" + ordinaryCurrentOnly + ";admitted_current_window_reason=" + plan.WindowReason
            + ";admitted_current_window_age_known=" + plan.AgeKnown
            + ";progressive_step=" + _reflectionProgressiveCadence.Step
            + ";resumed_pending=" + _memory.HasResumedReflectionExchange(selected.History)
            + ";budget_limited=" + selected.BudgetLimited + ";available_new_turns=" + selected.AvailableDialogueLines
            + ";admission_idle_seconds=" + selectedIdle.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)
            + ";admission_idle_source=" + (pickedCurrent && plan.AgeKnown ? "current_material_age_and_service"
                : !_config.ReflectionOnlyWhenIdle ? "idle_gate_disabled"
                : !pickedCurrent && activityIdle.HasValue ? "native_and_mod_activity" : "native")
            + ReflectionActivityEvidence());
    }
}
