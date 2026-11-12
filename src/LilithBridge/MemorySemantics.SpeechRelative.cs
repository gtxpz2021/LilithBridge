using System.Text.RegularExpressions;

namespace LilithBridge;

// A source-location query and a direction, not a certified episode/cause.
internal sealed record SpeechRelativeRecall(string AnchorText,string Speaker,string Scope);
internal static partial class MemorySemantics
{
    internal static SpeechRelativeRecall? ParseSpeechRelativeRecall(string? query)
    {
        string original=query??"";
        if(original.Length is <8 or >360 || DeclinesArchiveRecall(original))return null;
        // Speech-reference plus an explicit request for the surrounding past.
        // Ordinary present plans, bare "before", and unbound pronouns do not
        // grant a timeline expansion. Preserve the exact reference span.
        const string speech=@"(?:(?:上次|那次|当时|先前|之前|刚才|刚刚)\s*)?(?<who>我|你)(?:曾经|当时|上次|那次|先前|之前|刚才|刚刚)?(?:说过|说了|说|提到过|提到|提过|讲过|讲了|讲)";
        const string relative=@"(?<direction>之前|以前|之后|以后|前|后)";
        string structural=MaskRecallQuotes(original);
        var m=Regex.Match(structural,@"^(?:在|还记得|你还记得|你记得|记得)?\s*"+speech+@"(?<anchor>.{2,160}?)(?:[，,]\s*(?:那|这)(?:个时候)?|(?:那句话|这句话|的时候|这件事|这件事情)?(?:之)?|[，,]\s*)"+relative+@"[，,\s]*(?<ask>.{2,100})$");
        if(!m.Success || !Regex.IsMatch(m.Groups["ask"].Value,@"(?:我们|咱们|咱俩|你|我)?.{0,12}(?:说到哪|聊到哪|谈到哪|聊了什么|说了什么|谈了什么|谈到什么|说到什么|聊的(?:是)?什么|在说什么|在聊什么|在谈什么|什么话题|发生了什么|前因|后来怎样|怎么回事)"))return null;
        string anchor=original.Substring(m.Groups["anchor"].Index,m.Groups["anchor"].Length).Trim('，',',','。',' ','：',':','“','”','「','」','\"');
        if(anchor.Length<2 || Regex.IsMatch(anchor,@"^(?:那件事|这件事|那句话|这句话|那个|这个|什么)$"))return null;
        return new(anchor,m.Groups["who"].Value=="我"?"user":"assistant",
            m.Groups["direction"].Value is "之前" or "以前" or "前"?"before":"after");
    }
}
