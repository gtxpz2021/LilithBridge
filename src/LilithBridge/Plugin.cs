using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace LilithBridge;

[BepInPlugin("local.lilith.native", "Lilith Native Bridge", "0.3.150.63")]
[BepInProcess("Lilith.exe")]
public sealed class Plugin : BasePlugin
{
    internal static ManualLogSource PluginLog { get; private set; } = null!;
    private Harmony? _harmony;

    public override void Load()
    {
        PluginLog = Log;
        Log.LogInfo("Lilith Native Bridge 0.3.150.63-r7 loading");
        _harmony = new Harmony("local.lilith.native");
        SteamOfflineGuard.Apply(_harmony);
        NativeHooks.Apply(_harmony);
        AddComponent<BridgeController>();
        Log.LogInfo("Lilith Native Bridge loaded; original native interactions are preserved");
    }

    public override bool Unload()
    {
        NativeEventLog.Flush();
        _harmony?.UnpatchSelf();
        return true;
    }
}


