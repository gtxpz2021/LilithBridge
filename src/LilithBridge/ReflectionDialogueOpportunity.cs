using System;
using System.Linq;

namespace LilithBridge;

internal sealed record ReflectionDialogueOpportunityPlan(bool Applies, bool? WindowQualified,
    float RemainingSeconds, string WindowReason, string WaitReason, bool AgeKnown)
{
    internal static readonly ReflectionDialogueOpportunityPlan Original = new(false, null, 0, "original", "", false);
}

// No owner, work, coverage or model calls here. This is a soft qualification of
// the already selected current window. Existing execution/source guards remain;
// only this window's pure silence pacing is replaced by age and service spacing.
internal static class ReflectionDialogueOpportunity
{
    internal static bool Enabled(BridgeConfig c) => c.ReflectionDialogueServiceSeconds is > 0 and <= 86400
        && c.ReflectionDialogueAgeSeconds is > 0 and <= 86400;

    internal static ReflectionDialogueOpportunityPlan Evaluate(BridgeConfig c, ReflectionHistorySelection selected,
        ReflectionWindowSnapshot receipt, string session, bool current, bool requested, DateTimeOffset now)
    {
        if (!Enabled(c) || !current || requested || receipt.Work != null)
            return ReflectionDialogueOpportunityPlan.Original;
        double serviceWait = 0;
        if (receipt.LastCurrentDialogueService is { CurrentSessionCoverage: true } service && service.IsValid
            && service.CompletedInSessionId == session && now >= service.CoverageCompletionObservedAtUtc)
            serviceWait = Math.Max(0, c.ReflectionDialogueServiceSeconds
                - (now - service.CoverageCompletionObservedAtUtc).TotalSeconds);
        double? age = selected.DeliveryTiming is {} timing && selected.RecentGroups is { Length: > 0 } groups
            && timing.Groups.Select(g => g.GroupKey).SequenceEqual(groups.Select(g => g.Key))
            && timing.Groups.All(g => g.CompletedAtUtc is {} at && at <= now)
            ? timing.AgeSeconds(now) : null;
        int lines = MemoryStore.CountReflectionDialogueLines(selected.History);
        // Even a fast three-exchange batch waits for its oldest delivered
        // material. Quantity alone does not trigger a pass; new messages do not
        // restart that age. Unknown timestamps keep the original policy.
        bool? qualified = age.HasValue ? lines >= 2 && age.Value >= c.ReflectionDialogueAgeSeconds : null;
        double ageWait = qualified == false && lines >= 2 ? Math.Max(0, c.ReflectionDialogueAgeSeconds - age!.Value) : 0;
        string windowReason = !age.HasValue ? "original_age_unknown"
            : qualified == true ? "oldest_selected_age" : "waiting_selected_age";
        double wait = Math.Max(serviceWait, ageWait);
        string waitReason = wait <= 0 ? "" : serviceWait >= ageWait ? "dialogue_service_interval" : "dialogue_selected_age";
        return new(true, qualified, (float)wait, windowReason, waitReason, age.HasValue);
    }
}

// Suppresses repeated archive selection during a known positive soft wait. A
// new event wakes re-evaluation but never changes the underlying delivery time.
// Expiry permits fresh checks; it does not enqueue or catch up model calls.
internal sealed class ReflectionDialogueOpportunityWait
{
    private string _scope = "";
    private long _material, _request;
    private int _service, _age, _floor;
    private float _until;
    internal void Clear() => _until = 0;
    internal bool ProbeDue(BridgeConfig c, string scope, long material, long request, bool requested, float now)
        => !ReflectionDialogueOpportunity.Enabled(c) || requested || now >= _until || scope != _scope
            || material != _material || request != _request || c.ReflectionDialogueServiceSeconds != _service
            || c.ReflectionDialogueAgeSeconds != _age || c.ReflectionMinNewTurns != _floor;
    internal void Wait(BridgeConfig c, string scope, long material, long request, float until)
    {
        _scope=scope; _material=material; _request=request; _service=c.ReflectionDialogueServiceSeconds;
        _age=c.ReflectionDialogueAgeSeconds; _floor=c.ReflectionMinNewTurns; _until=until;
    }
}
