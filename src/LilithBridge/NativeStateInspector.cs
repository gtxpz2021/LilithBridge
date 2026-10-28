using System;
using System.Reflection;
using UnityEngine;

namespace LilithBridge;

/// <summary>
/// Reads the original desktop companion's native sensors and state machines so
/// the mod can coordinate instead of duplicating or fighting them.
/// </summary>
internal static class NativeStateInspector
{
    public static string ForegroundProcessName =>
        Read(() => SystemSensor.ForegroundProcessName ?? "", "");

    public static string ForegroundProcessDisplayName =>
        Read(() => SystemSensor.ForegroundProcessDisplayName ?? "", "");

    public static int Hour =>
        Read(() => SystemSensor.Hour, -1);

    public static bool IsNight =>
        Read(() => SystemSensor.IsNight, false);

    public static bool IsGamingHidden =>
        Read(() => HideDuringGame.IsHiddenNow, false);

    public static bool IsDisappearPending =>
        Read(() => LilithDisappearReturn.IsPending, false);

    public static bool IsDissipatePending =>
        Read(() => LilithDisappearReturn.IsDissipatePending, false);

    public static bool IsWithinCalledBackGrace =>
        Read(() => LilithDisappearReturn.IsWithinCalledBackGrace, false);

    public static bool IsNeglectHidden
    {
        get
        {
            CharacterController? controller = Find<CharacterController>();
            return Read(() => controller != null && controller.IsNeglectHidden, false);
        }
    }

    public static bool IsScreenPeeking
    {
        get
        {
            CharacterController? controller = Find<CharacterController>();
            return Read(() => controller != null && controller.IsScreenPeeking, false);
        }
    }

    public static bool IsPoseBlocked
    {
        get
        {
            LilithStateManager? state = Find<LilithStateManager>();
            return Read(() => state != null && (state.IsLieDown || state.IsSofaSit), false);
        }
    }

    public static bool IsSleeping =>
        Read(() => Find<CharacterController>()?.IsSleep ?? false, false);

    public static bool IsMusicPlayerRunning =>
        Read(() => AppComboAwareBehavior.AnyMusicPlayerRunning(), false);

    public static bool IsRivalPetRunning =>
        Read(() => DesktopPetRivalBehavior.AnyRivalPetRunning(), false);

    public static int WindowedAppCount =>
        Read(() => AppComboAwareBehavior.CountWindowedApps(0), 0);

    private static int _cachedDesktopIconCount = -1;
    private static DateTimeOffset _cachedDesktopIconCountAt = DateTimeOffset.MinValue;
    private static readonly TimeSpan DesktopIconCacheLifetime = TimeSpan.FromSeconds(60);

    public static int DesktopIconCount
    {
        get
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (_cachedDesktopIconCountAt != default
                && now - _cachedDesktopIconCountAt < DesktopIconCacheLifetime)
                return _cachedDesktopIconCount;
            int count = Read(() =>
            {
                DesktopIconCounter.TryGetCount(out int iconCount);
                return iconCount;
            }, -1);
            _cachedDesktopIconCount = count;
            _cachedDesktopIconCountAt = now;
            return count;
        }
    }

    public static bool IsForegroundFullscreen =>
        Read(() => PlatformService.Current.IsForegroundWindowFullscreen(), false);

    public static int WindowColliderCount
    {
        get
        {
            ActiveWindowCollider? colliders = Find<ActiveWindowCollider>();
            return Read(() => colliders?.ColliderWindowCount ?? 0, 0);
        }
    }

    public static bool WindowCollidersEnabled
    {
        get
        {
            ActiveWindowCollider? colliders = Find<ActiveWindowCollider>();
            return Read(() => colliders?.WindowCollidersEnabled ?? false, false);
        }
    }

    public static double TimeSinceLastInteractionSeconds
    {
        get
        {
            EmotionSystem? emotion = Find<EmotionSystem>();
            return Read(() => emotion != null
                ? (double)emotion.TimeSinceLastInteraction.TotalSeconds
                : 0d, 0d);
        }
    }

    public static double TimeSinceLastHardwareInputSeconds
    {
        get
        {
            EmotionSystem? emotion = Find<EmotionSystem>();
            return Read(() => emotion != null
                ? (double)emotion.TimeSinceLastHardwareInput.TotalSeconds
                : 0d, 0d);
        }
    }

    /// <summary>
    /// True while the original game does its own hiding/performance/pose work
    /// and mod micro-behavior should wait instead of competing.
    /// </summary>
    public static bool IsNativeQuietForMod()
    {
        return IsGamingHidden
            || IsDisappearPending
            || IsDissipatePending
            || IsNeglectHidden
            || IsForegroundFullscreen
            || IsPoseBlocked
            || IsSleeping;
    }

    public static string Describe()
    {
        var builder = new System.Text.StringBuilder();
        builder.Append("foreground=" + ForegroundProcessName);
        builder.Append(";display=" + ForegroundProcessDisplayName);
        // The game's own clock; the player's real clock is [now]/real_phase.
        builder.Append(NativeClockFields.Describe(Hour, IsNight));
        builder.Append(";gaming_hidden=" + IsGamingHidden);
        builder.Append(";disappear_pending=" + IsDisappearPending);
        builder.Append(";dissipate_pending=" + IsDissipatePending);
        builder.Append(";called_back_grace=" + IsWithinCalledBackGrace);
        builder.Append(";neglect_hidden=" + IsNeglectHidden);
        builder.Append(";screen_peeking=" + IsScreenPeeking);
        builder.Append(";pose_blocked=" + IsPoseBlocked);
        builder.Append(";music=" + IsMusicPlayerRunning);
        builder.Append(";rival_pet=" + IsRivalPetRunning);
        builder.Append(";foreground_fullscreen=" + IsForegroundFullscreen);
        builder.Append(";window_colliders=" + WindowColliderCount);
        builder.Append(";window_colliders_enabled=" + WindowCollidersEnabled);
        builder.Append(";last_interaction_s=" + TimeSinceLastInteractionSeconds.ToString("F0"));
        builder.Append(";last_hardware_s=" + TimeSinceLastHardwareInputSeconds.ToString("F0"));
        return builder.ToString();
    }

    public static void LogSnapshot(string reason)
    {
        try
        {
            NativeEventLog.Trace("native.state", "mod", "system",
                "native compatibility snapshot", reason + ";" + Describe());
        }
        catch
        {
            // Never break the game because of telemetry.
        }
    }

    public static void TrySummonIfHidden(string reason)
    {
        if (!IsGamingHidden && !IsDisappearPending && !IsDissipatePending)
            return;

        try
        {
            NativeEventLog.Trace("native.summon", "mod", "player",
                "player asked Lilith to return", reason);
            if (IsDisappearPending || IsDissipatePending)
                LilithDisappearReturn.MarkCalledBack();

            CharacterController? controller = Find<CharacterController>();
            if (controller == null)
                return;

            FieldInfo? arbiterField = typeof(CharacterController).GetField(
                "_arbiter",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            object? arbiter = arbiterField?.GetValue(controller);
            MethodInfo? recall = arbiter?.GetType().GetMethod(
                "RecallFromHiding",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (recall != null && arbiter != null)
                recall.Invoke(arbiter, null);
        }
        catch (Exception error)
        {
            NativeEventLog.Trace("native.summon_error", "mod", "player",
                "native summon failed", error.Message);
        }
    }

    private static T? Find<T>() where T : UnityEngine.Object
    {
        try
        {
            return UnityEngine.Object.FindObjectOfType<T>(true);
        }
        catch
        {
            return null;
        }
    }

    private static T Read<T>(Func<T> reader, T fallback)
    {
        try
        {
            return reader();
        }
        catch
        {
            return fallback;
        }
    }
}
