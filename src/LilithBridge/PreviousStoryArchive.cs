using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace LilithBridge;

internal sealed record StoryRecallResult(
    string Query,
    string Content,
    bool Found);

internal static class PreviousStoryArchive
{
    private const string ResourceName = "LilithBridge.PREVIOUS_GAME_STORY.md";

    // Availability is actual embedded content, never a configuration promise.
    internal static bool IsAvailable =>
        typeof(PreviousStoryArchive).Assembly.GetManifestResourceInfo(ResourceName) != null;
    // Story-specific terms. Only a turn that names the work itself (or one of
    // its named people, places and props) may open the full archive. Ordinary
    // common-history words (还记得/回忆/以前/之前) are deliberately NOT here:
    // those ask for shared memory recall, not for the previous game's script.
    private static readonly string[] StoryTerms =
    {
        "第一作", "前作", "游戏剧情", "银葱之森", "银葱", "逐梦之典", "曼德拉草",
        "无脸国王", "学者", "魔王", "草莓蛋糕", "烤箱", "时间机器",
        "FONTAINEBLEAU", "摩天轮", "Huis-clos", "夏特露酱", "弗子酱",
        "甜心护士", "糖果药丸", "第四颗糖", "黑色笔记本", "Tulpa",
        "tulpa", "孤独国王", "世界之外", "世界之内", "你存在吗",
        "我存在吗", "屏幕前的你", "不存在的你", "蛋糕时间",
        "心跳刺激", "青涩约会", "治疗"
    };

    // Ordinary common-history vocabulary. It asks for shared experience, not
    // for the previous work's chapter archive; kept separately so the archive
    // is not opened by "还记得/以前/回忆" alone (stage-two A line, 2026-10-17).
    internal static readonly string[] CommonHistoryTerms =
    {
        "还记得", "回忆", "以前", "之前", "以前的事"
    };

    public static string Build(BridgeConfig config)
    {
        if (config == null || !config.PreviousStoryEnabled || !IsAvailable)
            return "";
        return Fit(GetFullArchive(), config.PreviousStoryMaxChars);
    }

    public static string BuildNavigation() => "";

    /// <summary>
    /// The first story-specific term this input names, or null. This is the only
    /// gate that opens the full archive; it is separate from common-history
    /// vocabulary so "还记得/以前" stays on the memory-recall path.
    /// </summary>
    internal static string? FindStoryTerm(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        foreach (string term in StoryTerms)
        {
            if (text.Contains(term, StringComparison.OrdinalIgnoreCase))
                return term;
        }
        return null;
    }

    /// <summary>
    /// The ordinary common-history term this input names, or null. Reported for
    /// diagnostics only: it never opens the story archive.
    /// </summary>
    internal static string? FindCommonHistoryTerm(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        foreach (string term in CommonHistoryTerms)
        {
            if (text.Contains(term, StringComparison.OrdinalIgnoreCase))
                return term;
        }
        return null;
    }

    /// <summary>True when the current input itself already points at the first game.</summary>
    public static bool LooksStoryRelevant(string? text) => FindStoryTerm(text) != null;

    /// <summary>
    /// Compact always-on core: who she is and where she comes from, without the
    /// chapter-by-chapter archive. Used when the full archive is only supplied on
    /// story-relevant turns.
    /// </summary>
    public static string BuildIdentityCore() => "";

    /// <summary>
    /// Frames the archive as a partial record rather than an authoritative
    /// dossier or proof of the current player's history. Keep explicit record
    /// boundaries. Historical framing measurements in
    /// PRJL-DYNAMIC-PROMPT-EXPERIMENT-20260918.md apply to the previous wording;
    /// the corpus-based wording below has not been evaluated with a live model.
    /// </summary>
    public static string FrameAsPartialRecord(string archive) =>
        string.IsNullOrWhiteSpace(archive)
            ? archive
            : "[前作剧情档案·节选（记录并不完整）]\n" +
              "说明：下面是部分作品资料，可以用莉莉丝的视角谈起；共同经历以当前对话或记录确认的路径为准，不合并互斥分支，不据此推断玩家现实。未提供的细节不能靠想象补成回忆。\n" +
              // Added after the acceptance sweep: in two of four rounds she answered
              // "你当时怎么回应" by inventing a specific branch choice.
              "玩家问“我当时是怎么回应／选了什么／说过什么”时：档案只记录作品里存在哪些分支，没有记录他走过哪一条。先说这条选择没有留档，再谈作品里有哪些可能；不要替他确认走过哪一条。\n" +
              // 2026-09-25 user ruling: a player may have walked mutually exclusive
              // routes in different playthroughs. Accept that as multi-valued, but
              // never merge it into one continuity — and never ask him to pick a
              // single canon unless the current question needs the current path.
              "玩家说“两条（互斥路线）我都走过”时：可以承认他在不同周目里都经历过，" +
              "但不得把它们合并成同一周目、同一连续路径或当前共同过去；" +
              "只有当前问题确实需要确定哪条路线属于当前连续性、而记录不足时，才保持不确定或请他确认。\n" +
              archive +
              "\n（节选到此为止；缺失细节仅表示这份资料无法核对，不表示过去绝未发生。）";

    private static string? _lastAutoRecallQuery;
    private static DateTimeOffset _lastAutoRecallAt = DateTimeOffset.MinValue;

    public static StoryRecallResult? AutoRecall(
        string userText,
        string recentContext,
        BridgeConfig config)
    {
        if (config == null || !config.PreviousStoryAutoRecallEnabled)
            return null;
        string source = Clean(userText ?? "")
            + " " + Clean(recentContext ?? "");
        string? query = FindAutoRecallQuery(source);
        if (query == null)
            return null;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (string.Equals(_lastAutoRecallQuery, query, StringComparison.OrdinalIgnoreCase)
            && now - _lastAutoRecallAt < TimeSpan.FromSeconds(120))
        {
            return null;
        }
        StoryRecallResult recall = Recall(query, config);
        if (!recall.Found)
            return null;
        _lastAutoRecallQuery = query;
        _lastAutoRecallAt = now;
        return recall;
    }

    private static string? FindAutoRecallQuery(string source) => FindStoryTerm(source);

    // Test-only: the cross-request throttle is process state. A suite that
    // compares decisions for the same input resets it explicitly.
    internal static void ResetAutoRecallStateForTests()
    {
        _lastAutoRecallQuery = null;
        _lastAutoRecallAt = DateTimeOffset.MinValue;
    }

    public static StoryRecallResult Recall(string query, BridgeConfig config)
    {
        query = Clean(query);
        if (query.Length < 2)
            return new StoryRecallResult(query, "没有可检索的关键词", false);

        string full = GetFullArchive();
        if (full.Length == 0)
            return new StoryRecallResult(query, "完整前作剧情库不可用", false);

        string[] terms = BuildTerms(query);
        string[] lines = full.Replace("\r\n", "\n").Split('\n');
        var builder = new StringBuilder();
        builder.AppendLine("[前作剧情库补全：由莉莉丝主动召回]");
        builder.AppendLine("下面是作品片段，不是当前玩家的经历证明；只把已确认路径当作共同回忆，不合并互斥分支，不推断玩家现实。说话人须结合语境核对，归属不清时不作确定引用。"
            + "玩家问“我当时是怎么回应／选了什么／说过什么”时：档案只记录作品里有哪些分支，没有记录他走过哪一条；先说这条选择没有留档，不要替他确认。");
        builder.AppendLine("检索关键词：" + query);
        builder.AppendLine();

        bool found = false;
        var seen = new System.Collections.Generic.HashSet<int>();
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0)
                continue;
            if (!ContainsAny(line, terms))
                continue;
            found = true;
            int start = Math.Max(0, i - 2);
            int end = Math.Min(lines.Length - 1, i + 5);
            for (int j = start; j <= end; j++)
            {
                if (!seen.Add(j))
                    continue;
                string raw = lines[j];
                if (raw.Length <= 9000)
                    builder.AppendLine(raw.TrimEnd());
            }
        }

        if (!found)
        {
            string fallback = FindFallbackSection(full, query);
            if (fallback.Length > 0)
            {
                found = true;
                builder.AppendLine(fallback);
            }
        }

        if (!found)
            return new StoryRecallResult(query, "剧情库中暂时没有找到这条细节；不要补全。", false);

        string content = Fit(builder.ToString(), config.PreviousStoryRecallMaxChars);
        return new StoryRecallResult(query, content, true);
    }

    public static string BuildRecallContext(StoryRecallResult recall, BridgeConfig config)
    {
        if (recall == null || !recall.Found)
            return "";
        return recall.Content
            + Environment.NewLine
            + Environment.NewLine
            + "这是你主动调用的前作剧情补全。请自然使用它，不要再输出检索标签，不要解释程序或检索过程，不要像说明书一样朗读。";
    }

    private static string GetFullArchive()
    {
        try
        {
            using Stream? stream = typeof(PreviousStoryArchive).Assembly
                .GetManifestResourceStream(ResourceName);
            if (stream == null)
                return "";
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch
        {
            return "";
        }
    }

    private static string[] BuildTerms(string query)
    {
        var terms = new System.Collections.Generic.List<string>();
        string trimmed = Clean(query);
        if (trimmed.Length > 0)
            terms.Add(trimmed);
        foreach (Match match in Regex.Matches(
            query,
            @"([\p{IsCJKUnifiedIdeographs}]{2,6}|[A-Za-z]{3,}|FONTAINEBLEAU|Huis-clos|Tulpa)",
            RegexOptions.CultureInvariant))
        {
            string value = match.Value;
            if (value.Length > 1 && !terms.Contains(value, StringComparer.OrdinalIgnoreCase))
                terms.Add(value);
        }
        if (terms.Count == 0)
            terms.Add(query);
        return terms.ToArray();
    }

    private static bool ContainsAny(string source, string[] terms)
    {
        foreach (string term in terms)
        {
            if (source.Contains(term, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string FindFallbackSection(string full, string query)
    {
        string lower = query.Trim().ToLowerInvariant();
        string[] anchors =
        {
            "5.1 序章", "5.2 第一章", "5.3 第二章", "5.4 第三章",
            "5.5 第四章", "5.6 第五章", "5.7 第六章", "房间", "草莓蛋糕",
            "银葱之森", "摩天轮", "甜心护士", "黑色笔记本", "Tulpa",
            "第四颗糖", "Huis-clos", "逐梦之典", "不/存在的你，和我"
        };
        string? best = null;
        int bestScore = -1;
        foreach (string anchor in anchors)
        {
            if (!lower.Contains(anchor.ToLowerInvariant(), StringComparison.Ordinal)
                && !anchor.ToLowerInvariant().Contains(lower, StringComparison.Ordinal))
                continue;
            int index = full.IndexOf(anchor, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
                continue;
            int score = index >= 0 ? 1 : 0;
            if (score > bestScore)
            {
                bestScore = score;
                best = full.Substring(index, Math.Min(2200, full.Length - index));
            }
        }
        return best ?? "";
    }

    private static string Clean(string? value)
    {
        return (value ?? "").Replace("\r", "").Replace("\n", " ").Trim();
    }

    private static string Fit(string text, int maxChars)
    {
        text = (text ?? "").Trim();
        int limit = Math.Clamp(maxChars, 800, 12000);
        if (text.Length <= limit)
            return text;
        int cut = text.LastIndexOf('\n', limit);
        if (cut < limit / 2)
            cut = limit;
        return text[..cut].TrimEnd() + "\n\n[档案截断：更完整的前作语料可由配置文件路径加载]";
    }
}
