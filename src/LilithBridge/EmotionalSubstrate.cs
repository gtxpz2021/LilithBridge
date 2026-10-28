using System;
using System.Collections.Generic;

namespace LilithBridge;

internal readonly record struct EmotionTendencies(
    double Openness,
    double Restraint,
    double Curiosity);

/// <summary>
/// Continuous emotional residue between turns.
///
/// The existing mood enum is still used for the immediate, visible mood label.
/// This substrate is slower: it stores what was nudged by an event and lets it
/// decay over minutes. It is deliberately kept out of the visible bubble; only
/// natural-language fragments are injected into the model context so Lilith
/// can read her own resonance without being told it is a number.
/// </summary>
internal sealed class EmotionalSubstrate
{
    private const double WarmthBaseline = 0.50;
    private const double ClosenessBaseline = 0.34;
    private const double TensionBaseline = 0.18;
    private const double CuriosityBaseline = 0.52;

    private static readonly TimeSpan WarmthDecay = TimeSpan.FromMinutes(12);
    private static readonly TimeSpan ClosenessDecay = TimeSpan.FromMinutes(45);
    private static readonly TimeSpan TensionDecay = TimeSpan.FromSeconds(55);
    private static readonly TimeSpan CuriosityDecay = TimeSpan.FromMinutes(10);

    public double Warmth { get; private set; } = WarmthBaseline;
    public double Closeness { get; private set; } = ClosenessBaseline;
    public double Tension { get; private set; } = TensionBaseline;
    public double Curiosity { get; private set; } = CuriosityBaseline;
    public string LastNudge { get; private set; } = "";
    public DateTimeOffset LastNudgeAt { get; private set; } = DateTimeOffset.MinValue;
    public DateTimeOffset LastTickAt { get; private set; } = DateTimeOffset.UtcNow;

    public void Tick(
        DateTimeOffset now,
        TimePhase phase,
        bool sleeping)
    {
        double seconds = Math.Max(0.0, (now - LastTickAt).TotalSeconds);
        LastTickAt = now;

        double warmthTarget = WarmthBaseline;
        double closenessTarget = ClosenessBaseline;
        double tensionTarget = TensionBaseline + (phase == TimePhase.LateNight ? 0.04 : 0.0);
        double curiosityTarget = CuriosityBaseline
            + (phase == TimePhase.Morning ? 0.08
                : phase == TimePhase.LateNight ? -0.06
                : 0.02);

        if (sleeping)
        {
            warmthTarget += 0.04;
            closenessTarget += 0.02;
            tensionTarget = 0.05;
            curiosityTarget = 0.18;
        }

        Warmth = Approach(Warmth, warmthTarget, seconds, WarmthDecay.TotalSeconds);
        Closeness = Approach(Closeness, closenessTarget, seconds, ClosenessDecay.TotalSeconds);
        Tension = Approach(Tension, tensionTarget, seconds, TensionDecay.TotalSeconds);
        Curiosity = Approach(Curiosity, curiosityTarget, seconds, CuriosityDecay.TotalSeconds);

        if (Math.Abs(Warmth - WarmthBaseline) < 0.015
            && Math.Abs(Closeness - ClosenessBaseline) < 0.015
            && Math.Abs(Tension - TensionBaseline) < 0.015
            && Math.Abs(Curiosity - CuriosityBaseline) < 0.015)
        {
            LastNudge = "";
            LastNudgeAt = DateTimeOffset.MinValue;
        }
    }

    public void ApplyEvent(LilithSceneEvent sceneEvent)
    {
        if (sceneEvent == null)
            return;

        switch (sceneEvent.Kind)
        {
            case SceneEventKind.Touch:
            case SceneEventKind.NativeHitArea:
                Nudge(0.15, 0.09, 0.13, 0.07,
                    "touch:" + (sceneEvent.Area.Length > 0 ? sceneEvent.Area : "player"));
                break;
            case SceneEventKind.LongPress:
                Nudge(0.09, 0.05, 0.20, 0.08, "long_press");
                break;
            case SceneEventKind.DragStart:
                Nudge(0.07, 0.04, 0.28, 0.10, "drag_start");
                break;
            case SceneEventKind.DragEnd:
                Nudge(0.10, 0.06, -0.22, 0.07, "drag_end");
                break;
            case SceneEventKind.UserInput:
                ApplyPlayerWords(sceneEvent.Details);
                break;
            case SceneEventKind.OptionSelected:
                Nudge(0.07, 0.05, 0.03, 0.11, "option_selected");
                break;
            case SceneEventKind.AppForeground:
                Nudge(0.05, 0.04, -0.03, 0.18, "player_app_changed");
                break;
            case SceneEventKind.Proactive:
                Nudge(0.08, 0.05, 0.02, 0.14, "lilith_noticed");
                break;
            case SceneEventKind.NativeStateChanged:
                if (sceneEvent.Actor == "lilith")
                    ApplyStateChange(sceneEvent.Details);
                break;
            case SceneEventKind.RightClick:
                // The original right click is the system tray menu, not a
                // touch. It should not create emotional residue.
                break;
        }
    }

    public void ApplyPlayerWords(string text)
    {
        // Keep the event entry point, but do not infer emotional or relationship
        // changes from words alone. Substrings cannot establish the speaker,
        // target, negation or tone (in particular, a complaint is not a joke).
        // The current utterance is already supplied for contextual understanding.
        // Explicit desktop events and their existing decay still leave residue.
    }

    public void Nudge(
        double warmthDelta,
        double closenessDelta,
        double tensionDelta,
        double curiosityDelta,
        string reason)
    {
        Warmth = ApplyDelta(Warmth, warmthDelta);
        Closeness = ApplyDelta(Closeness, closenessDelta);
        Tension = ApplyDelta(Tension, tensionDelta);
        Curiosity = ApplyDelta(Curiosity, curiosityDelta);
        LastNudge = reason;
        LastNudgeAt = DateTimeOffset.UtcNow;
        NativeEventLog.Trace(
            "emotion.nudge",
            "mod",
            "lilith",
            reason,
            "warmth=" + Warmth.ToString("0.00")
            + ";closeness=" + Closeness.ToString("0.00")
            + ";tension=" + Tension.ToString("0.00")
            + ";curiosity=" + Curiosity.ToString("0.00"));
    }

    public string DescribeForPrompt()
    {
        var parts = new List<string>();

        if (Warmth > 0.72)
            parts.Add("先前互动留下的暖意还很明显");
        else if (Warmth > 0.60)
            parts.Add("刚才那件事带来的温度还没完全散掉");
        else if (Warmth < 0.40)
            parts.Add("现在很安静，没什么特别涌上来的情绪");

        if (Closeness > 0.70)
            parts.Add("和玩家的距离感比平时近了一点点");
        else if (Closeness > 0.56)
            parts.Add("先前互动留下了一点短时的亲近感");

        if (Tension > 0.64)
            parts.Add("刚才的紧张还没完全松开，我想慢半拍");
        else if (Tension > 0.46)
            parts.Add("还带着一点没落定的感觉");

        if (Curiosity > 0.70)
            parts.Add("仍有些好奇");
        else if (Curiosity < 0.34)
            parts.Add("现在好奇心和注意力都很淡");

        return string.Join("；", parts);
    }

    /// <summary>
    /// Exposes the few fixed emotional tendencies that other layers may use as
    /// behavioral modifiers. It never decides behavior; it only returns state
    /// as a small, stable vocabulary for a caller to weight.
    /// </summary>
    public EmotionTendencies GetTendencies()
    {
        double openness = Math.Clamp(Warmth * 0.5 + Closeness * 0.5, 0.0, 1.0);
        double restraint = Math.Clamp(Tension, 0.0, 1.0);
        double curiosity = Math.Clamp(Curiosity, 0.0, 1.0);
        return new EmotionTendencies(openness, restraint, curiosity);
    }

    public EmotionalStateSnapshot ToSnapshot()
    {
        return new EmotionalStateSnapshot
        {
            Warmth = Warmth,
            Closeness = Closeness,
            Tension = Tension,
            Curiosity = Curiosity,
            LastNudge = LastNudge,
            LastNudgeAt = LastNudgeAt,
            LastTickAt = LastTickAt
        };
    }

    public void ApplySnapshot(EmotionalStateSnapshot? snapshot)
    {
        if (snapshot == null)
            return;
        Warmth = Clamp(snapshot.Warmth);
        Closeness = Clamp(snapshot.Closeness);
        Tension = Clamp(snapshot.Tension);
        Curiosity = Clamp(snapshot.Curiosity);
        LastNudge = snapshot.LastNudge ?? "";
        LastNudgeAt = snapshot.LastNudgeAt;
        LastTickAt = snapshot.LastTickAt == default
            ? DateTimeOffset.UtcNow
            : snapshot.LastTickAt;
    }

    public void ResetToBaseline()
    {
        Warmth = WarmthBaseline;
        Closeness = ClosenessBaseline;
        Tension = TensionBaseline;
        Curiosity = CuriosityBaseline;
        LastNudge = "";
        LastNudgeAt = DateTimeOffset.MinValue;
        LastTickAt = DateTimeOffset.UtcNow;
    }

    private void ApplyStateChange(string details)
    {
        // These are complete canonical values from TrackCharacterState, never
        // natural-language details. sit/getup intentionally have no nudge.
        switch (details)
        {
            case "sleep":
                Nudge(0.03, 0.01, -0.12, -0.10, "fell_asleep");
                break;
            case "awake":
                Nudge(0.06, 0.04, 0.07, 0.08, "woke_up");
                break;
            case "lie":
                Nudge(0.04, 0.02, -0.04, -0.03, "lay_down");
                break;
            case "stand":
                Nudge(0.02, 0.02, -0.02, 0.05, "stood_up");
                break;
        }
    }

    private static double Approach(double current, double target, double seconds, double halfLife)
    {
        if (halfLife <= 0.0 || seconds <= 0.0)
            return current;
        double factor = 1.0 - Math.Pow(0.5, seconds / halfLife);
        return Clamp(current + (target - current) * factor);
    }

    private static double ApplyDelta(double current, double delta)
    {
        if (delta >= 0.0)
            return Clamp(current + delta * (1.0 - current));
        return Clamp(current + delta * current);
    }

    private static double Clamp(double value)
    {
        return Math.Clamp(value, 0.0, 1.0);
    }


}

internal sealed class EmotionalStateSnapshot
{
    public double Warmth { get; set; } = 0.50;
    public double Closeness { get; set; } = 0.34;
    public double Tension { get; set; } = 0.18;
    // Read-only compatibility payload: old files may contain this value, but
    // ApplySnapshot ignores it and new snapshots do not write it.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public double? Vitality { get; set; }
    public double Curiosity { get; set; } = 0.52;
    public string LastNudge { get; set; } = "";
    public DateTimeOffset LastNudgeAt { get; set; } = DateTimeOffset.MinValue;
    public DateTimeOffset LastTickAt { get; set; } = DateTimeOffset.UtcNow;
}
