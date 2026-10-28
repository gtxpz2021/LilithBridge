using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace LilithBridge;

/// <summary>
/// Pure memory semantics: relevance, conflict detection, local compression
/// and dream-style consolidation. Kept separate from storage so it can be
/// tested without Unity or the game API.
/// </summary>
internal static partial class MemorySemantics
{
    private static readonly Regex PlayerClaimMarker = new(
        @"我(?:之前|以前|上次|刚才|刚刚|当时|早就|曾经)?(?:跟你|和你|对你|给你)?(?:告诉过你|告诉过|说过|讲过|提过)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool IsThoughtQuery(string? query) => IsHistoricalThoughtQuery(query)
        || Regex.IsMatch(query ?? "", @"(?:你.{0,8}(?:怎么想|怎么看|想过|想法|看法|思考|反思)|改变.{0,6}(?:看法|想法)|重新.{0,6}(?:理解|考虑))");

    /// <summary>
    /// Observation only. Recognises a player message that asks her to confirm
    /// something he says he once told her, and returns the claimed content.
    ///
    /// Nothing in the runtime acts on this value: it exists so the frequency of
    /// these claims can be measured in real play before any boundary is designed
    /// (see PRJL-PLAYER-CLAIM-OBSERVATION-20260919.md). Returning a claim here
    /// does not mean the claim is false, and an empty result is not a verdict.
    /// </summary>
    public static string ExtractPlayerClaim(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "";
        Match marker = PlayerClaimMarker.Match(message);
        if (!marker.Success)
            return "";
        string rest = message[(marker.Index + marker.Length)..];
        int end = rest.IndexOfAny(new[] { '。', '！', '？', '!', '?', '\n', '；', ';' });
        if (end >= 0)
            rest = rest[..end];
        string claim = Regex.Replace(rest, @"[\s""'“”‘’「」『』《》()（）【】]+", "");
        // A marker inside a conditional clause used to leave pure punctuation and
        // a question fragment ("…如果我说过，你会怎么回？" → "，你会怎么回"),
        // which then ran through the whole source-check path as if it were a
        // proposition. Drop the leading punctuation, then refuse anything that is
        // still an interrogative fragment rather than a statement about the player.
        claim = Regex.Replace(claim, @"^[，、,:：;；.。!！?？\-]+", "");
        claim = Regex.Replace(claim, @"^(?:过|了|说|的是)+", "");
        // Only question / soft particles are trimmed. A trailing 了 or 的 can be
        // part of the claimed words themselves ("我把车修好了").
        claim = Regex.Replace(claim, @"(?:吗|吧|呢|啊|哦|呀|嘛|么|对不对|是不是)+$", "");
        if (claim.Length < 2 || claim.Length > 120)
            return "";
        if (claim.StartsWith("什么") || claim.StartsWith("啥") || claim.StartsWith("哪些"))
            return "";
        if (claim is "这件事" or "那件事" or "这种话" or "这话" or "的话" or "一件事")
            return "";
        if (Regex.IsMatch(claim, @"(?:怎么回|怎么说|怎么办|会怎么|该说|要说什么)$")
            || Regex.IsMatch(claim, @"^(?:会|要|想)?(?:怎么|如何)"))
            return "";
        return claim;
    }

    /// <summary>Observation only: which shape the claim took, for telemetry.</summary>
    public static string DescribePlayerClaimForm(string? message)
    {
        string value = message ?? "";
        if (Regex.IsMatch(value, @"(?:还)?记得|记不记得|想起来"))
            return "remember_question";
        return "statement";
    }

    public static bool IsHistoricalThoughtQuery(string? query)
    {
        // This route is for a request about Lilith's earlier inner state, not
        // temporal/mental words anywhere in ordinary conversation. Work on a
        // read-only view: quoted questions, reports and hypothetical clauses do
        // not become a question addressed to her merely by containing "you".
        string text = MaskRecallQuotes(query ?? "");
        const string past = @"(?:当时|那时|那会儿|那一刻|刚才|之前|以前|上次|昨天|过去|起初|最初|曾经|早就|当初)";
        const string thought = @"(?:怎么想|在想|想了什么|想过什么|想的|觉得|心里|心情|感受|感觉|担心|害怕|难过|开心|高兴|失望|生气|在意|喜欢|讨厌|想法|看法)";
        const string links = @"(?:到底|究竟|是不是|是否|有没有|心里|其实|真的|是|还|就|很|不|没|有|在)*";
        foreach (Match clause in Regex.Matches(text, @"[^，,。！？?；;\r\n]+[。！？?；;]?"))
        {
            string value = clause.Value.Trim();
            if (Regex.IsMatch(value, @"^(?:如果|假如|假设|倘若|要是|if\b)", RegexOptions.IgnoreCase)) continue;
            bool requested = Regex.IsMatch(value, @"^(?:请)?(?:说说|告诉我|我想知道|我想问)");
            value = Regex.Replace(value, @"^(?:(?:请)?(?:说说|告诉我)|我想知道|我想问|还有|那么|所以|可是|但)\s*", "");
            if (Regex.IsMatch(value, @"^(?:what were you thinking|how did you feel|what did you think|did you feel|did you think)\b", RegexOptions.IgnoreCase)) return true;
            if (!requested && !Regex.IsMatch(value, @"[？?]|吗|呢|么|是否|是不是|有没有|怎么|什么|为何|为什么")) continue;
            if (Regex.IsMatch(value, @"(?:如果|假如|假设|倘若|要是)")) continue;
            // An explicit subject may have a bounded topic between its time and
            // predicate. The subjectless form must begin with the predicate:
            // "当时怎么想的？" is addressed to her; "刚才同事说…" is not.
            if (Regex.IsMatch(value, @"^(?:你|莉莉丝)" + past + @"[^。！？?\r\n]{0,32}" + thought)
                || Regex.IsMatch(value, "^" + past + @"(?:你|莉莉丝)?" + links + thought)) return true;
        }
        return false;
    }

    public static string RequestedHistorySpeaker(string? query)
    {
        Match match = Regex.Match(MaskRecallQuotes(query ?? "").Trim(),
            @"^(?:莉莉丝[，,\s]*)?(?:(?<who>我|你)(?:" + RecallPast + @")|(?:" + RecallPast + @")(?<who>我|你))(?:.{0,8}说(?:了什么|过什么|的是什么|了哪|过哪|过几次|了几次)|" + RecallSpeechVerb + @"|(?:更正|改正|纠正|修正|改口|澄清)(?:过|了)?)");
        return !match.Success ? "" : match.Groups["who"].Value == "我" ? "user" : "assistant";
    }

    /// <summary>
    /// A lexical view for experience ranking only. Remove a leading address or
    /// a bounded question addressed to Lilith; never use this view as a player's
    /// purpose, speaker/time selection, claim or permission to supply a source.
    /// Ambiguous names, identity questions and bare references keep the original.
    /// </summary>
    public static string ExperienceScoringQuery(string? query)
    {
        string original = query ?? "";
        string value = original.TrimStart();
        Match prefix = Regex.Match(value, @"^莉莉丝(?:啊|呀)?[，,:：!！\s]+(?<body>.+)$", RegexOptions.Singleline);
        if (!prefix.Success)
        {
            if (!Regex.IsMatch(value, @"(?:[？?]|(?:吗|么|呢)[。！？!?？]*)\s*$"))
                return original;
            prefix = Regex.Match(value, @"^莉莉丝(?:觉得|认为|怎么看待|怎么看|如何看待)(?<body>.+)$", RegexOptions.Singleline);
        }
        if (!prefix.Success)
            return original;
        string body = prefix.Groups["body"].Value.Trim();
        if (Regex.IsMatch(body, @"莉莉丝|名字|姓名|昵称|称呼|身份|叫什么|为何叫|为什么叫|^(?:你|自己)(?:到底|究竟)?(?:是谁|是什么|是哪个)"))
            return original;
        string reference = Regex.Replace(body, @"^(?:你)?(?:觉得|认为|怎么看待|怎么看|如何看待)\s*", "");
        if (Regex.IsMatch(reference,
            @"^(?:(?:刚才|刚刚|之前|以前|上次)?(?:这件事情|那件事情|这个问题|那个问题|这件事|那件事|这回事|那回事|这句话|那句话|这段话|那段话|这个|那个|这种|那种|这些|那些|我们|你们|他们|她们|它们|它|他|她|你|我|这|那))?(?:你)?(?:怎么样|怎么想|怎么看待|怎么看|如何|好不好|行不行|可以|好|对)?[吗呢吧啊呀么？?！!。，,\s]*$"))
            return original;
        return body;
    }

    public static double Similarity(string? left, string? right)
    {
        HashSet<string> a = TokenizeForMatching(left);
        HashSet<string> b = TokenizeForMatching(right);
        if (a.Count == 0 || b.Count == 0)
            return 0d;
        int overlap = a.Count(token => b.Contains(token));
        return overlap / (double)Math.Max(1, Math.Min(a.Count, b.Count));
    }
    // B1 caches these derived search tokens; this does not change matching semantics.
    internal static HashSet<string> MatchingTokens(string? value) => TokenizeForMatching(value);

    /// <summary>Number of shared matching tokens (words and CJK bigrams).</summary>
    public static int MatchingOverlap(string? left, string? right)
    {
        HashSet<string> a = TokenizeForMatching(left);
        HashSet<string> b = TokenizeForMatching(right);
        return a.Count(token => b.Contains(token));
    }

    /// <summary>
    /// Relevance floor for recall. A single shared common bigram (for example
    /// "测试" inside both "分支测试" and "验收测试") used to be enough to pull an
    /// unrelated memory into the request; recall now needs both a minimum share
    /// of the shorter side and more than one shared token.
    /// </summary>
    public const double MinRecallSimilarity = 0.15;
    public const int MinRecallOverlap = 2;

    public static bool IsRelevantRecall(string? query, string? text)
    {
        if (string.IsNullOrWhiteSpace(query))
            return true;
        double score = Similarity(query, text);
        if (score < MinRecallSimilarity)
            return false;
        // Several shared tokens, or a short question the memory almost entirely
        // covers (for example "盆栽呢？" against a sentence about the plant).
        return MatchingOverlap(query, text) >= MinRecallOverlap || score >= 0.5;
    }

    /// <summary>
    /// Self-locking signal: the share of texts that have at least one near
    /// duplicate among the others. A high ratio means the persona keeps
    /// producing variations of the same thought instead of new observations.
    /// </summary>
    public static double RepetitionRatio(IEnumerable<string?> texts, double threshold = 0.75)
    {
        var values = (texts ?? Enumerable.Empty<string?>())
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Select(text => text!.Trim())
            .ToList();
        if (values.Count < 2)
            return 0d;
        int repeated = 0;
        for (int index = 0; index < values.Count; index++)
        {
            for (int other = 0; other < values.Count; other++)
            {
                if (other == index)
                    continue;
                if (Similarity(values[index], values[other]) >= threshold)
                {
                    repeated++;
                    break;
                }
            }
        }
        return repeated / (double)values.Count;
    }

    public static bool LooksLikeConflict(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second))
            return false;
        string a = first.Trim().ToLowerInvariant();
        string b = second.Trim().ToLowerInvariant();
        if (a == b)
            return false;
        bool polarityA = HasAny(a, "喜欢", "会", "可以", "确定", "肯定", "记得", "要", "计划", "like", "likes", "want", "will", "do", "plan");
        bool polarityB = HasAny(b, "讨厌", "不会", "不可以", "取消", "忘了", "不记得", "不要", "放弃", "not", "no longer", "does not");
        if (!polarityA || !polarityB)
            return false;
        return Tokenize(a).Count(token => Tokenize(b).Contains(token)) >= 2;
    }

    public static string CompressConversation(IReadOnlyList<MemoryTurn> turns, int maxChars = 600)
    {
        if (turns == null || turns.Count == 0)
            return "";
        var builder = new StringBuilder();
        builder.Append("过去你与玩家真实聊过的内容压缩如下：");
        int used = builder.Length;
        foreach (MemoryTurn turn in turns)
        {
            if (turn == null || string.IsNullOrWhiteSpace(turn.Text))
                continue;
            string role = turn.Role == "assistant" ? "莉莉丝" : "玩家";
            string line = "\n- " + role + "：" + truncate(turn.Text, 44);
            if (used + line.Length > maxChars)
                break;
            builder.Append(line);
            used += line.Length;
        }
        builder.Append("\n- 这是压缩摘要，不是逐字转述；需要精确细节时不要声称完全记得。");
        return builder.ToString();
    }

    public static string DreamForDay(
        IReadOnlyList<MemoryTurn> history,
        DateTimeOffset day,
        int maxChars = 360)
    {
        if (history == null || history.Count == 0)
            return "";
        var topics = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var feelings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int userLines = 0;
        foreach (MemoryTurn turn in history)
        {
            if (turn == null || string.IsNullOrWhiteSpace(turn.Text))
                continue;
            if (turn.Role != "user")
                continue;
            userLines++;
            foreach (string topic in TopicWords(turn.Text).Take(6))
                topics.Add(topic);
            foreach (string feeling in FeelingWords(turn.Text))
                feelings.Add(feeling);
        }
        string dayText = day.LocalDateTime.ToString("M月d日");
        string summary = "在" + dayText + "，你与玩家聊了"
            + Math.Max(1, userLines) + "次。"
            + (topics.Count > 0
                ? "印象最深的是" + string.Join("、", topics.Take(4)) + "。"
                : "没有特别清晰的话题，但陪伴本身也算记忆。")
            + (feelings.Count > 0
                ? "那天的情绪底色是" + string.Join("、", feelings.Take(4)) + "。"
                : "");
        return truncate(summary, maxChars);
    }

    public static IReadOnlyList<MemoryTurn> RankForRecall(
        IEnumerable<MemoryTurn> candidates,
        string? query,
        DateTimeOffset now,
        int max)
    {
        if (candidates == null)
            return Array.Empty<MemoryTurn>();
        var ranked = candidates
            .Select(turn => new
            {
                Turn = turn,
                QueryScore = string.IsNullOrWhiteSpace(query)
                    ? 0d
                    : Similarity(query, turn.Text),
                BaseScore = MemoryLifecycle.RecallScore(turn, now)
            })
            .Where(item => string.IsNullOrWhiteSpace(query) || item.QueryScore >= MinRecallSimilarity)
            .Where(item => string.IsNullOrWhiteSpace(query)
                || MatchingOverlap(query, item.Turn.Text) >= MinRecallOverlap)
            .OrderByDescending(item => item.QueryScore * 0.72d + item.BaseScore * 0.28d)
            .Take(Math.Max(0, max))
            .Select(item => item.Turn)
            .ToList();
        return ranked;
    }

    public static string BuildRecallContext(IReadOnlyList<MemoryTurn> turns)
    {
        if (turns == null || turns.Count == 0)
            return "";
        var builder = new StringBuilder();
        builder.AppendLine("[检索到的长期记录]");
        foreach (MemoryTurn turn in turns)
        {
            if (turn.NaturalMemoryReadContext.Length > 0) builder.AppendLine(turn.NaturalMemoryReadContext);
            string time = turn.UpdatedAt == default
                ? ""
                : "（" + turn.UpdatedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm") + "）";
            string certainty = turn.Validity == "corrected"
                ? "；这条后来被纠正过"
                : turn.Validity == "inferred"
                    ? "；这条是推断"
                    : "";
            builder.AppendLine("- [" + turn.RecallId + "] " + turn.Text + time + certainty
                + "; validity=" + turn.Validity + "; source_event_id=" + turn.SourceEventId
                + (turn.Evidence.Length > 0 ? "; 原文=" + turn.Evidence : ""));
        }
        builder.AppendLine("这些是找到的记录；有来源的发言也可能包含玩笑或推测。未找到不能证明从未发生。");
        return builder.ToString();
    }

    private static HashSet<string> Tokenize(string? value)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(value))
            return result;
        foreach (Match match in Regex.Matches(
                     value,
                     @"[\p{L}\p{N}]+",
                     System.Text.RegularExpressions.RegexOptions.CultureInvariant))
        {
            string token = match.Value.ToLowerInvariant();
            if (token.Length <= 1 && !char.IsLetter(token[0]))
                continue;
            result.Add(token);
        }
        return result;
    }

    /// <summary>
    /// Experimental C1 matching tokenizer.
    /// Keeps the original contiguous token for compatibility and adds
    /// Unicode-aware bigrams so Chinese and mixed-language queries can
    /// overlap with stored Chinese memory text without external dependencies.
    /// Used only by Similarity; conflict and scoring paths remain unchanged.
    /// </summary>
    private static HashSet<string> TokenizeForMatching(string? value)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(value))
            return result;
        foreach (Match match in Regex.Matches(
                     value,
                     @"[\p{L}\p{N}]+",
                     System.Text.RegularExpressions.RegexOptions.CultureInvariant))
        {
            string segment = match.Value;
            result.Add(segment.ToLowerInvariant());
            char[] chars = segment.ToCharArray();
            if (chars.Length < 2)
                continue;
            for (int i = 0; i < chars.Length - 1; i++)
            {
                result.Add((chars[i].ToString() + chars[i + 1]).ToLowerInvariant());
            }
        }
        return result;
    }

    private static IEnumerable<string> TopicWords(string text)
    {
        foreach (string word in new[]
                 {
                     "搬家", "生日", "工作", "考试", "家人", "宠物", "草莓蛋糕", "银葱之森",
                     "摩天轮", "游戏", "音乐", "代码", "旅行", "电影", "晚安", "喜欢", "约定"
                 })
        {
            if (text.Contains(word, StringComparison.OrdinalIgnoreCase))
                yield return word;
        }
    }

    private static IEnumerable<string> FeelingWords(string text)
    {
        foreach (string word in new[]
                 {
                     "累", "难过", "开心", "期待", "害怕", "焦虑", "想念", "害羞",
                     "担心", "幸福", "生气", "孤独", "安心"
                 })
        {
            if (text.Contains(word, StringComparison.OrdinalIgnoreCase))
                yield return word;
        }
    }

    private static bool HasAny(string value, params string[] terms)
    {
        return terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static string truncate(string value, int max)
    {
        return value.Length <= max ? value : value[..max] + "…";
    }
}
