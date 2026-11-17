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

    public const string Contract = "说话人标注仅表示该条消息的提交者；玩家原话里的引文、转述或假设不自动成为本人经历。有转述前缀时按原文区分转交者与被引对象，不默认来源身份已核实。";

    public static string Describe(string role, string text) => Reported(role,text) is { } frame
        ? "[转述前缀：" + frame.source_label + "，来源身份未核实] " : "";

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
