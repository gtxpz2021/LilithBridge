using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LilithBridge;

// Programme-owned identities of material actually supplied, not semantic attribution.
internal sealed record DaySourceDependency(string Kind, string EventId, string Fingerprint);
internal sealed record DayTaskSnapshot(string RequestId, string Source, IReadOnlyList<MemoryTurn> Evidence)
{
    public IReadOnlyList<DaySourceDependency> Dependencies { get; init; } = Array.Empty<DaySourceDependency>();
    public IReadOnlyList<MemoryTurn> RebuildTargets { get; init; } = Array.Empty<MemoryTurn>();
}
internal sealed class DayArtifactSupport
{
    public string[] SourceEventIds { get; set; } = Array.Empty<string>();
    public string[] SourceNoteIds { get; set; } = Array.Empty<string>();
    public string[] SourceDayIds { get; set; } = Array.Empty<string>();
}
internal sealed class DayTaskResult
{
    public string Consolidation { get; set; } = "";
    public string Dream { get; set; } = "";
    public DayArtifactSupport? ConsolidationSupport { get; set; }
    public DayArtifactSupport? DreamSupport { get; set; }
}

internal sealed partial class MemoryStore
{
    public const string DayConsolidationKind = "day.consolidation";
    public const string DayDreamKind = "day.dream";
    internal const string DayReceiptKind = "day.receipt";

    public IReadOnlyList<MemoryTurn> GetDayArtifacts(int max)
    {
        lock (_experienceLock)
        {
            var materialPermitted = CreateMaterialPermissionEvaluator();
            return _dreams.Where(t => t.Kind is DayConsolidationKind or DayDreamKind)
                .Where(t => IsMentionAllowed(t) && IsActiveLifecycle(t) && IsDayArtifactCurrent(t, materialPermitted)
                    && IsCurrentInterpretationBasis(t))
                .TakeLast(Math.Max(0, max)).Select(t => t.Copy()).ToList();
        }
    }

    // Membership in a successful window, not response time, defines coverage.
    private HashSet<string> DayCoveredIds() => _dreams.Where(t => t.Kind == DayReceiptKind)
        .SelectMany(t => t.DayCoveredEventIds).ToHashSet(StringComparer.Ordinal);

    public DayTaskSnapshot? CaptureDayTask()
    {
        lock (_experienceLock)
        {
            if (!_longTermEnabled || !_experienceArchiveEnabled) return null;
            var raw = ReadExperiences().ToDictionary(t => t.EventId, StringComparer.Ordinal);
            var reviewed = _dreams.Where(t => t.Kind == DayReceiptKind).SelectMany(t => t.SourceDayIds).ToHashSet(StringComparer.Ordinal);
            // A legacy mixed footprint is historical, not a semantic support map.
            // Revisit its original covered window without requiring four new events.
            var legacy = _dreams.Where(t => t.Kind is DayConsolidationKind or DayDreamKind)
                .Where(t => t.DayProvenanceVersion < 2 && IsActiveLifecycle(t) && IsMentionAllowed(t) && !reviewed.Contains(t.EventId))
                .GroupBy(t => t.OperationId).FirstOrDefault(g => g.Key.Length > 0
                    && _dreams.Any(r => r.Kind == DayReceiptKind && r.OperationId == g.Key
                        && r.DayCoveredEventIds.Length > 0 && r.DayCoveredEventIds.All(raw.ContainsKey)));
            var targets = legacy?.Select(t => t.Copy()).ToList() ?? new List<MemoryTurn>();
            HashSet<string> covered = DayCoveredIds();
            List<MemoryTurn> evidence;
            if (targets.Count > 0)
            {
                var receipt = _dreams.First(r => r.Kind == DayReceiptKind && r.OperationId == targets[0].OperationId);
                // Corrections are necessary inputs of the replacement, and the corrected
                // original keeps its own source identity beside them: a product that cites the
                // corrected statement must be able to declare the pair inside this bounded batch.
                var corrections = CorrectionInputIds(targets).Where(raw.ContainsKey)
                    .Distinct(StringComparer.Ordinal).ToList();
                var correctedOriginals = _autonomous
                    .Where(c => c.RequestKind == "correct" && IsAuthoritativeStoredCorrection(c) && c.SourceEventId.Length > 0 && corrections.Contains(c.SourceEventId))
                    .SelectMany(c => c.SourceEventIds.Where(id => id != c.SourceEventId && raw.ContainsKey(id)))
                    .Distinct(StringComparer.Ordinal).ToList();
                var necessary = corrections.Concat(correctedOriginals)
                    .Where(id => raw.ContainsKey(id)).Distinct(StringComparer.Ordinal).ToList();
                if (necessary.Count >= 48)
                {
                    // One bounded request cannot carry this reorganization's necessary material.
                    // Keep the legacy target pending with no receipt or lifecycle change, and let
                    // an unrelated ordinary window proceed instead of starving the whole entry.
                    NativeEventLog.Trace("memory.day_window", "mod", "system",
                        "legacy target exceeds one bounded batch and stays pending",
                        "necessary=" + necessary.Count + ";targets=" + targets.Count);
                    targets = new List<MemoryTurn>();
                    evidence = raw.Values.Where(t => !covered.Contains(t.EventId)).Take(48).Select(t => t.Copy()).ToList();
                }
                else
                {
                    // Only material supplied by batches after the original receipt counts as
                    // processed. The original covered window is history, not a claim that the
                    // newer reorganization already handled those items.
                    var processed = _dreams.Where(t => t.Kind == DayReceiptKind
                            && t.OperationId != receipt.OperationId && t.DayCoveredEventIds.Length > 0)
                        .SelectMany(t => t.DayCoveredEventIds).ToHashSet(StringComparer.Ordinal);
                    var remaining = receipt.DayCoveredEventIds.Where(raw.ContainsKey)
                        .Where(id => !necessary.Contains(id) && !processed.Contains(id)).ToList();
                    var chosenOld = remaining.Take(48 - necessary.Count);
                    int omitted = remaining.Count - chosenOld.Count();
                    if (omitted > 0)
                        NativeEventLog.Trace("memory.day_window", "mod", "system",
                            "bounded legacy batch leaves pending remainder", "omitted=" + omitted
                            + ";supplied=" + (necessary.Count + remaining.Count - omitted) + ";targets=" + targets.Count);
                    evidence = chosenOld.Concat(necessary).Select(id => raw[id].Copy()).ToList();
                }
            }
            else
                evidence = raw.Values.Where(t => !covered.Contains(t.EventId)).Take(48).Select(t => t.Copy()).ToList();
            if (evidence.Count == 0 || (targets.Count == 0 && evidence.Count < 4)) return null;
            MemoryTurn? previous = GetDayArtifacts(int.MaxValue).LastOrDefault(t => t.Kind == DayConsolidationKind);
            var notes = GetReflectionNotes(4).ToList();
            var dependencies = evidence.Select(t => DayDependency("utterance", t)).ToList();
            dependencies.AddRange(notes.Select(t => DayDependency("note", t)));
            if (previous != null) dependencies.Add(DayDependency("day", previous));
            if (!TryResolveDayDependencies(dependencies, out string[] rawSources)) return null;
            foreach (string id in rawSources)
            {
                var dependency = DayDependency("utterance", raw[id]);
                if (!dependencies.Contains(dependency)) dependencies.Add(dependency);
            }
            string source = JsonSerializer.Serialize(new
            {
                now = DateTimeOffset.UtcNow, window_start = evidence.Min(t => t.Timestamp), window_end = evidence.Max(t => t.Timestamp),
                utterances = evidence.Select(t => new { event_id = t.EventId, speaker = t.Role, origin = t.Origin, text = t.Text,
                    at = t.Timestamp, reported_speech = UtteranceAttribution.Reported(t.Role,t.Text) }),
                previous_consolidation = previous == null ? null : new { day_id = previous.EventId, revision = previous.Revision,
                    text = previous.Text, at = previous.Timestamp, source_context = DescribeSourceAttribution(previous) },
                recent_thoughts = notes.Select(t => new { note_id = t.EventId, revision = t.Revision, text = t.Text,
                    at = t.Timestamp, source_context = DescribeSourceAttribution(t) }),
                revision_targets = targets.Select(t => new { day_id = t.EventId, kind = t.Kind, text = t.Text,
                    status = "historical_unknown_support; target only, not current evidence" })
            });
            return new(Guid.NewGuid().ToString("N"), source, evidence.AsReadOnly())
                { Dependencies = dependencies.AsReadOnly(), RebuildTargets = targets.AsReadOnly() };
        }
    }

    public bool ApplyDayTask(DayTaskResult result, DayTaskSnapshot request, out string reason)
    {
        lock (_experienceLock)
        {
            if (result == null) { reason = "no_result"; return false; }
            if (_dreams.Any(t => t.OperationId == request.RequestId)) { reason = "already_applied"; return false; }
            string consolidation = Clean(result.Consolidation), dream = Clean(result.Dream);
            if (consolidation.Length > 400 || dream.Length > 300) { reason = "invalid_length"; return false; }
            if (request.Evidence.Count == 0 || request.Evidence.Any(t => !request.Dependencies.Any(d => d.Kind == "utterance" && d.EventId == t.EventId)))
                { reason = "no_evidence"; return false; }
            if (!TryResolveDayDependencies(request.Dependencies.Where(d => d.Kind == "utterance"
                && request.Evidence.Any(t => t.EventId == d.EventId)), out _))
                { reason = "source_changed"; return false; }
            // Permission/deletion applies even to context only; ordinary revision
            // of unselected historical material is not a semantic conflict.
            var rawNow = ReadExperiences().ToDictionary(t => t.EventId, StringComparer.Ordinal);
            foreach (var dep in request.Dependencies)
            {
                MemoryTurn? t = dep.Kind == "utterance" ? rawNow.GetValueOrDefault(dep.EventId)
                    : (dep.Kind == "note" ? _reflectionNotes : _dreams).FirstOrDefault(t => t.EventId == dep.EventId);
                if (t == null || !IsMentionAllowed(t)) { reason = "source_changed"; return false; }
            }
            // The same permission/existence view the readers use. A withdrawal that happened
            // after capture must reject the commit too, including material that was only read
            // through another record's footprint.
            var materialPermitted = CreateMaterialPermissionEvaluator();
            if (!request.Dependencies.All(materialPermitted)) { reason = "source_changed"; return false; }
            var targets = request.RebuildTargets.Select(t => _dreams.FirstOrDefault(d => d.EventId == t.EventId)).ToList();
            if (targets.Where((t,i) => t == null || !IsMentionAllowed(t) || !IsActiveLifecycle(t) || SourceVersion("day",t) != SourceVersion("day",request.RebuildTargets[i])).Any())
                { reason = "source_changed"; return false; }
            HashSet<string> covered = DayCoveredIds();
            if (targets.Count == 0 && request.Evidence.Any(t => covered.Contains(t.EventId)))
                { reason = "window_already_processed"; return false; }
            // A legacy reorganization is complete only when every item of its original window
            // has actually been supplied, either in this batch or in an earlier bounded batch.
            // The original receipt is history, not evidence that the newer batches covered it.
            bool completesLegacy = true;
            if (targets.Count > 0)
            {
                var legacyReceipt = _dreams.FirstOrDefault(r => r.Kind == DayReceiptKind
                    && r.OperationId == request.RebuildTargets[0].OperationId);
                if (legacyReceipt != null && legacyReceipt.DayCoveredEventIds.Length > 0)
                {
                    HashSet<string> supplied = request.Evidence.Select(t => t.EventId).ToHashSet(StringComparer.Ordinal);
                    var earlierBatches = _dreams.Where(t => t.Kind == DayReceiptKind
                            && t.OperationId != legacyReceipt.OperationId && t.DayCoveredEventIds.Length > 0)
                        .SelectMany(t => t.DayCoveredEventIds);
                    completesLegacy = legacyReceipt.DayCoveredEventIds.All(id => supplied.Contains(id) || earlierBatches.Contains(id));
                }
            }
            var chainedParents = new List<MemoryTurn>();
            var created = new List<MemoryTurn>();
            bool Add(string text, string kind, DayArtifactSupport? support)
            {
                if (text.Length == 0) return true;
                if (!TrySelectDaySupport(support, request, out var selected, out var sources)) return false;
                var parent = targets.FirstOrDefault(t => t!.Kind == kind);
                // Earlier bounded batches may already have produced replacements for the same
                // preserved target. Continue the recorded version chain to its newest product
                // (retiring it with this commit) instead of writing a sibling revision; that also
                // holds when the direct child was already retired by the previous batch.
                if (parent != null)
                {
                    MemoryTurn latest = LatestChainProduct(parent, kind);
                    if (latest != parent)
                    {
                        if (IsActiveLifecycle(latest)) chainedParents.Add(latest);
                        parent = latest;
                    }
                }
                created.Add(new MemoryTurn {
                    EventId = Guid.NewGuid().ToString("N"), RecallId = parent?.RecallId ?? Guid.NewGuid().ToString("N"),
                    Revision = (parent?.Revision ?? 0) + 1, SourceEventId = parent?.EventId ?? "",
                    OperationId = request.RequestId, SourceEventIds = sources,
                    SourceNoteIds = selected.Where(d => d.Kind == "note").Select(d => d.EventId).ToArray(),
                    SourceDayIds = selected.Where(d => d.Kind == "day").Select(d => d.EventId).ToArray(),
                    SourceDependencies = selected, ContextDependencies = request.Dependencies.ToArray(), DayProvenanceVersion = 2,
                    Role = "assistant", Kind = kind, Tier = "archival", Category = "memory", Origin = "reflection",
                    Source = "model_day_task", Text = kind == DayDreamKind ? "梦/想象，不是事实或新事件：" + text : text,
                    Validity = "inferred", LifecycleState = "active", SessionId = _sessionId });
                return true;
            }
            if (!Add(consolidation, DayConsolidationKind, result.ConsolidationSupport)
                || !Add(dream, DayDreamKind, result.DreamSupport)) { reason = "invalid_day_support"; return false; }
            // The new record, the version switch of its predecessor and the completion receipt
            // are one transaction. The read gate therefore evaluates the state this commit
            // actually leaves: a submitted product that declares support on the version this
            // same commit retires is rejected before anything is written, instead of being
            // saved and only failing on the next read.
            var replaced = new List<MemoryTurn>(chainedParents);
            if (completesLegacy)
                foreach (MemoryTurn? target in targets)
                    if (target != null && created.Any(c => c.Kind == target.Kind)) replaced.Add(target);
            var replacedStates = replaced.Select(t => t.LifecycleState).ToList();
            foreach (var t in replaced) t.LifecycleState = "superseded";
            int gateIndex = _dreams.Count;
            _dreams.AddRange(created);
            bool productsCurrent;
            try
            {
                var productPermitted = CreateMaterialPermissionEvaluator();
                productsCurrent = created.Where(t => t.Kind is DayConsolidationKind or DayDreamKind)
                    .All(t => IsDayArtifactCurrent(t, productPermitted));
            }
            finally { _dreams.RemoveRange(gateIndex, created.Count); }
            if (!productsCurrent)
            {
                for (int i = 0; i < replaced.Count; i++) replaced[i].LifecycleState = replacedStates[i];
                reason = "replacement_not_current"; return false;
            }
            // Valid empty answers consume the supplied scope without inventing a current
            // artifact. A partial batch records only what it actually processed and leaves the
            // target pending; only a batch that covered the whole window completes the target.
            created.Add(new MemoryTurn { EventId = Guid.NewGuid().ToString("N"), OperationId = request.RequestId,
                Kind = DayReceiptKind, Origin = "system", Validity = "operational", Text = "", SessionId = _sessionId,
                SourceDayIds = completesLegacy ? targets.Select(t => t!.EventId).ToArray() : Array.Empty<string>(),
                DayCoveredEventIds = request.Evidence.Select(t => t.EventId).ToArray() });
            _dreams.AddRange(created);
            if (!SaveList("v4-native-dreams.json", _dreams))
            {
                foreach (MemoryTurn turn in created) _dreams.Remove(turn);
                for (int i = 0; i < replaced.Count; i++) replaced[i].LifecycleState = replacedStates[i];
                reason = "persistence_failed"; return false;
            }
            reason = created.Count == 1 ? "no_new_artifact" : "saved";
            return created.Count > 1;
        }
    }

    // Version parents are explicit record links, so a bounded batch can resume the chain even
    // after the previous batch retired the direct child. Only same-kind descendants are
    // followed: consolidation and dream remain separate products of the same legacy target.
    private MemoryTurn LatestChainProduct(MemoryTurn target, string kind)
    {
        MemoryTurn current = target;
        var visited = new HashSet<string>(StringComparer.Ordinal) { target.EventId };
        while (true)
        {
            MemoryTurn? next = _dreams.Where(t => t.Kind == kind && t.SourceEventId == current.EventId
                    && !visited.Contains(t.EventId))
                .OrderByDescending(t => t.Revision).FirstOrDefault();
            if (next == null) return current;
            visited.Add(next.EventId);
            current = next;
        }
    }

    private bool TrySelectDaySupport(DayArtifactSupport? support, DayTaskSnapshot request,
        out DaySourceDependency[] selected, out string[] sources)
    {
        selected = Array.Empty<DaySourceDependency>(); sources = Array.Empty<string>();
        if (support == null || support.SourceEventIds == null || support.SourceNoteIds == null || support.SourceDayIds == null) return false;
        var ids = support.SourceEventIds.Select(id => (Kind:"utterance",Id:id))
            .Concat(support.SourceNoteIds.Select(id => (Kind:"note",Id:id)))
            .Concat(support.SourceDayIds.Select(id => (Kind:"day",Id:id))).Distinct().ToList();
        if (ids.Count == 0) return false;
        var deps = new List<DaySourceDependency>();
        foreach (var id in ids)
        {
            var dep = request.Dependencies.FirstOrDefault(d => d.Kind == id.Kind && d.EventId == id.Id);
            if (dep == null) return false;
            deps.Add(dep);
        }
        if (!TryResolveDayDependencies(deps, out sources) || !DayCommitBasisCurrent(deps)) return false;
        // Raw transitive support must match the captured version, not today's newly read value.
        foreach (string id in sources)
        {
            var dep = request.Dependencies.FirstOrDefault(d => d.Kind == "utterance" && d.EventId == id);
            if (dep == null) return false;
            if (!deps.Contains(dep)) deps.Add(dep);
        }
        selected = deps.ToArray();
        if (!TryResolveDayDependencies(selected, out sources)) return false;
        // Validate the selected support against the correction relation before anything is
        // written: an active correction of a declared raw makes that interpretation current
        // only when the correcting input is supplied and selected with it.
        string[] resolvedSources = sources;
        return !resolvedSources.Any(id => _autonomous.Any(c => c.RequestKind == "correct" && IsAuthoritativeStoredCorrection(c)
            && IsActiveLifecycle(c) && c.SourceEventId.Length > 0
            && c.SourceEventIds.Contains(id) && !resolvedSources.Contains(c.SourceEventId)));
    }

    private DaySourceDependency DayDependency(string kind, MemoryTurn t) => new(kind, t.EventId,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            t.EventId, t.Revision, t.Text, t.Role, t.Origin, t.Kind, t.DeliveryState,
            t.SourceEventId, t.Timestamp, t.SessionId, t.Sequence,
            t.SourceEventIds, t.SourceDayIds, t.SourceNoteIds, t.SourceDependencies, t.ContextDependencies, t.DayProvenanceVersion,
            corrections = kind == "utterance" ? _autonomous
                .Where(a => a.RequestKind == "correct" && IsAuthoritativeStoredCorrection(a) && a.SourceEventIds.Contains(t.EventId))
                .Select(a => new { a.EventId, a.Revision, a.Text }).OrderBy(a => a.EventId).ToArray() : null
        })))));

    private bool IsDayArtifactCurrent(MemoryTurn turn) =>
        IsDayArtifactCurrent(turn, CreateMaterialPermissionEvaluator());

    // Current supply needs both: valid declared support (the ordinary currentness rules) and
    // a permission/existence view of what the request actually read. A footprint that was
    // explicitly withdrawn or purged is not re-read through this product, while ordinary
    // revision of a footprint never retires an independent product.
    private bool IsDayArtifactCurrent(MemoryTurn turn, Func<DaySourceDependency, bool> materialPermitted) =>
        turn.DayProvenanceVersion == 2
        && turn.SourceDependencies.Length > 0
        && FootprintPermitted(turn, materialPermitted)
        && TryResolveDayDependencies(new[] { DayDependency("day", turn) }, out _, materialPermitted);

    // Exact source identities are checked again under the commit lock. Flattened
    // raw IDs let the existing delete owner purge every downstream day artifact.
    // Legacy incomplete artifacts remain on disk, but are not silently promoted
    // to complete provenance. Migration of real archives requires separate review.
    private bool TryResolveDayDependencies(IEnumerable<DaySourceDependency> dependencies, out string[] rawIds,
        Func<DaySourceDependency, bool>? materialPermitted = null)
    {
        materialPermitted ??= CreateMaterialPermissionEvaluator();
        var raw = ReadExperiences().ToDictionary(t => t.EventId, StringComparer.Ordinal);
        var notes = _reflectionNotes.GroupBy(t => t.EventId).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        var days = _dreams.Where(t => t.Kind is DayConsolidationKind or DayDreamKind)
            .GroupBy(t => t.EventId).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        var resolved = new HashSet<string>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var originals = new HashSet<string>(StringComparer.Ordinal);
        bool Visit(DaySourceDependency dependency)
        {
            string key = dependency.Kind + ":" + dependency.EventId;
            var index = dependency.Kind == "utterance" ? raw : dependency.Kind == "note" ? notes : days;
            if (dependency.Kind is not ("utterance" or "note" or "day")
                || !index.TryGetValue(dependency.EventId, out MemoryTurn? source)
                || DayDependency(dependency.Kind, source) != dependency) return false;
            if (resolved.Contains(key)) return true;
            if (!visiting.Add(key)) return false;
            bool valid;
            if (dependency.Kind == "utterance")
            {
                valid = source.DeliveryState is "received" or "delivered";
                if (valid) originals.Add(source.EventId);
            }
            else
            {
                // Only explicitly declared support is traversed. Retired support
                // makes this interpretation historical, without rewriting it or
                // freezing independent products that only saw it as context.
                valid = (IsActiveLifecycle(source) || IsSettledLifecycle(source)
                    )
                    && source.Origin == "reflection" && source.SourceEventIds.Length > 0
                    && !HasUnincorporatedCorrection(source)
                    && materialPermitted(dependency);
                if (valid && dependency.Kind == "day")
                    valid = source.DayProvenanceVersion == 2 && source.SourceDependencies.Length > 0
                        && FootprintPermitted(source, materialPermitted)
                        && source.SourceDependencies.All(Visit);
                if (valid)
                    valid = source.SourceEventIds.All(id => raw.TryGetValue(id, out var t)
                        && Visit(DayDependency("utterance", t)));
                if (valid && dependency.Kind == "note")
                    valid = source.SourceDayIds.All(id => days.TryGetValue(id, out var t)
                        && Visit(DayDependency("day", t)));
            }
            visiting.Remove(key);
            if (valid) resolved.Add(key);
            return valid;
        }
        bool success = dependencies.All(d => Visit(d));
        rawIds = success ? originals.OrderBy(id => id, StringComparer.Ordinal).ToArray() : Array.Empty<string>();
        return success && rawIds.Length > 0;
    }
}
