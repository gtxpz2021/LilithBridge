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
        "她离线时对这段日子的整理：归拢，不是新事实",
        "她的梦与想象：不是真实发生过的事",
        "她最近对这段关系的理解（她自己写的，可以随时改变，不是事实）",
        "她以前留下的想法：当时已经放下，现在可以重新想，也可以不提",
        "她以前留下的想法：可以重新考虑，也可以不提起",
        "她自己的经验法则：可以推翻，也不必遵照",
        "找回的原始发言片段", "原生互动", "输出格式", "记忆边界",
        "莉莉丝视角", "玩家事实", "莉莉丝不知道/不确定", "莉莉丝自省",
        "前作剧情档案规则", "前作剧情档案", "前作剧情库补全：由莉莉丝主动召回",
        "内部状态与倾听", "回应节奏与情绪余温", "莉莉丝状态与语气",
        "原版语感校准", "一致性自检", "边界",
        "本次历史发言核对", "本次核对重试",
        "当前场景与真实记忆（程序注入的现场日志，不是玩家说的话）",
        "当前上下文（程序提供，不是玩家本轮发言）", "Runtime Protocol",
        "来源与记忆边界", "原生互动与现场", "状态与表达", "现实边界",
        "前作故事背景（核心）",
        "玩家事实与现场（程序确认的外部事件，不是莉莉丝的记忆；基础按键不当作动作）",
        "莉莉丝长期人格", "莉莉丝会话层", "莉莉丝此刻层",
        "莉莉丝视角（只属于莉莉丝的亲身经历，不是普通日志）",
    };

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
            if (!InternalHeadings.Contains(match.Groups[1].Value)
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
