using System;

namespace LilithBridge;

// Successful ordinary old-material service has its own soft rhythm. This is
// neither a model-failure retry policy nor an authorization to run reflection.
// The candidate intervals deliberately reuse the existing idle sequence.
internal sealed class ReflectionProgressiveCadence
{
    private static readonly float[] Delays = { 60f, 180f, 300f, 480f, 780f };
    private string _scope = "", _attemptKey = "";
    private long _conversation, _request, _epoch, _attemptEpoch;
    private bool _observed, _attempt, _progressive;
    internal int Step { get; private set; }
    internal float NextAt { get; private set; }
    internal long Epoch => _epoch;

    internal void Observe(string scope, long conversation, long request)
    {
        if (_observed && scope == _scope && conversation == _conversation && request == _request) return;
        _observed = true; _scope = scope; _conversation = conversation; _request = request; _epoch++;
        Step = 0; NextAt = 0;
        // Keep the attempt's epoch: a late old result must not reinstall a wait.
    }

    internal bool Due(float now) => now >= NextAt;
    internal float Remaining(float now) => Math.Max(0f, NextAt - now);

    internal void Started(string workId, bool progressive)
    {
        _attempt = true; _attemptKey = workId; _progressive = progressive; _attemptEpoch = _epoch;
    }

    internal void Completed(float now, bool coverageAdvanced)
    {
        // Accepted local persistence recovery can complete this same operation
        // later without another send. Failure never earns a success interval.
        if (!_attempt || !coverageAdvanced) return;
        _attempt = false;
        if (!_progressive || _attemptEpoch != _epoch) return;
        NextAt = now + Delays[Math.Min(Step, Delays.Length - 1)];
        Step = Math.Min(Step + 1, Delays.Length);
    }

    // Cancellation is not successful service. A late result must not abandon
    // a different attempt, and neither cancellation nor absence earns a step.
    internal void Abandon(string workId)
    {
        if (_attempt && workId == _attemptKey) _attempt = false;
    }
}
