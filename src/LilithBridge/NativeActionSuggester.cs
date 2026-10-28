using System;

namespace LilithBridge;

internal static class NativeActionSuggester
{
    private const double FallbackRepeatWindowSeconds = 45.0;
    private static string _lastFallbackAction = "";
    private static DateTimeOffset _lastFallbackAt = DateTimeOffset.MinValue;

    public static void Apply(ApiResult reply, string userText, string context)
    {
        if (reply == null)
            return;
        // The automatic action guess must only react to the current player
        // input. The full context contains previous turns and can keep
        // repeating an old keyword such as "刚睡醒", which makes Lilith play
        // stretch after every reply. The model may still choose a deliberate
        // action tag; this fallback is only for the immediate input.
        string text = (userText ?? "").ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(reply.Action))
        {
            reply.Action = SuggestAction(text);
            if (reply.Action.Length > 0
                && reply.Action == _lastFallbackAction
                && DateTimeOffset.UtcNow - _lastFallbackAt
                    < TimeSpan.FromSeconds(FallbackRepeatWindowSeconds))
            {
                reply.Action = "";
            }
            else if (reply.Action.Length > 0)
            {
                _lastFallbackAction = reply.Action;
                _lastFallbackAt = DateTimeOffset.UtcNow;
            }
        }

        if (string.IsNullOrWhiteSpace(reply.Emotion)
            || reply.Emotion == "neutral")
            reply.Emotion = SuggestEmotion(text, reply.Action);
    }

    private static string SuggestAction(string text)
    {
        if (HasAny(text, "喜欢你", "爱你", "想你", "想你啦", "想我",
            "好想", "离不开你"))
            return "blush";
        if (HasAny(text, "你好", "哈喽", "嗨", "hello", "hi"))
            return "greet";
        if (HasAny(text, "开心", "高兴", "太好了", "好棒", "耶！", "哈哈"))
            return "happy";
        if (HasAny(text, "惊讶", "吓一跳", "突然", "没想到", "哇"))
            return "confuse";
        if (HasAny(text, "好奇", "想知道", "看看", "观察", "窗外", "天气", "花"))
            return "look_around";
        if (HasAny(text, "担心", "心疼", "关心", "陪你", "别难过"))
            return "tilt_head";
        if (HasAny(text, "摸头", "摸你", "揉头", "摸头发", "摸摸头", "捏脸"))
            return "rub_head";
        if (HasAny(text, "摸脸", "揉脸", "戳脸", "摸摸脸", "捏脸"))
            return "rub_face";
        if (HasAny(text, "摸手", "捏手", "拉手", "牵手", "握手"))
            return "beside_pat";
        if (HasAny(text, "摸裙", "裙子", "裙底"))
            return "pout";
        if (HasAny(text, "摸胸", "胸", "手手", "不要摸"))
            return "fake_angry";
        if (HasAny(text, "拖", "抓走", "移走", "拿走", "拽", "搬"))
            return "confuse";
        if (HasAny(text, "累", "疲惫", "辛苦", "加班", "熬夜", "困了",
            "没力气", "好累"))
            return "stretch";
        if (HasAny(text, "晚安", "睡觉", "要睡", "困", "zzz", "睡吧"))
            return "drowsy";
        if (HasAny(text, "早安", "早上好", "起床", "新的一天", "醒了"))
            return "stretch";
        if (HasAny(text, "蛋糕", "草莓", "甜点", "下午茶", "茶", "饿", "吃"))
            return "cake_up";
        if (HasAny(text, "魔法", "魔术", "愿望", "变个", "驱散"))
            return "magic_cast";
        if (HasAny(text, "音乐", "歌", "听歌", "唱"))
            return "music";
        if (HasAny(text, "游戏", "打游戏", "玩一会", "一起玩"))
            return "happy";
        if (HasAny(text, "窗外", "天气", "下雨", "阳光", "风", "花"))
            return "look_around";
        if (HasAny(text, "生气", "烦", "讨厌", "恶心", "气死", "坏蛋"))
            return "fake_angry";
        if (HasAny(text, "难过", "哭", "难受", "压力", "焦虑", "心累"))
            return "think";
        if (HasAny(text, "回来", "你回来", "回家了", "到家"))
            return "greet";
        if (HasAny(text, "动一下", "活动一下", "做动作", "转个圈", "转圈",
            "跳舞", "挥挥手", "挥个手", "打个招呼", "跳一下"))
            return "happy";
        return "";
    }

    private static string SuggestEmotion(string text, string action)
    {
        if (HasAny(text, "喜欢你", "爱你", "想你", "想我", "好想"))
            return "emoji_smile_4";
        if (HasAny(text, "难过", "哭", "难受", "压力", "焦虑"))
            return "emoji_sad_1";
        if (HasAny(text, "生气", "烦", "讨厌", "恶心"))
            return "emoji_angry_1";
        if (HasAny(text, "惊讶", "吓", "突然", "诶", "咦"))
            return "emoji_surprise_1";
        return action switch
        {
            "rub_head" or "rub_face" or "blush" or "shy_giggle" => "emoji_smile_4",
            "stretch" or "yawn" or "small_yawn" => "emoji_sleepy_1",
            "drowsy" or "sleep" => "emoji_sleepy_1",
            "cake_up" or "happy" => "emoji_smile_3",
            "confuse" => "emoji_daze_1",
            "fake_angry" => "emoji_angry_1",
            "look_around" => "emoji_calm_1",
            _ => "emoji_smile_1"
        };
    }

    private static bool HasAny(string text, params string[] terms)
    {
        foreach (string term in terms)
        {
            if (text.Contains(term, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
