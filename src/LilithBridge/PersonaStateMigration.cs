using System;
using System.Collections.Generic;

namespace LilithBridge;

/// <summary>
/// One-time, deterministic migration for the local persona/relationship file.
/// Older or partially corrupted files are lifted into the current
/// RelationshipSnapshot without resetting a long-lived pairing back to the
/// "first meeting" baseline.
/// </summary>
internal static class PersonaStateMigration
{
    /// <summary>Bump when the persisted persona schema changes in a way that
    /// needs a migration path (not when only labels change).</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// Build a semantically plausible relationship from the legacy fields that
    /// existed before the continuous relationship vector was introduced.
    /// </summary>
    public static RelationshipSnapshot BuildRelationship(
        int interactionCount,
        string? oldStage,
        string? oldTone)
    {
        int count = Math.Max(0, interactionCount);
        double familiarity = FamiliarityFor(count, oldStage);
        double warmth = WarmthFor(count, oldTone);
        double trust = TrustFor(count, oldStage);
        double comfort = ComfortFor(count, oldStage);
        double boundary = BoundaryFor(oldTone);

        return new RelationshipSnapshot
        {
            Familiarity = Clamp(familiarity, 0.05, 0.95),
            Warmth = Clamp(warmth, 0.10, 0.90),
            Trust = Clamp(trust, 0.10, 0.90),
            Comfort = Clamp(comfort, 0.10, 0.90),
            Boundary = Clamp(boundary, 0.10, 0.90),
            SharedStories = 0,
            Milestones = new List<string>(),
            LastUpdateAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>
    /// True only when the file carries no usable relationship snapshot (block absent, or
    /// its primary field missing/unreadable). The earlier rule also migrated when the
    /// interaction count was high and familiarity looked low — that replaced valid
    /// snapshots whose numbers happen to resemble defaults, and it was what hid the
    /// persona read-back defect for months (X1, 6D audit 2026-09-25). Values no longer
    /// decide migration; shape does.
    /// </summary>
    public static bool NeedsMigration(RelationshipSnapshot? relationship) => relationship == null;

    private static double FamiliarityFor(int count, string? stage)
    {
        double countFactor = 0.10 + 0.05 * Math.Sqrt(count);
        double stageFactor = StageFamiliarity(stage);
        return Math.Max(countFactor, stageFactor);
    }

    private static double WarmthFor(int count, string? tone)
    {
        double toneWarmth = ToneWarmth(tone);
        double countBoost = 0.015 * Math.Sqrt(count);
        return toneWarmth + countBoost;
    }

    private static double TrustFor(int count, string? stage)
    {
        double trust = 0.26 + 0.03 * Math.Sqrt(count);
        if (IsHighStage(stage))
            trust = Math.Max(trust, 0.60);
        return trust;
    }

    private static double ComfortFor(int count, string? stage)
    {
        double comfort = 0.30 + 0.028 * Math.Sqrt(count);
        if (IsHighStage(stage))
            comfort = Math.Max(comfort, 0.58);
        return comfort;
    }

    private static double BoundaryFor(string? tone)
    {
        string value = (tone ?? "").ToLowerInvariant();
        if (value.Contains("傲娇", StringComparison.Ordinal)
            || value.Contains("别扭", StringComparison.Ordinal))
            return 0.52;
        if (value.Contains("占有", StringComparison.Ordinal))
            return 0.50;
        if (value.Contains("温柔", StringComparison.Ordinal)
            || value.Contains("关心", StringComparison.Ordinal))
            return 0.44;
        return 0.48;
    }

    private static double StageFamiliarity(string? stage)
    {
        string value = (stage ?? "").Trim();
        if (value.Length == 0)
            return 0.10;
        if (ContainsAny(value, "初次见面", "初见"))
            return 0.14;
        if (ContainsAny(value, "还在认识", "认识中", "相识", "认识"))
            return 0.32;
        if (ContainsAny(value, "逐", "亲近"))
            return 0.55;
        if (ContainsAny(value, "陪伴者", "很熟悉", "熟悉"))
            return 0.60;
        if (ContainsAny(value, "彼此信任"))
            return 0.78;
        if (ContainsAny(value, "彼此的一部分"))
            return 0.88;
        if (ContainsAny(value, "暧昧", "恋人", "恋爱"))
            return 0.80;
        return 0.10;
    }

    private static double ToneWarmth(string? tone)
    {
        string value = (tone ?? "").Trim();
        if (value.Length == 0)
            return 0.50;
        if (value.Contains("占有", StringComparison.Ordinal))
            return 0.62;
        if (value.Contains("温柔", StringComparison.Ordinal)
            || value.Contains("多面", StringComparison.Ordinal)
            || value.Contains("关心", StringComparison.Ordinal))
            return 0.60;
        if (value.Contains("傲娇", StringComparison.Ordinal)
            || value.Contains("别扭", StringComparison.Ordinal))
            return 0.55;
        if (value.Contains("害羞", StringComparison.Ordinal))
            return 0.45;
        return 0.50;
    }

    private static bool IsHighStage(string? stage)
    {
        string value = (stage ?? "").Trim();
        return value.Contains("彼此信任", StringComparison.Ordinal)
            || value.Contains("彼此的一部分", StringComparison.Ordinal)
            || value.Contains("恋人", StringComparison.Ordinal)
            || value.Contains("暧昧", StringComparison.Ordinal);
    }

    private static double Clamp(double value, double min, double max)
    {
        return Math.Clamp(value, min, max);
    }

    private static bool ContainsAny(string text, params string[] terms)
    {
        foreach (string term in terms)
        {
            if (text.Contains(term, StringComparison.Ordinal))
                return true;
        }
        return false;
    }
}
