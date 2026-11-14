using System;
using System.Linq;

namespace LilithBridge;

// Optional per-Work retry budget. Native/provider/source gates remain separate.
// Local declarations are conservative upper bounds for entry permissions,
// not evidence of network sends, server acceptance, charges or exactly-once.
internal sealed record ReflectionRetryPolicy(int Limit, int WindowSeconds, int MinimumGapSeconds)
{
    internal bool Valid => Limit is >= 1 and <= 8 && WindowSeconds is >= 1 and <= 86400
        && MinimumGapSeconds >= 0 && MinimumGapSeconds <= WindowSeconds;
}

internal sealed record ReflectionRetryReturn(ReflectionLocalOutcomeKind Kind, ReflectionLocalSendState SendState,
    DateTimeOffset ObservedAtUtc, string TransportRequestId, string AttemptId, string TransportOutcome, string ParseCode)
{
    internal static ReflectionRetryReturn From(ReflectionLocalOutcome outcome, DateTimeOffset at) => new(
        outcome.Kind, outcome.SendState, at, outcome.TransportRequestId, outcome.AttemptId, outcome.TransportOutcome, outcome.ParseCode);
    internal bool Valid => Enum.IsDefined(typeof(ReflectionLocalOutcomeKind), Kind)
        && Enum.IsDefined(typeof(ReflectionLocalSendState), SendState) && ValidTime(ObservedAtUtc)
        && Token(TransportRequestId) && Token(AttemptId) && Token(TransportOutcome) && Token(ParseCode);
    internal static bool ValidTime(DateTimeOffset at) => at != default && at.Offset == TimeSpan.Zero;
    private static bool Token(string? value) => value != null && value.Length <= 128;
}

internal sealed record ReflectionRetryClaim(string OwnerId, DateTimeOffset DeclaredAtUtc, ReflectionRetryReturn? Return = null)
{
    internal bool Valid => !string.IsNullOrWhiteSpace(OwnerId) && OwnerId.Length <= 128
        && ReflectionRetryReturn.ValidTime(DeclaredAtUtc) && (Return == null || Return.Valid);
}

internal sealed record ReflectionRetryHistory(string RequestId, ReflectionRetryPolicy Policy,
    DateTimeOffset FirstRecordedAtUtc, long RecordedClaims, ReflectionRetryClaim[] Claims)
{
    // No historical attempt count/time is reconstructed for a Work without facts.
    // In this seam, every first observation starts with unknown prior history.
    public bool EarlierAttemptsUnknown { get; init; } = true;
    internal bool Valid(ReflectionWork work) => RequestId == work.Request.RequestId && Policy?.Valid == true
        && ReflectionRetryReturn.ValidTime(FirstRecordedAtUtc) && RecordedClaims > 0
        && Claims is { Length: > 0 } && Claims.Length <= Policy.Limit && RecordedClaims >= Claims.Length
        && Claims.All(c => c != null && c.Valid && c.DeclaredAtUtc >= FirstRecordedAtUtc)
        && Claims.Select(c => c.OwnerId).Distinct(StringComparer.Ordinal).Count() == Claims.Length
        && Claims.Zip(Claims.Skip(1), (a,b) => a.DeclaredAtUtc <= b.DeclaredAtUtc).All(ordered => ordered);
}

internal enum ReflectionRetryPlanKind { Ready, Wait, LocalResult, Unavailable, InvalidHistory, ClockUncertain }
internal sealed record ReflectionRetryPlan(ReflectionRetryPlanKind Kind, DateTimeOffset? NextAtUtc = null,
    int RecordedClaimsInWindow = 0, bool EarlierAttemptsUnknown = true, string LastReturn = "unobserved");
internal enum ReflectionRetryWriteKind { Stored, AlreadyRecorded, Waiting, LocalResult, PersistencePending, Unavailable, InvalidHistory, ClockUncertain }

internal static class ReflectionRetryFacts
{
    internal static ReflectionRetryPlan Evaluate(ReflectionWork? work, ReflectionRetryPolicy proposed, DateTimeOffset now)
    {
        if (work is not { Purpose: ReflectionWorkPurpose.Dialogue }) return new(ReflectionRetryPlanKind.Unavailable);
        // A stored value always takes the existing local recovery route, even
        // when the remote-attempt window is full or optional facts are invalid.
        if (work.Result != null) return new(ReflectionRetryPlanKind.LocalResult);
        if (!ReflectionRetryReturn.ValidTime(now)) return new(ReflectionRetryPlanKind.Unavailable);
        if (work.RetryHistory is not { } history) return new(proposed?.Valid == true
            ? ReflectionRetryPlanKind.Ready : ReflectionRetryPlanKind.Unavailable);
        if (!history.Valid(work)) return new(ReflectionRetryPlanKind.InvalidHistory);
        var last = history.Claims[^1];
        string lastReturn = last.Return?.Kind == ReflectionLocalOutcomeKind.AcceptedResult
            ? "accepted_value_not_in_work" : last.Return?.Kind.ToString() ?? "unobserved";
        if (now < history.FirstRecordedAtUtc || history.Claims.Any(c => c.DeclaredAtUtc > now || c.Return?.ObservedAtUtc > now)
            || history.Claims.Any(c => c.Return is {} r && r.ObservedAtUtc < c.DeclaredAtUtc))
            return new(ReflectionRetryPlanKind.ClockUncertain, EarlierAttemptsUnknown: history.EarlierAttemptsUnknown, LastReturn: lastReturn);
        var policy = history.Policy; // A pending Work keeps the policy that recorded its facts.
        var active = history.Claims.Where(c => now - c.DeclaredAtUtc < TimeSpan.FromSeconds(policy.WindowSeconds)).ToArray();
        DateTimeOffset from = last.Return?.ObservedAtUtc ?? last.DeclaredAtUtc;
        DateTimeOffset next = from.AddSeconds(policy.MinimumGapSeconds);
        if (active.Length >= policy.Limit)
        {
            var budgetAt = active[0].DeclaredAtUtc.AddSeconds(policy.WindowSeconds);
            if (budgetAt > next) next = budgetAt;
        }
        return new(next > now ? ReflectionRetryPlanKind.Wait : ReflectionRetryPlanKind.Ready,
            next > now ? next : null, active.Length, history.EarlierAttemptsUnknown, lastReturn);
    }
}
