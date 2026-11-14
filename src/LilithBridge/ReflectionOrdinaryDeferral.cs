using System;

namespace LilithBridge;

// A bounded explicit preference, not a null-result inference or a new task.
// The receipt belongs to one accepted operation and one runtime session.
internal sealed record ReflectionOrdinaryDeferral(string RequestId, string SessionId,
    DateTimeOffset ObservedAtUtc, DateTimeOffset UntilUtc)
{
    internal const int DelaySeconds = 300; // Reversible engineering initial value, not an empirical optimum.
    internal bool Valid => !string.IsNullOrWhiteSpace(RequestId) && !string.IsNullOrWhiteSpace(SessionId)
        && ObservedAtUtc != default && UntilUtc - ObservedAtUtc == TimeSpan.FromSeconds(DelaySeconds);

    internal static double RemainingSeconds(ReflectionWindowSnapshot receipt, string session, bool requested, DateTimeOffset now)
    {
        // Recovery, another session, absent legacy data or an uncertain clock
        // cannot turn this optional wait into an execution or lifetime barrier.
        if (requested || receipt.Work != null || receipt.OrdinaryDeferral is not {} value
            || !value.Valid || value.SessionId != session || now < value.ObservedAtUtc) return 0;
        return Math.Max(0, (value.UntilUtc - now).TotalSeconds);
    }
}
