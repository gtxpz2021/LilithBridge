using System;
using System.Collections.Generic;

namespace LilithBridge;

/// <summary>
/// Minimal Native Animation mapping generated from Raw Animation Probe.
/// It is not a second ActionKind definition and does not include behavior,
/// emotion or scoring. It only maps a semantic action to the original
/// animation name for a known Pose × Clothing context.
/// </summary>
internal static class NativeAnimationCatalog
{
    private static readonly Dictionary<string, string> Map =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Idle|lie|casual"] = "lie_pajamas_idle",
            ["Idle|sleep|casual"] = "sleep_pajamas_idle",
            ["Idle|stand|casual"] = "stand_pajamas_idle",

            ["Flirt|lie|casual"] = "lie_pajamas_flirt",
            ["ShakeLeg|lie|casual"] = "lie_pajamas_shake_leg",
            ["Snap|lie|casual"] = "lie_pajamas_snap",
            ["Snap|stand|casual"] = "stand_pajamas_snap",
            ["LieStandUp|lie|casual"] = "lie_pajamas_stand_up",

            ["CoverBlanket|sleep|casual"] = "sleep_pajamas_cover_blanket",
            ["Mumble|sleep|casual"] = "sleep_pajamas_mumble",
            ["RubEyes|sleep|casual"] = "sleep_pajamas_rub_eyes",
            ["TakeBlanket|sleep|casual"] = "sleep_pajamas_take_blanket",
            ["TwitchBare|sleep|casual"] = "sleep_pajamas_twitch_bare",
            ["StandUp|stand|casual"] = "sleep_pajamas_stand_up",

            ["DrowsySlump|stand|casual"] = "stand_pajamas_drowsy_slump",
            ["Falled|stand|casual"] = "stand_pajamas_falled",
            ["Falling|stand|casual"] = "stand_pajamas_falling",
            ["MagicCast|stand|casual"] = "stand_pajamas_magic_cast",
            ["Music|stand|casual"] = "stand_pajamas_music",
            ["RubHead|stand|casual"] = "stand_pajamas_rub_head",
            ["Stretch|stand|casual"] = "stand_pajamas_stretch",
            ["Tsundere|stand|casual"] = "stand_pajamas_tsundere",
            ["Yawn|stand|casual"] = "stand_pajamas_yawn",

            ["Blink|lie|casual"] = "lie_pajamas_idle",
            ["Blink|sleep|casual"] = "sleep_pajamas_idle",
            ["Blink|stand|casual"] = "stand_pajamas_idle"
        };

    public static bool TryResolve(
        string action,
        string pose,
        string clothing,
        out string? nativeAnimation)
    {
        string key = action + "|" + pose + "|" + clothing;
        return Map.TryGetValue(key, out nativeAnimation);
    }
}
