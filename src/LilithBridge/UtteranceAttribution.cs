using System;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LilithBridge;

// Derived request metadata only. The stored Role is the sender, not the author
// of every clause. Explicit source labels are reported claims, never identity
// verification. Ambiguous prose is left intact for the reader to interpret.
internal sealed record ReportedSpeechFrame(string source_label, string marker,
    int body_start, string basis = "explicit_source_prefix_in_original_text",
    string status = "reported_not_verified");

internal static class UtteranceAttribution
{
    private static readonly Regex Report = new(
        @"^(?:(?:好吧|好的|嗯|接下来)[，,：:\s]+)?(?<label>[\p{L}\p{N}_·.\-]{1,32}?)(?:回复(?:了)?|回应(?:了)?|说道|写道|说|的原话是)[：:]\s*",
        RegexOptions.CultureInvariant);
    private static readonly Regex SourceLabel = new(
        @"^(?<label>[A-Za-z][A-Za-z0-9_.\-]{0,31})\s*[：:]\s*",
        RegexOptions.CultureInvariant);

    public static ReportedSpeechFrame? Reported(string role, string? text)
    {
        if (role != "user" || string.IsNullOrWhiteSpace(text)) return null;
        string original = text!;
        int leading = original.Length - original.TrimStart().Length;
        string value = original[leading..];
        Match match = Report.Match(value);
        bool speechPrefix = match.Success;
        if (!match.Success) match = SourceLabel.Match(value);
        if (!match.Success || match.Length == value.Length) return null;
        string label = match.Groups["label"].Value;
        // First/second person and hypothetical subjects are not third parties.
        if (label is "我" or "你" or "我们" or "你们" or "自己"
            || Regex.IsMatch(label, @"^(?:如果|假如|假设|比如|例如|也许|可能|我想|我会|我准备|我刚|我又|我再|我曾|我自己|你想|你会)")) return null;
        return new(label, original[..(leading + match.Length)], leading + match.Length,
            speechPrefix ? "explicit_speech_prefix_in_original_text" : "leading_label_in_original_text");
    }

    public const string Contract = "speaker/role 表示该条消息的提交者；user 文本里的引用、转述、假设不因此成为玩家本人的经历或观点。reported_speech 只描述原文的发言标记或前缀标签，不验证第三方身份，也不保证标签是人名；按原文区分转交者、被引者和引文中的对象。";

    public static string Describe(string role, string text) => Reported(role,text) is { } frame
        ? "[提交者=user；原文发言标记或前缀标签=" + JsonSerializer.Serialize(frame.source_label, DialogueMaterialEnvelope.DataJson)
            + "；来源身份未验证] " : "";

    // Literal lookup is an additional ranking signal, not an attribution verdict.
    // Short ambiguous quotes do not get this priority; all existing permission,
    // speaker, date, deletion and identifier filters still run in MemoryStore.
    public static string[] LiteralQuotes(string? text) => Regex.Matches(text ?? "",
        "“([^“”\\r\\n]{8,240})”|「([^「」\\r\\n]{8,240})」|『([^『』\\r\\n]{8,240})』|\"([^\"\\r\\n]{8,240})\"")
        .Cast<Match>().Select(m => m.Groups.Cast<Group>().Skip(1).First(g=>g.Success).Value)
        .Where(q => q.Count(char.IsLetterOrDigit) >= 8).Distinct(StringComparer.Ordinal).ToArray();

    public static int QuoteMatch(string text, string[] quotes) => quotes
        .Where(q=>text.Contains(q,StringComparison.Ordinal)).Select(q=>q.Length).DefaultIfEmpty(0).Max();
}
