using UnityEngine;

namespace LilithBridge;

internal enum NativeAppearanceState { Pending, Appeared, Unobservable }
internal sealed record NativeAppearanceSample(NativeAppearanceState State, int VisibleCharacters = -1);

// Native UI evidence only, never player reading or proof of physical pixels.
// Assigning label.text does not show every character: the native typewriter
// hides a suffix with maxVisibleCharacters until its printing has finished.
internal static class NativeBubbleAppearance
{
    internal static Func<string, NativeAppearanceState>? ObserverForTests { get; set; }
    internal static NativeAppearanceState Observe(string text) => Sample(text).State;
    internal static NativeAppearanceSample Sample(string text)
    {
        if (ObserverForTests is { } injected)
        { try { return new(injected(text)); } catch { return new(NativeAppearanceState.Unobservable); } }
        try
        {
            var ui = UnityEngine.Object.FindObjectOfType<DialogueBubbleUI>(true);
            if (ui == null) return new(NativeAppearanceState.Pending);
            var label = ui._dialogueText;
            if (label == null || label.canvasRenderer == null) return new(NativeAppearanceState.Unobservable);
            if (!ui._isShowing || !ui.gameObject.activeInHierarchy || !label.isActiveAndEnabled
                || !string.Equals(label.text, text, StringComparison.Ordinal)
                || label.color.a <= 0 || label.canvasRenderer.GetAlpha() <= 0 || label.canvasRenderer.cull)
                return new(NativeAppearanceState.Pending);
            foreach (var group in ui.GetComponentsInParent<CanvasGroup>(true))
                if (group.alpha <= 0) return new(NativeAppearanceState.Pending);
            var info = label.textInfo;
            if (info == null || info.characterCount <= 0) return new(NativeAppearanceState.Pending);
            int visible = Math.Clamp(label.maxVisibleCharacters, 0, info.characterCount);
            if (ui._typewriter?.IsTyping == true || visible < info.characterCount
                || label.firstVisibleCharacter > 0 || label.maxVisibleLines < info.lineCount
                || label.maxVisibleWords < info.wordCount)
                return new(NativeAppearanceState.Pending, visible);
            return new(NativeAppearanceState.Appeared, visible);
        }
        catch { return new(NativeAppearanceState.Unobservable); }
    }
}

// Wait for the whole native text to appear. Printing may take longer than the
// initial appearance timeout; strict visible progress renews the stall clock.
// Stalling, rewinding, losing observability or cancellation never invents a
// full display receipt, nor permits an automatic second native submission.
internal sealed class NativeAppearanceWait
{
    internal const double TimeoutSeconds = 3;
    private readonly string _text;
    private readonly double _acceptedAt;
    private readonly int _acceptedFrame;
    private int _visibleCharacters;
    private double _lastProgressAt;
    private WorkDisplayOutcome _outcome = WorkDisplayOutcome.Accepted;
    internal NativeAppearanceWait(string text, double acceptedAt, int acceptedFrame)
    { _text = text; _acceptedAt = _lastProgressAt = acceptedAt; _acceptedFrame = acceptedFrame; }
    internal WorkDisplayOutcome Poll(double now, int frame, bool atCancellation = false)
    {
        if (_outcome != WorkDisplayOutcome.Accepted) return _outcome;
        if (!atCancellation && frame <= _acceptedFrame) return _outcome;
        return PollObserved(NativeBubbleAppearance.Sample(_text), now, frame, atCancellation);
    }
    internal WorkDisplayOutcome PollObserved(NativeAppearanceSample observed, double now, int frame, bool atCancellation = false)
    {
        if (_outcome != WorkDisplayOutcome.Accepted) return _outcome;
        if (!atCancellation && frame <= _acceptedFrame) return _outcome;
        if (observed.State == NativeAppearanceState.Appeared) return _outcome = WorkDisplayOutcome.Shown;
        if (observed.State == NativeAppearanceState.Unobservable || atCancellation)
            return _outcome = WorkDisplayOutcome.Unknown;
        if (observed.VisibleCharacters >= 0)
        {
            if (observed.VisibleCharacters < _visibleCharacters) return _outcome = WorkDisplayOutcome.Unknown;
            if (observed.VisibleCharacters > _visibleCharacters)
            { _visibleCharacters = observed.VisibleCharacters; _lastProgressAt = now; }
        }
        if (now - _lastProgressAt >= TimeoutSeconds) _outcome = WorkDisplayOutcome.Unknown;
        return _outcome;
    }
}
