using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace LilithBridge;

// Game 1.2's layered desktop window grows around its content. Screen UI must
// anchor to the character's physical monitor, not the changing window bounds.
internal static class NativeUiViewport
{
    private static readonly PropertyInfo? MonitorRect = typeof(TransparentWindowNew).Assembly
        .GetType("LilithWindow")?.GetProperty("CharacterMonitorInUnityScreen", BindingFlags.Public | BindingFlags.Static);
    private static readonly List<(Canvas Canvas, RectTransform Rect)> Entries = new();
    private static bool _warned;

    public static Transform Create(Canvas canvas)
    {
        // Preserve the existing hierarchy and layout on older game versions.
        if (MonitorRect == null) return canvas.transform;
        var root = new GameObject("LilithBridgeMonitorViewport");
        var rect = root.AddComponent<RectTransform>();
        rect.SetParent(canvas.transform, false);
        rect.anchorMin = rect.pivot = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = rect.offsetMax = Vector2.zero;
        Entries.Add((canvas, rect));
        Tick();
        return rect;
    }

    public static void Tick()
    {
        if (Entries.Count == 0) return;
        Rect monitor;
        try
        {
            if (MonitorRect?.GetValue(null) is not Rect bounds || bounds.width <= 0 || bounds.height <= 0
                || !float.IsFinite(bounds.x) || !float.IsFinite(bounds.y)
                || !float.IsFinite(bounds.width) || !float.IsFinite(bounds.height))
                return;
            monitor = bounds;
        }
        catch (Exception error)
        {
            if (!_warned)
            {
                _warned = true;
                Plugin.PluginLog.LogWarning("Native UI monitor mapping unavailable: " + error.GetBaseException().GetType().Name);
            }
            return;
        }
        for (int i = Entries.Count - 1; i >= 0; i--)
        {
            var entry = Entries[i];
            if (entry.Canvas == null || entry.Rect == null) { Entries.RemoveAt(i); continue; }
            float scale = entry.Canvas.scaleFactor > 0f ? entry.Canvas.scaleFactor : 1f;
            entry.Rect.anchorMin = entry.Rect.anchorMax = Vector2.zero;
            entry.Rect.anchoredPosition = new Vector2(monitor.x / scale, monitor.y / scale);
            entry.Rect.sizeDelta = new Vector2(monitor.width / scale, monitor.height / scale);
        }
    }

    public static void Clear() => Entries.Clear();
}
