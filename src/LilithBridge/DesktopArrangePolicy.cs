using System;
using System.Collections.Generic;

namespace LilithBridge;

internal sealed record DesktopArrangeEvaluation(
    bool Allowed,
    string Reason,
    int IconCount,
    int OverlapCount,
    int MinIconCount,
    int MinOverlaps)
{
    public string Describe()
    {
        return "[desktop_arrange_state] auto_propose_allowed="
            + (Allowed ? "true" : "false")
            + ";icon_count=" + IconCount
            + ";overlap_count=" + OverlapCount
            + ";min_icons=" + MinIconCount
            + ";min_overlaps=" + MinOverlaps
            + ";reason=" + Reason;
    }
}

internal static class DesktopArrangePolicy
{
    private static DateTimeOffset _lastCheckAt = DateTimeOffset.MinValue;
    private static DesktopArrangeEvaluation? _cached;

    public static DesktopArrangeEvaluation Evaluate(BridgeConfig config)
    {
        if (config == null)
            return Denied("no_config", 0, 0);
        if (!config.DesktopArrangeEnabled)
            return Denied("disabled", 0, 0);
        if (!config.DesktopArrangeAutoProposeEnabled)
            return Denied("auto_propose_disabled", 0, 0);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (_cached != null
            && now - _lastCheckAt
                < TimeSpan.FromSeconds(config.DesktopArrangeProposalInspectionIntervalSeconds))
            return _cached;

        int count = -1;
        try
        {
            count = NativeStateInspector.DesktopIconCount;
        }
        catch
        {
            // Fall through to a conservative denial.
        }
        if (count < 0)
            return Cache(Denied("icon_count_unknown", count, 0));
        if (count < config.DesktopArrangeProposalMinIconCount)
            return Cache(Denied("not_enough_icons", count, 0));

        DesktopArrangeResult inspection;
        try
        {
            inspection = DesktopArrangeService.Inspect(config);
        }
        catch
        {
            return Cache(Denied("inspection_failed", count, 0));
        }
        if (!inspection.Ok || inspection.Before == null)
            return Cache(Denied("inspection_unavailable", count, 0));

        int overlaps = CountOverlaps(inspection.Before.Positions, config.DesktopArrangeProposalMinGap);
        bool allowed = overlaps >= config.DesktopArrangeProposalMinOverlaps;
        return Cache(new DesktopArrangeEvaluation(
            allowed,
            allowed ? "overlaps_found" : "no_significant_overlap",
            count,
            overlaps,
            config.DesktopArrangeProposalMinIconCount,
            config.DesktopArrangeProposalMinOverlaps));
    }

    public static string Describe(BridgeConfig config)
    {
        return Evaluate(config).Describe();
    }

    public static bool AllowPropose(BridgeConfig config)
    {
        return Evaluate(config).Allowed;
    }

    private static DesktopArrangeEvaluation Cache(DesktopArrangeEvaluation value)
    {
        _cached = value;
        _lastCheckAt = DateTimeOffset.UtcNow;
        return value;
    }

    private static DesktopArrangeEvaluation Denied(
        string reason,
        int count,
        int overlaps)
    {
        return new DesktopArrangeEvaluation(false, reason, count, overlaps, 0, 0);
    }

    private static int CountOverlaps(
        List<DesktopIconPoint> positions,
        int minimumGap)
    {
        if (positions == null || positions.Count < 2)
            return 0;
        int gapSquared = Math.Max(1, minimumGap * minimumGap);
        int overlaps = 0;
        for (int i = 0; i < positions.Count; i++)
        {
            for (int j = i + 1; j < positions.Count; j++)
            {
                int dx = positions[i].X - positions[j].X;
                int dy = positions[i].Y - positions[j].Y;
                if (dx * dx + dy * dy < gapSquared)
                    overlaps++;
            }
        }
        return overlaps;
    }
}
