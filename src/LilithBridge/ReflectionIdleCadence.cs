using System;
namespace LilithBridge;
// Soft waits only. This class never authorizes a pass or owns coverage/work.
// A candidate sequence, not an optimized or user-selected set of constants.
internal sealed class ReflectionIdleCadence
{
    private static readonly float[] Delays = { 60f, 180f, 300f, 480f, 780f };
    private string _scope = "", _retryKey = "", _attemptKey = "";
    private long _conversation, _material, _facts, _request, _epoch, _attemptEpoch;
    private bool _observed, _attempt;
    internal int EmptyStep { get; private set; }
    internal float NextIdleProbeAt { get; private set; }
    internal int RetryStep { get; private set; }
    internal float RetryAt { get; private set; }
    internal void Observe(string scope, long conversation, long material, long facts, long request)
    {
        if (_observed && scope == _scope && conversation == _conversation && material == _material
            && facts == _facts && request == _request) return;
        _observed = true; _scope = scope; _conversation = conversation; _material = material;
        _facts = facts; _request = request; _epoch++;
        EmptyStep = RetryStep = 0; NextIdleProbeAt = RetryAt = 0; _retryKey = "";
        // Preserve the captured epoch: an older in-flight result cannot undo this reset.
    }
    internal bool IdleProbeDue(float now) => now >= NextIdleProbeAt;
    internal void EmptyProbe(float now)
    {
        NextIdleProbeAt = now + Delays[Math.Min(EmptyStep, Delays.Length - 1)];
        EmptyStep = Math.Min(EmptyStep + 1, Delays.Length);
    }
    internal void MaterialPending() { EmptyStep = 0; NextIdleProbeAt = 0; }
    internal bool RetryDue(float now, string workId) => workId != _retryKey || now >= RetryAt;
    internal void Started(string workId) { _attempt = true; _attemptKey = workId; _attemptEpoch = _epoch; }
    internal void Completed(float now, bool coverageAdvanced, bool retryableFailure)
    {
        if (!_attempt) return;
        _attempt = false;
        if (_attemptEpoch != _epoch) return;
        if (coverageAdvanced) { Covered(); return; }
        if (!retryableFailure) return; // accepted local commit is not a model failure
        if (_retryKey != _attemptKey) RetryStep = 0;
        _retryKey = _attemptKey;
        RetryAt = now + Delays[Math.Min(RetryStep, Delays.Length - 1)];
        RetryStep = Math.Min(RetryStep + 1, Delays.Length);
    }
    internal void Covered() { RetryStep = 0; RetryAt = 0; _retryKey = ""; MaterialPending(); }
}
