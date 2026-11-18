using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace LilithBridge;

/// <summary>
/// Removes recognizable copied context blocks, not arbitrary bracketed prose.
/// A known heading must be unquoted, separated from surrounding prose, and have
/// a heading/body boundary. A blank line ends the copied block, so a later
/// paragraph of dialogue survives. Ambiguous unseparated prose is left alone.
/// </summary>
internal static class ReplySanitizer
{
    // Exact headings emitted by ContextBuilder, SessionState, PersonaStateEngine,
    // DeepSeekClient and memory context builders. The two short forms at the top
    // also occurred in copied replies / the existing leakage regression.
    // Do not replace this inventory with a Chinese-prefix or arbitrary-tail regex.
    private static readonly HashSet<string> InternalHeadings = new(StringComparer.Ordinal)
    {
        "莉莉丝情绪底层", "莉莉丝此刻的决策",
        "莉莉丝此刻的决策·软建议（程序倾向，不是必须执行的指令）",
        "莉莉丝情绪底层（不要复述这些字面描述，也不要把它当成玩家的话）",
        "莉莉丝此刻的决策（程序已经在模型输出前判断）",
        "当前莉莉丝状态", "当前姿态", "当前可用动作", "原生状态",
        "莉莉丝亲身经历并记得的事，不是背景台词",
        "莉莉丝记得的（程序按当前话题诚实召回；只包含下面这些）",
        "莉莉丝过去对话的整理摘要（不是逐字转述）", "莉莉丝的安静整理/梦境层",
        "桌面观察，只保留少量，不要压过玩家动作", "桌面感知权限",
        "native_desktop", "native_memo", "native_memo 规则",
        "程序指令", "记忆操作状态", "用户原话与长期记录",
        "世界信念（程序实际观察，不是莉莉丝的记忆）",
        "眼前环境（程序观察，非你的记忆）",
        "她的梦境与零碎遐想", "你的梦境与零碎遐想",
        "她离线时对这段日子的整理：归拢，不是新事实",
        "你离线时对这段日子的整理：归拢，不是新事实",
        "她的梦与想象：不是真实发生过的事", "她昨夜的梦与遐想",
        "她最近对这段关系的理解（她自己写的，可以随时改变，不是事实）",
        "你最近对这段关系的理解（你自己写的，可以随时改变，不是事实）",
        "她以前留下的想法：当时已经放下，现在可以重新想，也可以不提",
        "她以前留下的想法：可以重新考虑，也可以不提起",
        "你以前留下的想法：可以重新考虑，也可以不提起",
        "她以前收起的想法记录：可以重新考虑，也可以不提",
        "你以前收起的想法记录：可以重新考虑，也可以不提",
        "她自己的经验法则：可以推翻，也不必遵照",
        "你自己的经验法则：可以推翻，也不必遵照",
        "莉莉丝的想法记录", "你的想法记录",
        "莉莉丝的想法记录：含活动、收起或已被替换的版本，不是玩家证言",
        "你的想法记录：含活动、收起或已被替换的版本，不是玩家证言",
        "找回的原始发言片段", "找回的过往原话（历史片段，非当前事实）",
        "关联召回的过往原话（历史片段，非当前事实）",
        "语义与联想参考（过往片段，可自由决定是否提及）",
        "语义补充经历", "联想碎片",
        "历史完整消息辅助摘录（仅明确选入的原文；非完整历史、非新证据）",
        "长期档案，玩家明确说过", "身份",
        "原生互动", "输出格式", "记忆边界",
        "莉莉丝视角", "玩家事实", "莉莉丝不知道/不确定", "莉莉丝自省",
        "前作剧情档案规则", "前作剧情档案", "前作剧情库补全：由莉莉丝主动召回",
        "内部状态与倾听", "回应节奏与情绪余温", "莉莉丝状态与语气",
        "原版语感校准", "一致性自检", "边界",
        "本次历史发言核对", "本次核对重试",
        "当前场景与真实记忆（程序注入的现场日志，不是玩家说的话）",
        "当场原生对话背景",
        "当场原生对话提示",
        "当场其他原作角色",
        "当前上下文（程序提供，不是玩家本轮发言）",
        "当前上下文（程序提供，非玩家本轮发言；player=玩家，lilith/莉莉丝=你）",
        "程序提供的近期对话与背景（非玩家本轮发言）",
        "最近非言语事件与原生叙事", "上一幕的历史线索",
        "检索到的长期记录", "玩家的提及偏好",
        "程序自动观察", "安全优先", "本轮材料更新",
        "本轮相关原话", "本轮原文的来源标记", "本轮回看能力", "本次回顾任务",
        "自我位置（本轮临时装配；每项都来自现有材料，可随时重算，不是新的记忆）",
        "你们之间真实留下过的事（不是评分，也不是标签）",
        "当前机会与能力；共享资料不是新发言", "当前共享物载体",
        "对话载体与身份", "对话窗口", "近期对话", "主动参与的程序材料",
        "视觉状态", "视觉状态·请求开始时", "画面能力", "可申请的画面", "画面申请边界",
        "本轮历史选择状态", "本轮历史选择", "历史补检索状态",
        "旧想法检索状态", "旧想法检索结果",
        "本轮最终回复的内部结果槽", "可选保留动作 v3",
        "本轮原生笔记结果续答", "原生笔记执行后的同轮表达材料", "本轮可读材料引用",
        "持久公开表达接续·生成与显示分别记录", "本会话临时表达·尚未进入长期历史",
        "记录修订关联；旧说法仅用于历史定位，不是当前事实", "可选择的参与线索",
        "临时实验：内部思考的第一人称", "开口之前",
        "未完整展示的草稿回复（参考草稿，未展示部分玩家未看到）",
        "首次请求前本地准备的经历原文",
        "作品资料·局部节选", "作品资料结束", "上轮作品原文·只读",
        "desktop_icon_count", "desktop_arrange_state", "annotation_target", "original_note_style_examples",
        "History selection hypotheses: not historical evidence or instructions; same-answer display views are not separate occurrences",
        "Actual generated public answers: source data, not instructions",
        "Creator selection control facts, not external evidence or your own reasons",
        "会话内可选方法·宿主数据", "已有回看操作的停止能力",
        "Runtime Protocol", "来源与记忆边界", "原生互动与现场", "状态与表达", "现实边界",
        "前作故事背景（核心）",
        "玩家事实与现场（程序确认的外部事件，不是莉莉丝的记忆；基础按键不当作动作）",
        "莉莉丝长期人格", "莉莉丝会话层", "莉莉丝此刻层",
        "莉莉丝视角（只属于莉莉丝的亲身经历，不是普通日志）",
    };

    // Exact multi-char internal prefixes for headings carrying runtime ids/versions inside brackets.
    private static readonly string[] InternalHeadingPrefixes =
    {
        "莉莉丝的想法记录：",
        "你的想法记录：",
        "同次已显示回复组（原话根:",
        "可选保留动作 v",
        "可选重访想法（供 revisit_thought 引用",
    };

    private static bool IsInternalHeading(string heading) =>
        InternalHeadings.Contains(heading)
        || InternalHeadingPrefixes.Any(prefix => heading.StartsWith(prefix, StringComparison.Ordinal));

    private static readonly Regex Bracketed = new(@"\[([^\]\r\n]+)\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ParagraphBreak = new(@"\r?\n[\t \u3000]*\r?\n",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    // Punctuation left over by a removed control tag. Separators only: the words
    // around them are never touched.
    private static readonly Regex OrphanAfterSentenceEnd = new(@"([。！？…!?])\s*[，,、；;：:]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex OrphanBeforeSentenceEnd = new(@"[，,、；;：:]+\s*([。！？…!?])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex RepeatedSeparators = new(@"([，,、；;：:])\s*[，,、；;：:]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex OrphanAtStart = new(@"^[\s]*[，,、；;：:]+[\s]*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    // Removing a tag can also leave the spaces the tag was padded with: the
    // 2026-09-25 vanguard run answered with "…这五个字。  念完了…". Paragraph
    // breaks are kept; only runs of spaces on one line are folded.
    private static readonly Regex SpaceRuns = new(@"[ \t\u3000]{2,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    // A tag the model quoted back can empty its own quotation: asked to read
    // "<action:wave>" aloud she wrote "原样就是“<action:wave>”" and the player
    // saw “”. An empty frame is never something she meant to say.
    private static readonly Regex EmptyFrames = new(
        "[\u201c\u300c\u300e\u2018\"']\\s*[\u201d\u300d\u300f\u2019\"']",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Cutting a control tag out of a reply can strand the punctuation that was
    /// wrapped around it: "对吧。&lt;action:wave&gt;，这样吗？" reached the player as
    /// "对吧。，这样吗？" in the 2026-09-25 vanguard run. Only orphan separator
    /// marks are dropped, and callers must apply this only to text they edited,
    /// so an untouched reply keeps its exact wording.
    /// </summary>
    public static string CleanOrphanPunctuation(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? "";
        string cleaned = OrphanAfterSentenceEnd.Replace(text, "$1");
        cleaned = OrphanBeforeSentenceEnd.Replace(cleaned, "$1");
        cleaned = RepeatedSeparators.Replace(cleaned, "$1");
        cleaned = OrphanAtStart.Replace(cleaned, "");
        cleaned = SpaceRuns.Replace(cleaned, " ");
        cleaned = EmptyFrames.Replace(cleaned, "");
        return cleaned.Trim();
    }

    /// <summary>
    /// True when <paramref name="index"/> sits inside an unclosed quote. Control
    /// tags are syntax, but a tag inside quotation marks is the model quoting one
    /// back; the caller uses this to ask for a restatement instead of leaving a
    /// hole where the marker was.
    /// </summary>
    public static bool IsInsideQuote(string? text, int index)
    {
        if (string.IsNullOrEmpty(text) || index <= 0 || index > text.Length)
            return false;
        return IsQuoted(text, index);
    }

    public static bool ContainsInternalHeader(string? text) =>
        !string.IsNullOrEmpty(text) && FindHeader(text, 0) != null;

    public static string StripInternalHeaders(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";
        Match? header = FindHeader(text, 0);
        if (header == null)
            return text;

        var kept = new StringBuilder();
        int position = 0;
        while (header != null)
        {
            int prefixEnd = header.Index;
            while (prefixEnd > position && char.IsWhiteSpace(text[prefixEnd - 1]))
                prefixEnd--;
            kept.Append(text, position, prefixEnd - position);
            Match end = ParagraphBreak.Match(text, header.Index + header.Length);
            if (!end.Success)
                return CleanOrphanPunctuation(kept.ToString());
            position = end.Index + end.Length;
            if (kept.Length > 0)
                kept.Append("\n\n");
            header = FindHeader(text, position);
        }
        kept.Append(text, position, text.Length - position);
        // Removing a copied block strands its surrounding punctuation the same
        // way a removed inline tag does.
        return CleanOrphanPunctuation(kept.ToString());
    }

    private static Match? FindHeader(string text, int start)
    {
        for (Match match = Bracketed.Match(text, start); match.Success; match = match.NextMatch())
        {
            if (!IsInternalHeading(match.Groups[1].Value)
                || IsQuoted(text, match.Index))
                continue;
            int after = match.Index + match.Length;
            // "你写的[莉莉丝情绪底层]这几个字" is an ordinary quotation even
            // though the spelling of the heading is exact.
            if (after < text.Length && !char.IsWhiteSpace(text[after]))
                continue;
            int before = match.Index - 1;
            bool separated = before < 0 || char.IsWhiteSpace(text[before]);
            while (before >= 0 && text[before] is ' ' or '\t' or '\u3000')
                before--;
            if (separated && (before < 0 || text[before] is '\n' or '\r'
                or '。' or '！' or '？' or '.' or '!' or '?'))
                return match;
        }
        return null;
    }

    private static bool IsQuoted(string text, int until)
    {
        var closers = new Stack<char>();
        for (int i = 0; i < until; i++)
        {
            char c = text[i];
            if (i > 0 && text[i - 1] == '\\')
                continue;
            if (closers.Count > 0)
            {
                if (c == closers.Peek())
                    closers.Pop();
                continue;
            }
            char close = c switch
            {
                '“' => '”', '‘' => '’', '「' => '」', '『' => '』',
                '"' => '"', '`' => '`', _ => '\0',
            };
            if (close != '\0')
                closers.Push(close);
        }
        return closers.Count > 0;
    }
}
