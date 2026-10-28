using System;
using System.Collections.Generic;
using System.Linq;

namespace LilithBridge;

/// <summary>
/// Maps the model's short action names to the game's native LilithActionType
/// values. Unknown names are rejected instead of being passed to the native
/// animation API as a guessed animation file.
/// </summary>
internal static class ActionCatalog
{
    private static readonly Dictionary<string, LilithActionType> Aliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["idle"] = LilithActionType.Idle,
            ["blink"] = LilithActionType.Blink,
            ["greet"] = LilithActionType.Greet,
            ["hello"] = LilithActionType.Greet,
            ["wave"] = LilithActionType.LazyWave,
            ["yawn"] = LilithActionType.Yawn,
            ["small_yawn"] = LilithActionType.SmallYawn,
            ["perk_yawn"] = LilithActionType.PerkYawn,
            ["stretch"] = LilithActionType.Stretch,
            ["think"] = LilithActionType.Think,
            ["confuse"] = LilithActionType.Confuse,
            ["confused"] = LilithActionType.Confuse,
            ["blush"] = LilithActionType.Blush,
            ["shy"] = LilithActionType.ShyGiggle,
            ["shy_giggle"] = LilithActionType.ShyGiggle,
            ["giggle"] = LilithActionType.ShyGiggle,
            ["laugh"] = LilithActionType.ShyGiggle,
            ["happy"] = LilithActionType.HappyHop,
            ["hop"] = LilithActionType.HappyHop,
            ["pout"] = LilithActionType.Pout,
            ["tsundere"] = LilithActionType.Tsundere,
            ["stern_smile"] = LilithActionType.SternSmile,
            ["flirt"] = LilithActionType.Flirt,
            ["look"] = LilithActionType.LookAround,
            ["look_around"] = LilithActionType.LookAround,
            ["tilt_head"] = LilithActionType.TiltHead,
            ["turn_head"] = LilithActionType.Turnhead,
            ["rub_face"] = LilithActionType.RubFace,
            ["rub_head"] = LilithActionType.RubHead,
            ["beside_pat"] = LilithActionType.BesidePat,
            ["hug"] = LilithActionType.HugAsk,
            ["cry"] = LilithActionType.FakeCry,
            ["fake_cry"] = LilithActionType.FakeCry,
            ["angry"] = LilithActionType.FakeAngry,
            ["fake_angry"] = LilithActionType.FakeAngry,
            ["wronged"] = LilithActionType.FakeWronged,
            ["tense"] = LilithActionType.Tense,
            ["sleep"] = LilithActionType.Sleep,
            ["sleepy"] = LilithActionType.Drowsy,
            ["drowsy"] = LilithActionType.Drowsy,
            ["drowsy_slump"] = LilithActionType.DrowsySlump,
            ["lie"] = LilithActionType.PillowLie,
            ["stand"] = LilithActionType.StandUp,
            ["stand_up"] = LilithActionType.StandUp,
            ["gift_give"] = LilithActionType.GiftGive,
            ["lie_stand_up"] = LilithActionType.LieStandUp,
            ["sofa_doubt"] = LilithActionType.SofaDoubt,
            ["sofa_stand_up"] = LilithActionType.SofaStandUp,
            ["snap"] = LilithActionType.Snap,
            ["arrange"] = LilithActionType.Arrange,
            ["music"] = LilithActionType.Music,
            ["magic"] = LilithActionType.MagicCast,
            ["magic_cast"] = LilithActionType.MagicCast,
            ["cake"] = LilithActionType.CakeUp,
            ["cake_up"] = LilithActionType.CakeUp,
            ["dessert"] = LilithActionType.DessertUp,
            ["dessert_up"] = LilithActionType.DessertUp,
            ["confetti"] = LilithActionType.Confetti,
            ["hide"] = LilithActionType.Hide,
            ["secret_hide"] = LilithActionType.SecretHide,
            ["screen_peek"] = LilithActionType.ScreenPeek,
            ["peek"] = LilithActionType.ScreenPeek,
            ["clap"] = LilithActionType.Clap,
            ["sneeze"] = LilithActionType.SneezeTiny,
            ["sneeze_tiny"] = LilithActionType.SneezeTiny,
            ["pucker"] = LilithActionType.Pucker,
            ["happy_sigh"] = LilithActionType.HappySigh,
            ["half_awake"] = LilithActionType.HalfAwake,
            ["stern_smile"] = LilithActionType.SternSmile,
            ["empty_hands"] = LilithActionType.EmptyHands,
            ["lazy_wave"] = LilithActionType.LazyWave,
            ["squint"] = LilithActionType.Squint,
            ["peek_idle"] = LilithActionType.PeekIdle,
            ["peek_end"] = LilithActionType.PeekEnd,
            ["shake_leg"] = LilithActionType.ShakeLeg,
            ["up_left"] = LilithActionType.UpLeft,
            ["spread"] = LilithActionType.Spread,
            ["cake_idle"] = LilithActionType.CakeIdle,
            ["cake_end"] = LilithActionType.CakeEnd,
            ["dessert_idle"] = LilithActionType.DessertIdle,
            ["mumble"] = LilithActionType.Mumble,
            ["cover_blanket"] = LilithActionType.CoverBlanket,
            ["take_blanket"] = LilithActionType.TakeBlanket,
            ["twitch_bare"] = LilithActionType.TwitchBare,
            ["\u601d\u8003"] = LilithActionType.Think,
            ["\u60f3"] = LilithActionType.Think,
            ["\u7591\u60d1"] = LilithActionType.Confuse,
            ["\u5bb3\u7f9e"] = LilithActionType.ShyGiggle,
            ["\u8138\u7ea2"] = LilithActionType.Blush,
            ["\u5f00\u5fc3"] = LilithActionType.HappyHop,
            ["\u9ad8\u5174"] = LilithActionType.HappyHop,
            ["\u751f\u6c14"] = LilithActionType.FakeAngry,
            ["\u6325\u624b"] = LilithActionType.LazyWave,
            ["\u5f53\u624b"] = LilithActionType.LazyWave,
            ["\u770b"] = LilithActionType.LookAround,
            ["\u6478\u5934"] = LilithActionType.RubHead,
            ["\u63c9\u8138"] = LilithActionType.RubFace,
            ["\u54c8\u6b20"] = LilithActionType.Yawn,
            ["\u4f38\u61d2\u8170"] = LilithActionType.Stretch,
            ["\u7761\u89c9"] = LilithActionType.Sleep,
            ["\u8d2c"] = LilithActionType.Drowsy,
            ["\u8eba"] = LilithActionType.PillowLie,
            ["\u7ad9\u8d77"] = LilithActionType.StandUp,
            ["\u62cd\u624b"] = LilithActionType.Clap,
            ["\u9b54\u6cd5"] = LilithActionType.MagicCast,
            ["\u86cb\u7cd5"] = LilithActionType.CakeUp,
            ["\u85cf"] = LilithActionType.Hide,
            ["\u5077\u770b"] = LilithActionType.ScreenPeek,
            ["\u54c7"] = LilithActionType.Pucker,
            ["\u5c16\u5634"] = LilithActionType.Pucker,
            ["\u54c8\u54c8"] = LilithActionType.ShyGiggle,
            ["\u7b11"] = LilithActionType.ShyGiggle
        };

    private static readonly HashSet<LilithActionType> ExposedNativeActions =
        BuildExposedNativeActions();

    public static LilithActionType? TryParseAction(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        string normalized = value.Trim().Replace('-', '_').Replace(' ', '_').ToLowerInvariant();
        if (Aliases.TryGetValue(normalized, out LilithActionType mapped))
            return mapped;
        try
        {
            if (Enum.TryParse<LilithActionType>(normalized, true, out LilithActionType parsed))
                return parsed;
            string pascal = string.Concat(
                normalized
                    .Split('_', StringSplitOptions.RemoveEmptyEntries)
                    .Select(part => part.Length == 0
                        ? ""
                        : char.ToUpperInvariant(part[0]) + part[1..]));
            if (Enum.TryParse<LilithActionType>(pascal, true, out LilithActionType pascalParsed))
                return pascalParsed;
        }
        catch
        {
            // Ignore.
        }
        return null;
    }

    public static bool TryParseAllowedAction(string? value, out LilithActionType action)
    {
        action = LilithActionType.None;
        if (TryParseAction(value) is { } parsed
            && ExposedNativeActions.Contains(parsed))
        {
            action = parsed;
            return true;
        }
        return false;
    }

    public static string NormalizeEmotion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        return value.Trim().ToLowerInvariant() switch
        {
            "smile" => "emoji_smile_1",
            "happy" or "excited" => "emoji_smile_3",
            "calm" or "gentle" => "emoji_calm_1",
            "blush" or "shy" => "emoji_smile_4",
            "sad" => "emoji_sad_1",
            "angry" => "emoji_angry_1",
            "surprised" => "emoji_surprise_1",
            "confused" or "confuse" => "emoji_daze_1",
            "tense" => "emoji_calm_1",
            "sleep" or "sleepy" => "emoji_sleepy_1",
            "pout" => "emoji_smile_4",
            "think" => "emoji_calm_1",
            "wronged" => "emoji_wronged_1",
            _ => value.Trim()
        };
    }

    public static string[] AllowedActionNames()
    {
        var names = new List<string>
        {
            "idle", "blink", "greet", "wave", "lazy_wave", "yawn", "small_yawn",
            "perk_yawn", "stretch", "think", "confuse", "blush", "shy",
            "shy_giggle", "giggle", "happy", "hop", "pout", "tsundere",
            "stern_smile", "flirt", "look", "look_around", "tilt_head",
            "turn_head", "rub_face", "rub_head", "rub_eyes", "beside_pat",
            "hug", "hug_ask", "sleep", "sleepy", "drowsy", "drowsy_slump",
            "lie", "pillow_lie", "stand_up", "snap", "arrange", "music",
            "magic", "magic_cast", "cake", "cake_up", "cake_idle", "cake_end",
            "dessert", "dessert_up", "dessert_idle",
            "confetti", "hide", "secret_hide", "screen_peek", "peek",
            "peek_idle", "peek_end", "clap", "sneeze", "pucker",
            "happy_sigh", "half_awake", "spread", "up_left",
            "fake_cry", "fake_angry", "fake_wronged", "empty_hands", "squint",
            "shake_leg", "mumble", "cover_blanket",
            "take_blanket", "twitch_bare", "tense", "angry",
            "gift_give", "lie_stand_up", "sofa_doubt", "sofa_stand_up"
        };
        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names.ToArray();
    }

    private static HashSet<LilithActionType> BuildExposedNativeActions()
    {
        var set = new HashSet<LilithActionType>();
        foreach (string name in AllowedActionNames())
        {
            if (TryParseAction(name) is { } parsed)
                set.Add(parsed);
        }
        return set;
    }
}
