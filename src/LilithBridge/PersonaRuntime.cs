using System;
using System.Collections.Generic;
using System.Linq;

namespace LilithBridge;

/// <summary>
/// Legacy relationship snapshot compatibility. Automatic per-turn scores and
/// keyword milestones are retired; historical fields remain readable/removable
/// without becoming default evidence of the current relationship.
/// </summary>
internal sealed class RelationshipState
{
    public double Familiarity { get; private set; } = 0.10;
    public double Warmth { get; private set; } = 0.34;
    public double Trust { get; private set; } = 0.26;
    public double Comfort { get; private set; } = 0.30;
    public double Boundary { get; private set; } = 0.48;
    public int SharedStories { get; private set; }
    public List<string> Milestones { get; } = new();
    public DateTimeOffset LastUpdateAt { get; private set; } = DateTimeOffset.UtcNow;

    public string Stage { get; private set; } = "初次见面";
    public string Tone { get; private set; } = "害羞但好奇";

    public void ApplyTurn(
        string? userText,
        string? replyText,
        ApiResult? reply,
        SessionState state,
        DateTimeOffset now)
    {
        // Compatibility entry point: ordinary turns, quoted words and model
        // replies do not establish a relationship change or a shared milestone.
        // Keep the saved snapshot intact; source-backed understandings have their
        // own lifecycle and selection outside this legacy numeric container.
    }

    public void ApplySnapshot(RelationshipSnapshot? snapshot)
    {
        if (snapshot == null)
            return;
        Familiarity = Clamp(snapshot.Familiarity);
        Warmth = Clamp(snapshot.Warmth);
        Trust = Clamp(snapshot.Trust);
        Comfort = Clamp(snapshot.Comfort);
        Boundary = Clamp(snapshot.Boundary);
        SharedStories = Math.Max(0, snapshot.SharedStories);
        Milestones.Clear();
        if (snapshot.Milestones != null)
            Milestones.AddRange(snapshot.Milestones.Take(24));
        LastUpdateAt = snapshot.LastUpdateAt == default
            ? DateTimeOffset.UtcNow
            : snapshot.LastUpdateAt;
        RecalculateLabels();
    }

    public RelationshipSnapshot ToSnapshot()
    {
        return new RelationshipSnapshot
        {
            Familiarity = Familiarity,
            Warmth = Warmth,
            Trust = Trust,
            Comfort = Comfort,
            Boundary = Boundary,
            SharedStories = SharedStories,
            Milestones = Milestones.ToList(),
            LastUpdateAt = LastUpdateAt
        };
    }

    public string DescribeForPrompt()
    {
        // Old milestones were created by substring matches without source IDs.
        // Preserve them in snapshots for historical inspection, but do not claim
        // they are verified shared events in every current request.
        return "";
    }

    public void RemoveTextReferences(IReadOnlyList<string> texts)
    {
        Milestones.RemoveAll(m => texts.Any(t => t.Length >= 4 && m.Contains(t,StringComparison.Ordinal)));
    }

    private void RecalculateLabels()
    {
        double familiarity = Familiarity;
        double warmth = Warmth;
        double trust = Trust;
        double comfort = Comfort;
        double boundary = Boundary;

        Stage = familiarity switch
        {
            < 0.18 => "初次见面",
            < 0.38 => "还在认识",
            < 0.62 => "逐渐亲近",
            < 0.82 when trust > 0.55 && comfort > 0.55 => "彼此信任",
            < 0.82 => "很熟悉",
            _ => "彼此的一部分"
        };

        if (familiarity < 0.25 && warmth < 0.50)
            Tone = "害羞但好奇";
        else if (comfort < 0.42 || boundary > 0.66)
            Tone = "嘴上别扭但会认真看你";
        else if (trust > 0.60 && warmth > 0.65)
            Tone = "温柔中带着一点占有欲";
        else if (warmth > 0.62)
            Tone = "骄傲但不掩饰关心";
        else
            Tone = "安静陪伴的多面女孩";
    }

    // Keep headroom: an axis pinned at 1.0 stops describing anything.
    private const double AxisCeiling = 0.98;

    private static double Clamp(double value) => Math.Clamp(value, 0.0, AxisCeiling);
}

internal sealed class RelationshipSnapshot
{
    public double Familiarity { get; set; } = 0.10;
    public double Warmth { get; set; } = 0.34;
    public double Trust { get; set; } = 0.26;
    public double Comfort { get; set; } = 0.30;
    public double Boundary { get; set; } = 0.48;
    public int SharedStories { get; set; }
    public List<string> Milestones { get; set; } = new();
    public DateTimeOffset LastUpdateAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Delivery settlement still calls this compatibility hook. A generated or
/// delivered reply is not a new desktop event and does not feed its own wording,
/// action or emotion labels back into emotional or relationship scores.
/// </summary>
internal static class PersonaRuntime
{
    public static void ObserveReply(ApiResult? result, SessionState state, DateTimeOffset now)
    {
        if (state == null)
            return;
        state.TickRelationshipEvolution();
    }

    public static void ResetToDefaults(SessionState state)
    {
        if (state == null)
            return;
        state.Relationship.ApplySnapshot(null);
        state.TickRelationshipEvolution();
    }
}
