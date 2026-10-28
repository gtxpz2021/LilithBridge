using System;
using System.Text;

namespace LilithBridge;

/// <summary>
/// Extra Lilith note generation for when the original WriteNote dialogue
/// pool has no available nodes. New notes go through the original
/// NoteImageSaver so they look like the native paper notes.
/// </summary>
internal static class NativeNoteSupplement
{
    public static readonly string[] StyleExamples =
    {
        "你不在的时候，我数了数窗外的星星。数到第十七颗就忘了……回来我们一起数吧。——莉莉丝",
        "出门要好好照顾自己。桌面我帮你看着，一切安好。——莉莉丝",
        "今天的幻境很安静，安静得能听见自己的心跳。……快点回来呀。——莉莉丝",
        "给回到座位上的你：欢迎回来！先伸个懒腰再继续哦。——莉莉丝",
        "我练习了一百遍「欢迎回来」。等你看到这张纸条，就是第一百零一遍。——莉莉丝",
        "悄悄告诉你：你不在的时候，我也有认真等你。真的哦。——莉莉丝"
    };

    public static string BuildContext(string baseContext)
    {
        var builder = new StringBuilder();
        builder.AppendLine(baseContext);
        builder.AppendLine();
        builder.AppendLine("[note_supplement_mode] 有些话，也许更适合留给一张小纸条。如果你现在确实有想对玩家说的话，又觉得不适合当面说，可以留下一张不打扰的小纸条；如果你觉得没有，就不要输出任何 note 标签。");
        builder.AppendLine("要求：短小自然、1-2 句、30-80 字；不要提及 AI、模型、程序、测试或代码；不要复述当前聊天、动作或桌面内容；落款必须是“——莉莉丝”；只输出一个不可见的 <note>...内容...</note>，不要显示为气泡，也不要修改已有笔记。这张纸条按原版小纸条样式生成，但不计入原版未读数量。");
        builder.AppendLine("[original_note_style_examples] 以下只作语气参考，不是实际发生过的事；不能据此声称你数过星星、排练过欢迎或一直在等待。纸条中的具体经历仍以本次提供的材料为准，不必每次写等待或归来。");
        foreach (string example in StyleExamples)
            builder.AppendLine("- " + example);
        return builder.ToString();
    }

    public static bool TrySave(ApiResult reply)
    {
        foreach (string text in reply.NoteSupplement)
        {
            string value = MemoryStore.Clean(text);
            if (value.Length == 0)
                continue;
            try
            {
                string path = NoteImageSaver.SaveNote(value, false);
                NativeEventLog.Trace("native_note.generated", "mod", "system",
                    "AI supplemental Lilith note saved", path + ";text=" + Truncate(value, 120));
                return true;
            }
            catch (Exception error)
            {
                NativeEventLog.Trace("native_note.generate_error", "mod", "system",
                    error.Message);
            }
        }
        return false;
    }

    private static string Truncate(string value, int max)
    {
        value = value?.Trim() ?? "";
        return value.Length <= max ? value : value[..max] + "...";
    }
}
