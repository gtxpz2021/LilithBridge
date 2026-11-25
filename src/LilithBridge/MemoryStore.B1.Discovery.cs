using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace LilithBridge;

internal sealed partial class MemoryStore
{
    // Strip addressee self-name before CJK bigramming so calling "莉莉丝" never
    // creates boundary-crossing bigrams (e.g. "欢莉"/"丝有") or inflates B1 overlap.
    private static readonly Regex B1AddresseeMask = new(@"莉莉丝|lilith", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static bool B1IsParticleGlueToken(string t)
    {
        if (t.Length != 2) return false;
        char c0 = t[0], c1 = t[1];
        if (c0 < '\u3400' || c0 > '\u9fff' || c1 < '\u3400' || c1 > '\u9fff') return false;
        const string particles = "的了吗呢呀啊哦喔嘛呐诶欸哈嘿唔";
        return particles.IndexOf(c0) >= 0 || particles.IndexOf(c1) >= 0;
    }

    private static HashSet<string> B1SearchTokens(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new HashSet<string>(StringComparer.Ordinal);
        string masked = B1AddresseeMask.Replace(text, " ");
        var raw = MemorySemantics.MatchingTokens(masked);
        raw.RemoveWhere(t => B1WeakSearchTokens.Contains(t) || B1IsParticleGlueToken(t));
        return raw;
    }

    // These are discovery hints, never new object anchors or semantic premises.
    // A source must actually have been supplied to this generation, and share a
    // concrete token with its retained content. This avoids indexing an entire
    // conversation as if all of it described the same object.
    private static bool B1ConcreteDiscoveryToken(string t) => t.Length >= 2 && !B1WeakSearchTokens.Contains(t)
        && !B1IsParticleGlueToken(t)
        && t is not ("假设" or "玩家" or "现实" or "物品" or "经历" or "仅限" or "涉及" or "不涉"
            or "前面" or "之前" or "刚才" or "先前" or "原先" or "当时" or "一直" or "之后"
            or "一张" or "一个" or "一下" or "一些" or "一点" or "一种" or "一次" or "一件"
            or "这回" or "那回" or "这次" or "那次" or "这个" or "那个" or "这些" or "那些");

    private List<(B1Ref Ref, HashSet<string> Tokens)> B1DiscoverySources(B1Entry entry)
    {
        var body = B1SearchTokens(entry.Text + " " + entry.Scope);
        return entry.Packet.Footprint.Where(r => r.Kind == "utterance")
            .GroupBy(r => r.Key).Select(g => g.First()).TakeLast(96)
            .Select(r => (Ref:r, Turn:B1Turn(r.Kind, r.Version)))
            .Where(x => x.Turn is { Role: "user", Origin: "user", DeliveryState: "received", SchemaVersion: 1 }
                && (x.Ref.Sha256.Length == 0 || x.Ref.Sha256 == B1Hash(x.Turn.Text)))
            .Select(x => (x.Ref, Tokens:B1SearchTokens(x.Turn!.Text).Where(B1DiscoveryContent).ToHashSet(StringComparer.Ordinal)))
            .Select(x => (x.Ref, x.Tokens, Pivots:x.Tokens.Count(t => body.Contains(t) && B1ConcreteDiscoveryToken(t))))
            .Where(x => x.Pivots > 0).OrderByDescending(x => x.Pivots).Take(8)
            .Select(x => (x.Ref, x.Tokens)).ToList();
    }

    private bool SemanticB1QueryScope(string query,B1Entry entry, bool requireLiteralQuotes = true)
    {
        var ids=MemorySemantics.RecallIdentifiers(query);
        var quotes=UtteranceAttribution.LiteralQuotes(query);
        string available=B1DiscoveryText(entry)+" "+string.Join(" ",entry.ObjectAnchors.Concat(entry.Sources)
            .Where(r=>r.Kind!="b1").Select(r=>B1Turn(r.Kind,r.Version)?.Text??""));
        return ids.All(id=>MemorySemantics.ContainsRecallIdentifier(available,id))
            &&(!requireLiteralQuotes || quotes.All(q=>available.Contains(q,StringComparison.Ordinal)));
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

    private const int MaxB1SemanticGlobalRank = 24;
    private const double MinB1SemanticScore = 0.33;
    private const double MinB1StrongUtteranceScore = 0.45;

    private static bool B1IsExplicitLookup(string query)
        => MemorySemantics.RecallIdentifiers(query).Length > 0
            || UtteranceAttribution.LiteralQuotes(query).Length > 0
            || MemorySemantics.ParseExperienceRecallQuery(query) != null
            || MemorySemantics.RequestedHistorySpeaker(query).Length > 0;

    // Mirror SemanticRecall's filler & salience discipline so casual greetings or
    // low-rank background buzz in a full-corpus ranking never trigger speculative
    // B1 family + historical reply-group fan-out.
    private static bool B1SemanticDiscoveryPermitted(string query, SemanticRecallResult semantic)
    {
        if (semantic.RelevancePlan != null) return true;
        bool explicitLookup = B1IsExplicitLookup(query);
        if (!explicitLookup && !B1HasRecallIntent(query) && MemorySemantics.IsLowInfoGreetingOrFiller(query))
            return false;
        bool geometryActive = semantic.Geometry != null;
        bool geometryHighOrderEmpty = semantic.OrthogonalSparks.Count == 0 && semantic.WormholeSparks.Count == 0
            && semantic.BridgeSparks.Count == 0 && semantic.SceneSparks.Count == 0;
        if (geometryActive && !explicitLookup && !B1HasRecallIntent(query) && semantic.SalienceGated && geometryHighOrderEmpty)
            return false;
        return true;
    }

    private static bool B1SemanticCandidatePermitted(SemanticRecallCandidate candidate, SemanticRecallResult semantic,
        bool explicitLookup, bool requireStrongUnplanned = false)
    {
        if (semantic.RelevancePlan != null) return true;
        double minScore = requireStrongUnplanned ? MinB1StrongUtteranceScore : MinB1SemanticScore;
        if (candidate.Score < minScore) return false;
        // Full-corpus queries assign Rank across all domains (Events + B1 + Thoughts).
        // A B1 entry sitting at global rank #231 or #456 is anisotropic background buzz,
        // not a genuine hit—even if its raw cosine clears 0.45–0.60. Legacy/mock callers
        // that omit Rank (<= 0) keep passing when their Score clears the floor.
        int effectiveRank = candidate.DomainRank > 0 ? candidate.DomainRank : candidate.Rank;
        if (!explicitLookup && effectiveRank > MaxB1SemanticGlobalRank) return false;
        return true;
    }

    private static bool B1UtteranceMatchesQuery(string query, string utteranceText, bool explicitLookup)
    {
        if (explicitLookup) return true;
        var qTokens = B1SearchTokens(query).Where(B1DiscoveryContent).ToHashSet(StringComparer.Ordinal);
        if (qTokens.Count == 0) return false;
        var uTokens = B1SearchTokens(utteranceText);
        if (uTokens.Count == 0) return false;
        int overlap = qTokens.Count(uTokens.Contains);
        if (overlap <= 0) return false;
        double score = overlap / (double)Math.Max(1, Math.Min(qTokens.Count, uTokens.Count));
        return score >= MemorySemantics.MinRecallSimilarity
            && (overlap >= MemorySemantics.MinRecallOverlap || score >= 0.5);
    }

    private static bool B1SemanticUtteranceMatchesQuery(string query, string utteranceText,
        SemanticRecallCandidate candidate, bool explicitLookup)
    {
        if (explicitLookup) return true;
        // Synthetic/unit-test candidates omit global corpus Rank (<= 0).
        if (candidate.Rank <= 0) return candidate.Score >= MinB1StrongUtteranceScore;
        // High-confidence top-5 cross-word semantic hit is permitted even without
        // lexical overlap; lower-rank semantic candidates must meet the same
        // substantive token similarity/overlap floor on the user utterance itself
        // (preventing assistant-hit spillover or a single incidental 2-char token
        // in a long old paste from pulling unrelated sessions into B1 fan-out).
        if (candidate.Score >= 0.68 && candidate.Rank <= 5) return true;
        return B1UtteranceMatchesQuery(query, utteranceText, explicitLookup);
    }

    private IReadOnlyList<MemoryTurn> GetB1ExperienceDiscovery(string query, string currentInput, int max,SemanticRecallResult? semantic=null)
    {
        if (max <= 0 || MemorySemantics.DeclinesArchiveRecall(query)) return Array.Empty<MemoryTurn>();
        var excluded = new[] { currentInput };
        bool explicitQueryLookup = B1IsExplicitLookup(query);
        // Reuse the existing archive lookup and all of its role/date/identifier
        // boundaries. This path supplies original utterances even with zero B.
        // Require the user turn itself to match substantive query tokens on
        // non-explicit queries so an assistant-side match in a LogicalDialogueUnit
        // never drags its unrelated user prompt into B1 root fan-out.
        var normal = GetExperienceCandidates(query, max * 3, excluded).Where(t => t.Role == "user"
            && t.Origin == "user" && t.Text != query
            && B1UtteranceMatchesQuery(query, t.Text, explicitQueryLookup)).Take(max).ToList();
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
        if(SemanticResultMatches(query,semantic,SemanticDomain.Events) && B1SemanticDiscoveryPermitted(query, semantic!))
        {
            var scope=MemorySemantics.ParseExperienceRecallQuery(query);
            var ids=MemorySemantics.RecallIdentifiers(query);
            var quotes=UtteranceAttribution.LiteralQuotes(query);
            string requested=MemorySemantics.RequestedHistorySpeaker(query);
            bool explicitLookup = ids.Length > 0 || quotes.Length > 0 || scope != null || requested.Length > 0;
            var permitted=ReadExperiences().Where(t=>t.EventId!=currentInput&&t.Role=="user"&&t.Origin=="user"
                &&t.DeliveryState=="received"&&t.Text!=query&&IsMentionAllowed(t)&&IsNativeSupplyAllowed(t)
                &&requested!="assistant"&&(scope==null||scope.AllowsSourceScope(t))
                &&ids.All(id=>MemorySemantics.ContainsRecallIdentifier(t.Text,id))
                &&quotes.All(q=>t.Text.Contains(q,StringComparison.Ordinal))
                &&(explicitLookup || !MemorySemantics.IsLowInfoGreetingOrFiller(t.Text))).ToList();
            vector=semantic!.Candidates.Where(c=>c.Kind=="utterance" && B1SemanticCandidatePermitted(c, semantic, explicitLookup, requireStrongUnplanned: !explicitLookup))
                .OrderByDescending(c=>c.Score)
                .Select(c=>(Candidate:c, Turn:permitted.FirstOrDefault(t=>SemanticCandidateMatches(c,t))))
                .Where(x=>x.Turn!=null && B1SemanticUtteranceMatchesQuery(query, x.Turn.Text, x.Candidate, explicitLookup))
                .Select(x=>x.Turn!).ToList();
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
