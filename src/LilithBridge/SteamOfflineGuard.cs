using System;
using System.Reflection;
using HarmonyLib;

namespace LilithBridge;

internal static class SteamOfflineGuard
{
    public static void Apply(Harmony harmony)
    {
        try
        {
            Type? manager = FindSteamManager();
            if (manager == null)
            {
                Plugin.PluginLog.LogWarning("SteamManager not found; skipping offline guard.");
                return;
            }
            foreach (string name in new[] { "Awake", "CompleteSteamSetup", "InitWithRetryForAutoStart" })
            {
                MethodInfo? method = AccessTools.Method(manager, name);
                if (method == null) continue;
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(SteamOfflineGuard), nameof(Block)));
            }
            Plugin.PluginLog.LogInfo("Offline Steam guard installed.");
        }
        catch (Exception error)
        {
            Plugin.PluginLog.LogWarning("Offline Steam guard failed: " + error.Message);
        }
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
                    if (found != null)
                        return found;
                }
            }
            return Type.GetType("SteamManager, Assembly-CSharp-firstpass");
        }
        catch
        {
            return null;
        }
    }

    private static bool Block() => false;
}
