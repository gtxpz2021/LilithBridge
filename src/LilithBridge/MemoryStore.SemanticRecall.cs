using System.Security.Cryptography;
using System.Text;

namespace LilithBridge;

// Offline prototype exchange only. Scores discover candidates, never grant
// permission or establish truth/current status. No persistent memory writer.
internal sealed record SemanticRecallCandidate(string EventId, string TextHash, double Score,
    int ChunkStart, int ChunkEnd,string Kind="utterance",string Version="",int Rank=0);
internal sealed record SemanticRecallResult(string QueryId, string QueryHash, string ModelRevision,
    string IndexVersion, IReadOnlyList<SemanticRecallCandidate> Candidates, string Unavailable = "", bool IndexComplete = true,
    string DataRoot="",string SessionId="",SemanticDomain Domains=SemanticDomain.All,long TargetWatermark=0,long CompletedWatermark=0,long Generation=0);
internal sealed class SemanticRecallTrace
{
    internal void BindQuery(string query)
    {
        string hash = MemoryStore.SemanticTextHash(query);
        if (QueryHash.Length > 0 && QueryHash != hash) throw new InvalidOperationException("Semantic trace belongs to another query");
        QueryHash = hash;
    }
    public string Status { get; set; } = "not_requested";
    public List<string> Rejections { get; } = new();
    public List<string> Top24 { get; } = new();
    public List<string> Anchors { get; } = new();
    public List<string> Supplied { get; } = new();
    public int BodyChars { get; set; }
    internal string QueryHash { get; set; } = "";
    internal IReadOnlyList<MemoryTurn> Snapshots { get; set; } = Array.Empty<MemoryTurn>();
    internal List<MemoryTurn> Dependents { get; } = new();
    internal Dictionary<string, DirectRecallRank> DirectRanks { get; } = new(StringComparer.Ordinal);
}
internal sealed record DirectRecallRank(int Priority, int Rank);
internal sealed partial class MemoryStore
{
    internal string SemanticDataRoot => _root;
    // Owner takes immutable raw copies only. Hashing, JSON, IPC, model work and
    // index reconstruction happen in the child/background, never on Unity state.
    internal IReadOnlyList<SemanticIndexRecord> CaptureSemanticIndexSnapshot()
    {
        using(PreparationTiming.Measure("semantic.snapshot_lock_wait"))System.Threading.Monitor.Enter(_experienceLock);
        try
        {
            var raw=ReadExperiences().Where(t=>t.DeliveryState is "received" or "delivered" && t.Role is "user" or "assistant"
                && t.Origin is "user" or "model" or "game" or LocalFallbackOrigin)
                .GroupBy(t=>t.EventId).Select(g=>g.First()).Select(t=>new SemanticIndexRecord(t.EventId,t.Text,t.Role,t.Origin,t.DeliveryState,
                    Version:SemanticSourceVersion(t))).ToList();
            // Retained bodies are indexed as derived material, never as speech.
            // Their storage lifecycle and exact permission resolver still own supply.
            // Reuse the existing distinction between true absence and an
            // unreadable/lost known store. Failed reads are never complete zeros.
            var presence=InspectB1ManagementStorage();
            if(presence==B1ManagementStoragePresence.Uncertain)
                throw new SemanticSnapshotUnavailableException("semantic_b1_snapshot_unavailable");
            var doc=presence==B1ManagementStoragePresence.Present?LoadB1():null;
            if(presence==B1ManagementStoragePresence.Present&&doc==null)
                throw new SemanticSnapshotUnavailableException("semantic_b1_snapshot_unreadable");
            if(doc!=null)raw.AddRange(doc.Entries.Where(e=>B1ManagementState(doc,e.Version) is not
                    ("suppressed" or "withdrawn" or "deleting" or "deleted"))
                .Select(e=>new SemanticIndexRecord(e.Version,e.Text+" "+e.Scope,"assistant","retained","delivered","b1",e.Revision.ToString())));
            raw.AddRange(_reflectionNotes.Concat(_reflectionLessons).Where(t=>t.LifecycleState is "active" or "settled" or "superseded")
                .Select(t=>new SemanticIndexRecord(t.EventId,t.Text,"assistant","retained","delivered",B1Kind(t),SemanticSourceVersion(t))));
            return raw.GroupBy(r=>r.WireId,StringComparer.Ordinal).Select(g=>g.First()).ToArray();
        }
        finally{System.Threading.Monitor.Exit(_experienceLock);}
    }
    internal static string SemanticTextHash(string text) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    // This is a typed isolated harness entry, not a production scheduler.
    // The ordinary Build/Send path remains unchanged when no result is passed.
    internal IReadOnlyList<MemoryTurn> SelectSemanticExperiences(string query, SemanticRecallResult? result,
        IEnumerable<string> suppliedIds, bool previousSession, SemanticRecallTrace trace)
    {
        trace.BindQuery(query);
        if (result == null) return Array.Empty<MemoryTurn>();
        if (result.Unavailable.Length > 0) { trace.Status = "unavailable:" + result.Unavailable; return Array.Empty<MemoryTurn>(); }
        if (string.IsNullOrWhiteSpace(query) || MemorySemantics.DeclinesArchiveRecall(query))
        { trace.Status = "disabled_for_query"; return Array.Empty<MemoryTurn>(); }
        if (!SemanticResultMatches(query,result,SemanticDomain.Events))
        { trace.Status = "stale_or_invalid_result"; return Array.Empty<MemoryTurn>(); }
        trace.Status = result.IndexComplete ? "available" : "available_partial_index";
        var excluded = suppliedIds.ToHashSet(StringComparer.Ordinal);
        var all = ReadExperiences();
        var byId = all.GroupBy(t => t.EventId).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        string speaker = MemorySemantics.RequestedHistorySpeaker(query);
        var scope = MemorySemantics.ParseExperienceRecallQuery(query);
        string[] identifiers = MemorySemantics.RecallIdentifiers(query);
        string[] quotes = UtteranceAttribution.LiteralQuotes(query);
        bool Eligible(MemoryTurn t) => t.DeliveryState is "received" or "delivered"
            && t.Role is "user" or "assistant" && IsMentionAllowed(t) && IsNativeSupplyAllowed(t)
            && (!previousSession || t.SessionId != _sessionId) && (speaker.Length == 0 || t.Role == speaker)
            && (scope == null || scope.AllowsSourceScope(t))
            && identifiers.All(id => MemorySemantics.ContainsRecallIdentifier(t.Text, id))
            && quotes.All(q => t.Text.Contains(q, StringComparison.Ordinal));
        // Permission/hash/scope filtering precedes top-k. Invalid high scores do
        // not consume the 24; previously supplied records do not consume anchors.
        var candidates = new List<MemoryTurn>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in result.Candidates.OrderByDescending(c => c.Score))
        {
            if(candidate.Kind!="utterance")continue;
            if (!seen.Add(candidate.EventId)) continue;
            byId.TryGetValue(candidate.EventId, out var turn);
            string reason = turn == null ? "source_missing" : !SemanticCandidateMatches(candidate,turn)
                ? "hash_changed" : !Eligible(turn) ? "permission_or_scope" : excluded.Contains(turn.EventId) ? "already_supplied" : "";
            if (reason.Length > 0) { trace.Rejections.Add(candidate.EventId + ":" + reason); continue; }
            candidates.Add(turn!);
            // All later rows have a lower rank. Once 24 lawful candidates are
            // known, rechecking the rest cannot change the filtered top-24.
            if(candidates.Count==24)break;
        }
        trace.Top24.AddRange(candidates.Select(t => t.EventId));
        var units = new List<List<MemoryTurn>>();
        var selected = new HashSet<string>(excluded, StringComparer.Ordinal);
        var families = new HashSet<string>(StringComparer.Ordinal);
        // Explicitly bound revisions are indivisible units. No inferred family
        // links and no textual dedup across independent objects.
        List<MemoryTurn>? Unit(MemoryTurn anchor, out string family)
        {
            var current = _autonomous.FirstOrDefault(t => t.Kind == NaturalRevisionKind && IsActiveLifecycle(t)
                && (t.SourceEventId == anchor.EventId || t.SourceEventIds.Contains(anchor.EventId)));
            family = current?.RecallId ?? "event:" + anchor.EventId;
            if (current != null && !IsDerivedMemoryVisible(current)) return null;
            // Natural correction receipts may also cite retained memory IDs.
            // Resolve those through the same known family, never invent raw text.
            var required = current == null ? new[] { anchor.EventId } : current.SourceEventIds
                .Where(id => byId.ContainsKey(id) || !_autonomous.Any(t => t.EventId == id && t.RecallId == current.RecallId))
                .Append(current.SourceEventId).Append(anchor.EventId).Distinct();
            var unit = new List<MemoryTurn>();
            foreach (string id in required)
            {
                byId.TryGetValue(id, out var raw);
                if (raw == null || !Eligible(raw)) return null;
                if (!selected.Contains(id)) unit.Add(WithNaturalMemoryContext(raw));
            }
            return unit;
        }
        bool Fits(List<MemoryTurn> unit) => selected.Count - excluded.Count + unit.Count <= 24
            && RenderSemanticExperiences(units.SelectMany(x => x).Concat(unit).ToList()).Length <= 6000;
        foreach (var anchor in candidates)
        {
            if (selected.Contains(anchor.EventId)) continue;
            var unit = Unit(anchor, out string family);
            if (families.Contains(family)) continue;
            if (unit == null) { trace.Rejections.Add(anchor.EventId + ":required_revision_unavailable"); continue; }
            if (unit.Count == 0) continue;
            if (!Fits(unit)) { trace.Rejections.Add(anchor.EventId + ":complete_unit_capacity"); continue; }
            families.Add(family); units.Add(unit); selected.UnionWith(unit.Select(t => t.EventId)); trace.Anchors.Add(anchor.EventId);
            bool exact = (identifiers.Length > 0 || quotes.Length > 0 || scope?.From != null)
                && identifiers.All(id=>MemorySemantics.ContainsRecallIdentifier(anchor.Text,id))
                && quotes.All(q=>anchor.Text.Contains(q,StringComparison.Ordinal))
                && (scope==null||scope.AllowsSourceScope(anchor));
            trace.DirectRanks[anchor.EventId] = new(exact ? 0 : 1, candidates.FindIndex(t => t.EventId == anchor.EventId) + 1);
        }
        // Only after reserving anchors, use the existing same-session adjacency
        // and explicit source links. Whole revision units remain indivisible.
        foreach (var anchorId in trace.Anchors.ToArray())
        {
            var anchor = all.First(t => t.EventId == anchorId);
            foreach (var adjacent in all.Where(t => t.SessionId == anchor.SessionId && Math.Abs(t.Sequence - anchor.Sequence) <= 1 && Eligible(t)))
            {
                if (selected.Contains(adjacent.EventId)) continue;
                var unit = Unit(adjacent, out string family);
                if (unit == null || families.Contains(family) || !Fits(unit)) continue;
                families.Add(family); units.Add(unit); selected.UnionWith(unit.Select(t => t.EventId));
            }
        }
        var supplied = units.SelectMany(x => x).OrderBy(t => t.Timestamp).ThenBy(t => t.SessionId, StringComparer.Ordinal).ThenBy(t => t.Sequence).ToList();
        trace.Supplied.AddRange(supplied.Select(t => t.EventId)); trace.BodyChars = RenderSemanticExperiences(supplied).Length;
        trace.Snapshots = supplied.Select(t => t.Copy()).ToArray();
        PreparationTiming.Current?.Value("semantic_event_rejected",trace.Rejections.Count);
        PreparationTiming.Current?.Value("semantic_event_supplied",supplied.Count);
        return supplied;
    }

    internal bool SemanticSupplyPermitted(SemanticRecallTrace trace, string query)
    {
        if (trace.Snapshots.Count == 0 && trace.Dependents.Count == 0) return true;
        if (trace.QueryHash != SemanticTextHash(query) || MemorySemantics.DeclinesArchiveRecall(query)) return false;
        var all = ReadExperiences();
        bool rawPermitted = trace.Snapshots.All(old => all.FirstOrDefault(t => t.EventId == old.EventId) is { } current
            && IsMentionAllowed(current) && IsNativeSupplyAllowed(current)
            && SemanticTextHash(current.Text) == SemanticTextHash(old.Text)
            && NaturalMemoryContext(current) == old.NaturalMemoryReadContext);
        if (!rawPermitted) return false;
        var availableThoughts = GetReflectionNotes(int.MaxValue).Concat(GetSettledNotes(int.MaxValue))
            .Concat(GetSupersededNotes(int.MaxValue)).Concat(GetReflectionLessons(int.MaxValue)).ToList();
        return trace.Dependents.Where(t => t.Kind is "reflection.note" or "reflection.lesson")
            .All(old => availableThoughts.Any(current => current.EventId == old.EventId
                && current.Revision == old.Revision && current.LifecycleState == old.LifecycleState
                && current.Text == old.Text && IsMentionAllowed(current) && IsNativeSupplyAllowed(current)));
    }

    internal static string RenderSemanticExperiences(IReadOnlyList<MemoryTurn> turns) => turns.Count == 0 ? "" :
        "[语义补充经历]\n以下是按语义相近找回的过往材料，可能有用，也可能与现在无关；结合本轮交流决定是否使用或提及。\n"
        + BuildExperienceContext(turns);
}
