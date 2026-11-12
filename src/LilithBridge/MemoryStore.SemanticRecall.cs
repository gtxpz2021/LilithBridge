using System.Security.Cryptography;
using System.Text;

namespace LilithBridge;

// Offline prototype exchange only. Scores discover candidates, never grant
// permission or establish truth/current status. No persistent memory writer.
internal sealed record SemanticRecallCandidate(string EventId, string TextHash, double Score,
    int ChunkStart, int ChunkEnd,string Kind="utterance",string Version="",int Rank=0,int? CharStart=null,int? CharEnd=null)
{
    // Preserve the original internal reflection/harness constructor shape while
    // newer worker replies additionally carry exact character hit positions.
    internal SemanticRecallCandidate(string eventId,string textHash,double score,int chunkStart,int chunkEnd,string kind,string version,int rank)
        :this(eventId,textHash,score,chunkStart,chunkEnd,kind,version,rank,null,null){}
}
internal sealed record SemanticRecallResult(string QueryId, string QueryHash, string ModelRevision,
    string IndexVersion, IReadOnlyList<SemanticRecallCandidate> Candidates, string Unavailable = "", bool IndexComplete = true,
    string DataRoot="",string SessionId="",SemanticDomain Domains=SemanticDomain.All,long TargetWatermark=0,long CompletedWatermark=0,long Generation=0)
{ internal ContextRelevancePlan? RelevancePlan {get;init;} internal bool RelevanceBypassed {get;init;} }
internal sealed class SemanticRecallTrace
{
    internal void BindQuery(string query)
    {
        string hash = MemoryStore.SemanticTextHash(query);
        if (QueryHash.Length > 0 && QueryHash != hash) throw new InvalidOperationException("Semantic trace belongs to another query");
        QueryHash = hash;
    }
    internal ContextRelevancePlan? RelevancePlan {get;set;}
    internal BridgeConfig? RelevanceConfig {get;set;}
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
    internal Dictionary<string, string> UnitFingerprints { get; } = new(StringComparer.Ordinal);
    public List<string> Top24Units { get; } = new();
}
internal sealed record DirectRecallRank(int Priority, int Rank);
internal sealed partial class MemoryStore
{
    internal string SemanticDataRoot => _root;
    // Capture immutable raw copies under the store lock. Unit encoding happens
    // after that lock; vector chunks, model work and index I/O belong to worker.
    internal IReadOnlyList<SemanticIndexRecord> CaptureSemanticIndexSnapshot()
    {
        List<MemoryTurn> raw;
        var retained = new List<SemanticIndexRecord>();
        using(PreparationTiming.Measure("semantic.snapshot_lock_wait"))System.Threading.Monitor.Enter(_experienceLock);
        try
        {
            raw=ReadExperiences().Where(t=>t.DeliveryState is "received" or "delivered" && t.Role is "user" or "assistant"
                && t.Origin is "user" or "model" or "game" or LocalFallbackOrigin)
                .Select(t => t.Copy()).ToList();
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
            if(doc!=null)retained.AddRange(doc.Entries.Where(e=>B1ManagementState(doc,e.Version) is not
                    ("suppressed" or "withdrawn" or "deleting" or "deleted"))
                .Select(e=>new SemanticIndexRecord(e.Version,B1DiscoveryText(e),"assistant","retained","delivered","b1",e.Revision.ToString())));
            retained.AddRange(_reflectionNotes.Concat(_reflectionLessons).Where(t=>t.LifecycleState is "active" or "settled" or "superseded")
                .Select(t=>new SemanticIndexRecord(t.EventId,t.Text,"assistant","retained","delivered",B1Kind(t),SemanticSourceVersion(t))));
        }
        finally{System.Threading.Monitor.Exit(_experienceLock);}
        // Unit hashing/encoding happens after immutable raw capture releases the
        // store lock. The worker still owns vector chunks and derived index I/O.
        var catalog = LogicalDialogueCatalog.Build(raw);
        if (catalog.Omissions.Count > 0) throw new SemanticSnapshotUnavailableException("logical_source_identity_unavailable");
        var generated = GetQualifiedGeneratedReplies();
        var generatedOwners = generated.Where(source=> {
            var unit=catalog.FindByKey(source.LogicalOwnerKey);
            if(unit==null)return true;
            var mapped=GeneratedMappedOriginals(source).Select(t=>t.EventId).ToHashSet(StringComparer.Ordinal);
            return unit.Members.All(t=>mapped.Contains(t.EventId));
        }).Select(source=>source.LogicalOwnerKey).ToHashSet(StringComparer.Ordinal);
        var records = catalog.Units.Where(unit=>!generatedOwners.Contains(unit.Key)).Select(u => u.IsMessage
            ? new SemanticIndexRecord(u.Key, u.Text, u.Role, u.Origin, "delivered", "message", u.VersionFingerprint)
            : new SemanticIndexRecord(u.AnchorEventId, u.Text, u.Role, u.Origin, u.Members[0].DeliveryState,
                Version: SemanticSourceVersion(u.Members[0])));
        var generatedRows=generated.Select(source=>new SemanticIndexRecord("generated/"+source.Key,
            source.BodyText,"assistant","generated_public_answer","received_public_text","generated_reply",source.VersionFingerprint));
        return records.Concat(generatedRows).Concat(retained).GroupBy(r=>r.WireId,StringComparer.Ordinal).Select(g=>g.First()).ToArray();
    }
    internal static string SemanticTextHash(string text) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    // This is a typed isolated harness entry, not a production scheduler.
    // The ordinary Build/Send path remains unchanged when no result is passed.
    internal IReadOnlyList<MemoryTurn> SelectSemanticExperiences(string query, SemanticRecallResult? result,
        IEnumerable<string> suppliedIds, bool previousSession, SemanticRecallTrace trace)
    {
        trace.BindQuery(query);
        // A trace can be reused for the same query. Selection is request-local;
        // reflection dependents collected elsewhere remain separately owned.
        trace.Status = "not_requested"; trace.Rejections.Clear(); trace.Top24.Clear(); trace.Top24Units.Clear();
        trace.Anchors.Clear(); trace.Supplied.Clear(); trace.DirectRanks.Clear(); trace.UnitFingerprints.Clear();
        trace.Snapshots = Array.Empty<MemoryTurn>(); trace.BodyChars = 0;
        if (result == null) return Array.Empty<MemoryTurn>();
        if (result.Unavailable.Length > 0) { trace.Status = "unavailable:" + result.Unavailable; return Array.Empty<MemoryTurn>(); }
        if (string.IsNullOrWhiteSpace(query) || MemorySemantics.DeclinesArchiveRecall(query))
        { trace.Status = "disabled_for_query"; return Array.Empty<MemoryTurn>(); }
        if (!SemanticResultMatches(query,result,SemanticDomain.Events))
        { trace.Status = "stale_or_invalid_result"; return Array.Empty<MemoryTurn>(); }
        trace.Status = result.RelevancePlan is {} relevance ? "relevance:"+relevance.Status
            :result.IndexComplete ? "available" : "available_partial_index";
        var excluded = suppliedIds.ToHashSet(StringComparer.Ordinal);
        var all = ReadExperiences();
        var catalog = LogicalDialogueCatalog.Build(all);
        string speaker = MemorySemantics.RequestedHistorySpeaker(query);
        var scope = MemorySemantics.ParseExperienceRecallQuery(query);
        string[] identifiers = MemorySemantics.RecallIdentifiers(query);
        string[] quotes = UtteranceAttribution.LiteralQuotes(query);
        bool Required(LogicalDialogueUnit unit)=>result.RelevancePlan?.Selected.Any(i=>i.Provenance.Any(p=>unit.Members.Any(t=>t.EventId==p.EventId)))==true;
        bool Eligible(LogicalDialogueUnit unit) => unit.Members.All(t =>
                t.DeliveryState is "received" or "delivered"
                && t.Role is "user" or "assistant" && IsMentionAllowed(t) && IsNativeSupplyAllowed(t)
                && (result.RelevancePlan!=null||Required(unit)||!previousSession || t.SessionId != _sessionId)
                && (result.RelevancePlan!=null||Required(unit)||speaker.Length == 0 || t.Role == speaker)
                && (result.RelevancePlan!=null||Required(unit)||scope == null || scope.AllowsSourceScope(t)))
            && (result.RelevancePlan!=null||Required(unit)||identifiers.All(id => unit.Runs.Any(r => MemorySemantics.ContainsRecallIdentifier(r.Text, id)))
                && quotes.All(unit.ContainsExact));
        // Source qualification and unit dedup precede top-k. A long reply or a
        // gapped owner cannot consume one anchor per display bubble.
        var candidates = new List<(SemanticRecallCandidate Hit, LogicalDialogueUnit Unit)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in result.Candidates.OrderByDescending(c => c.Score))
        {
            if (candidate.Kind is not ("utterance" or "message")) continue;
            var unit = catalog.ResolveCandidate(candidate);
            string reason = unit == null ? "unit_source_or_fingerprint_changed"
                : !Eligible(unit) ? "permission_or_scope"
                : unit.Members.All(t => excluded.Contains(t.EventId)) ? "already_supplied" : "";
            if (reason.Length > 0) { trace.Rejections.Add(candidate.EventId + ":" + reason); continue; }
            if (!seen.Add(unit!.Key)) continue;
            candidates.Add((candidate, unit));
            if (result.RelevancePlan==null && candidates.Count == 24) break;
        }
        trace.Top24.AddRange(candidates.Select(c => c.Unit.AnchorEventId));
        trace.Top24Units.AddRange(candidates.Select(c => c.Unit.Key));
        var selected = new Dictionary<string, LogicalDialogueUnit>(StringComparer.Ordinal);
        var direct = new List<string>();
        List<LogicalDialogueUnit>? Closure(LogicalDialogueUnit anchor)
        {
            var closure = new Dictionary<string, LogicalDialogueUnit>(StringComparer.Ordinal);
            var pending = new Queue<LogicalDialogueUnit>(); pending.Enqueue(anchor);
            while (pending.Count > 0)
            {
                var unit = pending.Dequeue();
                if (!closure.TryAdd(unit.Key, unit)) continue;
                if (result.RelevancePlan!=null
                    ?!unit.Members.All(t=>t.DeliveryState is "received" or "delivered"&&t.Role is "user" or "assistant"&&IsMentionAllowed(t)&&IsNativeSupplyAllowed(t))
                    :!Eligible(unit)) return null;
                // Preserve existing explicit natural-correction support. This
                // does not infer a family from similar language or timestamps.
                foreach (var member in unit.Members)
                {
                    var current = _autonomous.FirstOrDefault(t => t.Kind == NaturalRevisionKind && IsActiveLifecycle(t)
                        && (t.SourceEventId == member.EventId || t.SourceEventIds.Contains(member.EventId)));
                    if (current == null) continue;
                    if (!IsDerivedMemoryVisible(current)) return null;
                    foreach (string id in current.SourceEventIds.Append(current.SourceEventId).Append(member.EventId)
                        .Where(id => id.Length > 0).Distinct(StringComparer.Ordinal))
                    {
                        // Retained correction IDs are not invented raw speech.
                        if (_autonomous.Any(t => t.EventId == id && t.RecallId == current.RecallId)) continue;
                        var required = catalog.FindByEvent(id);
                        if (required == null) return null;
                        pending.Enqueue(required);
                    }
                }
            }
            return closure.Values.ToList();
        }
        foreach (var candidate in candidates)
        {
            if (selected.ContainsKey(candidate.Unit.Key)) continue;
            var closure = Closure(candidate.Unit);
            if (closure == null) { trace.Rejections.Add(candidate.Unit.Key + ":required_revision_unavailable"); continue; }
            int count = selected.Keys.Union(closure.Select(u => u.Key), StringComparer.Ordinal).Count();
            if (result.RelevancePlan==null && count > 24) { trace.Rejections.Add(candidate.Unit.Key + ":complete_unit_capacity"); continue; }
            // No legacy 6000-character cutoff: selected logical messages carry
            // their full available text. Existing final-request gates still own
            // transport capacity; never silently emit a positive half-clause.
            foreach (var unit in closure) selected.TryAdd(unit.Key, unit);
            string hit = candidate.Hit.Kind == "utterance" ? candidate.Hit.EventId : candidate.Unit.AnchorEventId;
            direct.Add(hit); trace.Anchors.Add(hit);
            bool exact = (identifiers.Length > 0 || quotes.Length > 0 || scope?.From != null)
                && identifiers.All(id => candidate.Unit.Runs.Any(r => MemorySemantics.ContainsRecallIdentifier(r.Text, id)))
                && quotes.All(candidate.Unit.ContainsExact);
            if(result.RelevancePlan==null || result.RelevancePlan.Admits(candidate.Hit.EventId))
                foreach (var member in candidate.Unit.Members)
                    trace.DirectRanks[member.EventId] = new(exact ? 0 : 1, candidates.FindIndex(c => c.Unit.Key == candidate.Unit.Key) + 1);
        }
        // Same-owner completion is already contained in each unit. Sequence +/-1
        // is not a second way to guess an experience boundary.
        var selectedUnits = selected.Values.OrderBy(u => u.Timestamp).ThenBy(u => u.Key, StringComparer.Ordinal).ToArray();
        var supplied = new LogicalDialogueSelection(selectedUnits, direct, WithNaturalMemoryContext);
        foreach (var unit in supplied.Units) trace.UnitFingerprints[unit.Key] = unit.VersionFingerprint;
        trace.Supplied.AddRange(supplied.Select(t => t.EventId));
        trace.BodyChars = RenderSemanticExperiences(supplied).Length;
        trace.Snapshots = supplied.Select(t => t.Copy()).ToArray();
        PreparationTiming.Current?.Value("semantic_event_rejected", trace.Rejections.Count);
        PreparationTiming.Current?.Value("semantic_event_supplied", supplied.Count);
        PreparationTiming.Current?.Value("semantic_logical_units_supplied", supplied.Units.Count);
        return supplied;
    }

    internal bool SemanticSupplyPermitted(SemanticRecallTrace trace, string query)
    {
        lock (_experienceLock) return SemanticSupplyPermittedLocked(trace, query);
    }
    private bool SemanticSupplyPermittedLocked(SemanticRecallTrace trace, string query)
    {
        if(trace.RelevancePlan is {} plan && trace.RelevanceConfig is {} config
            && plan.Status=="accepted"&&!ContextRelevanceSourcesCurrent(plan.Snapshot,config))return false;
        if (trace.Snapshots.Count == 0 && trace.Dependents.Count == 0) return true;
        if (trace.QueryHash != SemanticTextHash(query) || MemorySemantics.DeclinesArchiveRecall(query)) return false;
        var currentCatalog = GetExperienceLogicalCatalog(scoped: false);
        if (trace.UnitFingerprints.Any(pair => currentCatalog.FindByKey(pair.Key) is not { } unit
                || unit.VersionFingerprint != pair.Value)) return false;
        bool rawPermitted = trace.Snapshots.All(old => currentCatalog.FindByEvent(old.EventId)?.Members.FirstOrDefault(t => t.EventId == old.EventId) is { } current
            && IsMentionAllowed(current) && IsNativeSupplyAllowed(current)
            && LogicalDialogueCatalog.RawFingerprint(current) == LogicalDialogueCatalog.RawFingerprint(old)
            && NaturalMemoryContext(current) == old.NaturalMemoryReadContext);
        if (!rawPermitted) return false;
        if (!trace.Dependents.Any(t => t.Kind is "reflection.note" or "reflection.lesson")) return true;
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
