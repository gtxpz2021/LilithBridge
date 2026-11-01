using System.Text;

namespace LilithBridge;

/// <summary>
/// One capability-format contract owned by Runtime. Main requests include it
/// once in the system prompt; scene/state facts remain in ContextBuilder.
/// Build keeps the old migration-switch behavior for standalone callers.
/// </summary>
internal static class RuntimeProtocol
{
    public static string Build(BridgeConfig config, SessionState? state)
    {
        if (!config.RuntimeProtocolEnabled)
            return "";

        return BuildContract(config, state, allowStoryRecall: true, allowMemoryCommands: true);
    }

    public static string BuildSystemContract(BridgeConfig config, bool allowStoryRecall = true, bool allowMemoryCommands = true)
    {
        // The old static system instructions stayed active with the migration
        // switch off. Keep that behavior while centralizing their formats here.
        return BuildContract(config, null, allowStoryRecall, allowMemoryCommands);
    }

    private static string BuildContract(BridgeConfig config, SessionState? state, bool allowStoryRecall, bool allowMemoryCommands)
    {
        var builder = new StringBuilder();
        builder.AppendLine("[Runtime Protocol]");
        builder.AppendLine("这是程序的能力契约，不是莉莉丝的想法，也不要向玩家解释它。");
        builder.AppendLine("权限默认：上下文没有明确提供的能力默认不可用；`[当前可用动作]`、`[desktop_arrange_state]`、`[native_memo]` 都是程序判断，不是玩家说的话。");
        if (state != null)
            builder.AppendLine("当前姿态=" + ActionCapability.DescribePose(state) + "；动作合法性以 [当前可用动作] 为准。");

        if (config.ActionCapabilityEnabled)
        {
            builder.AppendLine("Action：<action:名称>；名称只能来自 [当前可用动作]，姿态不适用时不生成。");
        }

        if (config.ActionSequencesEnabled)
        {
            builder.AppendLine("Action Sequence：<actions>名称:延迟毫秒:时长毫秒,...</actions>；名称必须来自 [当前可用动作]，一次最多 " + config.ActionSequenceMaxSteps + " 步。");
        }

        builder.AppendLine("Emotion：<emotion:名称>；一次选一个：emoji_smile_1/emoji_smile_2/emoji_smile_3/emoji_smile_4/emoji_surprise_1/emoji_surprise_2/emoji_sad_1/emoji_sad_2/emoji_wronged_1/emoji_wronged_2/emoji_angry_1/emoji_calm_1/emoji_daze_1/emoji_sleepy_1/emoji_sleepy_2/emoji_sleepy_3/emoji_sleepy_4。");

        if (config.DesktopArrangeEnabled)
        {
            builder.AppendLine("Desktop：只有 [desktop_arrange_state] 允许时才输出 <desktop_arrange:propose/confirm/cancel/execute/undo>。");
        }

        if (allowMemoryCommands && config.MemoryHonestRecallEnabled)
        {
            builder.AppendLine("Memory Recall：玩家明确询问过去而现有记录不足时，可输出 <memory_recall>关键词</memory_recall> 请求查找；记录足够时直接回答。");
        }

        if (allowMemoryCommands && config.PreviousStoryEnabled && allowStoryRecall
            && PreviousStoryArchive.IsAvailable)
        {
            builder.AppendLine("Story Recall：玩家明确问前作片段而原文不足时，可输出 <story:recall>关键词或章节</story:recall> 请求查找；只使用实际提供的档案。");
        }

        if (allowMemoryCommands && config.MemoWriteEnabled)
        {
            builder.AppendLine("Memo：玩家明确要求记下时可输出 <memo>待办内容</memo> 或 <scratch>随手记内容</scratch>；内容以玩家本次要求为准。");
            builder.AppendLine("面向玩家的原生笔记回复分段：只要输出 memo/scratch/annotation，普通对话正文与操作成功确认必须分开。正文不得预先宣称写入成功；仅将可选成功确认放在 <memo_confirmation>确认文字</memo_confirmation>，无需确认时也输出空槽 <memo_confirmation></memo_confirmation>。JSON 回复以 text 放正文、memo_confirmation 字符串放确认（无需确认时为空串）。程序执行成功才显示确认；失败只改操作回执。未分段的旧式混合写入未全部确认成功时，程序可在同一请求中给出真实聚合结果，请你重新表达一次；续答仅表达，不重做操作。不要把动作标签放进确认槽。静默后台任务仍只输出隐藏操作，不生成确认槽。");
        }

        if (allowMemoryCommands && config.MemoWriteEnabled && config.MemoAnnotationEnabled)
        {
            builder.AppendLine("Annotation：看到 [native_memo] 中真实提供原文的未批注笔记时，可主动输出 <annotation>简短回应</annotation>；保留原文。专门批注任务按 [annotation_mode] 要求安静完成。");
        }

        if (allowMemoryCommands && config.NoteSupplementEnabled)
        {
            builder.AppendLine("Note：想留一张不打扰的小纸条时可以输出 <note>内容</note>；按原版样式生成，不计入未读数。");
        }

        if (allowMemoryCommands && config.ReflectionEnabled)
        {
            builder.AppendLine("Reflect：确实想安静回看某件事时可以输出 <reflect>一句话原因</reflect>；不显示给玩家，程序可能稍后再安排。");
        }

        builder.AppendLine("Voice：只有程序提供语音 ID 时才输出 <voice:数字ID>。");
        builder.AppendLine("Output：控制标签不会显示在气泡里，玩家看到的只有莉莉丝的话。");
        return builder.ToString().TrimEnd();
    }
}
