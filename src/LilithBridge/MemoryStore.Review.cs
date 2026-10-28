using System;
using System.Collections.Generic;
using System.Linq;

namespace LilithBridge;

internal enum SourceContinuity { Same, Unknown, KnownInvalid }

/// <summary>
/// Read-only adapter for the optional review tool. Visibility, withdrawal and
/// supply rules stay owned by MemoryStore; the first version is deliberately
/// limited to records written by the current MemoryStore instance session.
/// </summary>
internal sealed partial class MemoryStore
{
    private static SourceContinuity MergeContinuity(SourceContinuity a,SourceContinuity b)
        => (SourceContinuity)Math.Max((int)a,(int)b);
    private bool HasReviewRetractionFact(MemoryTurn turn)
    {
        var suppressed=SuppressedExperienceIds();
        return turn.LifecycleState is "forgotten" or "deleting" or "deleted"
            ||suppressed.Contains(turn.EventId)||suppressed.Contains(turn.SourceEventId)
            ||turn.SourceEventIds.Any(suppressed.Contains)
            ||turn.SourceDependencies.Concat(turn.ContextDependencies).Any(d=>suppressed.Contains(d.EventId))
            ||SuppressedTextMatches(turn.Text)||SuppressedTextMatches(turn.Evidence);
    }
    // Neutral facts only. Missing/unreadable archives never imply deletion.
    internal SourceContinuity CheckReviewOutcomeContinuity(ReviewToolOutcome outcome)
    {
        lock(_experienceLock)
        {
            var result=CheckMethodSourceContinuity(outcome.DerivedMethodSources??Array.Empty<MethodSource>());
            if(SuppressedTextMatches(outcome.Json))result=SourceContinuity.KnownInvalid;
            var records=outcome.SourceRecords??Array.Empty<MemoryTurn>();
            MemoryTurn[]? raw=null;
            try { if(records.Count>0)raw=ReadExperiences(true,false).ToArray(); }
            catch { if(records.Count>0)result=MergeContinuity(result,SourceContinuity.Unknown); }
            foreach(var old in records)
            {
                try
                {
                if(HasReviewRetractionFact(old)){result=SourceContinuity.KnownInvalid;continue;}
                var captured=SourceVersion("utterance",old);
                var versions=(outcome.SourceVersions??Array.Empty<DaySourceDependency>())
                    .Where(v=>v.Kind=="utterance"&&v.EventId==old.EventId).Distinct().ToArray();
                var matches=raw?.Where(t=>t.EventId==old.EventId).ToArray();
                if(old.EventId.Length==0||old.SessionId!=_sessionId||old.SchemaVersion!=1
                    ||versions.Length>1||versions.Length==1&&versions[0]!=captured
                    ||records.Count(t=>t.EventId==old.EventId)!=1||matches?.Length!=1)
                {result=MergeContinuity(result,SourceContinuity.Unknown);continue;}
                var current=matches[0];
                if(HasReviewRetractionFact(current)){result=SourceContinuity.KnownInvalid;continue;}
                if(current.SchemaVersion!=1||current.SessionId!=old.SessionId||current.Kind!=old.Kind
                    ||current.Role!=old.Role||current.Origin!=old.Origin||current.Sequence!=old.Sequence
                    ||current.Timestamp!=old.Timestamp)
                {result=MergeContinuity(result,SourceContinuity.Unknown);continue;}
                if(SourceVersion("utterance",current)!=captured||B1Hash(current.Text)!=B1Hash(old.Text))
                    result=SourceContinuity.KnownInvalid;
                }
                // A malformed dependency cannot erase positive invalidation
                // evidence already found in another dependency.
                catch { result=MergeContinuity(result,SourceContinuity.Unknown); }
            }
            return result;
        }
    }
    internal IReadOnlyList<DaySourceDependency> CaptureReviewSourceVersions(IReadOnlyList<MemoryTurn> records)
    {
        lock (_experienceLock) return records.Select(t => SourceVersion("utterance", t)).ToArray();
    }
    internal bool ReviewRecordsStillPermitted(IReadOnlyList<MemoryTurn> records)
    {
        var current = GetReviewRecordsByIds(records.Select(t => t.EventId).ToArray())
            .ToDictionary(t => t.EventId, StringComparer.Ordinal);
        return records.All(old => current.TryGetValue(old.EventId, out var now)
            && SourceVersion("utterance", now) == SourceVersion("utterance", old));
    }
    internal IReadOnlyList<MemoryTurn> GetReviewCandidates(string query, int max,
        IReadOnlyCollection<string>? excludedIds = null,SemanticRecallResult? semantic=null)
    {
        int limit = Math.Clamp(max, 1, ReviewContext.MaxRecordsLimit + 1);
        var excluded = new HashSet<string>(excludedIds ?? Array.Empty<string>(), StringComparer.Ordinal);
        var identifiers=MemorySemantics.RecallIdentifiers(query);
        var quotes=UtteranceAttribution.LiteralQuotes(query);
        var scope=MemorySemantics.ParseExperienceRecallQuery(query);
        string speaker=MemorySemantics.RequestedHistorySpeaker(query);
        List<MemoryTurn> current = ReadExperiences()
            .Where(t => t.SessionId == _sessionId
                && (t.Role == "user" || t.Role == "assistant")
                && t.EventId.Length > 0
                && !excluded.Contains(t.EventId)
                && IsMentionAllowed(t)
                && IsNativeSupplyAllowed(t)
                && (speaker.Length==0||t.Role==speaker) && (scope==null||scope.AllowsSourceScope(t))
                && identifiers.All(id=>MemorySemantics.ContainsRecallIdentifier(t.Text,id))
                && quotes.All(q=>t.Text.Contains(q,StringComparison.Ordinal)))
            .OrderBy(t => t.Sequence)
            .ToList();
        if (current.Count == 0)
            return current;
        string clean = ReviewContext.Clean(query);
        if (clean.Length == 0)
            return current.TakeLast(Math.Min(limit, current.Count)).ToList();

        var ranked = current
            .Select(t => new { Turn = t, Score = MemorySemantics.Similarity(t.Text, clean) })
            .Where(x => x.Score > 0 || MemorySemantics.IsRelevantRecall(clean, x.Turn.Text))
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Turn.Sequence)
            .Take(Math.Max(1, limit / 2))
            .Select(x => x.Turn)
            .ToList();
        // Exact IDs/versions/permissions precede the bound. Vector candidates
        // need no second lexical match and cannot enter another session.
        var vector=SemanticResultMatches(query,semantic,SemanticDomain.Events)
            ? semantic!.Candidates.Where(c=>c.Kind=="utterance").OrderByDescending(c=>c.Score)
                .Select(c=>current.FirstOrDefault(t=>SemanticCandidateMatches(c,t))).Where(t=>t!=null).Cast<MemoryTurn>().ToList()
            : new List<MemoryTurn>();
        PreparationTiming.Current?.Value("semantic_review_candidates",vector.Count);
        PreparationTiming.Current?.Value("semantic_review_rejected",Math.Max(0,(semantic?.Candidates.Count(c=>c.Kind=="utterance")??0)-vector.Count));
        ranked=ranked.Take(1).Concat(vector.Take(Math.Max(1,limit/2))).Concat(ranked.Skip(1))
            .DistinctBy(t=>t.EventId).Take(limit).ToList();
        if (ranked.Count == 0)
            return current.TakeLast(Math.Min(limit, current.Count)).ToList();

        var positions = current.Select((turn, index) => new { turn.EventId, index })
            .ToDictionary(x => x.EventId, x => x.index, StringComparer.Ordinal);
        var selected = ranked.Take(limit).ToList();
        var seen = selected.Select(t=>t.EventId).ToHashSet(StringComparer.Ordinal);
        foreach (MemoryTurn anchor in ranked)
        {
            if (!positions.TryGetValue(anchor.EventId, out int at)) continue;
            // Keep one adjacent record as context, never a wider slice.
            for (int delta = -1; delta <= 1 && selected.Count < limit; delta++)
            {
                int index = at + delta;
                if (index < 0 || index >= current.Count) continue;
                if (seen.Add(current[index].EventId)) selected.Add(current[index]);
            }
        }
        // The caller applies its final display bound before sorting for reading.
        // Keep candidate anchors ahead of adjacency through that final cut.
        return semantic!=null?selected.Take(limit).ToList():selected.OrderBy(t => t.Sequence).Take(limit).ToList();
    }

    /// <summary>
    /// Past provider reasoning may only be supplied when its source is still
    /// readable under the same visibility, withdrawal and supply rules as the
    /// original records: the user turn must still exist in this session and at
    /// least one delivered assistant record tied to it must be readable. When
    /// source_refs fix a record set, the past thought must belong to that set.
    /// </summary>
    internal IReadOnlyList<SessionThoughtLedger.Entry> SelectReviewThoughts(string query,
        IReadOnlyCollection<string>? allowedEventIds, int max)
    {
        string clean = ReviewContext.Clean(query);
        if (clean.Length == 0) return Array.Empty<SessionThoughtLedger.Entry>();
        List<MemoryTurn> experiences = ReadExperiences();
        HashSet<string> suppressed = SuppressedExperienceIds();
        var allowed = allowedEventIds == null || allowedEventIds.Count == 0
            ? null
            : new HashSet<string>(allowedEventIds, StringComparer.Ordinal);

        // Eligibility first, then relevance ordering and the limit. Ranking before
        // these checks would hide an eligible older entry behind newer but
        // out-of-scope ones.
        var eligible = new List<SessionThoughtLedger.Entry>();
        foreach (SessionThoughtLedger.Entry entry in ThoughtLedger.CompletedEntries())
        {
            // The supplied text is the reasoning itself, so the current mention
            // rules must apply to it, not only to the source user/assistant speech.
            if (!IsMentionTextAllowed(entry.Reasoning)) continue;
            MemoryTurn? userTurn = experiences.FirstOrDefault(t => t.EventId == entry.UserEventId
                && t.SessionId == _sessionId && t.Role == "user"
                && IsMentionAllowed(t) && IsNativeSupplyAllowed(t));
            if (userTurn == null) continue;
            bool deliveredAssistant = experiences.Any(t => t.SessionId == _sessionId
                && t.Role == "assistant" && t.SourceEventId == entry.UserEventId
                && t.DeliveryState == "delivered" && !suppressed.Contains(t.EventId)
                && IsMentionAllowed(t) && IsNativeSupplyAllowed(t));
            if (!deliveredAssistant) continue;
            if (allowed != null)
            {
                bool inScope = allowed.Contains(entry.UserEventId)
                    || experiences.Any(t => t.SourceEventId == entry.UserEventId
                        && t.SessionId == _sessionId && allowed.Contains(t.EventId));
                if (!inScope) continue;
            }
            eligible.Add(entry);
        }
        return eligible
            .Select(entry => new { Entry = entry, Score = MemorySemantics.Similarity(entry.Reasoning, clean) })
            .Where(x => x.Score > 0 || MemorySemantics.IsRelevantRecall(clean, x.Entry.Reasoning))
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Entry.RecordedAt)
            .Take(Math.Clamp(max, 1, 4))
            .Select(x => x.Entry)
            .ToList();
    }

    internal IReadOnlyList<MemoryTurn> GetReviewRecordsByIds(IReadOnlyCollection<string> ids)
    {
        if (ids.Count == 0) return Array.Empty<MemoryTurn>();
        var wanted = new HashSet<string>(ids, StringComparer.Ordinal);
        return ReadExperiences()
            .Where(t => t.SessionId == _sessionId
                && wanted.Contains(t.EventId)
                && (t.Role == "user" || t.Role == "assistant")
                && IsMentionAllowed(t)
                && IsNativeSupplyAllowed(t))
            .OrderBy(t => t.Sequence)
            .ToList();
    }
}
