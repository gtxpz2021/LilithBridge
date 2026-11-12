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

    public static string BuildSystemContract(BridgeConfig config, bool allowStoryRecall = true, bool allowMemoryCommands = true,
        bool nativeMemoryRecall = false, bool nativeStoryRecall = false)
    {
        // The old static system instructions stayed active with the migration
        // switch off. Keep that behavior while centralizing their formats here.
        return BuildContract(config, null, allowStoryRecall, allowMemoryCommands, nativeMemoryRecall, nativeStoryRecall);
    }

    private static string BuildContract(BridgeConfig config, SessionState? state, bool allowStoryRecall, bool allowMemoryCommands,
        bool nativeMemoryRecall = false, bool nativeStoryRecall = false)
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

        if (nativeMemoryRecall)
            builder.AppendLine("聊天经历需要补查时使用本轮 review_context 原生工具；先读记录再自然回答，不另发 <memory_recall> 标签启动第二条补查链。可以改写查询、继续读取或联想扩查，已有材料够用时直接回答。");
        else if (allowMemoryCommands && config.MemoryHonestRecallEnabled)
        {
            builder.AppendLine("Memory Recall：玩家明确询问过去而现有记录不足时，可输出 <memory_recall>关键词</memory_recall> 请求查找；记录足够时直接回答。");
        }

        if (nativeStoryRecall)
        {
            builder.AppendLine("作品剧情可使用本轮 recall_work_reference 原生工具查询选定的第一作资料，按对话线索改写或扩查。作品档案与本机聊天历史来自不同数据源。本轮使用原生工具，不另发 <story:recall> 标签启动第二条查询链。");
        }
        else if (allowMemoryCommands && config.PreviousStoryEnabled && allowStoryRecall
            && PreviousStoryArchive.CanSupply(config) && PreviousStoryArchive.IsAvailable(config))
        {
            builder.AppendLine("Story Recall：需要第一作资料时可输出 <story:recall>关键词或章节</story:recall> 查询；可按线索改写或扩查。");
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
            builder.AppendLine("Reflect：想安静回看时可输出 <reflect>简短原因</reflect>。若明确指本轮已给原话，可用 <reflect>{\"reason\":\"原因\",\"target_event_ids\":[\"原话event_id\"]}</reflect>，可用本轮已供原话的event_id或source_ref，最多4项；不知道对象就不填，不用出处冒充对象。不必每轮请求，不显示给玩家，可能稍后安排。");
        }

        builder.AppendLine("Voice：只有程序提供语音 ID 时才输出 <voice:数字ID>。");
        builder.AppendLine("Output：控制标签不会显示在气泡里，玩家看到的只有莉莉丝的话。");
        return builder.ToString().TrimEnd();
    }
}
