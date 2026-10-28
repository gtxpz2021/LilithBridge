using System;
using System.Collections.Generic;
using System.Text;

namespace LilithBridge;

/// <summary>
/// Splits a reply into bubble-sized chunks.
///
/// Two rules matter here:
///  - the chunks must read like the original game's lines (short), because the
///    native bubble's own timing gives short lines proportionally more reading
///    time per character;
///  - nothing may be lost. The previous implementation used
///    <c>[^。！？!?；;…]+[。！？!?；;…]*</c>, which silently dropped leading
///    punctuation — a reply starting with "……" lost those characters.
/// </summary>
// Immutable coordinates for one planned or actually shown bubble. StartChar /
// GeneratedChars refer to the unmodified generation; DisplayStartChar / Chars
// refer to the exact rendered text. A trimmed gap may lie inside a raw span.
internal sealed class ReplyDeliveryChunk
{
    public int Index { get; init; }
    public int StartChar { get; init; }
    public int GeneratedChars { get; init; }
    public int DisplayStartChar { get; init; }
    public int Chars { get; init; }
    public string Text { get; init; } = "";
    public string PlanVersion { get; init; } = "";
}

internal sealed record BubbleWhitespaceOmission(int GeneratedStartChar, int GeneratedChars, int DisplayOffset);

// One ephemeral plan from the real renderer. UI and delivery share this object;
// it never rewrites the generated text or grants permission to unshown text.
internal sealed class BubbleDisplayPlan
{
    public string Version { get; }
    public string GeneratedText { get; }
    public string RenderedText { get; }
    public IReadOnlyList<ReplyDeliveryChunk> Chunks { get; }
    public IReadOnlyList<BubbleWhitespaceOmission> WhitespaceOmissions { get; }
    public bool IsValid { get; }
    internal BubbleDisplayPlan(string version, string generated, string rendered,
        List<ReplyDeliveryChunk> chunks, List<BubbleWhitespaceOmission> omissions, bool valid)
    {
        Version = version; GeneratedText = generated; RenderedText = rendered;
        Chunks = Array.AsReadOnly(chunks.ToArray());
        WhitespaceOmissions = Array.AsReadOnly(omissions.ToArray()); IsValid = valid;
    }
}

public static class BubbleChunks
{
    /// <summary>A reply at or below this length stays in one bubble.</summary>
    public const int SingleBubbleMaxChars = 28;

    /// <summary>Merge sentences until the buffer reaches roughly this length.</summary>
    public const int MergeTargetChars = 24;

    /// <summary>A trailing buffer shorter than this is folded into the previous bubble.</summary>
    public const int TailMergeMaxChars = 10;

    /// <summary>A single sentence longer than this is split further at commas.</summary>
    public const int CommaSplitThreshold = 60;

    /// <summary>After merging, a bubble longer than this is broken at soft punctuation.</summary>
    public const int MaxBubbleChars = 32;

    /// <summary>Upper bound for the gap after a mid-sentence break.</summary>
    public const int PauseAfterContinuationMs = 200;

    /// <summary>Upper bound for the gap after a sentence-ending bubble.</summary>
    public const int PauseAfterSentenceEndMs = 550;

    /// <summary>Floor for the gap after a mid-sentence break.</summary>
    public const int PauseAfterContinuationFloorMs = 60;

    /// <summary>Floor for the gap after a sentence end.</summary>
    public const int PauseAfterSentenceEndFloorMs = 140;

    private static readonly char[] BoundaryChars = { '。', '！', '？', '!', '?', '；', ';', '…' };
    private static readonly char[] SoftBreakChars = { '，', ',', '：', ':', '、' };
    private static readonly char[] SentenceEndChars = { '。', '！', '？', '!', '?', '…' };
    private static readonly char[] TrailingClosers = { '”', '』', '」', '）', ')', '》', '】', '’' };
    private static readonly char[] QuoteOpeners = { '“', '‘', '「', '『', '"' };
    private static readonly char[] QuoteClosers = { '”', '’', '」', '』', '"' };

    public static List<string> Split(string? text) => SplitCore(text);

    internal static BubbleDisplayPlan BuildPlan(string text)
        => MapPlan(text, SplitCore(text), true);

    // Compatibility for internal callers with another legal partition. It may
    // repartition the renderer's exact text, never invent a whitespace policy.
    internal static BubbleDisplayPlan BuildPlan(string text, IReadOnlyList<string> chunks)
        => MapPlan(text, chunks, string.Concat(chunks) == string.Concat(SplitCore(text)));

    private static BubbleDisplayPlan MapPlan(string generated, IReadOnlyList<string> chunks, bool valid)
    {
        string version = Guid.NewGuid().ToString("N");
        var mapped = new List<ReplyDeliveryChunk>();
        var omissions = new List<BubbleWhitespaceOmission>();
        int raw = 0, display = 0;
        for (int i = 0; i < chunks.Count; i++)
        {
            string text = chunks[i]; int start = -1;
            if (text.Length == 0) valid = false;
            for (int j = 0; j < text.Length; j++)
            {
                int omitted = raw;
                while (raw < generated.Length && generated[raw] != text[j] && char.IsWhiteSpace(generated[raw])) raw++;
                if (raw > omitted) omissions.Add(new(omitted, raw - omitted, display + j));
                if (raw >= generated.Length || generated[raw] != text[j]) { valid = false; break; }
                if (start < 0) start = raw;
                raw++;
            }
            mapped.Add(new ReplyDeliveryChunk { Index = i, StartChar = start,
                GeneratedChars = start < 0 ? 0 : raw - start, DisplayStartChar = display,
                Chars = text.Length, Text = text, PlanVersion = version });
            display += text.Length;
        }
        int tail = raw;
        while (raw < generated.Length && char.IsWhiteSpace(generated[raw])) raw++;
        if (raw > tail) omissions.Add(new(tail, raw - tail, display));
        valid &= chunks.Count > 0 && raw == generated.Length;
        return new(version, generated, string.Concat(chunks), mapped, omissions, valid);
    }

    private static List<string> SplitCore(string? text)
    {
        string trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0)
            return new List<string>();
        if (trimmed.Length <= SingleBubbleMaxChars)
            return new List<string> { trimmed };

        List<string> sentences = SplitSentences(trimmed);
        if (sentences.Count == 1 && sentences[0].Length > CommaSplitThreshold)
        {
            List<string> parts = SplitOnSoftBreaks(sentences[0]);
            if (parts.Count > 1)
                sentences = parts;
        }

        List<string> merged = MergeChunks(sentences);

        // The merge above can still leave a long bubble when one sentence is long.
        // Break those at soft punctuation, then merge the pieces again.
        var capped = new List<string>();
        foreach (string chunk in merged)
        {
            if (chunk.Length <= MaxBubbleChars)
            {
                capped.Add(chunk);
                continue;
            }
            List<string> pieces = SplitOnSoftBreaks(chunk);
            if (pieces.Count <= 1)
            {
                capped.Add(chunk);
                continue;
            }
            capped.AddRange(MergeChunks(pieces));
        }

        return capped.Count > 0 ? capped : new List<string> { trimmed };
    }

    private static List<string> MergeChunks(List<string> sentences)
    {
        var merged = new List<string>();
        var buffer = new StringBuilder();
        foreach (string sentence in sentences)
        {
            buffer.Append(sentence);
            if (buffer.Length >= MergeTargetChars)
            {
                merged.Add(buffer.ToString());
                buffer.Clear();
            }
        }
        if (buffer.Length > 0)
        {
                // Fold a short tail back, but never past the bubble cap — otherwise a
                // short final sentence can glue a whole reply into one long bubble.
                if (merged.Count > 0
                    && buffer.Length < TailMergeMaxChars
                    && merged[^1].Length + buffer.Length <= MaxBubbleChars)
                    merged[^1] += buffer.ToString();
            else
                merged.Add(buffer.ToString());
        }
        return merged;
    }

    /// <summary>
    /// Cuts after every sentence-ending mark, keeping every character —
    /// including punctuation that appears before the first word.
    /// </summary>
    private static List<string> SplitSentences(string text)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        bool inToken = false;
        char quoteCloser = '\0';
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            current.Append(c);
            // A marker the player asked to see must arrive in one piece: its own
            // colon used to read as a soft break, so "<action:" and "wave>。"
            // reached the player as two bubbles on 2026-09-25.
            if (c == '>' && inToken)
            {
                inToken = false;
                continue;
            }
            if (!inToken && StartsAngleToken(text, i))
                inToken = true;
            // A quotation must arrive whole as well: the live 6B-C run split
            // "你原话是“我小时候养过一只叫小满的猫。" from "”——后面那句…".
            quoteCloser = TrackQuote(c, quoteCloser);
            if (inToken || quoteCloser != '\0')
                continue;
            if (Array.IndexOf(BoundaryChars, c) < 0)
                continue;
            result.Add(current.ToString().Trim());
            current.Clear();
        }
        if (current.Length > 0)
            result.Add(current.ToString().Trim());
        result.RemoveAll(part => part.Length == 0);
        return result;
    }

    private static List<string> SplitOnSoftBreaks(string text)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        bool inToken = false;
        char quoteCloser = '\0';
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            current.Append(c);
            if (c == '>' && inToken)
            {
                inToken = false;
                continue;
            }
            if (!inToken && StartsAngleToken(text, i))
                inToken = true;
            quoteCloser = TrackQuote(c, quoteCloser);
            if (inToken || quoteCloser != '\0')
                continue;
            if (Array.IndexOf(SoftBreakChars, c) < 0)
                continue;
            result.Add(current.ToString().Trim());
            current.Clear();
        }
        if (current.Length > 0)
            result.Add(current.ToString().Trim());
        result.RemoveAll(part => part.Length == 0);
        return result;
    }

    /// <summary>
    /// True when a marker-like token starts here: "&lt;", a name, and a closing
    /// "&gt;" close behind. Plain text such as "&lt;3" must not switch the guard on
    /// for the rest of the reply.
    /// </summary>
    private static bool StartsAngleToken(string text, int index)
    {
        if (text[index] != '<' || index + 2 >= text.Length)
            return false;
        char next = text[index + 1];
        if (!char.IsLetter(next) && next != '_')
            return false;
        int close = text.IndexOf('>', index + 1);
        return close > index && close - index <= 40;
    }

    /// <summary>
    /// Tracks whether the cursor sits inside a quotation. Returns the closer it
    /// is waiting for, or '\0' when the text is back outside a quote.
    /// </summary>
    private static char TrackQuote(char c, char quoteCloser)
    {
        if (quoteCloser != '\0')
            return c == quoteCloser ? '\0' : quoteCloser;
        int index = Array.IndexOf(QuoteOpeners, c);
        return index < 0 ? '\0' : QuoteClosers[index];
    }

    /// <summary>
    /// True when the bubble ends a sentence rather than trailing off mid-sentence.
    /// Quotation marks and brackets after the punctuation are looked through, so
    /// "他说“好。”" still counts as a sentence end.
    /// </summary>
    public static bool EndsWithSentenceEnd(string? chunk)
    {
        string text = (chunk ?? "").TrimEnd();
        while (text.Length > 0 && Array.IndexOf(TrailingClosers, text[^1]) >= 0)
            text = text[..^1].TrimEnd();
        return text.Length > 0 && Array.IndexOf(SentenceEndChars, text[^1]) >= 0;
    }

    /// <summary>
    /// How long to wait before the next bubble, in milliseconds.
    ///
    /// The pause scales with how much was just read: a three-character "好吧。"
    /// does not need the same silence as a full thirty-character sentence. Both
    /// branches keep a floor so the bubbles never run together, and a ceiling so
    /// a long sentence still hands off promptly.
    /// </summary>
    public static int PauseAfter(string? chunk)
    {
        int length = (chunk ?? "").Trim().Length;
        return EndsWithSentenceEnd(chunk)
            ? Math.Clamp(120 + length * 7, PauseAfterSentenceEndFloorMs, PauseAfterSentenceEndMs)
            : Math.Clamp(50 + length * 3, PauseAfterContinuationFloorMs, PauseAfterContinuationMs);
    }
}
