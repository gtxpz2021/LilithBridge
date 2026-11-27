using System.Security.Cryptography;
using System.Text;

namespace LilithBridge;

// Offline prototype exchange only. Scores discover candidates, never grant
// permission or establish truth/current status. No persistent memory writer.
internal sealed record SemanticRecallCandidate(string EventId, string TextHash, double Score,
    int ChunkStart, int ChunkEnd,string Kind="utterance",string Version="",int Rank=0,int? CharStart=null,int? CharEnd=null)
{
    internal int DomainRank { get; init; }
    // Preserve the original internal reflection/harness constructor shape while
    // newer worker replies additionally carry exact character hit positions.
    internal SemanticRecallCandidate(string eventId,string textHash,double score,int chunkStart,int chunkEnd,string kind,string version,int rank)
        :this(eventId,textHash,score,chunkStart,chunkEnd,kind,version,rank,null,null){}
}
internal sealed record SemanticGeometryMetrics(double NoveltyEta, double Tortuosity, double NetDisplacementRad,
    double ArcLengthRad, double StepJumpRatio, int WhiteningD, IReadOnlyList<string> DppSelectedIds,
    IReadOnlyDictionary<string,double> OrthSins)
{
    internal static readonly SemanticGeometryMetrics Empty = new(0,0,0,0,0,0,Array.Empty<string>(),
        new Dictionary<string,double>());
}
// Geometry sparks are retrieval candidates only. Scores never establish truth,
// permission or current status, and no trajectory/regime label is carried here.
internal sealed record SemanticGeometrySpark(string EventId,string TextHash,double Score,double ZScore,
    string BridgeVia="",double BridgeScore=0,string BridgeViaHash="")
{
    internal static readonly IReadOnlyList<SemanticGeometrySpark> Empty=Array.Empty<SemanticGeometrySpark>();
}
internal sealed record SemanticRecallResult(string QueryId, string QueryHash, string ModelRevision,
    string IndexVersion, IReadOnlyList<SemanticRecallCandidate> Candidates, string Unavailable = "", bool IndexComplete = true,
    string DataRoot="",string SessionId="",SemanticDomain Domains=SemanticDomain.All,long TargetWatermark=0,long CompletedWatermark=0,long Generation=0)
{
    internal ContextRelevancePlan? RelevancePlan {get;init;} internal bool RelevanceBypassed {get;init;}
    internal SemanticGeometryMetrics? Geometry {get;init;}
    internal bool SalienceGated {get;init;}
    internal double SalienceZ {get;init;}
    internal IReadOnlyList<SemanticGeometrySpark> OrthogonalSparks {get;init;}=SemanticGeometrySpark.Empty;
    internal IReadOnlyList<SemanticGeometrySpark> WormholeSparks {get;init;}=SemanticGeometrySpark.Empty;
    internal IReadOnlyList<SemanticGeometrySpark> BridgeSparks {get;init;}=SemanticGeometrySpark.Empty;
    internal IReadOnlyList<SemanticGeometrySpark> SceneSparks {get;init;}=SemanticGeometrySpark.Empty;
    internal IReadOnlyDictionary<string,double> DocSims {get;init;}=new Dictionary<string,double>();
    internal IReadOnlyDictionary<string,double> DocQuantiles {get;init;}=new Dictionary<string,double>();
    internal IReadOnlyList<string> ProbeWireIds {get;init;}=Array.Empty<string>();
}
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
    public List<string> SparkEventIds { get; } = new();
    public List<string> ProbeEventIds { get; } = new();
    public int BodyChars { get; set; }
    internal string QueryHash { get; set; } = "";
    internal string LensShiftB1Version { get; set; } = "";
    internal int SparkCandidateCount { get; set; }
    // Tokens are retained request-locally for a read-only word-overlap telemetry
    // count. No spark body, event id or token is written to that telemetry line.
    internal HashSet<string> SparkTokens { get; } = new(StringComparer.OrdinalIgnoreCase);
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
    // Spark exposure is a pure anti-repetition scheduling fact. It never feeds
    // persona state, affection, mood or any generation-side diagnostic label.
    private readonly object _sparkExposureLock = new();
    private readonly Dictionary<string,long> _recentSparkExposure = new(StringComparer.Ordinal);
    private long _semanticSelectionTurns;
    internal int SemanticExposureCooldownTurns { get; set; } = 12;
    // Capture immutable raw copies under the store lock. Unit encoding happens
    // after that lock; vector chunks, model work and index I/O belong to worker.
    internal IReadOnlyList<SemanticIndexRecord> CaptureSemanticIndexSnapshot()
    {
        LogicalDialogueCatalog catalog;
        var retained = new List<SemanticIndexRecord>();
        using(PreparationTiming.Measure("semantic.snapshot_lock_wait"))System.Threading.Monitor.Enter(_experienceLock);
        try
        {
            using (PreparationTiming.Measure("sem.snap.catalog"))
            {
                var view = GetExperienceValidationView();
                var denied = SuppressedExperienceIds();
                catalog = view.SemanticCatalog(t => !denied.Contains(t.EventId)
                    && t.DeliveryState is "received" or "delivered" && t.Role is "user" or "assistant"
                    && t.Origin is "user" or "model" or "game" or LocalFallbackOrigin);
            }
            // Retained bodies are indexed as derived material, never as speech.
            // Their storage lifecycle and exact permission resolver still own supply.
            // Reuse the existing distinction between true absence and an
            // unreadable/lost known store. Failed reads are never complete zeros.
            B1Document? doc;
            using (PreparationTiming.Measure("sem.snap.b1"))
            {
                var presence=InspectB1ManagementStorage();
                if(presence==B1ManagementStoragePresence.Uncertain)
                    throw new SemanticSnapshotUnavailableException("semantic_b1_snapshot_unavailable");
                doc=presence==B1ManagementStoragePresence.Present?LoadB1():null;
                if(presence==B1ManagementStoragePresence.Present&&doc==null)
                    throw new SemanticSnapshotUnavailableException("semantic_b1_snapshot_unreadable");
                if(doc!=null)retained.AddRange(doc.Entries.Where(e=>B1ManagementState(doc,e.Version) is not
                        ("suppressed" or "withdrawn" or "deleting" or "deleted"))
                    .Select(e=>
                    {
                        string mgmt = B1ManagementState(doc, e.Version);
                        bool settled = IsB1SettledState(mgmt);
                        bool isCurrent = !settled && doc.Current.GetValueOrDefault(e.LogicalId) == e.Version;
                        string lifecycle = settled ? "settled"
                            : !isCurrent ? "superseded"
                            : e.Nature is "inference" or "legacy_unspecified" ? "active"
                            : "retained_non_bridge";
                        return new SemanticIndexRecord(e.Version,B1DiscoveryText(e),"assistant","retained","delivered","b1",
                            e.Revision.ToString(), lifecycle);
                    }));
            }
            using (PreparationTiming.Measure("sem.snap.reflections"))
            {
                retained.AddRange(_reflectionNotes.Concat(_reflectionLessons).Concat(_relationshipReadings)
                    .Where(t=>t.LifecycleState is "active" or "settled" or "superseded")
                    .Select(t=>new SemanticIndexRecord(t.EventId,t.Text,"assistant","retained","delivered",B1Kind(t),
                        SemanticSourceVersion(t),t.LifecycleState)));
            }
        }
        finally{System.Threading.Monitor.Exit(_experienceLock);}
        // Unit hashing/encoding happens after immutable raw capture releases the
        // store lock. The worker still owns vector chunks and derived index I/O.
        if (catalog.Omissions.Count > 0) throw new SemanticSnapshotUnavailableException("logical_source_identity_unavailable");
        IReadOnlyList<GeneratedReplySnapshot> generated;
        HashSet<string> generatedOwners;
        using (PreparationTiming.Measure("sem.snap.generated"))
        {
            generated = GetQualifiedGeneratedReplies();
            generatedOwners = generated.Where(source=> {
                var unit=catalog.FindByKey(source.LogicalOwnerKey);
                if(unit==null)return true;
                var mapped=GeneratedMappedOriginals(source).Select(t=>t.EventId).ToHashSet(StringComparer.Ordinal);
                return unit.Members.All(t=>mapped.Contains(t.EventId));
            }).Select(source=>source.LogicalOwnerKey).ToHashSet(StringComparer.Ordinal);
        }
        using (PreparationTiming.Measure("sem.snap.project"))
        {
            var records = catalog.Units.Where(unit=>!generatedOwners.Contains(unit.Key)).Select(u => u.IsMessage
                ? new SemanticIndexRecord(u.Key, u.Text, u.Role, u.Origin, "delivered", "message", u.VersionFingerprint) { KnownTextHash = u.TextHash }
                : new SemanticIndexRecord(u.AnchorEventId, u.Text, u.Role, u.Origin, u.Members[0].DeliveryState,
                    Version: SemanticSourceVersion(u.Members[0])) { KnownTextHash = u.TextHash });
            var generatedRows=generated.Select(source=>new SemanticIndexRecord("generated/"+source.Key,
                source.BodyText,"assistant","generated_public_answer","received_public_text","generated_reply",source.VersionFingerprint) { KnownTextHash = source.BodyHash });
            return records.Concat(generatedRows).Concat(retained).GroupBy(r=>r.WireId,StringComparer.Ordinal).Select(g=>g.First()).ToArray();
        }
    }
    private static string SemanticWireIdForUnit(LogicalDialogueUnit unit) =>
        unit.IsMessage ? unit.Key : unit.AnchorEventId;

    internal IReadOnlyList<string> ResolveRecentSemanticUnitIds(IReadOnlyList<MemoryTurn> history,
        string? excludeUserEventId, Func<string?, int> effectiveCharacters, int maxUnits = 6)
    {
        if (history == null || history.Count == 0 || maxUnits <= 0) return Array.Empty<string>();
        var catalog = GetExperienceLogicalCatalog(scoped: false, dialogueOnly: false);
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ordered = history.Where(t => t.EventId.Length > 0 && t.EventId != excludeUserEventId
                && t.DeliveryState is "received" or "delivered" && t.Role is "user" or "assistant")
            .OrderBy(t => t.Timestamp).ThenBy(t => t.Sequence).ThenBy(t => t.EventId, StringComparer.Ordinal);
        foreach (var turn in ordered)
        {
            if (catalog.FindByEvent(turn.EventId) is { } unit)
            {
                if (effectiveCharacters(unit.Text) < 8) continue;
                string unitId = SemanticWireIdForUnit(unit);
                if (seen.Add(unitId)) result.Add(unitId);
            }
            else if (effectiveCharacters(turn.Text) >= 8 && seen.Add(turn.EventId))
            {
                result.Add(turn.EventId);
            }
        }
        return result.Count <= maxUnits ? result.ToArray() : result.TakeLast(maxUnits).ToArray();
    }

    // Request-local lexical prefilter. It uses the same token index and document
    // frequency accounting as the ordinary experience candidate pass, and sends
    // only wire identities to the private Python child.
    internal IReadOnlyList<string> SelectSemanticProbeWireIds(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<string>();
        var catalog = GetExperienceLogicalCatalog(scoped:false, dialogueOnly:false);
        var available = catalog.Units;
        if (available.Count == 0) return Array.Empty<string>();
        bool[] availableMask = Enumerable.Repeat(true, available.Count).ToArray();
        var documentFrequency = new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
        var tokenIndex = catalog.TokenIndex;
        void EnsureDocumentFrequencies(IEnumerable<string> tokensToMeasure)
        {
            foreach (string token in tokensToMeasure)
            {
                if (!documentFrequency.TryAdd(token, 0)) continue;
                if (!tokenIndex.TryGetValue(token, out int[]? indices)) continue;
                int count = 0;
                foreach (int index in indices)
                    if (availableMask[index]) count++;
                documentFrequency[token] = count;
            }
        }
        double TokenIdf(string word) => Math.Log((available.Count + 1d) / (documentFrequency.GetValueOrDefault(word) + 1d));
        var queryTokens = MemorySemantics.MatchingTokens(query);
        EnsureDocumentFrequencies(queryTokens);
        int maxDf = Math.Max(4, available.Count / 15);
        var ageCut = DateTimeOffset.UtcNow.AddDays(-7);
        var probes = new List<(LogicalDialogueUnit Unit, double Weight)>();
        foreach (var unit in available)
        {
            if (unit.Timestamp > ageCut) continue;
            double weight = 0;
            foreach (string token in queryTokens)
            {
                if (token.Length < 2 || !unit.MatchingTokens.Contains(token)) continue;
                int df = documentFrequency.GetValueOrDefault(token);
                if (df < 1 || df > maxDf) continue;
                weight += TokenIdf(token);
            }
            if (weight > 0) probes.Add((unit, weight));
        }
        return probes.OrderByDescending(p => p.Weight).ThenBy(p => p.Unit.Timestamp)
            .ThenBy(p => p.Unit.Key, StringComparer.Ordinal)
            .Take(16).Select(p => SemanticWireIdForUnit(p.Unit)).ToArray();
    }

    private static SemanticRecallCandidate SemanticHitForSpark(SemanticGeometrySpark spark, LogicalDialogueUnit unit)
    {
        // The original eight-argument internal constructor remains the boundary
        // for harnesses; geometry never needs character offsets for selection.
        string kind = unit.IsMessage ? "message" : "utterance";
        string version = unit.IsMessage ? unit.VersionFingerprint : "";
        return new SemanticRecallCandidate(spark.EventId, spark.TextHash, spark.Score, 0, 0, kind, version, 0);
    }

    // M-2: the middle node of a two-hop bridge lives in the whitened, unprefixed
    // q_doc space and often never enters the q_query direct Top-K. Validating it
    // against result.Candidates therefore vetoed valuable bridges. Validate the
    // active local derived record directly instead.
    private bool SemanticBridgeViaPermitted(SemanticRecallResult result, SemanticGeometrySpark spark)
    {
        if (spark.BridgeVia.Length == 0) return true;
        SemanticWireCodec.DecodeBridgeVia(spark.BridgeVia, out string viaKind, out string cleanId);
        // generated_reply is never an acceptable bridge node.
        if (viaKind == "generated_reply" || cleanId.Length == 0
            || spark.BridgeVia.StartsWith("derived/generated_reply/", StringComparison.Ordinal))
            return false;
        string expectedHash = spark.BridgeViaHash;
        if (expectedHash.Length == 0)
        {
            // Legacy payload without bridge_via_hash: fall back to the candidate
            // copy only for identity, never as the source of permission.
            var legacy = result.Candidates.FirstOrDefault(c => c.EventId == cleanId || c.EventId == spark.BridgeVia);
            if (legacy == null || legacy.Kind is not ("b1" or "note" or "lesson")) return false;
            expectedHash = legacy.TextHash;
            if (viaKind.Length == 0) viaKind = legacy.Kind;
        }
        string[] kindsToTry = viaKind is "b1" or "note" or "lesson" ? new[] { viaKind } : new[] { "b1", "note", "lesson" };
        foreach (string kind in kindsToTry)
        {
            if (kind == "b1")
            {
                var doc = LoadB1(force: false);
                var entry = doc?.Entries.FirstOrDefault(e => e.Version == cleanId);
                if (doc != null && entry != null
                    && doc.Current.GetValueOrDefault(entry.LogicalId) == entry.Version
                    && !IsB1ManagedInactiveState(B1ManagementState(doc, entry.Version))
                    && entry.Nature is "inference" or "legacy_unspecified"
                    && SemanticTextHash(B1DiscoveryText(entry)) == expectedHash
                    && !IsExperienceSuppressed(entry.Version)
                    && IsMentionTextAllowed(entry.Text + " " + entry.Scope)
                    && !SuppressedTextMatches(entry.Text))
                {
                    return true;
                }
                continue;
            }
            var pool = kind == "note" ? _reflectionNotes : _reflectionLessons;
            var activeTurn = pool.FirstOrDefault(t => t.EventId == cleanId && IsActiveLifecycle(t));
            var candidate = activeTurn ?? B1Turn(kind, cleanId);
            if (candidate != null
                && (string.IsNullOrEmpty(candidate.LifecycleState) || IsActiveLifecycle(candidate))
                && SemanticTextHash(candidate.Text) == expectedHash)
            {
                return !IsExperienceSuppressed(candidate.EventId)
                    && IsMentionAllowed(candidate) && IsNativeSupplyAllowed(candidate);
            }
        }
        return false;
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
        trace.SparkEventIds.Clear(); trace.ProbeEventIds.Clear(); trace.SparkTokens.Clear();
        trace.LensShiftB1Version=""; trace.SparkCandidateCount=0;
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
        var catalog = GetExperienceLogicalCatalog(scoped: false, dialogueOnly: false);
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
        var candidates = new List<(SemanticRecallCandidate Hit, LogicalDialogueUnit Unit, bool IsSpark)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        bool explicitLookup = identifiers.Length > 0 || quotes.Length > 0 || scope != null || speaker.Length > 0;
        bool pureFillerQuery = !explicitLookup && MemorySemantics.IsLowInfoGreetingOrFiller(query);
        bool geometryActive = result.Geometry != null && result.RelevancePlan == null;
        bool geometryHighOrderEmpty = result.OrthogonalSparks.Count == 0 && result.WormholeSparks.Count == 0
            && result.BridgeSparks.Count == 0 && result.SceneSparks.Count == 0;
        if (geometryActive && !explicitLookup && result.SalienceGated && geometryHighOrderEmpty)
        {
            trace.Status = "available_salience_gated";
            trace.Snapshots = Array.Empty<MemoryTurn>();
            return Array.Empty<MemoryTurn>();
        }
        long selectionTurn = System.Threading.Interlocked.Increment(ref _semanticSelectionTurns);
        var excludedUnits = new List<LogicalDialogueUnit>();
        var excludedUnitKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (string id in excluded)
        {
            if (catalog.FindByEvent(id) is { } exUnit && !string.IsNullOrWhiteSpace(exUnit.Text) && excludedUnitKeys.Add(exUnit.Key))
                excludedUnits.Add(exUnit);
        }
        // RRF fuses the direct semantic rank with lexical evidence. DPP remains
        // the primary order for its selected subset so collinear paraphrase
        // clones cannot crowd out an orthogonal memory; RRF orders the remainder.
        var queryTokenSet = MemorySemantics.MatchingTokens(query);
        var lexicalRows = new List<(SemanticRecallCandidate Hit, LogicalDialogueUnit Unit, int Overlap)>();
        foreach (var hit in result.Candidates.Where(c => c.Kind is "utterance" or "message"))
        {
            var lexicalUnit = catalog.ResolveCandidate(hit);
            if (lexicalUnit == null) continue;
            int overlap = 0;
            foreach (string token in queryTokenSet)
                if (lexicalUnit.MatchingTokens.Contains(token)) overlap++;
            lexicalRows.Add((hit, lexicalUnit, overlap));
        }
        var lexicalRank = lexicalRows
            .OrderByDescending(r => r.Overlap).ThenByDescending(r => r.Hit.Score)
            .ThenBy(r => r.Hit.EventId, StringComparer.Ordinal)
            .Select((r, index) => (r.Hit.EventId, Rank: index + 1))
            .GroupBy(r => r.EventId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Rank, StringComparer.Ordinal);
        var fusedRank = result.Candidates.ToDictionary(c => c.EventId, c =>
        {
            double semantic = 1d / (60 + Math.Max(1, c.Rank));
            double lexical = lexicalRank.TryGetValue(c.EventId, out int rank) && rank < int.MaxValue
                ? 1d / (60 + rank) : 0d;
            int lexicalAt=lexicalRows.FindIndex(r=>r.Hit.EventId==c.EventId);
            int overlap=lexicalAt>=0?lexicalRows[lexicalAt].Overlap:0;
            if (overlap == 0 && geometryActive && !explicitLookup && c.Rank is > 0 and <= 15) lexical += 0.0005d;
            return semantic + lexical;
        }, StringComparer.Ordinal);
        var dppRank = (result.Geometry?.DppSelectedIds ?? Array.Empty<string>())
            .Select((id, index) => (id, index)).ToDictionary(p => p.id, p => p.index, StringComparer.Ordinal);
        // H-1: explicit lookup disables Python geometry in BridgeController,
        // so RRF must NOT require geometryActive. It only relies on semantic
        // candidate ranks + local lexical ranks.
        IEnumerable<SemanticRecallCandidate> orderedCandidates;
        if (explicitLookup && fusedRank.Count > 0)
            orderedCandidates = result.Candidates
                .OrderByDescending(c => fusedRank.GetValueOrDefault(c.EventId))
                .ThenByDescending(c => c.Score);
        else if (geometryActive && !explicitLookup && dppRank.Count > 0)
            orderedCandidates = result.Candidates
                .Select(c => (Hit:c, InDpp:dppRank.ContainsKey(c.EventId), Rank:dppRank.GetValueOrDefault(c.EventId, int.MaxValue)))
                .OrderByDescending(r => r.InDpp).ThenBy(r => r.Rank)
                .ThenByDescending(r => fusedRank.GetValueOrDefault(r.Hit.EventId))
                .Select(r => r.Hit);
        else
            orderedCandidates = result.Candidates.OrderByDescending(c => c.Score);
        var perSessionDirect = new Dictionary<string, int>(StringComparer.Ordinal);
        var extraDirectPool = new List<(SemanticRecallCandidate Hit, LogicalDialogueUnit Unit)>();
        var midBandPool = new List<(SemanticRecallCandidate Hit, LogicalDialogueUnit Unit)>();
        bool mentionRecallPolicyActive = _mentionReadPolicy.Value?.Rules.Any(r => r.RecallAllowed) == true;
        foreach (var candidate in orderedCandidates)
        {
            if (candidate.Kind is not ("utterance" or "message")) continue;
            if (result.RelevancePlan == null && candidate.Score < 0.33)
            {
                trace.Rejections.Add(candidate.EventId + ":low_semantic_score");
                continue;
            }
            bool isMidBand = result.RelevancePlan == null && candidate.Score < 0.45;
            if (isMidBand && (explicitLookup || pureFillerQuery))
            {
                trace.Rejections.Add(candidate.EventId + ":low_semantic_score");
                continue;
            }
            if (isMidBand && midBandPool.Count >= 4) continue;
            var unit = catalog.ResolveCandidate(candidate);
            string reason = unit == null ? "unit_source_or_fingerprint_changed"
                : !Eligible(unit) ? "permission_or_scope"
                : unit.Members.All(t => excluded.Contains(t.EventId)) ? "already_supplied" : "";
            if (reason.Length > 0) { trace.Rejections.Add(candidate.EventId + ":" + reason); continue; }
            if (!seen.Add(unit!.Key)) continue;
            bool requiredOrExplicit = Required(unit)
                || (mentionRecallPolicyActive && unit.Members.Any(IsExplicitMentionRecall));
            if (result.RelevancePlan == null && !explicitLookup && !requiredOrExplicit)
            {
                if (unit.IsLowInfoFiller || (isMidBand && unit.Text.Trim().Length < 8))
                {
                    trace.Rejections.Add(candidate.EventId + ":low_info_filler");
                    continue;
                }
                if (excludedUnits.Any(ex => MemorySemantics.AreNearDuplicateUnits(ex, unit))
                    || candidates.Any(c => MemorySemantics.AreNearDuplicateUnits(c.Unit, unit)))
                {
                    trace.Rejections.Add(candidate.EventId + ":near_duplicate");
                    continue;
                }
            }
            if (isMidBand)
            {
                midBandPool.Add((candidate, unit));
                if (candidates.Count >= 6 && midBandPool.Count >= 4) break;
                continue;
            }
            if (result.RelevancePlan == null && !explicitLookup && !requiredOrExplicit
                && perSessionDirect.GetValueOrDefault(unit.SessionId) >= 2)
            {
                trace.Rejections.Add(candidate.EventId + ":session_saturated");
                continue;
            }
            if (result.RelevancePlan == null && !explicitLookup && !pureFillerQuery && candidates.Count >= 6)
            {
                if (extraDirectPool.Count < 2) extraDirectPool.Add((candidate, unit));
                perSessionDirect[unit.SessionId] = perSessionDirect.GetValueOrDefault(unit.SessionId) + 1;
                continue;
            }
            candidates.Add((candidate, unit, false));
            perSessionDirect[unit.SessionId] = perSessionDirect.GetValueOrDefault(unit.SessionId) + 1;
            if (result.RelevancePlan == null && (explicitLookup || pureFillerQuery) && candidates.Count == 8) break;
        }
        foreach (var extra in extraDirectPool)
        {
            if (candidates.Count >= 7) break;
            if (!candidates.Any(c => MemorySemantics.AreNearDuplicateUnits(c.Unit, extra.Unit)))
                candidates.Add((extra.Hit, extra.Unit, false));
        }
        var sparkEventIds = new HashSet<string>(StringComparer.Ordinal);
        var geometrySparkChoices = new List<(SemanticGeometrySpark Spark, LogicalDialogueUnit Unit, double Weight)>();
        if (geometryActive && !explicitLookup && !pureFillerQuery && candidates.Count < 8)
        {
            var recentCut = DateTimeOffset.UtcNow.AddHours(-12);
            foreach (var spark in result.OrthogonalSparks.Concat(result.WormholeSparks)
                .Concat(result.BridgeSparks).Concat(result.SceneSparks))
            {
                LogicalDialogueUnit? unit;
                if (spark.EventId.StartsWith("message/", StringComparison.Ordinal))
                {
                    unit = catalog.FindByKey(spark.EventId);
                    if (unit == null || !unit.IsMessage || unit.TextHash != spark.TextHash) continue;
                }
                else
                {
                    var probe = new SemanticRecallCandidate(spark.EventId, spark.TextHash, spark.Score, 0, 0,
                        "utterance", "", 0);
                    unit = catalog.ResolveCandidate(probe);
                    if (unit == null || SemanticWireIdForUnit(unit) != spark.EventId || unit.TextHash != spark.TextHash)
                        continue;
                }
                if (unit.Timestamp >= recentCut || !Eligible(unit)
                    || unit.Members.All(t => excluded.Contains(t.EventId))
                    || candidates.Any(c => MemorySemantics.AreNearDuplicateUnits(c.Unit, unit))
                    || !SemanticBridgeViaPermitted(result, spark)) continue;
                double weight = Math.Max(Math.Abs(spark.ZScore), Math.Abs(spark.Score));
                lock (_sparkExposureLock)
                {
                    if (_recentSparkExposure.TryGetValue(unit.Key, out long exposedAt)
                        && selectionTurn - exposedAt < Math.Max(1, SemanticExposureCooldownTurns))
                        weight *= 0.35d;
                }
                bool sessionTail = catalog.Units.Where(u => u.SessionId == unit.SessionId)
                    .OrderBy(u => u.Timestamp).TakeLast(2).Any(u => u.Key == unit.Key);
                if (sessionTail && (unit.Text.Contains('？') || unit.Text.Contains('?')
                    || unit.Text.Contains("以后再说", StringComparison.Ordinal)
                    || unit.Text.Contains("下次再", StringComparison.Ordinal)))
                    weight *= 1.10d;
                geometrySparkChoices.Add((spark, unit, weight));
            }
            trace.SparkCandidateCount = geometrySparkChoices.Count;
            var chosen = geometrySparkChoices.OrderByDescending(c => c.Weight).FirstOrDefault();
            if (chosen.Unit != null)
            {
                var hit = SemanticHitForSpark(chosen.Spark, chosen.Unit);
                candidates.Add((hit, chosen.Unit, true));
                foreach (var member in chosen.Unit.Members)
                {
                    sparkEventIds.Add(member.EventId);
                    trace.SparkEventIds.Add(member.EventId);
                }
                foreach (string token in chosen.Unit.MatchingTokens) trace.SparkTokens.Add(token);
                lock (_sparkExposureLock) _recentSparkExposure[chosen.Unit.Key] = selectionTurn;
                SemanticWireCodec.DecodeBridgeVia(chosen.Spark.BridgeVia, out string chosenViaKind, out string chosenViaId);
                if (chosenViaKind == "b1" && chosenViaId.Length > 0)
                    trace.LensShiftB1Version = chosenViaId;
                else
                {
                    var via = result.Candidates.FirstOrDefault(c => c.EventId == (chosenViaId.Length > 0 ? chosenViaId : chosen.Spark.BridgeVia));
                    if (via?.Kind == "b1") trace.LensShiftB1Version = via.EventId;
                }
            }
        }
        if (result.RelevancePlan == null && !explicitLookup && !pureFillerQuery
            && candidates.Count < 8 && (!geometryActive || geometrySparkChoices.Count == 0) && midBandPool.Count > 0)
        {
            var directSessions = candidates.Select(c => c.Unit.SessionId).ToHashSet(StringComparer.Ordinal);
            var sparkPick = midBandPool
                .Where(m => !candidates.Any(c => MemorySemantics.AreNearDuplicateUnits(c.Unit, m.Unit)))
                .OrderByDescending(m => !directSessions.Contains(m.Unit.SessionId) ? 1 : 0)
                .ThenByDescending(m => m.Hit.Score)
                .FirstOrDefault();
            if (sparkPick.Unit != null)
            {
                candidates.Add((sparkPick.Hit, sparkPick.Unit, true));
                foreach (var member in sparkPick.Unit.Members)
                {
                    sparkEventIds.Add(member.EventId);
                    trace.SparkEventIds.Add(member.EventId);
                }
                foreach (string token in sparkPick.Unit.MatchingTokens) trace.SparkTokens.Add(token);
                lock (_sparkExposureLock) _recentSparkExposure[sparkPick.Unit.Key] = selectionTurn;
            }
        }
        if (result.RelevancePlan == null && candidates.Count < 8)
        {
            foreach (var extra in extraDirectPool)
            {
                if (candidates.Count >= 8) break;
                if (candidates.All(c => c.Unit.Key != extra.Unit.Key)
                    && !candidates.Any(c => MemorySemantics.AreNearDuplicateUnits(c.Unit, extra.Unit)))
                    candidates.Add((extra.Hit, extra.Unit, false));
            }
        }
        foreach (var dropped in midBandPool.Where(m => !candidates.Any(c => c.Unit.Key == m.Unit.Key)))
            trace.Rejections.Add(dropped.Hit.EventId + ":low_semantic_score");
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
            if (!candidate.IsSpark)
            {
                string hit = candidate.Hit.Kind == "utterance" ? candidate.Hit.EventId : candidate.Unit.AnchorEventId;
                direct.Add(hit); trace.Anchors.Add(hit);
            }
            else
            {
                trace.Anchors.Add(candidate.Hit.Kind == "utterance" ? candidate.Hit.EventId : candidate.Unit.AnchorEventId);
            }
            bool exact = (identifiers.Length > 0 || quotes.Length > 0 || scope?.From != null)
                && identifiers.All(id => candidate.Unit.Runs.Any(r => MemorySemantics.ContainsRecallIdentifier(r.Text, id)))
                && quotes.All(candidate.Unit.ContainsExact);
            if(result.RelevancePlan==null || result.RelevancePlan.Admits(candidate.Hit.EventId))
                foreach (var member in candidate.Unit.Members)
                    trace.DirectRanks[member.EventId] = new(exact ? 0 : 1, candidates.FindIndex(c => c.Unit.Key == candidate.Unit.Key) + 1);
        }
        // Same-owner completion is already contained in each unit. Sequence +/-1
        // is not a second way to guess an experience boundary.
        var probeEventIds = new List<string>();
        if (geometryActive && !explicitLookup && result.ProbeWireIds.Count > 0)
        {
            foreach (string wireId in result.ProbeWireIds)
            {
                if (!result.DocQuantiles.TryGetValue(wireId, out double quantile) || quantile > 0.35d) continue;
                var unit = catalog.Units.FirstOrDefault(u => SemanticWireIdForUnit(u) == wireId);
                if (unit == null || selected.ContainsKey(unit.Key) || !Eligible(unit)
                    || unit.Members.All(t => excluded.Contains(t.EventId))
                    || selected.Values.Any(s => MemorySemantics.AreNearDuplicateUnits(s, unit))) continue;
                if (selected.Keys.Count + 1 > 24) break;
                selected.TryAdd(unit.Key, unit);
                probeEventIds.AddRange(unit.Members.Select(t => t.EventId));
                trace.ProbeEventIds.AddRange(unit.Members.Select(t => t.EventId));
                break;
            }
        }
        if (result.RelevancePlan == null && !explicitLookup && direct.Count > 0 && selected.Keys.Count < 24)
        {
            // H-3 & WP-4: walk direct anchors in selection priority, not oldest-first,
            // and lift the single-bubble restriction so multi-bubble assistant replies
            // backfill their triggering user question (and vice versa).
            var directAnchorUnitKeys = candidates.Where(c => !c.IsSpark).Select(c => c.Unit.Key).ToHashSet(StringComparer.Ordinal);
            bool backfilled = false;
            foreach (var anchor in selected.Values.ToArray())
            {
                if (!directAnchorUnitKeys.Contains(anchor.Key)) continue;
                string anchorSourceUserId = anchor.Role == "assistant"
                    ? anchor.Members.Select(m => m.SourceEventId).FirstOrDefault(id => id.Length > 0) ?? ""
                    : "";
                bool IsLinkedCounterpart(LogicalDialogueUnit u) =>
                    (anchor.Role == "assistant" && anchorSourceUserId.Length > 0 && u.Members.Any(m => m.EventId == anchorSourceUserId))
                    || (anchor.Role == "user" && u.Role == "assistant" && u.Members.Any(m => m.SourceEventId == anchor.AnchorEventId));
                var counterparts = catalog.Units
                    .Where(u => u.SessionId == anchor.SessionId
                        && u.Role != anchor.Role
                        && !selected.ContainsKey(u.Key)
                        && Eligible(u)
                        && (IsLinkedCounterpart(u) || Math.Abs((u.Timestamp - anchor.Timestamp).TotalMinutes) <= 5d))
                    .OrderBy(u => IsLinkedCounterpart(u) ? 0 : 1)
                    .ThenBy(u => Math.Abs((u.Timestamp - anchor.Timestamp).TotalMinutes))
                    .ThenBy(u => u.Timestamp);

                foreach (var counterpart in counterparts)
                {
                    DateTimeOffset first = counterpart.Timestamp < anchor.Timestamp ? counterpart.Timestamp : anchor.Timestamp;
                    DateTimeOffset last = counterpart.Timestamp < anchor.Timestamp ? anchor.Timestamp : counterpart.Timestamp;
                    bool interposed = !IsLinkedCounterpart(counterpart)
                        && catalog.Units.Any(u => u.SessionId == anchor.SessionId
                            && u.Key != anchor.Key && u.Key != counterpart.Key
                            && u.Timestamp >= first && u.Timestamp <= last);
                    if (interposed || selected.Keys.Count + 1 > 24) continue;
                    // Same 24-unit capacity; never enters trace.Anchors.
                    selected.TryAdd(counterpart.Key, counterpart);
                    backfilled = true;
                    break;
                }
                if (backfilled) break;
            }
        }
        var selectedUnits = selected.Values.OrderBy(u => u.Timestamp).ThenBy(u => u.Key, StringComparer.Ordinal).ToArray();
        var supplied = new LogicalDialogueSelection(selectedUnits, direct, WithNaturalMemoryContext, sparkEventIds, probeEventIds);
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
