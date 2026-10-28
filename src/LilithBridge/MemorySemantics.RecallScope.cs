using System.Text.RegularExpressions;

namespace LilithBridge;

// A transient read-side query. It narrows source selection, never establishes
// truth, rewrites an archived utterance, or creates persistent memory state.
internal sealed class ExperienceRecallQuery
{
    public string Subject { get; init; } = "";
    public string[] ExcludedSubjects { get; init; } = Array.Empty<string>();
    public string[] Quotes { get; init; } = Array.Empty<string>();
    public string[] Titles { get; init; } = Array.Empty<string>();
    public string ScoringQuery { get; init; } = "";
    public (int Year, int Month, int Day)? From { get; init; }
    public (int Year, int Month, int Day)? Until { get; init; }
    public int? FromMinute { get; init; }
    public int? UntilMinute { get; init; }
    public bool UnresolvedTime { get; init; }
    public string[] FocusTerms { get; init; } = Array.Empty<string>();
    public bool CompareVersions { get; init; }
    public bool WantsQuotation { get; init; }

    // A bounded content view can match coordinated one-character nouns. The
    // source remains verbatim; this only qualifies/ranks it, never proves it.
    public bool MatchesContent(string text) => FocusTerms.Length >= 2
        && FocusTerms.All(x => ContainsTerm(text, x))
        || WantsQuotation && MemorySemantics.IsRelevantRecall("小说 台词",text);

    public int SourcePreference(string text)
    {
        if (WantsQuotation) return Regex.IsMatch(text, "小说|角色|台词|剧本|[“「『]") ? 2 : 0;
        bool queryOrGap = Regex.IsMatch(text, "[？?]|(?:哪里|哪儿|何处|多少|什么).{0,8}[吗呢]|没有找到|还没找到|记不清|不记得|未找到");
        if (queryOrGap) return 0;
        return MatchesContent(text) || MemorySemantics.IsCorrectionDeclaration(text) ? 2 : 1;
    }

    public bool Allows(MemoryTurn turn)
        => AllowsSourceScope(turn) && (Subject.Length == 0 || ContainsTerm(turn.Text, Subject));

    // Descriptive subject words guide discovery; only independently recognized
    // scope (quotes, explicit exclusions, dates/titles) authorizes boundaries.
    public bool AllowsSourceScope(MemoryTurn turn)
    {
        if (UnresolvedTime) return false;
        if (ExcludedSubjects.Any(x => ContainsTerm(turn.Text, x))) return false;
        if (Quotes.Any(x => !ContainsTerm(turn.Text, x))) return false;
        // A complete book title has an explicit closing boundary. Ordinary
        // quoted fragments still match their original words inside a sentence.
        if (Titles.Any(x => !turn.Text.Contains(x, StringComparison.Ordinal))) return false;
        if (FromMinute is { } minute)
        {
            int recordedMinute = turn.Timestamp.LocalDateTime.Hour * 60 + turn.Timestamp.LocalDateTime.Minute;
            if (recordedMinute < minute || recordedMinute > (UntilMinute ?? minute)) return false;
        }
        if (From is not { } from) return true;
        // A date written as part of the source describes its content. Otherwise
        // the archive timestamp is the available date evidence. Keep the two
        // kinds visible in the original material; neither changes its timestamp.
        var dates = FromMinute.HasValue ? new List<(int Year, int Month, int Day)>() : MemorySemantics.RecallDates(turn.Text);
        if (dates.Count == 0) dates.Add((turn.Timestamp.LocalDateTime.Year, turn.Timestamp.LocalDateTime.Month, turn.Timestamp.LocalDateTime.Day));
        var until = Until ?? from;
        return dates.Any(d =>
        {
            // A yearless original stays yearless evidence. It may be relevant
            // to a year-specific question, but selection cannot certify a year
            // that the original never said. Explicit years remain discriminators.
            bool year = d.Year != 0 && from.Year != 0 && until.Year != 0;
            int key=DateKey(d,year),start=DateKey(from,year),end=DateKey(until,year);
            return start <= end ? key >= start && key <= end : !year && (key >= start || key <= end);
        });
    }

    private static int DateKey((int Year, int Month, int Day) d, bool withYear)
        => (withYear ? d.Year * 10000 : 0) + d.Month * 100 + d.Day;

    private static bool ContainsTerm(string text, string term)
    {
        static bool Identifier(char c) => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z'
            or >= '0' and <= '9' or '_' or '-';
        for (int start = 0; start <= text.Length - term.Length;)
        {
            int at = text.IndexOf(term, start, StringComparison.Ordinal);
            if (at < 0) return false;
            int end = at + term.Length;
            if ((!Identifier(term[0]) || at == 0 || !Identifier(text[at - 1]))
                && (!Identifier(term[^1]) || end == text.Length || !Identifier(text[end]))) return true;
            start = at + 1;
        }
        return false;
    }
}

internal static partial class MemorySemantics
{
    private const string RecallMarker = "更正|改正|纠正|修正|改口|澄清";
    private const string RecallPast = "之前|上次|刚才|刚刚|先前|前面|以前|当时|后来";
    private const string RecallSpeechVerb = "说过什么|说了什么|说过|说了|提到过|提到|提过|讲过|讲了|谈过";
    private const string DateNumber = @"[0-9零〇一二三四五六七八九十]{1,4}";
    private static readonly Regex RecallDatePattern = new(
        @"(?<![A-Za-z0-9])(?:(?:(?<year>" + DateNumber + @")年)?(?<month>" + DateNumber + @")月(?<day>" + DateNumber + @")(?:日|号|(?![A-Za-z0-9]))|(?<year>[12][0-9]{3})[-/](?<month>[0-9]{1,2})[-/](?<day>[0-9]{1,2})(?![0-9]))",
        RegexOptions.CultureInvariant);

    public static ExperienceRecallQuery? ParseExperienceRecallQuery(string? query)
    {
        string original = query ?? "";
        if (original.Length == 0 || original.Length > 360) return null;
        // Mask quoted spans before looking for instructions or question marks.
        // A quoted "不要" or "一句话" is content, never an output directive.
        var quotes = new List<string>();
        var titles = new List<string>();
        string masked = Regex.Replace(original, "“(?<q>[^”]+)”|‘(?<q>[^’]+)’|「(?<q>[^」]+)」|『(?<q>[^』]+)』|《(?<q>[^》]+)》|\"(?<q>[^\"]+)\"|'(?<q>[^']+)'", m =>
        {
            quotes.Add(m.Groups["q"].Value);
            if (m.Value.StartsWith("《", StringComparison.Ordinal)) titles.Add(m.Value);
            return new string(' ', m.Length);
        });
        Match marker = Regex.Match(masked, RecallMarker);
        if (!marker.Success) return null;
        // Without a past/correction question, leave ordinary current dialogue on
        // its existing path. The source policy always receives original below.
        if (!Regex.IsMatch(masked, @"之前|上次|先前|当时|后来|记得|回忆|最初|前后|更正后|改正后|修正后|纠正后|[？?]|吗")) return null;

        var dates = RecallDates(masked);
        if (dates.Count > 2) return null;
        bool unresolvedTime = RecallDatePattern.Matches(masked).Count != dates.Count;
        MatchCollection dateSpans = RecallDatePattern.Matches(masked);
        string noDates;
        if (dateSpans.Count == 2 && Regex.IsMatch(
            masked[(dateSpans[0].Index + dateSpans[0].Length)..dateSpans[1].Index], @"^\s*(?:到|至|[-–])\s*$"))
        {
            // The connector belongs to the date interval, not the named object.
            int start = dateSpans[0].Index, end = dateSpans[1].Index + dateSpans[1].Length;
            noDates = masked[..start] + " " + masked[end..];
        }
        else noDates = RecallDatePattern.Replace(masked, " ");
        int? fromMinute = null, untilMinute = null;
        Match clock = Regex.Match(noDates, @"(?<![A-Za-z0-9])(?<h>[0-9]{1,2})[:：](?<m>[0-9]{2})(?:\s*(?:到|至|[-–])\s*(?<eh>[0-9]{1,2})[:：](?<em>[0-9]{2}))?");
        if (clock.Success)
        {
            int h=int.Parse(clock.Groups["h"].Value),m=int.Parse(clock.Groups["m"].Value);
            int eh=clock.Groups["eh"].Success?int.Parse(clock.Groups["eh"].Value):h;
            int em=clock.Groups["em"].Success?int.Parse(clock.Groups["em"].Value):m;
            unresolvedTime |= h>23||eh>23||m>59||em>59||eh*60+em<h*60+m;
            fromMinute=h*60+m;untilMinute=eh*60+em;
            noDates=noDates.Remove(clock.Index,clock.Length);
        }
        Match daypart = Regex.Match(noDates,@"上午|早上|早晨|中午|下午|晚上|夜里");
        if (daypart.Success)
        {
            (fromMinute,untilMinute)=daypart.Value switch
            {
                "上午" or "早上" or "早晨" => (0,719),
                "中午" => (660,839),
                "下午" => (720,1079),
                _ => (1080,1439)
            };
            noDates=noDates.Remove(daypart.Index,daypart.Length);
        }
        Match cleanMarker = Regex.Match(noDates, RecallMarker);
        string subject = CleanRecallSubject(noDates[..cleanMarker.Index]);
        // A temporal preface is not an object. Resolve a following explicit
        // object only at a clause boundary, never by guessing from content.
        if (Regex.IsMatch(subject, @"^(?:说反|说错)(?:了)?(?:之)?(?:前|后|以前|以后)?$")) subject = "";
        // Reuse 150.6's independently preserved object span whenever its bounded
        // grammar applies. Short, unnamed descriptors already admitted a source
        // that used a pronoun rather than repeating the generic object noun.
        string legacyView=ExperienceContentQuery(original);
        bool legacyApplies=legacyView!=original;
        if(legacyApplies)
        {
            int legacyMarker=legacyView.IndexOf("更正",StringComparison.Ordinal);
            string legacySubject=legacyMarker>=0?legacyView[..legacyMarker].Trim():"";
            string originalSubject=Regex.Replace(subject,@"(?:描述|那处|这处|那张|这张|那幅|这幅|的)+$", "").Trim();
            // Ranking text may only reuse the independently identified original
            // span. It must not manufacture a scope from question framing.
            legacyApplies=originalSubject.Length>0 && legacySubject==originalSubject;
            if(legacyApplies) subject=legacySubject;
        }
        // Explicit content scope may occur after the question or after a format
        // phrase. Only the named span is read, never delete the entire suffix.
        Match positive = Regex.Match(noDates,
            @"(?:只(?:回答|说|问)|我问(?:的)?是|用一句话回答)(?<object>[^，,。！？?：:；;]{2,36})");
        if (positive.Success && !Regex.IsMatch(positive.Groups["object"].Value,
            @"^(?:一|两|三|几|1|2|3)(?:句|句话|个字)(?:以内|就好|即可)?$"))
            subject = CleanRecallSubject(positive.Groups["object"].Value);
        Match alternative = Regex.Match(noDates,
            @"不是(?<excluded>[^，,。！？?：:；;]{2,30})[，,]\s*是(?<object>[^，,。！？?：:；;]{2,30})");
        var excluded = new List<string>();
        if (alternative.Success && subject.Length==0)
        {
            subject = CleanRecallSubject(alternative.Groups["object"].Value);
            excluded.Add(CleanRecallSubject(alternative.Groups["excluded"].Value));
        }
        foreach (Match m in Regex.Matches(noDates,
            @"(?:别把|不要把|别混|不问)(?<object>[^，,。！？?：:；;]{2,30}?)(?:混进来|混在一起|[，,。！？?：:；;]|$)"))
            excluded.Add(CleanRecallSubject(m.Groups["object"].Value));
        if (subject.Length == 0 && quotes.Count == 0)
        {
            // Correction-first is a normal word order, but this extractor only
            // accepts an explicit bounded object ending at a clause boundary.
            Match after = Regex.Match(noDates[cleanMarker.Index..],
                @"^(?:" + RecallMarker + @")(?:过的|后的|的是|了)?(?<object>[^，,。！？?：:；;]{2,30}?)(?:的记录|的描述|时|后|[，,。！？?：:；;])");
            if (after.Success) subject = CleanRecallSubject(after.Groups["object"].Value);
        }
        // An unknown/ambiguous object cannot be reconstructed by deleting
        // arbitrary interior characters. Generic objects can still retrieve
        // several relevant originals and leave resolution to the answer.
        if (!ValidRecallSubject(subject)) subject = "";
        string[] focus = RecallFocusTerms(noDates);
        bool quotation = Regex.IsMatch(masked, @"小说|角色|剧本|台词");
        // Bare media descriptions are not stable object identities. Preserve
        // named spans, quoted titles and explicit identifiers as hard scope.
        if (Regex.IsMatch(subject, @"^(?:图|图片|照片|画|画面|文字|说明|描述|记录)(?:里|中)?$")) subject = "";
        excluded = excluded.Where(ValidRecallSubject).Distinct(StringComparer.Ordinal).ToList();
        if(legacyApplies && subject.Length<3 && quotes.Count==0 && dates.Count==0
            && excluded.Count==0 && !fromMinute.HasValue && !unresolvedTime && focus.Length<2 && !quotation) return null;
        if (subject.Length == 0 && quotes.Count == 0 && dates.Count == 0 && !unresolvedTime && !fromMinute.HasValue && focus.Length < 2 && !quotation) return null;
        string scoring = legacyApplies ? legacyView : string.Join(" ", new[] { subject }.Concat(quotes)
            .Concat(focus).Concat(quotation ? new[]{"台词", "小说"} : Array.Empty<string>())
            .Where(x => x.Length > 0).Append(cleanMarker.Value).Append("更正").Distinct(StringComparer.Ordinal));
        return new ExperienceRecallQuery
        {
            Subject = subject, Quotes = quotes.Distinct(StringComparer.Ordinal).ToArray(),
            Titles = titles.Distinct(StringComparer.Ordinal).ToArray(),
            ExcludedSubjects = excluded.ToArray(), ScoringQuery = scoring,
            From = dates.Count > 0 ? dates[0] : null,
            Until = dates.Count > 1 ? dates[1] : null,
            FromMinute=fromMinute,UntilMinute=untilMinute,UnresolvedTime=unresolvedTime,
            FocusTerms=focus, CompareVersions=Regex.IsMatch(masked, @"(?:更正|改正|纠正|修正)前后|最初.*后来|原来.*现在"), WantsQuotation=quotation
        };
    }

    // Only grammatical edges are removed. No object vocabulary, synonyms or
    // answer values are supplied by this view.
    private static string[] RecallFocusTerms(string text)
    {
        foreach (string raw in Regex.Split(text, @"[，,。！？?：:；;]"))
        {
            string clause = Regex.Replace(raw.Trim(), @"^(?:(?:更正|改正|纠正|修正)(?:后|过)(?:的)?|原来|最初|后来|现在|请问)+", "");
            Match m = Regex.Match(clause, @"^(?<nouns>[^，,。！？?：:；;]{1,30}?)(?:分别|各)?(?:在哪里|在哪儿|在何处|是什么|是多少|在什么地方|在什么位置)");
            if (!m.Success) continue;
            string nouns = Regex.Replace(m.Groups["nouns"].Value, @"(?:的位置|位置|的)$", "");
            string[] terms = Regex.Split(nouns, @"和|与|及|、").Select(x => x.Trim()).ToArray();
            if (terms.Length is >= 2 and <= 4 && terms.All(x => x.Length is >= 1 and <= 12
                && !Regex.IsMatch(x, @"的|之前|之后|更正|改正|纠正|修正|说过|提到|[\s]"))) return terms;
        }
        return Array.Empty<string>();
    }

    // A request-local abstention, never a delete/forget command. Quoted words
    // are content and cannot switch retrieval off.
    public static bool DeclinesArchiveRecall(string? query)
    {
        string value = MaskRecallQuotes(query ?? "");
        return Regex.IsMatch(value, @"(?:^|[，,。；;])\s*(?:今天|这次|本轮|现在)?(?:我们|我|请)?(?:先|暂时)?(?:不|不用|不要|别|无需|先不)(?:再|去)?(?:翻|查|找|检索|回顾)(?:阅|看|找)?(?:以前的|过去的|之前的|旧|历史)(?:记录|纪录|档案|对话|聊天)(?:了|吧)?(?=[，,。！？?；;\s]|$)");
    }

    internal static string MaskRecallQuotes(string text) => Regex.Replace(text,
        "“[^”]*”|‘[^’]*’|「[^」]*」|『[^』]*』|《[^》]*》|\\\"[^\\\"]*\\\"|'[^']*'", m => new string(' ',m.Length));

    // Explicit alphanumeric names are independently delimited, including when
    // no correction marker exists. This prevents a missing named target from
    // being replaced by ordinary history. Uncertain prose has no hard object.
    public static string[] RecallIdentifiers(string? query) => Regex.Matches(MaskRecallQuotes(query ?? ""),
        @"(?<![A-Za-z0-9_-])[A-Za-z][A-Za-z0-9_-]*[0-9][A-Za-z0-9_-]*(?![A-Za-z0-9_-])")
        .Select(m => m.Value).Distinct(StringComparer.Ordinal).ToArray();

    public static bool ContainsRecallIdentifier(string text, string term) => Regex.IsMatch(text,
        @"(?<![A-Za-z0-9_-])" + Regex.Escape(term) + @"(?![A-Za-z0-9_-])");

    private static string CleanRecallSubject(string text)
    {
        string value = text.Trim();
        value = Regex.Replace(value, @"^(?:莉莉丝[，,\s]*)?(?:(?:我|你)(?:" + RecallPast + @")(?:" + RecallSpeechVerb + @")?(?:关于|对)?|(?:还记得|记得|请回忆|回忆)(?:" + RecallPast + @")?|(?:" + RecallPast + @")(?:说反后|我对|我|你|对)?(?:关于)?|关于|对)", "").Trim();
        value = Regex.Replace(value, @"^(?:那张|这张|那份|这份|那幅|这幅|那次|那句|那处|的|标题为)+", "").Trim();
        value = Regex.Replace(value, @"(?:的记录|的描述|那张|那份|那次|那句|那处|之|的)+$", "").Trim();
        return value;
    }

    private static bool ValidRecallSubject(string value) => value.Length is >= 2 and <= 36
        && !Regex.IsMatch(value, @"[，,。！？?：:；;\s]|在哪里|是什么|是不是|分别|之前|上次|更正|改正|纠正|修正|只回答|说反|说错|以后|以前|提到|说过|记得|回忆|怎么|是否");

    internal static List<(int Year, int Month, int Day)> RecallDates(string text)
    {
        var result = new List<(int, int, int)>();
        foreach (Match m in RecallDatePattern.Matches(text))
        {
            int year = m.Groups["year"].Success ? RecallNumber(m.Groups["year"].Value) : 0;
            int month = RecallNumber(m.Groups["month"].Value), day = RecallNumber(m.Groups["day"].Value);
            if (month is < 1 or > 12 || day is < 1 or > 31 || (year != 0 && year < 1900)) continue;
            try { _ = new DateTime(year == 0 ? 2000 : year, month, day); }
            catch (ArgumentOutOfRangeException) { continue; }
            result.Add((year, month, day));
        }
        return result;
    }

    private static int RecallNumber(string value)
    {
        if (int.TryParse(value, out int number)) return number;
        const string digits = "零一二三四五六七八九";
        if (value.Contains('十'))
        {
            string[] parts = value.Split('十');
            return (parts[0].Length == 0 ? 1 : RecallNumber(parts[0])) * 10
                + (parts.Length == 1 || parts[1].Length == 0 ? 0 : RecallNumber(parts[1]));
        }
        int total = 0;
        foreach (char c in value) { int d = c == '〇' ? 0 : digits.IndexOf(c); if (d < 0) return -1; total = total * 10 + d; }
        return total;
    }
}
