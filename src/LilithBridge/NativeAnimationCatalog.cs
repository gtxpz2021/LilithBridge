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
            // Standing · Casual (verified against LilithAnimName / Spine metadata)
            ["Idle|stand|casual"] = "stand_casual_idle",
            ["Blink|stand|casual"] = "stand_casual_idle",
            ["Arrange|stand|casual"] = "stand_casual_arrange",
            ["Blush|stand|casual"] = "stand_casual_blush",
            ["CakeEnd|stand|casual"] = "stand_casual_cake_end",
            ["CakeIdle|stand|casual"] = "stand_casual_cake_idle",
            ["CakeUp|stand|casual"] = "stand_casual_cake_up",
            ["Clap|stand|casual"] = "stand_casual_clap",
            ["Confetti|stand|casual"] = "stand_casual_confetti",
            ["Confuse|stand|casual"] = "stand_casual_confuse",
            ["DessertIdle|stand|casual"] = "stand_casual_dessert_idle",
            ["DessertUp|stand|casual"] = "stand_casual_dessert_up",
            ["Drowsy|stand|casual"] = "stand_casual_drowsy",
            ["EmptyHands|stand|casual"] = "stand_casual_empty_hands",
            ["FakeAngry|stand|casual"] = "stand_casual_fake_angry",
            ["FakeCry|stand|casual"] = "stand_casual_fake_cry",
            ["FakeWronged|stand|casual"] = "stand_casual_fake_wronged",
            ["Falled|stand|casual"] = "stand_casual_falled",
            ["Falling|stand|casual"] = "stand_casual_falling",
            ["Flirt|stand|casual"] = "stand_casual_flirt",
            ["GiftGive|stand|casual"] = "stand_casual_gift_give",
            ["Greet|stand|casual"] = "stand_casual_greet",
            ["HalfAwake|stand|casual"] = "stand_casual_half_awake",
            ["HappySigh|stand|casual"] = "stand_casual_happy_sigh",
            ["Hide|stand|casual"] = "stand_casual_hide",
            ["LazyWave|stand|casual"] = "stand_casual_lazy_wave",
            ["LookAround|stand|casual"] = "stand_casual_look_around",
            ["MagicCast|stand|casual"] = "stand_casual_magic_cast",
            ["Music|stand|casual"] = "stand_casual_music",
            ["PeekEnd|stand|casual"] = "stand_casual_peek_end",
            ["PeekIdle|stand|casual"] = "stand_casual_peek_idle",
            ["PerkYawn|stand|casual"] = "stand_casual_perk_yawn",
            ["Pout|stand|casual"] = "stand_casual_pout",
            ["Pucker|stand|casual"] = "stand_casual_pucker",
            ["RubEyes|stand|casual"] = "stand_casual_rub_eyes",
            ["RubFace|stand|casual"] = "stand_casual_rub_face",
            ["RubHead|stand|casual"] = "stand_casual_rub_head",
            ["ScreenPeek|stand|casual"] = "stand_casual_screen_peek",
            ["SecretHide|stand|casual"] = "stand_casual_secret_hide",
            ["ShyGiggle|stand|casual"] = "stand_casual_shy_giggle",
            ["SmallYawn|stand|casual"] = "stand_casual_small_yawn",
            ["Snap|stand|casual"] = "stand_casual_snap",
            ["SneezeTiny|stand|casual"] = "stand_casual_sneeze_tiny",
            ["Spread|stand|casual"] = "stand_casual_spread",
            ["Squint|stand|casual"] = "stand_casual_squint",
            ["SternSmile|stand|casual"] = "stand_casual_stern_smile",
            ["Stretch|stand|casual"] = "stand_casual_stretch",
            ["Tense|stand|casual"] = "stand_casual_tense",
            ["Think|stand|casual"] = "stand_casual_think",
            ["UpLeft|stand|casual"] = "stand_casual_up_left",
            ["Yawn|stand|casual"] = "stand_casual_yawn",

            // Standing · Pajamas (note: no Think, Greet, LookAround, TiltHead, or Turnhead in Pajamas)
            ["Idle|stand|pajamas"] = "stand_pajamas_idle",
            ["Blink|stand|pajamas"] = "stand_pajamas_idle",
            ["DrowsySlump|stand|pajamas"] = "stand_pajamas_drowsy_slump",
            ["Falled|stand|pajamas"] = "stand_pajamas_falled",
            ["Falling|stand|pajamas"] = "stand_pajamas_falling",
            ["MagicCast|stand|pajamas"] = "stand_pajamas_magic_cast",
            ["Music|stand|pajamas"] = "stand_pajamas_music",
            ["RubHead|stand|pajamas"] = "stand_pajamas_rub_head",
            ["Snap|stand|pajamas"] = "stand_pajamas_snap",
            ["StandUp|stand|pajamas"] = "sleep_pajamas_stand_up",
            ["Stretch|stand|pajamas"] = "stand_pajamas_stretch",
            ["Tsundere|stand|pajamas"] = "stand_pajamas_tsundere",
            ["Yawn|stand|pajamas"] = "stand_pajamas_yawn",

            // Sitting · Casual (window/edge sit; pajamas do not enter sit pose)
            ["Idle|sit|casual"] = "sit_casual_idle",
            ["Blink|sit|casual"] = "sit_casual_idle",
            ["ShakeLeg|sit|casual"] = "sit_casual_shake_leg",
            ["Turnhead|sit|casual"] = "sit_casual_turnhead",

            // Sofa Sit · Casual
            ["Idle|sofa|casual"] = "sit_casual_sofa_idle",
            ["Blink|sofa|casual"] = "sit_casual_sofa_idle",
            ["SofaDoubt|sofa|casual"] = "sit_casual_sofa_doubt",
            ["SofaStandUp|sofa|casual"] = "sit_casual_get_off_sofa",

            // Lying Down (uses lie_pajamas_* / sleep_pajamas_* across clothing states)
            ["Idle|lie|casual"] = "lie_pajamas_idle",
            ["Idle|lie|pajamas"] = "lie_pajamas_idle",
            ["Blink|lie|casual"] = "lie_pajamas_idle",
            ["Blink|lie|pajamas"] = "lie_pajamas_idle",
            ["Flirt|lie|casual"] = "lie_pajamas_flirt",
            ["Flirt|lie|pajamas"] = "lie_pajamas_flirt",
            ["ShakeLeg|lie|casual"] = "lie_pajamas_shake_leg",
            ["ShakeLeg|lie|pajamas"] = "lie_pajamas_shake_leg",
            ["Snap|lie|casual"] = "lie_pajamas_snap",
            ["Snap|lie|pajamas"] = "lie_pajamas_snap",
            ["PillowLie|lie|casual"] = "lie_pajamas_idle",
            ["PillowLie|lie|pajamas"] = "lie_pajamas_idle",
            ["LieStandUp|lie|casual"] = "lie_pajamas_stand_up",
            ["LieStandUp|lie|pajamas"] = "lie_pajamas_stand_up",
            ["CoverBlanket|lie|casual"] = "sleep_pajamas_cover_blanket",
            ["CoverBlanket|lie|pajamas"] = "sleep_pajamas_cover_blanket",
            ["Mumble|lie|casual"] = "sleep_pajamas_mumble",
            ["Mumble|lie|pajamas"] = "sleep_pajamas_mumble",
            ["RubEyes|lie|casual"] = "sleep_pajamas_rub_eyes",
            ["RubEyes|lie|pajamas"] = "sleep_pajamas_rub_eyes",
            ["TakeBlanket|lie|casual"] = "sleep_pajamas_take_blanket",
            ["TakeBlanket|lie|pajamas"] = "sleep_pajamas_take_blanket",
            ["TwitchBare|lie|casual"] = "sleep_pajamas_twitch_bare",
            ["TwitchBare|lie|pajamas"] = "sleep_pajamas_twitch_bare",

            // Sleeping (uses sleep_pajamas_* across clothing states)
            ["Idle|sleep|casual"] = "sleep_pajamas_idle",
            ["Idle|sleep|pajamas"] = "sleep_pajamas_idle",
            ["Blink|sleep|casual"] = "sleep_pajamas_idle",
            ["Blink|sleep|pajamas"] = "sleep_pajamas_idle",
            ["Sleep|sleep|casual"] = "sleep_pajamas_idle",
            ["Sleep|sleep|pajamas"] = "sleep_pajamas_idle",
            ["CoverBlanket|sleep|casual"] = "sleep_pajamas_cover_blanket",
            ["CoverBlanket|sleep|pajamas"] = "sleep_pajamas_cover_blanket",
            ["Mumble|sleep|casual"] = "sleep_pajamas_mumble",
            ["Mumble|sleep|pajamas"] = "sleep_pajamas_mumble",
            ["RubEyes|sleep|casual"] = "sleep_pajamas_rub_eyes",
            ["RubEyes|sleep|pajamas"] = "sleep_pajamas_rub_eyes",
            ["TakeBlanket|sleep|casual"] = "sleep_pajamas_take_blanket",
            ["TakeBlanket|sleep|pajamas"] = "sleep_pajamas_take_blanket",
            ["TwitchBare|sleep|casual"] = "sleep_pajamas_twitch_bare",
            ["TwitchBare|sleep|pajamas"] = "sleep_pajamas_twitch_bare"
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
