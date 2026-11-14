using System;
using System.Linq;

namespace LilithBridge;

// A terminal presenter observation tied to one exact whole-group identity.
internal sealed record ReflectionDeliveryTimeFact(string GroupKey, DateTimeOffset? CompletedAtUtc);

internal sealed record ReflectionDeliveryTiming(ReflectionDeliveryTimeFact[] Groups)
{
    public int KnownCount => Groups.Count(g => g.CompletedAtUtc is { } at && at != default);
    // Selected prefix only. One unknown group means no claim about its oldest
    // completion. Later messages do not rewrite the earlier group's timestamp.
    public DateTimeOffset? OldestCompletedAtUtc => Groups.Length > 0 && KnownCount == Groups.Length
        ? Groups.Min(g => g.CompletedAtUtc) : null;
    public double? AgeSeconds(DateTimeOffset now) => OldestCompletedAtUtc is { } at && now >= at
        ? (now - at).TotalSeconds : null;
}

// Normal dialogue coverage only. This sample is taken at its successful receipt
// transaction boundary, not model response time or a claim that a note exists.
// It gives no timing permission, pause intent, or autonomous follow-up request.
internal sealed record ReflectionServiceCompletion(string RequestId, string CompletedInSessionId,
    bool CurrentSessionCoverage, DateTimeOffset CoverageCompletionObservedAtUtc)
{
    public bool IsValid => !string.IsNullOrEmpty(RequestId) && !string.IsNullOrEmpty(CompletedInSessionId)
        && CoverageCompletionObservedAtUtc != default;
}
