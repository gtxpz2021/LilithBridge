using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace LilithBridge;

internal static class SteamOfflineGuard
{
    internal static IReadOnlyList<MethodInfo> Prepare()
    {
        Type? manager = FindSteamManager();
        if (manager == null)
        {
            Plugin.PluginLog.LogWarning("SteamManager not found; skipping offline guard.");
            return Array.Empty<MethodInfo>();
        }
        var targets = new List<MethodInfo>();
        foreach (string name in new[] { "Awake", "CompleteSteamSetup", "InitWithRetryForAutoStart" })
        {
            MethodInfo? method = AccessTools.Method(manager, name);
            if (method != null) targets.Add(method);
        }
        return targets;
    }

    public static void Apply(Harmony harmony) => Apply(harmony, Prepare());

    internal static void Apply(Harmony harmony, IReadOnlyList<MethodInfo> targets)
    {
        // Activation errors must reach Plugin.Load's owner-scoped rollback.
        foreach (MethodInfo method in targets)
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(SteamOfflineGuard), nameof(Block)));
        if (targets.Count > 0) Plugin.PluginLog.LogInfo("Offline Steam guard installed.");
    }

    private static Type? FindSteamManager()
    {
        try
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.GetName().Name?.Contains("Assembly-CSharp-firstpass", StringComparison.OrdinalIgnoreCase) == true)
                {
                    Type? found = assembly.GetType("SteamManager");
                    if (found != null) return found;
                }
            }
            return Type.GetType("SteamManager, Assembly-CSharp-firstpass");
        }
        catch { return null; }
    }

    private static bool Block() => false;
}
