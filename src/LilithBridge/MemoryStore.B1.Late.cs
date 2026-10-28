using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace LilithBridge;

internal sealed partial class MemoryStore
{
    private sealed record B1LateWord(MemoryTurn Turn, B1Ref Root, double Score);
    // Read-side language framing only, never a list of objects or update verbs
    // that would declare a correction. Masking also removes crossing bigrams.
    private static readonly Lazy<Regex> B1LateFrames = new(() => new Regex(string.Join("|",
        B1WeakSearchTokens!.Concat(B1RecallFraming!).Concat(new[] {
            "今天", "明天", "昨天", "早上", "晚上", "中午", "这次", "这段", "一只", "一件", "一点", "一下",
            "我们", "你们", "觉得", "看法", "意见", "原话", "顺便", "事情", "东西", "真的", "非常",
            "today", "tomorrow", "yesterday", "please", "maybe", "before", "after", "again", "this", "that" })
        .Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(t => t.Length).Select(Regex.Escape)),
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase));
    private static (string Text, HashSet<string> Tokens) B1LateContent(string text)
    {
        string masked = B1LateFrames.Value.Replace(text.ToLowerInvariant(), m => new string(' ', m.Length));
        var tokens = MemorySemantics.MatchingTokens(masked).Where(B1DiscoveryContent)
            .Where(t => Regex.IsMatch(t, @"^[\u3400-\u9fff]{2}$")
                || t.Length >= 3 && Regex.IsMatch(t, @"^[a-z0-9]+$")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return (masked, tokens);
    }
    private static bool B1QueryAllowsWord(MemoryTurn t, string query)
    {
        string speaker = MemorySemantics.RequestedHistorySpeaker(query);
        var policy = MemorySemantics.ParseExperienceRecallQuery(query);
        return (speaker.Length == 0 || t.Role == speaker) && (policy == null || policy.Allows(t))
            && MemorySemantics.RecallIdentifiers(query).All(id => MemorySemantics.ContainsRecallIdentifier(t.Text, id));
    }
    // Once per snapshot: no recursive roots, no global relevance-floor change,
    // no permission-footprint-to-object conversion and no persistent index.
    private IReadOnlyList<B1LateWord> B1LateRelatedWords(IReadOnlyList<B1Ref> roots,
        string query, string input, B1Document doc)
    {
        if (roots.Count == 0 || MemorySemantics.DeclinesArchiveRecall(query)
            || MemorySemantics.RequestedHistorySpeaker(query) == "assistant") return Array.Empty<B1LateWord>();
        var actualRoots = roots.Where(r => r.Kind == "utterance")
            .Select(r => (Ref:r, Turn:B1Turn(r.Kind, r.Version), Resolved:ResolveB1(r, doc, new())))
            .Where(x => x.Turn is { Role:"user", Origin:"user", DeliveryState:"received" }
                && x.Turn.SessionId.Length > 0 && x.Resolved.Found && B1QueryAllowsWord(x.Turn, query))
            .GroupBy(x => x.Ref.Key).Select(g => g.First()).Take(4).ToList();
        var rootIds = actualRoots.Select(x => x.Ref.Version).ToHashSet();
        var sessionIds = actualRoots.Select(x => x.Turn!.SessionId).ToHashSet();
        var sessions = _b1ReadTurns.Values.Where(t => t.SchemaVersion == 1 && t.Role == "user" && t.Origin == "user"
                && t.DeliveryState == "received" && t.EventId != input && sessionIds.Contains(t.SessionId))
            .GroupBy(t => t.SessionId).ToDictionary(g => g.Key,
                g => g.OrderBy(t => t.Timestamp).ThenBy(t => t.Sequence).ToList());
        var content = sessions.Values.SelectMany(x => x).ToDictionary(t => t.EventId, t => B1LateContent(t.Text));
        var frequencies = sessions.ToDictionary(s => s.Key, s => s.Value
            .SelectMany(t => content[t.EventId].Tokens).GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase));
        var candidates = new List<B1LateWord>();
        foreach (var rootView in actualRoots)
        {
            var root = rootView.Turn!;
            if (!sessions.TryGetValue(root.SessionId, out var users)) continue;
            int start = users.FindIndex(t => t.EventId == root.EventId);
            if (start < 0) continue;
            // Use the exact resolved source view, not an unchecked raw-turn lookup.
            var basis = B1LateContent(rootView.Resolved.Material!.Text).Tokens;
            foreach (var later in users.Skip(start + 1))
            {
                // A later ordinary turn cannot jump over an existing refusal.
                if (MemorySemantics.DeclinesArchiveRecall(later.Text)) break;
                if (rootIds.Contains(later.EventId) || !B1QueryAllowsWord(later, query)) continue;
                var value = content[later.EventId];
                var spans = new List<(int Start, int End, double Weight)>();
                foreach (string term in basis.Where(value.Tokens.Contains))
                {
                    double weight = 1.0 / (1.0 + Math.Log(1 + frequencies[root.SessionId][term]));
                    for (int at = value.Text.IndexOf(term, StringComparison.Ordinal); at >= 0;
                        at = value.Text.IndexOf(term, at + term.Length, StringComparison.Ordinal))
                        spans.Add((at, at + term.Length, weight));
                }
                // Contiguous overlapping bigrams form ONE shared fragment.
                // Repeating the same fragment does not supply independent evidence.
                var merged = new List<(int Start, int End, double Weight)>();
                foreach (var span in spans.OrderBy(s => s.Start).ThenByDescending(s => s.End))
                {
                    if (merged.Count > 0 && span.Start < merged[^1].End)
                    {
                        var last = merged[^1]; merged[^1] = (last.Start, Math.Max(last.End, span.End), Math.Max(last.Weight, span.Weight));
                    }
                    else merged.Add(span);
                }
                var pieces = merged.Select(s => (Text: value.Text.Substring(s.Start, s.End - s.Start), s.Weight))
                    .GroupBy(s => s.Text).Select(g => g.First()).OrderByDescending(s => s.Text.Length).ToList();
                pieces = pieces.Where(s => !pieces.Any(other => other.Text != s.Text && other.Text.Contains(s.Text, StringComparison.Ordinal))).ToList();
                bool distinctiveLong = pieces.Any(s => s.Text.Length >= 6 || s.Text.Length >= 4 && s.Weight >= 1.0 / (1.0 + Math.Log(4)));
                if (pieces.Count < 2 && !distinctiveLong) continue;
                var reference = new B1Ref("utterance", later.EventId, B1Hash(later.Text), "history");
                if (!ResolveB1(reference, doc, new()).Found) continue;
                candidates.Add(new(later, rootView.Ref with { Purpose = "history" },
                    pieces.Sum(s => s.Weight + Math.Min(8, s.Text.Length) * 0.02)));
            }
        }
        // Content has already admitted every candidate. Reserve each root's
        // newest related wording before early high-overlap repetitions compete.
        var newest = candidates.GroupBy(c => c.Root.Key).Select(g => g.OrderByDescending(c => c.Turn.Timestamp)
                .ThenByDescending(c => c.Turn.Sequence).First())
            .OrderByDescending(c => c.Turn.Timestamp).ThenByDescending(c => c.Score);
        return newest.Concat(candidates.OrderByDescending(c => c.Score).ThenByDescending(c => c.Turn.Timestamp))
            .GroupBy(c => c.Turn.EventId).Select(g => g.First()).Take(4).ToList();
    }
}
