using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace LilithBridge;

/// <summary>
/// One request-time decision about her own relationship interpretation.
/// Read-only: identical input returns the same decision for every consumer, and
/// a caller never has to re-run the heuristics for a second block.
/// </summary>
internal sealed record RelationshipMaterialSelection(
    MemoryTurn? Turn, string Reason, string Query, string Source)
{
    internal string EventId => Turn?.EventId ?? "";
}

internal sealed partial class MemoryStore
{
    private const string RelationshipReadingSource = "memory_store.relationship_readings";

    // Equivalent natural phrasings that ask about the relationship itself. This
    // is a bounded grammar, not a keyword OR list: each pattern needs a
    // relationship object (我们/咱俩/我对你/你对我/我在你心里) bound to a
    // question or predicate. Ordinary small talk ("今天天气不错/讲个笑话")
    // matches none of them.
    private static readonly Regex[] RelationshipQuestionPatterns =
    {
        // A relationship object AND a relationship noun AND a relationship
        // predicate, bound together by a bounded gap. The noun is required: the
        // earlier `{0,8}` repeated the noun group (and, at zero repetitions,
        // accepted "我们怎么样" without any relationship word), while the stated
        // intent was a small allowed gap between the noun and the predicate
        // ("我们的关系最近怎么样"). "我们晚饭吃面条怎么样" is an activity
        // proposal: it has "我们" and "怎么样" but no relationship noun, so it
        // still selects no reading. The noun alternatives are relationship
        // vocabulary, not a per-sentence exclusion list.
        new(@"(?:我们|咱俩)(?:之间|俩|的?(?:关系|相处|感情|连接|联系|信任|距离))"
            + @".{0,8}(?:怎么样|怎样|如何|好不好|还好吗|还行吗|是什么|算什么|算什么样)", RegexOptions.CultureInvariant),
        // The same binding with the predicate before the noun: "我们算什么关系".
        new(@"(?:我们|咱俩)(?:现在|到底|究竟)?(?:算|是)(?:什么|啥|哪种|怎样|什么样的)?(?:关系|相处|感情|连接|联系)", RegexOptions.CultureInvariant),
        new(@"(?:你|您)(?:会|是|有|到底|究竟)?(?:怎么|怎样|如何)(?:看|想|觉得|看待|形容|对待|评价|理解)(?:我|我们|咱俩)(?:[？?！!。，,\s]|$|这个人|之间|俩|的关系|这段关系)", RegexOptions.CultureInvariant),
        new(@"(?:你|您)(?:觉得|认为|以为)(?:我|我们|咱俩)(?:怎么样|如何|是什么|算什么|怎样)", RegexOptions.CultureInvariant),
        new(@"我对你.{0,12}(?:是什么|算什么|算是|意味着什么|意味着|怎么样|怎样|如何|什么)", RegexOptions.CultureInvariant),
        new(@"我对你(?:来说|而言)?.{0,6}(?:重要|特别|唯一|什么)", RegexOptions.CultureInvariant),
        new(@"我在你(?:心里|眼中|眼里|看来|那儿|这里).{0,15}", RegexOptions.CultureInvariant),
        new(@"关系.{0,8}(?:怎么看|怎么想|如何|怎么样|怎样|是什么)", RegexOptions.CultureInvariant),
        new(@"(?:怎么|怎样|如何)形容(?:我们|咱俩|这段关系|我们的关系)", RegexOptions.CultureInvariant),
        new(@"(?:你|您)对我(?:来说|而言)?.{0,8}(?:什么|怎么|怎样|如何|重要|特别)", RegexOptions.CultureInvariant),
        new(@"(?:你|您)(?:眼(?:中|里)|心里|心(?:中|里))的(?:我|我们)", RegexOptions.CultureInvariant)
    };

    /// <summary>True when this line asks about the relationship itself.</summary>
    internal bool LooksLikeRelationshipQuestion(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return false;
        foreach (Regex pattern in RelationshipQuestionPatterns)
        {
            if (pattern.IsMatch(query))
                return true;
        }
        return false;
    }

    /// <summary>
    /// The one request-time relationship decision. The selected reading, its
    /// reason and its source are serialized with the request dump, and both
    /// ContextBuilder and SelfPositionView consume this result instead of
    /// re-deriving it from the query.
    /// </summary>
    internal RelationshipMaterialSelection SelectRequestRelationshipMaterial(string? query)
    {
        string text = query ?? "";
        IReadOnlyList<MemoryTurn> candidates = FilterMentionSupply(GetRelationshipReadings(24));
        if (string.IsNullOrWhiteSpace(text))
            return new RelationshipMaterialSelection(null, "no_query", text, RelationshipReadingSource);
        // Source match means the reading rests on an experience this request
        // recalled as directly relevant to its own line. The expanded adjacency
        // window is not a relevance claim: a neighbour can be attached to the
        // anchor turn while saying nothing about the current topic, and that must
        // not pull an unrelated reading into the request.
        var sourceIds = new HashSet<string>(StringComparer.Ordinal);
        GetExperienceCandidates(text, 6, onAnchorSelection: anchors =>
        {
            foreach (MemoryTurn anchor in anchors)
                sourceIds.Add(anchor.EventId);
        });
        bool explicitRelationship = LooksLikeRelationshipQuestion(text);
        MemoryTurn? selected = candidates
            .Where(t => explicitRelationship || t.SourceEventIds.Any(sourceIds.Contains)
                || MemorySemantics.IsRelevantRecall(text, t.Text))
            .OrderByDescending(t => MemorySemantics.Similarity(text, t.Text))
            .ThenByDescending(t => t.Timestamp)
            .FirstOrDefault();
        string reason = selected == null
            ? "no_current_relevant_material"
            : explicitRelationship ? "relationship_question" : "topic_or_source_match";
        NativeEventLog.Trace("material.selection", "mod", "system", "relationship material selection",
            "candidate_count=" + candidates.Count + ";selected=" + (selected?.EventId ?? "none")
            + ";reason=" + reason + ";explicit_relationship_question=" + explicitRelationship);
        return new RelationshipMaterialSelection(selected, reason, text, RelationshipReadingSource);
    }

    /// <summary>
    /// Compatibility entry point for callers that only need the reading. It is a
    /// delegate of the unified selection, never a second heuristic.
    /// </summary>
    internal MemoryTurn? SelectRelationshipReading(string? query) =>
        SelectRequestRelationshipMaterial(query).Turn;
}