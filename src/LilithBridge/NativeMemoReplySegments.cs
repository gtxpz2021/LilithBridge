using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace LilithBridge;

// A response-only segment for the existing native notebook writes. It grants no
// operation or authority; the writer outcome alone decides whether it is shown.
internal static class NativeMemoReplySegments
{
    internal static bool AwaitingSettlement(ApiResult? reply) => reply != null && !reply.MemoReplySettled
        && (reply.MemoEntries.Count > 0 || reply.ScratchEntries.Count > 0 || reply.MemoAnnotations.Count > 0
            || reply.MemoConfirmation.Length > 0 || reply.MemoConfirmationProvided && !reply.MemoReplySeparated);

    internal static string Extract(ApiResult reply, string text, string? playerText, bool literal)
    {
        bool Syntax(Match m) => !ReplySanitizer.IsInsideQuote(text, m.Index)
            && !(literal && playerText?.Contains(m.Value, StringComparison.Ordinal) == true);
        var markers = Regex.Matches(text, @"(?is)<\s*/?\s*memo_confirmation\b[^>]*>").Where(Syntax).ToArray();
        if (markers.Length == 0) return text;
        var segments = Regex.Matches(text, @"(?is)<memo_confirmation>(.*?)</memo_confirmation>").Where(Syntax).ToArray();
        bool valid = !reply.MemoConfirmationProvided && markers.Length == 2 && segments.Length == 1;
        reply.MemoConfirmationProvided = true;
        reply.MemoReplySeparated = valid;
        if (!valid)
        {
            // Malformed/duplicate confirmation boundaries cannot certify a
            // remaining span as ordinary dialogue. No language guessing.
            reply.MemoConfirmation = "";
            reply.Text = "";
            return "";
        }
        reply.MemoConfirmation = MemoryStore.Clean(segments[0].Groups[1].Value);
        reply.Text = text.Remove(segments[0].Index, segments[0].Length);
        return reply.Text;
    }
}
