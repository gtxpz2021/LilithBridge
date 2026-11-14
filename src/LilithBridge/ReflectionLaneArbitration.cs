using System;

namespace LilithBridge;

internal enum ReflectionOpportunityLane { None, Current, Older }

// Qualifications are computed separately, with their own material/time facts.
// Debt breaks a tie between eligible lanes; it cannot block the only eligible one.
internal static class ReflectionLaneArbitration
{
    internal static ReflectionOpportunityLane Choose(bool currentReady, bool olderReady, int recentPasses)
        => currentReady && olderReady ? recentPasses >= 2 ? ReflectionOpportunityLane.Older : ReflectionOpportunityLane.Current
            : currentReady ? ReflectionOpportunityLane.Current : olderReady ? ReflectionOpportunityLane.Older : ReflectionOpportunityLane.None;

    // A current soft-wait hint never hides a potentially eligible older lane.
    internal static bool ProbeDue(bool currentProbeDue, bool olderClockAllows, bool olderProbeDue)
        => currentProbeDue || olderClockAllows && olderProbeDue;
}
