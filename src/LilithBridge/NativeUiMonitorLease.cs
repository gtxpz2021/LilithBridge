using System;
using System.Reflection;

namespace LilithBridge;

// Newer layered hosts crop their native window around the character. Mapping
// the UI canvas to monitor coordinates alone cannot enlarge that window.
internal sealed class NativeUiMonitorLease
{
    private static readonly Action<Il2CppSystem.Object, float>? Request;
    private static readonly Action<Il2CppSystem.Object>? Release;
    private Il2CppSystem.Object? _token;
    private bool _held, _failed;

    static NativeUiMonitorLease()
    {
        // Bind through reflection so the same plugin still loads on 1.0.4,
        // whose Assembly-CSharp has no LilithWindow/full-monitor contract.
        var type = typeof(TransparentWindowNew).Assembly.GetType("LilithWindow");
        var request = type?.GetMethod("RequestFullMonitor", BindingFlags.Public | BindingFlags.Static,
            null, new[] { typeof(Il2CppSystem.Object), typeof(float) }, null);
        var release = type?.GetMethod("ReleaseFullMonitor", BindingFlags.Public | BindingFlags.Static,
            null, new[] { typeof(Il2CppSystem.Object) }, null);
        if (request == null || release == null) return;
        Request = (Action<Il2CppSystem.Object, float>)request.CreateDelegate(typeof(Action<Il2CppSystem.Object, float>));
        Release = (Action<Il2CppSystem.Object>)release.CreateDelegate(typeof(Action<Il2CppSystem.Object>));
    }

    public void Hold()
    {
        if (Request == null || _failed) return;
        try
        {
            _token ??= new Il2CppSystem.Object();
            // Renew only while its UI is visible. The short native expiry is
            // also a bound if the owner stops ticking without normal cleanup.
            Request(_token, 1f);
            _held = true;
        }
        catch (Exception error)
        {
            _failed = true;
            Clear();
            Plugin.PluginLog.LogWarning("Native UI monitor request unavailable: " + error.GetBaseException().GetType().Name);
        }
    }

    public void Clear()
    {
        if (!_held || _token == null) return;
        _held = false;
        try { Release?.Invoke(_token); }
        catch (Exception error)
        {
            Plugin.PluginLog.LogWarning("Native UI monitor release unavailable: " + error.GetBaseException().GetType().Name);
        }
    }
}
