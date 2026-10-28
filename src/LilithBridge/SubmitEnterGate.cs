namespace LilithBridge;

// Only the Enter opening edge after a formal UI submission is guarded. Sampling
// continues without focus so an already held key is not replayed on focus return.
internal sealed class SubmitEnterGate
{
    private bool _down;
    private bool _awaitRelease;
    private float _reopenAfter = float.NegativeInfinity;

    internal void Submitted(float now)
    {
        _down = true;
        _awaitRelease = true;
        _reopenAfter = now + .18f;
    }

    internal bool Read(bool down, bool focused, bool listening, float now)
    {
        bool edge = down && !_down;
        _down = down;
        if (!down) _awaitRelease = false;
        if (!edge || !focused) return false;
        return listening || (!_awaitRelease && now >= _reopenAfter);
    }
}
