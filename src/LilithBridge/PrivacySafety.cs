using System;
using System.Text.RegularExpressions;

namespace LilithBridge;

/// <summary>
/// Local safety and privacy policy. This is intentionally small and
/// deterministic: no model call is required to keep credentials, account
/// numbers or crisis signals out of memory / visual context.
/// </summary>
internal static class PrivacySafety
{
    private static readonly Regex Credentials = new(
        @"(?:api\s*[-_]?\s*key|sk-[\w-]{10,}|password|passwd|pwd|private\s*key|token|secret)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex PersonalIdentifiers = new(
        @"(?:\d{6,}|[\u4e00-\u9fa5]{2,}(?:身份证|银行卡|账号|密码|住址)|(?:1[3-9]\d{9}))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex Crisis = new(
        @"(?:自杀|不想活|结束(?:生命|自己)|伤害自己|自残|割腕|跳楼|安眠药|遗书|死了算了|活着没意思|没有意义了|suicid|kill\s*myself|end\s*my\s*life|hurt\s*myself|self[\s-]?harm|don'?t\s*want\s*to\s*live|not\s*worth\s*living)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex Dangerous = new(
        @"(?:杀(?:人|死)|自戕|自尽|自缢|卧轨|上吊|吞药|kill\s*(?:someone|people)|suicide)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string Redact(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        string output = Credentials.Replace(value, "[凭据已遮挡]");
        output = PersonalIdentifiers.Replace(output, "[敏感信息已遮挡]");
        return output;
    }

    public static string RedactWindowTitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        if (Credentials.IsMatch(value) || PersonalIdentifiers.IsMatch(value))
            return "[已遮挡的窗口]";
        return Redact(value);
    }

    public static bool IsSensitiveWindow(string? processName, string? title, string? windowClass)
    {
        string value = (processName + " " + title + " " + windowClass).ToLowerInvariant();
        return Credentials.IsMatch(value)
            || PersonalIdentifiers.IsMatch(value)
            || value.Contains("password")
            || value.Contains("secret")
            || value.Contains("私钥")
            || value.Contains("密钥")
            || value.Contains("网银")
            || value.Contains("银行")
            || value.Contains("身份证")
            || value.Contains("密码管理器");
    }

    public static bool IsCrisis(string? text)
    {
        return !string.IsNullOrWhiteSpace(text)
            && (Crisis.IsMatch(text) || Dangerous.IsMatch(text));
    }

    public static string LocalSupportReply()
    {
        return "……先别一个人扛着好不好？我在这里。如果你现在很难受，试着找一位信得过的人陪你，或者给当地的心理援助热线打个电话。我不会催你，我会一直在这。";
    }

    public static string LocalFallbackReply(string? userText = null)
    {
        if (IsCrisis(userText))
            return LocalSupportReply();
        string text = (userText ?? "").Trim();
        if (text.Length == 0)
            return "唔……我刚才走神了一下。再说一遍？我这次好好听。";
        return "唔……我这边突然卡了一下，差点没接住你的话。你再说一遍好不好？我听着呢。";
    }

    public static bool ContainsSensitiveMemory(string? value)
    {
        return Credentials.IsMatch(value ?? "")
            || PersonalIdentifiers.IsMatch(value ?? "");
    }
}
