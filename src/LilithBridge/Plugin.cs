using System;
using System.Runtime.CompilerServices;
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
    private object? _ownedHost;
    private bool _loaded;
    private bool _nativeRegistrationTouched;

    public override void Load()
    {
        PluginLog = Log;
        if (_loaded) return;
        if (_harmony != null || _ownedHost != null)
            throw new InvalidOperationException("Previous LilithBridge cleanup is incomplete");
        Log.LogInfo("Lilith Native Bridge 0.3.150.63-r7 loading");
        _harmony = new Harmony("local.lilith.native");
        try
        {
            ActivateNative(_harmony);
            _loaded = true;
            Log.LogInfo("Lilith Native Bridge loaded; original native interactions are preserved");
        }
        catch (Exception error)
        {
            bool cleaned = CleanupOwnedState();
            Log.LogError("LilithBridge initialization failed: " + error.GetType().Name
                + "; own cleanup complete=" + cleaned);
            throw;
        }
    }

    // Keep game/interop types behind this non-inlined boundary so Load catches
    // missing types or methods even when compiling the activation body fails.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ActivateNative(Harmony harmony)
    {
        var steam = SteamOfflineGuard.Prepare();
        var native = NativeHooks.Prepare(harmony);
        if (!Il2CppInterop.Runtime.Injection.ClassInjector.IsTypeRegisteredInIl2Cpp<BridgeController>())
            Il2CppInterop.Runtime.Injection.ClassInjector.RegisterTypeInIl2Cpp<BridgeController>();

        SteamOfflineGuard.Apply(harmony, steam);
        _nativeRegistrationTouched = true;
        NativeHooks.Apply(harmony, native);

        var host = new UnityEngine.GameObject("LilithBridge");
        _ownedHost = host; // Retain ownership before any later native call can fail.
        host.hideFlags = UnityEngine.HideFlags.HideAndDontSave;
        host.SetActive(false);
        UnityEngine.Object.DontDestroyOnLoad(host);
        if (host.AddComponent<BridgeController>() == null)
            throw new InvalidOperationException("BridgeController could not be created");
        host.SetActive(true);
    }

    private bool CleanupOwnedState()
    {
        _loaded = false;
        bool complete = true;
        if (_harmony != null)
        {
            try { _harmony.UnpatchSelf(); }
            catch (Exception error) { complete = false; Log.LogError("LilithBridge own unpatch failed: " + error.GetType().Name); }
            if (_nativeRegistrationTouched)
            {
                try { ForgetNativeRegistration(_harmony.Id); _nativeRegistrationTouched = false; }
                catch (Exception error) { complete = false; Log.LogError("LilithBridge registration cleanup failed: " + error.GetType().Name); }
            }
        }
        if (_ownedHost != null)
        {
            try { DestroyOwnedHost(); }
            catch (Exception error) { complete = false; Log.LogError("LilithBridge object cleanup failed: " + error.GetType().Name); }
        }
        if (complete) _harmony = null;
        return complete;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ForgetNativeRegistration(string owner) => NativeHooks.ForgetRegistration(owner);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void DestroyOwnedHost()
    {
        var host = (UnityEngine.GameObject)_ownedHost!;
        if (host != null)
        {
            host.SetActive(false);
            UnityEngine.Object.Destroy(host);
        }
        _ownedHost = null;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void FlushNativeEvents() => NativeEventLog.Flush();

    public override bool Unload()
    {
        try { if (_loaded) FlushNativeEvents(); }
        catch (Exception error) { Log.LogWarning("LilithBridge event flush failed: " + error.GetType().Name); }
        return CleanupOwnedState();
    }
}
