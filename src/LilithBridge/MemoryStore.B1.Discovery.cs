using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace LilithBridge;

internal sealed partial class MemoryStore
{
    // These are discovery hints, never new object anchors or semantic premises.
    // A source must actually have been supplied to this generation, and share a
    // concrete token with its retained content. This avoids indexing an entire
    // conversation as if all of it described the same object.
    private static bool B1ConcreteDiscoveryToken(string t) => t.Length >= 2 && !B1WeakSearchTokens.Contains(t)
        && t is not ("假设" or "玩家" or "现实" or "物品" or "经历" or "仅限" or "涉及" or "不涉"
            or "前面" or "之前" or "刚才" or "先前" or "原先" or "当时" or "一直" or "之后");

    private List<(B1Ref Ref, HashSet<string> Tokens)> B1DiscoverySources(B1Entry entry)
    {
        var body = MemorySemantics.MatchingTokens(entry.Text + " " + entry.Scope);
        return entry.Packet.Footprint.Where(r => r.Kind == "utterance")
            .GroupBy(r => r.Key).Select(g => g.First()).TakeLast(96)
            .Select(r => (Ref:r, Turn:B1Turn(r.Kind, r.Version)))
            .Where(x => x.Turn is { Role: "user", Origin: "user", DeliveryState: "received", SchemaVersion: 1 }
                && (x.Ref.Sha256.Length == 0 || x.Ref.Sha256 == B1Hash(x.Turn.Text)))
            .Select(x => (x.Ref, Tokens:MemorySemantics.MatchingTokens(x.Turn!.Text)))
            .Select(x => (x.Ref, x.Tokens, Pivots:x.Tokens.Count(t => body.Contains(t) && B1ConcreteDiscoveryToken(t))))
            .Where(x => x.Pivots > 0).OrderByDescending(x => x.Pivots).Take(8)
            .Select(x => (x.Ref, x.Tokens)).ToList();
    }

    private bool SemanticB1QueryScope(string query,B1Entry entry)
    {
        var ids=MemorySemantics.RecallIdentifiers(query);
        var quotes=UtteranceAttribution.LiteralQuotes(query);
        string available=entry.Text+" "+entry.Scope+" "+string.Join(" ",entry.ObjectAnchors.Concat(entry.Sources)
            .Where(r=>r.Kind!="b1").Select(r=>B1Turn(r.Kind,r.Version)?.Text??""));
        return ids.All(id=>MemorySemantics.ContainsRecallIdentifier(available,id))
            &&quotes.All(q=>available.Contains(q,StringComparison.Ordinal));
    }

    private IEnumerable<string> B1CorrectingUtterances(string oldEvent) => _autonomous
        .Where(c => c.RequestKind == "correct" && IsActiveLifecycle(c) && c.SourceEventId != oldEvent
            && c.SourceEventIds.Contains(oldEvent)).Select(c => c.SourceEventId)
        .Concat(_readSourceCorrections.GetValueOrDefault(oldEvent) ?? new List<string>())
        .Concat(_readPossibleSourceCorrections.GetValueOrDefault(oldEvent) ?? new List<string>())
        .Where(id => id.Length > 0).Distinct()
        .OrderByDescending(id => B1Turn("utterance",id)?.Timestamp ?? DateTimeOffset.MinValue)
        .ThenBy(id => id,StringComparer.Ordinal);

    private static readonly HashSet<string> B1RecallFraming = new(StringComparer.Ordinal)
    { "还记", "记得", "记不", "不记", "不清", "清就", "就直", "直说", "聊过", "面聊", "过的", "我们", "们后",
      "后来", "改正", "更正", "纠正", "信息", "哪个", "个信", "息吗", "回来", "知道", "什么", "怎么", "继续",
      "之前", "前面", "的那", "的哪", "你还", "了吗", "过什", "了什", "记清", "前聊", "还记得" };
    private static bool B1DiscoveryContent(string token) => B1ConcreteDiscoveryToken(token) && !B1RecallFraming.Contains(token);
    private static bool B1RecallReferenceOnly(string text) => Regex.IsMatch(text,
        @"^(?:前面|之前|上次|以前|你还记得|还记得|记不记得).*(?:[？?]|记不清|告诉我)", RegexOptions.Singleline);

    private IReadOnlyList<MemoryTurn> GetB1ExperienceDiscovery(string query, string currentInput, int max,SemanticRecallResult? semantic=null)
    {
        if (max <= 0 || MemorySemantics.DeclinesArchiveRecall(query)) return Array.Empty<MemoryTurn>();
        var excluded = new[] { currentInput };
        // Reuse the existing archive lookup and all of its role/date/identifier
        // boundaries. This path supplies original utterances even with zero B.
        var normal = GetExperienceCandidates(query, max * 3, excluded).Where(t => t.Role == "user"
            && t.Origin == "user" && t.Text != query).Take(max).ToList();
        var vector=new List<MemoryTurn>();
        IReadOnlyList<MemoryTurn> Merge(IEnumerable<MemoryTurn> lexical)
        {
            // Finish lexical/correction discovery first, then reserve a lawful
            // semantic slot in the one final bounded union. No later lexical
            // insertion may evict that slot. Scores from the two ranks stay separate.
            var rankedLexical=lexical.DistinctBy(t=>t.EventId).ToList();
            return rankedLexical.Take(1).Concat(vector.Take(max)).Concat(rankedLexical.Skip(1))
                .DistinctBy(t=>t.EventId).Take(max).ToList();
        }
        if(SemanticResultMatches(query,semantic,SemanticDomain.Events))
        {
            var scope=MemorySemantics.ParseExperienceRecallQuery(query);
            var ids=MemorySemantics.RecallIdentifiers(query);
            var quotes=UtteranceAttribution.LiteralQuotes(query);
            string requested=MemorySemantics.RequestedHistorySpeaker(query);
            var permitted=ReadExperiences().Where(t=>t.EventId!=currentInput&&t.Role=="user"&&t.Origin=="user"
                &&t.DeliveryState=="received"&&t.Text!=query&&IsMentionAllowed(t)&&IsNativeSupplyAllowed(t)
                &&requested!="assistant"&&(scope==null||scope.AllowsSourceScope(t))
                &&ids.All(id=>MemorySemantics.ContainsRecallIdentifier(t.Text,id))
                &&quotes.All(q=>t.Text.Contains(q,StringComparison.Ordinal))).ToList();
            vector=semantic!.Candidates.Where(c=>c.Kind=="utterance").OrderByDescending(c=>c.Score)
                .Select(c=>permitted.FirstOrDefault(t=>SemanticCandidateMatches(c,t))).Where(t=>t!=null).Cast<MemoryTurn>().ToList();
        }
        if (!Regex.IsMatch(query, @"记得|记不记得|聊过|更正|改正|纠正|之前|前面|上次")) return Merge(normal);
        var policy = MemorySemantics.ParseExperienceRecallQuery(query);
        string speaker = MemorySemantics.RequestedHistorySpeaker(query);
        if (speaker == "assistant") return Merge(normal);
        var identifiers = MemorySemantics.RecallIdentifiers(query);
        var terms = MemorySemantics.MatchingTokens(query).Where(B1DiscoveryContent).ToHashSet(StringComparer.Ordinal);
        var available = ReadExperiences().Where(t => t.EventId != currentInput && t.Role == "user" && t.Origin == "user"
                && t.DeliveryState == "received" && t.Text != query && IsMentionAllowed(t) && IsNativeSupplyAllowed(t)
                && (policy == null || policy.Allows(t))
                && identifiers.All(id => MemorySemantics.ContainsRecallIdentifier(t.Text, id))).ToList();
        // A rare exact content fragment opens a candidate view, not an asserted
        // object identity. Keep two alternatives; no synonym or object-name list.
        var ranked = available.Select(t => (Turn:t, Tokens:MemorySemantics.MatchingTokens(t.Text)))
            .Select(x => (x.Turn, Hits:terms.Count(x.Tokens.Contains)))
            .Where(x => x.Hits > 0).OrderBy(x => B1RecallReferenceOnly(x.Turn.Text))
            .ThenByDescending(x => x.Hits).ThenBy(x => x.Turn.Timestamp)
            .Take(max).Select(x => x.Turn).ToList();
        if (ranked.Count > 0) return Merge(normal.Take(1).Concat(ranked).Concat(normal.Skip(1)));
        // An explicitly broad correction question can inspect a bounded set of
        // actual correcting utterances. It is not an exhaustive history claim.
        if (Regex.IsMatch(query, @"(?:更正|改正|纠正).{0,6}(?:什么|哪些|哪条|哪个)"))
            return Merge(available.Where(t => _readSourceCorrections.ContainsKey(t.EventId))
                .OrderByDescending(t => t.Timestamp).Take(max));
        return Merge(normal);
    }
}
