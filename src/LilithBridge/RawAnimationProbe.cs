using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace LilithBridge;

internal sealed record RawAnimationProbeEntry(
    string Key,
    string Pose,
    string Clothing,
    string Action,
    string AnimationName,
    bool HasAnimation);

internal sealed record RawAnimationProbeReport(
    DateTimeOffset GeneratedAt,
    string ProtocolVersion,
    List<RawAnimationProbeEntry> Entries,
    int UniqueAnimationCount,
    int VerifiedCount);

/// <summary>
/// Read-only raw animation name probe. It does not call PlayAnimation or
/// PlayLilithAction. It only calls GetAnimationName / HasAnimation through
/// reflection and writes the resulting names to diagnostics.
/// </summary>
internal static class RawAnimationProbe
{
    public static RawAnimationProbeReport BuildReport()
    {
        var entries = new List<RawAnimationProbeEntry>();
        var uniqueNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int verified = 0;

        try
        {
            var state = UnityEngine.Object.FindObjectOfType<LilithStateManager>(true);
            if (state == null)
            {
                NativeEventLog.Trace("raw_animation.probe_error", "mod", "system",
                    "LilithStateManager not found");
                return Empty(uniqueNames, entries, verified);
            }

            Type? poseType = ResolveType(state.GetType().Assembly, "LilithPoseState");
            Type? clothingType = ResolveType(state.GetType().Assembly, "LilithClothingState");
            var methodInfo = FindGetAnimationName(state.GetType());
            if (methodInfo == null)
            {
                NativeEventLog.Trace("raw_animation.probe_error", "mod", "system",
                    "GetAnimationName method not found");
                return Empty(uniqueNames, entries, verified);
            }

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
                        string? name = InvokeGetAnimationName(methodInfo, state, pose, clothing, action);
                        if (string.IsNullOrWhiteSpace(name))
                            continue;
                        name = name.Trim();
                        bool has = false;
                        try
                        {
                            has = state.HasAnimation(name);
                        }
                        catch
                        {
                            // Unknown raw names are still recorded.
                        }
                        string key = (pose?.ToString() ?? "?")
                            + "|" + (clothing?.ToString() ?? "?")
                            + "|" + action;
                        entries.Add(new RawAnimationProbeEntry(
                            key,
                            pose?.ToString() ?? "?",
                            clothing?.ToString() ?? "?",
                            action.ToString(),
                            name,
                            has));
                        if (uniqueNames.Add(name))
                        {
                            NativeEventLog.Trace(
                                "raw_animation.probe.name",
                                "mod",
                                "system",
                                name,
                                "has_animation=" + has
                                + ";source=" + (pose?.ToString() ?? "?") + "/" + (clothing?.ToString() ?? "?"));
                        }
                        if (has)
                            verified++;
                    }
                }
            }
        }
        catch (Exception error)
        {
            NativeEventLog.Trace("raw_animation.probe_error", "mod", "system", error.Message);
        }

        return new RawAnimationProbeReport(
            DateTimeOffset.UtcNow,
            "0.3.42",
            entries,
            uniqueNames.Count,
            verified);
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
                Path.Combine(dir, "RawAnimationProbe.json"),
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
            File.WriteAllText(
                Path.Combine(dir, "RawAnimationProbe.md"),
                BuildMarkdown(report),
                new UTF8Encoding(false));
            NativeEventLog.Trace("raw_animation.probe.done", "mod", "system",
                "raw animation probe written", "path=" + dir);
            return true;
        }
        catch (Exception error)
        {
            NativeEventLog.Trace("raw_animation.probe_error", "mod", "system", error.Message);
            return false;
        }
    }

    private static RawAnimationProbeReport Empty(
        HashSet<string> uniqueNames,
        List<RawAnimationProbeEntry> entries,
        int verified)
    {
        return new RawAnimationProbeReport(
            DateTimeOffset.UtcNow,
            "0.3.42",
            entries,
            uniqueNames.Count,
            verified);
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
            object? result;
            object?[] args = method.GetParameters().Length == 3
                ? new[] { pose, clothing, action }
                : new[] { pose, action };
            if (method.IsStatic)
                result = method.Invoke(null, args);
            else
                result = method.Invoke(state, args);
            return result as string;
        }
        catch
        {
            return null;
        }
    }

    private static string BuildMarkdown(RawAnimationProbeReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Raw Animation Probe");
        builder.AppendLine();
        builder.AppendLine("> Generated: " + report.GeneratedAt.ToString("yyyy-MM-dd HH:mm:ss"));
        builder.AppendLine();
        builder.AppendLine("| Pose | Clothing | Action | AnimationName | HasAnimation |");
        builder.AppendLine("|---|---|---|---|---|");
        foreach (RawAnimationProbeEntry entry in report.Entries.GroupBy(x => x.AnimationName)
                     .Select(g => g.First())
                     .OrderBy(x => x.AnimationName, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append("| ")
                .Append(entry.Pose).Append(" | ")
                .Append(entry.Clothing).Append(" | ")
                .Append(entry.Action).Append(" | ")
                .Append(entry.AnimationName).Append(" | ")
                .Append(entry.HasAnimation ? "yes" : "no")
                .AppendLine(" |");
        }
        builder.AppendLine();
        builder.AppendLine("Entries = " + report.Entries.Count);
        builder.AppendLine("UniqueAnimationCount = " + report.UniqueAnimationCount);
        builder.AppendLine("VerifiedCount = " + report.VerifiedCount);
        return builder.ToString();
    }
}
