using System.Text.RegularExpressions;

namespace LilithBridge;

internal static partial class MemorySemantics
{
    // A deliberately narrow scoring view, never a replacement for the user's
    // words, source policy, speaker selection, or a claim that a fact is true.
    public static string ExperienceContentQuery(string? query)
    {
        string original = query ?? "";
        if (original.Length > 240) return original;
        int end = original.IndexOfAny(new[] { '？', '?' });
        if (end < 0) return original;
        string body = original[..end];
        string tail = original[(end + 1)..];
        if (body.Length > 100
            || !Regex.IsMatch(body, "更正|改正|纠正|修正|改口|澄清")
            || !Regex.IsMatch(body, "之前|刚才|刚刚|上次|先前|后来|当时|记得|回忆|更正后|改正后|修正后|纠正后")
            || !Regex.IsMatch(body, "哪里|哪儿|何处|位置|什么|哪些|怎样|如何|多少|记得")) return original;
        // Do not compress quoted language, negation, alternatives, dates or
        // specific time ranges; the lexical view cannot faithfully resolve them.
        if (Regex.IsMatch(body, "[“”‘’\"'「」『』《》0-9]|昨天|前天|去年|上周|本周|那天|今天|明天|[一二三四五六七八九十]+[年月日]|以前.*以后|(?:更正|改正|纠正|修正)(?:之)?前|不|未|无|没|非|除了|或者|还是"))
            return original;
        foreach (string clause in Regex.Split(tail, "[，,；;。.!！\\r\\n]+"))
        {
            string instruction = clause.Trim();
            if (instruction.Length == 0) continue;
            if (!Regex.IsMatch(instruction,
                @"^(?:(?:只|请)?(?:据|根据|按)(?:已有|现有|相关)?(?:记录|原话)(?:说|回答)(?:一|两|三|几|1|2|3)?句?|(?:如果|若|要是)?(?:没有|没)(?:相关)?(?:记录|依据)就说(?:记不清|不记得)|(?:不用|无需|不要)截图|(?:只|请)?(?:说|答|回答)(?:一|两|三|几|1|2|3)句|(?:只用)?(?:一|两|三|1|2|3)句(?:话回答|话|以内)?|(?:简短回答|不要提问|不提问))$"))
                return original;
        }
        // This bounded grammar requires the object before the correction marker.
        // Independently recover that original span, removing framing only at its
        // edges. If later normalization changes it, decline the scoring view.
        Match originalMarker = Regex.Match(body, "更正|改正|纠正|修正|改口|澄清");
        // A correction-first question can have only framing before the marker.
        // Such framing is not an object and must never become ranking content.
        if (CleanRecallSubject(body[..originalMarker.Index]).Length == 0) return original;
        string originalSubject = body[..originalMarker.Index];
        originalSubject = Regex.Replace(originalSubject,
            @"^(?:我|你)?(?:还记得|能回忆|请回忆|回忆|记得)?(?:之前|刚刚|刚才|上次|先前|前面|后来|当时)?(?:说过什么|说了什么)?(?:关于)?", "");
        originalSubject = Regex.Replace(originalSubject, @"(?:描述|那处|这处|那张|这张|那幅|这幅|的)+$", "").Trim();
        if (originalSubject.Length == 0) return original;
        string value = Regex.Replace(body, "更正|改正|纠正|修正|改口|澄清", " 更正 ");
        value = Regex.Replace(value, "分别在什么地方|分别在哪里|各在什么地方|各在何处|在什么位置|在什么地方|在哪里|在哪儿|在何处|什么位置|改成了什么|说过什么|说了什么|哪些内容|什么内容|是什么|是多少|分别|位置|怎样|如何", " ");
        value = Regex.Replace(value, "还记得|你记得|能回忆|请回忆|回忆|记得|之前|刚刚|刚才|上次|先前|前面|后来|当时|关于|那张|这张|那幅|这幅|那处|这处|那一个|那个|这个", " ");
        value = Regex.Replace(value, @"^[我你]\s+|描述(?=\s*(?:的\s*)?更正)|的话|吗(?=\s*[,，]|\s*$)", " ");
        // Keep the explicit object before the correction marker intact. Words
        // such as 和顺古镇 and 描述馆 are names, not conjunctions or framing.
        int marker = value.IndexOf("更正", StringComparison.Ordinal);
        string subject = value[..marker].Trim().TrimEnd('的').Trim();
        if (!string.Equals(subject, originalSubject, StringComparison.Ordinal)) return original;
        string question = value[marker..];
        question = Regex.Replace(question, "[，,：:、\\s]+|(?:之后的|后的|过的|的|和|与|及|各)", " ");
        value = subject + " " + question;
        // Only remove these particles next to the normalized correction marker;
        // globally deleting 中/里/后 would corrupt names such as 山中照片.
        value = Regex.Replace(value, @"(更正\s+)(?:后|里|中)(?=\s|$)", "$1 ");
        string[] terms = value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToArray();
        if (terms.Any(t => t.Length > 12) || terms.Count(t => t != "更正") < 2) return original;
        return string.Join(" ", terms);
    }

    public static bool IsContentRecallMatch(string view, string text)
    {
        string[] terms = view.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t != "更正").Distinct(StringComparer.Ordinal).ToArray();
        // The marker alone, or one short common noun, must never rescue a source.
        // Long retained descriptors (e.g. a named picture) cannot be silently lost.
        return terms.Count(t => text.Contains(t, StringComparison.Ordinal)) >= 2
            && terms.Where(t => t.Length >= 3).All(t => text.Contains(t, StringComparison.Ordinal))
            && IsRelevantRecall(view, text);
    }

    public static bool IsCorrectionDeclaration(string text) => ExperienceContentQuery(text) == text && Regex.IsMatch(text,
        @"^(?:[^，,。！？?：:；;]{0,24})?(?:更正|改正|纠正|修正|改口|澄清)[^，,。！？?：:；;]{0,24}[：:]",
        RegexOptions.CultureInvariant);
}
