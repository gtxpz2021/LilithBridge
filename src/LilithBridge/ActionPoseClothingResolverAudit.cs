using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace LilithBridge;

internal sealed record ActionPoseClothingAuditEntry(
    string Key,
    string Action,
    string Pose,
    string Clothing,
    string AnimationName,
    bool HasAnimation,
    bool RequiresTransition,
    string Status,
    string Reason);

internal sealed record ActionPoseClothingAuditReport(
    DateTimeOffset GeneratedAt,
    string ProtocolVersion,
    List<ActionPoseClothingAuditEntry> Entries,
    int DirectCount,
    int TransitionCount,
    int UnavailableCount,
    int UniqueAnimationCount);

/// <summary>
/// Read-only Action × Pose × Clothing resolver audit.
/// It only queries GetAnimationName / HasAnimation and never plays an action.
/// </summary>
internal static class ActionPoseClothingResolverAudit
{
    public static ActionPoseClothingAuditReport BuildReport()
    {
        var entries = new List<ActionPoseClothingAuditEntry>();
        int direct = 0;
        int transition = 0;
        int unavailable = 0;
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var state = UnityEngine.Object.FindObjectOfType<LilithStateManager>(true);
            if (state == null)
                return Empty(entries, direct, transition, unavailable, unique);

            Type? poseType = ResolveType(state.GetType().Assembly, "LilithPoseState");
            Type? clothingType = ResolveType(state.GetType().Assembly, "LilithClothingState");
            MethodInfo? method = FindGetAnimationName(state.GetType());
            if (method == null)
                return Empty(entries, direct, transition, unavailable, unique);

            object?[] poses = poseType == null
                ? new object?[] { null }
                : Enum.GetValues(poseType).Cast<object?>().ToArray();
            object?[] clothings = clothingType == null
                ? new object?[] { null }
                : Enum.GetValues(clothingType).Cast<object?>().ToArray();

            foreach (LilithActionType action in Enum.GetValues<LilithActionType>())
            {
                foreach (object? pose in poses)
                {
                    foreach (object? clothing in clothings)
                    {
                        string? name = InvokeGetAnimationName(method, state, pose, clothing, action);
                        bool has = false;
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            name = name.Trim();
                            try
                            {
                                has = state.HasAnimation(name);
                            }
                            catch
                            {
                                // Still record unknown.
                            }
                        }

                        bool requiresTransition = ActionCapability.IsPoseTransition(action);
                        string status = has
                            ? (requiresTransition ? "TRANSITION" : "DIRECT")
                            : "UNAVAILABLE";
                        string reason = has
                            ? (requiresTransition ? "transition_required" : "")
                            : "no_animation";
                        string key = action + "|" + (pose?.ToString() ?? "?") + "|" + (clothing?.ToString() ?? "?");
                        entries.Add(new ActionPoseClothingAuditEntry(
                            key,
                            action.ToString(),
                            pose?.ToString() ?? "?",
                            clothing?.ToString() ?? "?",
                            string.IsNullOrWhiteSpace(name) ? "" : name,
                            has,
                            requiresTransition,
                            status,
                            reason));
                        if (has && !string.IsNullOrWhiteSpace(name))
                            unique.Add(name);
                        if (status == "DIRECT")
                            direct++;
                        else if (status == "TRANSITION")
                            transition++;
                        else
                            unavailable++;
                    }
                }
            }
        }
        catch (Exception error)
        {
            NativeEventLog.Trace("action.resolver_audit.error", "mod", "system", error.Message);
        }

        return new ActionPoseClothingAuditReport(
            DateTimeOffset.UtcNow,
            "0.3.43",
            entries,
            direct,
            transition,
            unavailable,
            unique.Count);
    }

    public static bool TryWriteReport(BridgeConfig config)
    {
        try
        {
            var report = BuildReport();
            string dir = string.IsNullOrWhiteSpace(config.DataDirectory)
                ? Path.Combine(DataPaths.Root(), "diagnostics")
                : Path.Combine(config.DataDirectory, "diagnostics");
            Directory.CreateDirectory(dir);
            File.WriteAllText(
                Path.Combine(dir, "ActionPoseClothingResolverAudit.json"),
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
            File.WriteAllText(
                Path.Combine(dir, "ActionPoseClothingResolverAudit.md"),
                BuildMarkdown(report),
                new UTF8Encoding(false));
            NativeEventLog.Trace("action.resolver_audit.done", "mod", "system",
                "action resolver audit written", "path=" + dir);
            return true;
        }
        catch (Exception error)
        {
            NativeEventLog.Trace("action.resolver_audit.error", "mod", "system", error.Message);
            return false;
        }
    }

    private static ActionPoseClothingAuditReport Empty(
        List<ActionPoseClothingAuditEntry> entries,
        int direct,
        int transition,
        int unavailable,
        HashSet<string> unique)
    {
        return new ActionPoseClothingAuditReport(
            DateTimeOffset.UtcNow,
            "0.3.43",
            entries,
            direct,
            transition,
            unavailable,
            unique.Count);
    }

    private static Type? ResolveType(Assembly assembly, string name)
    {
        try
        {
            return assembly.GetType(name, false);
        }
        catch
        {
            return null;
        }
    }

    private static MethodInfo? FindGetAnimationName(Type stateType)
    {
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        return stateType.GetMethods(flags)
            .FirstOrDefault(m => m.Name == "GetAnimationName" && m.GetParameters().Length is 2 or 3);
    }

    private static string? InvokeGetAnimationName(
        MethodInfo method,
        LilithStateManager state,
        object? pose,
        object? clothing,
        LilithActionType action)
    {
        try
        {
            object?[] args = method.GetParameters().Length == 3
                ? new[] { pose, clothing, action }
                : new[] { pose, action };
            object? result = method.IsStatic
                ? method.Invoke(null, args)
                : method.Invoke(state, args);
            return result as string;
        }
        catch
        {
            return null;
        }
    }

    private static string BuildMarkdown(ActionPoseClothingAuditReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Action × Pose × Clothing Resolver Audit");
        builder.AppendLine();
        builder.AppendLine("> Generated: " + report.GeneratedAt.ToString("yyyy-MM-dd HH:mm:ss"));
        builder.AppendLine();
        builder.AppendLine("| Action | Pose | Clothing | AnimationName | HasAnimation | RequiresTransition | Status |");
        builder.AppendLine("|---|---|---|---|---|---|---|");
        foreach (ActionPoseClothingAuditEntry entry in report.Entries
                     .GroupBy(x => x.Key)
                     .Select(g => g.First())
                     .OrderBy(x => x.Action, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(x => x.Pose)
                     .ThenBy(x => x.Clothing))
        {
            builder.Append("| ")
                .Append(entry.Action).Append(" | ")
                .Append(entry.Pose).Append(" | ")
                .Append(entry.Clothing).Append(" | ")
                .Append(entry.AnimationName).Append(" | ")
                .Append(entry.HasAnimation ? "yes" : "no").Append(" | ")
                .Append(entry.RequiresTransition ? "yes" : "no").Append(" | ")
                .Append(entry.Status).AppendLine(" |");
        }
        builder.AppendLine();
        builder.AppendLine("Direct = " + report.DirectCount);
        builder.AppendLine("Transition = " + report.TransitionCount);
        builder.AppendLine("Unavailable = " + report.UnavailableCount);
        builder.AppendLine("UniqueAnimationCount = " + report.UniqueAnimationCount);
        return builder.ToString();
    }
}
