using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace LilithBridge;

// A read assessment, never a new lifecycle or a persisted judgement of truth.
internal sealed record MaterialSupplyAssessment(string EventId, string Kind, string Origin, int Revision,
    string Lifecycle, string Delivery, DateTimeOffset At, IReadOnlyList<string> Sources, bool Current, string Reason);

internal sealed partial class MemoryStore
{
    // SelectRelationshipReading and the request-time relationship decision live
    // in MemoryStore.MaterialSelection.cs. The old narrow explicit-question regex
    // used to be here; keeping a second heuristic in this file would let the two
    // consumers drift apart again.
    // Corrections are append-only operations. Raw history is preserved; an
    // interpretation that has not incorporated the correcting input is not current.
    private static bool SameSupport(IEnumerable<string> a, IEnumerable<string> b) =>
        a.ToHashSet(StringComparer.Ordinal).SetEquals(b);

    private bool HasUnincorporatedCorrection(MemoryTurn turn) => _autonomous.Any(c =>
        c.RequestKind == "correct" && IsAuthoritativeStoredCorrection(c) && IsActiveLifecycle(c) && c.SourceEventId.Length > 0
        && !turn.SourceEventIds.Contains(c.SourceEventId)
        && c.SourceEventIds.Any(id => id != c.SourceEventId && turn.SourceEventIds.Contains(id)));

    internal MaterialSupplyAssessment AssessMaterialSupply(MemoryTurn turn)
    {
        bool derived = turn.Kind.StartsWith("reflection.", StringComparison.Ordinal)
            || turn.Kind is DayConsolidationKind or DayDreamKind;
        string reason = !IsMentionAllowed(turn) ? "mention_restricted"
            : !IsActiveLifecycle(turn) ? "lifecycle_" + turn.LifecycleState
            : derived && HasUnincorporatedCorrection(turn) ? "source_corrected"
            : derived && !IsCurrentInterpretationBasis(turn) ? "basis_retired"
            : !derived && turn.DeliveryState is not ("received" or "delivered") ? "delivery_unconfirmed"
            : "current";
        return new(turn.EventId, turn.Kind, turn.Origin, turn.Revision, turn.LifecycleState,
            turn.DeliveryState, turn.Timestamp, turn.SourceEventIds, reason == "current", reason);
    }

    // Follow only declared semantic support, never temporal adjacency, version
    // parents (SourceEventId), or all descendants of a raw event. A replacement
    // is free to cite fresh current support. Mixed legacy artifacts are withheld
    // as a unit until revised; no lifecycle or archive content is changed.
    private bool IsCurrentInterpretationBasis(MemoryTurn turn) => CreateCurrentBasisEvaluator()(turn);

    // One read-only graph per selection, not an archive scan for every candidate.
    private Func<MemoryTurn, bool> CreateCurrentBasisEvaluator(HashSet<string>? rawIds = null)
    {
        var raw = rawIds ?? ReadExperiences().Select(t => t.EventId).ToHashSet(StringComparer.Ordinal);
        var notes = _reflectionNotes.GroupBy(t => t.EventId).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        var days = _dreams.GroupBy(t => t.EventId).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var memo = new Dictionary<string, bool>(StringComparer.Ordinal);
        var materialPermitted = CreateMaterialPermissionEvaluator();
        bool Visit(MemoryTurn t)
        {
            // A day product is current only under the rule its own consumer applies:
            // declared support, the recorded read footprint and the per-source
            // version fingerprints must all still resolve. This shared view used to
            // re-derive a local version of that question, so a thought read through
            // a day could drift from the day consumer's answer; now it asks the day
            // supplier itself, keeping one definition of "this day is current".
            if ((t.Kind is DayConsolidationKind or DayDreamKind)
                && !IsDayArtifactCurrent(t, materialPermitted)) return false;
            if (!(IsActiveLifecycle(t) || IsSettledLifecycle(t)) || HasUnincorporatedCorrection(t)
                || !IsMentionAllowed(t) || t.SourceEventIds.Length == 0 || !t.SourceEventIds.All(raw.Contains)) return false;
            if (memo.TryGetValue(t.EventId, out bool known)) return known;
            if (!visiting.Add(t.EventId)) return false;
            bool current = t.SourceNoteIds.All(id => notes.TryGetValue(id, out var n) && Visit(n))
                && t.SourceDayIds.All(id => days.TryGetValue(id, out var d) && Visit(d));
            visiting.Remove(t.EventId);
            memo[t.EventId] = current;
            return current;
        }
        return Visit;
    }

    // Permission and existence view over the material a request actually read. Versions,
    // fingerprints, lifecycle and currentness of a footprint are deliberately NOT compared:
    // ordinary revision of material that was only read is not a conflict (R4). Explicit
    // withdrawal (suppress/delete) and purged records are, and the consequence follows
    // recorded material identities, so a withdrawn footprint cannot be re-read through a
    // record that itself only saw it as context.
    private Func<DaySourceDependency, bool> CreateMaterialPermissionEvaluator()
    {
        var suppressed = SuppressedExperienceIds();
        var raw = ReadExperiences(true).ToDictionary(t => t.EventId, StringComparer.Ordinal);
        var notes = _reflectionNotes.GroupBy(t => t.EventId).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        var lessons = _reflectionLessons.GroupBy(t => t.EventId).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        var readings = _relationshipReadings.GroupBy(t => t.EventId).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        var days = _dreams.Where(t => t.Kind is DayConsolidationKind or DayDreamKind)
            .GroupBy(t => t.EventId).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var memo = new Dictionary<string, bool>(StringComparer.Ordinal);
        bool Material(string kind, string id)
        {
            string key = kind + ":" + id;
            if (memo.TryGetValue(key, out bool known)) return known;
            if (!visiting.Add(key)) return false;
            bool permitted;
            if (kind == "utterance")
                permitted = raw.ContainsKey(id) && !suppressed.Contains(id);
            else if (kind == "note")
                permitted = notes.TryGetValue(id, out var note) && note != null
                    && DerivedPermitted(note, suppressed, Material, recurseFootprint: false);
            else if (kind == "lesson")
                permitted = lessons.TryGetValue(id, out var lesson) && lesson != null
                    && DerivedPermitted(lesson, suppressed, Material, recurseFootprint: false);
            else if (kind == "reading")
                permitted = readings.TryGetValue(id, out var reading) && reading != null
                    && DerivedPermitted(reading, suppressed, Material, recurseFootprint: false);
            else if (kind == "day")
                permitted = days.TryGetValue(id, out var day) && day != null
                    && DerivedPermitted(day, suppressed, Material, recurseFootprint: true);
            else
                permitted = false;
            visiting.Remove(key);
            memo[key] = permitted;
            return permitted;
        }
        return dependency => Material(dependency.Kind, dependency.EventId);
    }

    // The same permission view applied to any of her own records, not only day products.
    // Used where an old interpretation is re-read or re-used as a revision target: a record
    // whose recorded basis was withdrawn or purged must not come back through the repair
    // route, while an interpretation retired by an ordinary correction stays revisable.
    private Func<MemoryTurn, bool> CreateRecordPermissionEvaluator()
    {
        var material = CreateMaterialPermissionEvaluator();
        return turn =>
        {
            string kind = turn.Kind == "reflection.note" ? "note"
                : turn.Kind == "reflection.lesson" ? "lesson"
                : turn.Kind == RelationshipReadingKind ? "reading"
                : turn.Kind is DayConsolidationKind or DayDreamKind ? "day" : "";
            return kind.Length > 0 && material(new DaySourceDependency(kind, turn.EventId, ""));
        };
    }

    private bool RecordPermitted(MemoryTurn turn) => CreateRecordPermissionEvaluator()(turn);

    // A local historical read view: unlike current support, ordinary revisions
    // and corrections do not retire it. Unlike DerivedPermitted alone, every
    // declared raw identity must still exist and be readable. Capture the exact
    // identity footprint so a later day revision cannot hide a withdrawn input.
    private Func<string, string, bool> CreateNoteHistoryPermissionEvaluator(out HashSet<DaySourceDependency> footprint)
    {
        var captured = new HashSet<DaySourceDependency>();
        footprint = captured;
        var raw = ReadExperiences().Where(t => IsMentionAllowed(t) && IsNativeSupplyAllowed(t))
            .ToDictionary(t => t.EventId, StringComparer.Ordinal);
        var records = _reflectionNotes.Select(t => (Kind: "note", Turn: t))
            .Concat(_reflectionLessons.Select(t => (Kind: "lesson", Turn: t)))
            .Concat(_relationshipReadings.Select(t => (Kind: "reading", Turn: t)))
            .Concat(_dreams.Where(t => t.Kind is DayConsolidationKind or DayDreamKind).Select(t => (Kind: "day", Turn: t)))
            .GroupBy(p => (p.Kind, p.Turn.EventId)).ToDictionary(g => g.Key, g => g.Select(p => p.Turn).ToArray());
        var permitted = CreateMaterialPermissionEvaluator();
        var visiting = new HashSet<(string, string)>();
        var memo = new Dictionary<(string, string), bool>();
        bool Visit(string kind, string id)
        {
            var key = (kind, id);
            if (memo.TryGetValue(key, out bool known)) return known;
            if (!visiting.Add(key)) return false;
            bool allowed;
            if (kind == "utterance") allowed = raw.ContainsKey(id);
            else
            {
                allowed = records.TryGetValue(key, out var found) && found.Length == 1;
                if (allowed)
                {
                    var record = found![0];
                    allowed = (IsActiveLifecycle(record) || IsSettledLifecycle(record) || IsSupersededLifecycle(record))
                        && IsMentionAllowed(record) && permitted(new(kind, id, ""))
                        && record.SourceEventIds.Length > 0
                        && record.SourceEventIds.All(source => Visit("utterance", source))
                        && record.SourceNoteIds.All(source => Visit("note", source))
                        && record.SourceDayIds.All(source => Visit("day", source))
                        && (kind != "day" || record.ContextDependencies.Concat(record.SourceDependencies)
                            .All(d => Visit(d.Kind, d.EventId)));
                }
            }
            visiting.Remove(key);
            memo[key] = allowed;
            if (allowed) captured.Add(new(kind, id, ""));
            return allowed;
        }
        return Visit;
    }

    private bool NoteHistoryStillReadable(ReflectionRequestSnapshot request, out string reason)
    {
        reason = "note_history_unavailable";
        if (request.NoteHistory.Count == 0) return true;
        var readable = CreateNoteHistoryPermissionEvaluator(out _);
        if (request.NoteHistoryPermissions.Any(d => !readable(d.Kind, d.EventId))) return false;
        foreach (var prior in request.NoteHistory)
        {
            var matches = _reflectionNotes.Where(t => t.EventId == prior.EventId).ToArray();
            if (matches.Length != 1 || !readable("note", prior.EventId)) return false;
            var current = matches[0];
            // Exclude lifecycle and correction status: these change availability
            // as current support, not permission to read the archived content.
            if (current.RecallId != prior.RecallId || DayDependency("note", current) != DayDependency("note", prior))
            { reason = "note_history_changed"; return false; }
        }
        return true;
    }

    // A derived record is available for reading while its own identity, its recorded source
    // identities and (for a day product) its recorded read footprint are still permitted.
    private static bool DerivedPermitted(MemoryTurn turn, HashSet<string> suppressed,
        Func<string, string, bool> material, bool recurseFootprint) =>
        !suppressed.Contains(turn.EventId)
        && !turn.SourceEventIds.Any(suppressed.Contains)
        && !turn.SourceNoteIds.Any(suppressed.Contains)
        && !turn.SourceDayIds.Any(suppressed.Contains)
        && turn.SourceNoteIds.All(id => material("note", id))
        && turn.SourceDayIds.All(id => material("day", id))
        && (!recurseFootprint || FootprintPermitted(turn, d => material(d.Kind, d.EventId)));

    private static bool FootprintPermitted(MemoryTurn turn, Func<DaySourceDependency, bool> materialPermitted) =>
        (turn.ContextDependencies ?? Array.Empty<DaySourceDependency>()).All(materialPermitted);

    private IReadOnlyList<MemoryTurn> CaptureRevisionTargets(IEnumerable<MemoryTurn> relevantMaterial)
    {
        var raw = ReadExperiences().Select(t => t.EventId).ToHashSet(StringComparer.Ordinal);
        var isCurrent = CreateCurrentBasisEvaluator(raw);
        var permitted = CreateRecordPermissionEvaluator();
        var relevant = relevantMaterial.SelectMany(t => t.SourceEventIds.Append(t.EventId)).ToHashSet(StringComparer.Ordinal);
        return _reflectionNotes.Concat(_reflectionLessons).Concat(_relationshipReadings)
            .Where(t => (IsActiveLifecycle(t) || IsSettledLifecycle(t)) && IsMentionAllowed(t) && !isCurrent(t))
            // A withdrawn or purged basis also removes the old body from the repair route:
            // "kept for locating an old revision" is not a new read permission.
            .Where(permitted)
            .Where(t => t.SourceEventIds.Length > 0 && t.SourceEventIds.All(raw.Contains))
            .Where(t => t.SourceEventIds.Any(relevant.Contains)
                || _autonomous.Any(c => c.RequestKind == "correct" && IsAuthoritativeStoredCorrection(c) && relevant.Contains(c.SourceEventId)
                    && c.SourceEventIds.Any(t.SourceEventIds.Contains)))
            .GroupBy(t => t.Kind).SelectMany(g => g.OrderByDescending(t => t.Timestamp).Take(2))
            .Select(t => t.Copy()).ToList();
    }

    private IEnumerable<string> CorrectionInputIds(IEnumerable<MemoryTurn> targets)
    {
        var sources = targets.SelectMany(t => t.SourceEventIds).ToHashSet(StringComparer.Ordinal);
        return _autonomous.Where(t => t.RequestKind == "correct" && IsAuthoritativeStoredCorrection(t) && IsActiveLifecycle(t)
            && t.SourceEventIds.Any(sources.Contains)).Select(t => t.SourceEventId);
    }

    private bool DayCommitBasisCurrent(IEnumerable<DaySourceDependency> dependencies) => dependencies
        .Where(d => d.Kind is "note" or "day").All(d =>
        {
            MemoryTurn? t = (d.Kind == "note" ? _reflectionNotes : _dreams).FirstOrDefault(t => t.EventId == d.EventId);
            return t != null && IsCurrentInterpretationBasis(t);
        });

    // Reuse day provenance identities, adding current state for an in-flight
    // commit. Historical day citations keep their separate archive semantics.
    private DaySourceDependency SourceVersion(string kind, MemoryTurn turn) => new(kind, turn.EventId,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            DayDependency(kind, turn).Fingerprint + "|" + turn.LifecycleState + "|" + turn.Validity))));

    private IReadOnlyList<DaySourceDependency> CaptureReflectionVersions(
        IEnumerable<MemoryTurn> evidence, IEnumerable<MemoryTurn> notes,
        IEnumerable<MemoryTurn> lessons, IEnumerable<MemoryTurn> readings,
        IEnumerable<MemoryTurn> days) => evidence.Select(t => SourceVersion("utterance", t))
            .Concat(notes.Select(t => SourceVersion("note", t)))
            .Concat(lessons.Select(t => SourceVersion("lesson", t)))
            .Concat(readings.Select(t => SourceVersion("reading", t)))
            .Concat(days.Select(t => SourceVersion("day", t))).Distinct().ToArray();

    private bool ReflectionVersionsMatch(ReflectionRequestSnapshot request,
        IEnumerable<string> rawIds, IEnumerable<string> dayIds, IEnumerable<string> noteIds,
        string target = "")
    {
        var required = rawIds.Select(id => (Kind: "utterance", Id: id))
            .Concat(dayIds.Select(id => (Kind: "day", Id: id)))
            .Concat(noteIds.Where(id => id.Length > 0).Select(id => (Kind: "note", Id: id))).ToList();
        if (target.Length > 0)
        {
            string kind = request.Lessons.Concat(request.RevisionTargets.Where(t => t.Kind == "reflection.lesson"))
                .Any(t => t.EventId == target) ? "lesson" : "reading";
            required.Add((kind, target));
        }
        var raw = ReadExperiences().ToDictionary(t => t.EventId, StringComparer.Ordinal);
        foreach (var key in required.Distinct())
        {
            var pool = key.Kind switch
            {
                "note" => _reflectionNotes, "lesson" => _reflectionLessons,
                "reading" => _relationshipReadings, "day" => _dreams, _ => raw.Values.ToList()
            };
            MemoryTurn? current = pool.FirstOrDefault(t => t.EventId == key.Id);
            DaySourceDependency? prior = request.Dependencies.FirstOrDefault(d => d.Kind == key.Kind && d.EventId == key.Id);
            if (current == null || prior == null || SourceVersion(key.Kind, current) != prior) return false;
        }
        return true;
    }
}
