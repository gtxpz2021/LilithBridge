using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace LilithBridge;

internal sealed record NativeActionInventoryEntry(
    string ActionName,
    bool CatalogExists,
    bool NativeAnimationExists,
    bool AmbientWaitingAvailable,
    IReadOnlyDictionary<string, bool> PoseAvailability,
    bool? ExecutableNow,
    string RejectionReason);

internal sealed record NativeActionInventoryReport(
    DateTimeOffset GeneratedAt,
    string ProtocolVersion,
    List<NativeActionInventoryEntry> Actions,
    int CatalogCount,
    int NativeCount,
    int AmbientWaitingCount,
    int CurrentPoseUsableCount);

/// <summary>
/// Diagnostic-only inventory of what the original game can actually play.
/// It does not modify the prompt, ActionCatalog, Runtime Protocol, parser,
/// executor or fallback. It only observes.
/// </summary>
internal static class NativeActionInventory
{
    public static NativeActionInventoryReport BuildReport(SessionState? state)
    {
        var entries = new List<NativeActionInventoryEntry>();
        int catalogCount = 0;
        int nativeCount = 0;
        int ambientCount = 0;
        int currentPoseUsableCount = 0;

        foreach (LilithActionType action in Enum.GetValues<LilithActionType>())
        {
            string name = action.ToString();
            bool catalogExists = ActionCatalog.TryParseAction(name) == action;
            bool native = false;
            bool ambient = false;
            var poseAvailability = new Dictionary<string, bool>();
            try
            {
                native = ActionExecutor.HasNativeAnimation(action);
                ambient = ActionExecutor.HasAmbientWaitingAnimation(action);
                poseAvailability["idle"] = ActionCapability.IsPoseSafe(action, null);
                poseAvailability["sofa"] = IsPoseSafeIn(action, new SessionState { Native = { IsSofaSit = true } });
                poseAvailability["lying"] = IsPoseSafeIn(action, new SessionState { Native = { IsLieDown = true } });
                poseAvailability["sleep"] = IsPoseSafeIn(action, new SessionState { Native = { IsSleep = true } });
            }
            catch
            {
                native = false;
                poseAvailability.Clear();
                poseAvailability["idle"] = false;
                poseAvailability["sofa"] = false;
                poseAvailability["lying"] = false;
                poseAvailability["sleep"] = false;
            }

            bool currentPoseSafe = state == null
                ? poseAvailability["idle"]
                : ActionCapability.IsPoseSafe(action, state);
            bool playable = native || ambient;
            bool? executable = playable && currentPoseSafe ? true : false;
            string reason = !playable
                ? "no_native_animation"
                : !native && ambient
                    ? "ambient_waiting_only"
                : !currentPoseSafe
                    ? "pose_restricted"
                    : "";

            entries.Add(new NativeActionInventoryEntry(
                name,
                catalogExists,
                native,
                ambient,
                poseAvailability,
                executable,
                reason));
            if (catalogExists)
                catalogCount++;
            if (native)
                nativeCount++;
            if (ambient)
                ambientCount++;
            if (executable == true)
                currentPoseUsableCount++;

            NativeEventLog.Trace(
                "action.inventory.item",
                "mod",
                "system",
                name,
                "catalog=" + Bool(catalogExists)
                + ";native=" + Bool(native)
                + ";ambient=" + Bool(ambient)
                + ";idle=" + Bool(poseAvailability["idle"])
                + ";sofa=" + Bool(poseAvailability["sofa"])
                + ";lying=" + Bool(poseAvailability["lying"])
                + ";sleep=" + Bool(poseAvailability["sleep"])
                + ";executable=" + Bool(executable == true));
        }

        return new NativeActionInventoryReport(
            DateTimeOffset.UtcNow,
            "0.3.41",
            entries,
            catalogCount,
            nativeCount,
            ambientCount,
            currentPoseUsableCount);
    }

    private static bool IsPoseSafeIn(LilithActionType action, SessionState state)
    {
        try
        {
            return ActionCapability.IsPoseSafe(action, state);
        }
        catch
        {
            return false;
        }
    }

    public static bool TryWriteReport(BridgeConfig config, SessionState? state)
    {
        try
        {
            NativeActionInventoryReport report = BuildReport(state);
            string dir = string.IsNullOrWhiteSpace(config.DataDirectory)
                ? Path.Combine(DataPaths.Root(), "diagnostics")
                : Path.Combine(config.DataDirectory, "diagnostics");
            Directory.CreateDirectory(dir);
            File.WriteAllText(
                Path.Combine(dir, "NativeActionInventory.json"),
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
            File.WriteAllText(
                Path.Combine(dir, "NativeActionInventory.md"),
                BuildMarkdown(report),
                new UTF8Encoding(false));
            NativeEventLog.Trace(
                "action.inventory.done",
                "mod",
                "system",
                "native action inventory written",
                "path=" + dir);
            return true;
        }
        catch (Exception error)
        {
            NativeEventLog.Trace(
                "action.inventory.error",
                "mod",
                "system",
                error.Message);
            return false;
        }
    }

    private static string BuildMarkdown(NativeActionInventoryReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Native Action Inventory");
        builder.AppendLine();
        builder.AppendLine("> Generated: " + report.GeneratedAt.ToString("yyyy-MM-dd HH:mm:ss"));
        builder.AppendLine();
        builder.AppendLine("| Action | Catalog | Native | Ambient | Idle | Sofa | Lying | Sleep | Current | Reason |");
        builder.AppendLine("|---|---|---|---|---|---|---|---|---|---|");
        foreach (NativeActionInventoryEntry entry in report.Actions)
        {
            builder.Append("| ")
                .Append(entry.ActionName)
                .Append(" | ")
                .Append(Bool(entry.CatalogExists))
                .Append(" | ")
                .Append(Bool(entry.NativeAnimationExists))
                .Append(" | ")
                .Append(Bool(entry.AmbientWaitingAvailable))
                .Append(" | ")
                .Append(Bool(entry.PoseAvailability["idle"]))
                .Append(" | ")
                .Append(Bool(entry.PoseAvailability["sofa"]))
                .Append(" | ")
                .Append(Bool(entry.PoseAvailability["lying"]))
                .Append(" | ")
                .Append(Bool(entry.PoseAvailability["sleep"]))
                .Append(" | ")
                .Append(Format(entry.ExecutableNow))
                .Append(" | ")
                .Append(entry.RejectionReason)
                .AppendLine(" |");
        }
        builder.AppendLine();
        builder.AppendLine("CatalogCount = " + report.CatalogCount);
        builder.AppendLine("NativeCount = " + report.NativeCount);
        builder.AppendLine("AmbientWaitingCount = " + report.AmbientWaitingCount);
        builder.AppendLine("CurrentPoseUsableCount = " + report.CurrentPoseUsableCount);
        return builder.ToString();
    }

    private static string Format(bool? value)
    {
        return value.HasValue ? Bool(value.Value) : "unknown";
    }

    private static string Bool(bool value)
    {
        return value ? "yes" : "no";
    }
}
