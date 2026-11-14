using System;
using System.Collections.Generic;
using System.Linq;

namespace LilithBridge;

// Reuse the existing slot-aware, three-state source continuity contract.
internal sealed record ReflectionReasonBasis(MethodSource[] Dependencies, bool Complete,
    [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] PublicExpressionGeneratedSource[]? GeneratedSources=null);

internal sealed partial class MemoryStore
{
    private ReflectionReasonBasis CaptureReflectionReasonSources(IEnumerable<MemoryTurn> records,
        IEnumerable<MethodSource> derived, B1Packet? packet, bool complete)
    {
        BeginB1Read(); B1Document? document = null;
        var sources = derived.Distinct().ToList(); var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(B1Ref reference, string expected = "")
        {
            if (!seen.Add(reference.Key + "/" + reference.Purpose)) return;
            if (reference.Kind == "b1") document ??= LoadB1();
            var source = ResolveMethodSource(reference, document);
            if (source == null || expected.Length > 0 && source.Fingerprint != expected)
            {
                complete = false;
                // Retain known identity/version facts for later explicit withdrawal
                // checks even when this read's full provenance could not be resolved.
                sources.Add(new(reference.Kind, reference.Version, reference.Sha256, expected, reference.Purpose, "capture_unknown"));
                return;
            }
            sources.Add(source);
            if (reference.Kind == "b1" && document != null && FindB1(document, reference.Version) is {} entry)
                foreach (var prior in entry.Sources.Concat(entry.ObjectAnchors).Concat(entry.Packet.Footprint).Concat(B1DisplayFootprint(entry.Packet)))
                    Add(prior with { Purpose = "permission" });
        }
        foreach (var turn in records.DistinctBy(t => (t.Kind, t.EventId, t.Role)))
        {
            string kind = turn.Kind == "profile" ? "profile_slot" : IsMethodRecentSlot(turn) ? "recent_slot" : B1Kind(turn);
            string id = kind == "profile_slot" ? turn.Role : kind == "recent_slot" ? MethodRecentSlotId(turn) : turn.EventId;
            Add(new(kind, id, B1Hash(turn.Text), "permission"), SourceVersion(kind, turn).Fingerprint);
            // Dependency identities are permission checks, not additional model bodies.
            foreach (string raw in turn.SourceEventIds) Add(new("utterance", raw, "", "permission"));
        }
        if (packet != null)
        {
            foreach (var material in packet.Materials) Add(material.Ref, material.VersionFingerprint);
            foreach (var reference in packet.Footprint.Concat(B1DisplayFootprint(packet))) Add(reference with { Purpose = "permission" });
        }
        return new(sources.Distinct().ToArray(), complete);
    }

    internal void CaptureReflectionReasonBasis(DialogueRequest request, IEnumerable<MemoryTurn> history,
        IReadOnlyList<RequestSourceBody> bodies, string actualContext, B1Packet? actualPacket, bool boundedInput)
    {
        lock (_experienceLock)
        {
            request.StagedReflectionTargetIds = bodies.Where(b => b.Qualified && b.Kind == "utterance")
                .Select(b => b.EventId).Distinct(StringComparer.Ordinal).ToArray();
            bool complete = boundedInput && (string.IsNullOrWhiteSpace(actualContext) || request.MethodRuntimeTracked)
                && request.StorySupplyChecks.Count == 0 && !request.ReviewDerivationUntracked;
            var captured = CaptureReflectionReasonSources(history.Concat(request.MethodRuntimeSources).Concat(request.B1HostFootprint),
                Array.Empty<MethodSource>(), actualPacket, complete);
            // Preparation can be replaced before any HTTP. Only the dispatch
            // boundary commits this exact latest base as actually supplied.
            request.StagedReflectionReasonBasis = captured;
            CaptureRevisitBaseTargets(request, actualContext);
        }
    }

    // Called only inside successful RegisterReviewOutcome's actual transcript
    // append. If no continuation is sent there is no accepted reflect to persist.
    internal void AppendReflectionToolBasis(DialogueRequest request, ReviewToolOutcome outcome)
    {
        if (outcome.IsControlReceipt) return;
        lock (_experienceLock)
        {
            var records = outcome.SourceRecords ?? Array.Empty<MemoryTurn>();
            var views = outcome.SourceViews ?? Array.Empty<ReviewMaterialView>();
            var offered = views.Where(v => records.Any(t => ReviewContext.ShortId(t) == v.Id))
                .Select(v => records.Single(t => ReviewContext.ShortId(t) == v.Id).EventId);
            request.ReflectionToolTargetIds = request.ReflectionToolTargetIds.Concat(offered).Distinct(StringComparer.Ordinal).ToArray();
            foreach (var view in views)
            {
                var matches = records.Where(t => ReviewContext.ShortId(t) == view.Id).ToArray();
                if (matches.Length == 1 && (!request.ReflectionTargetOfferedAliases.TryGetValue(view.Id, out var oldId) || oldId == matches[0].EventId))
                    request.ReflectionTargetOfferedAliases[view.Id] = matches[0].EventId;
            }
            var captured = CaptureReflectionReasonSources(records, outcome.DerivedMethodSources ?? Array.Empty<MethodSource>(), null,
                outcome.MethodSourcesTracked && !outcome.SuppliesPriorThought);
            var old = request.ReflectionToolReasonBasis;
            request.ReflectionToolReasonBasis = old == null ? captured
                : new(old.Dependencies.Concat(captured.Dependencies).Distinct().ToArray(), old.Complete && captured.Complete);
        }
    }

    private bool ReflectionReasonBasisCurrent(ReflectionReasonBasis? basis, ReflectionRequestSnapshot? request = null)
    {
        if (basis == null) return true;
        if (basis.Dependencies == null) return false;
        if (!ReflectionExpressionSourcesCurrent(basis.GeneratedSources)) return false;
        var dependencies = basis.Dependencies.Where(source => !OwnReflectionReasonLifecycleOnly(source, request)).ToArray();
        if (CheckMethodSourceContinuity(dependencies) == SourceContinuity.KnownInvalid) return false;
        // Lifetime unknown is not a denial. Where neutral resolution can prove
        // the source still exists, the current Mention/Native scope must also
        // permit it. The neutral continuity probe alone cannot authorize supply.
        foreach (var source in dependencies)
        {
            if (CheckMethodSourceContinuity(new[] { source }) != SourceContinuity.Same) continue;
            BeginB1Read(); var document = source.Kind == "b1" ? LoadB1() : null;
            if (ResolveMethodSource(new(source.Kind, source.Id, source.Hash, source.Purpose), document) == null) return false;
        }
        return true;
    }
    private bool OwnReflectionReasonLifecycleOnly(MethodSource source, ReflectionRequestSnapshot? request)
    {
        if (request == null || source.Kind is not ("note" or "lesson" or "reading")) return false;
        var original = request.Notes.Concat(request.Settled).Concat(request.Lessons).Concat(request.Readings)
            .Concat(request.RevisionTargets).FirstOrDefault(t => t.EventId == source.Id);
        if (original == null) return false;
        IEnumerable<MemoryTurn> pool = source.Kind == "note" ? _reflectionNotes : source.Kind == "lesson" ? _reflectionLessons : _relationshipReadings;
        bool own = pool.Any(t => t.OperationId == request.RequestId && t.SourceEventId == source.Id)
            || source.Kind == "note" && pool.Any(t => t.EventId == source.Id && t.ReflectionSettlementOperationId == request.RequestId);
        var current = pool.FirstOrDefault(t => t.EventId == source.Id);
        if (!own || current == null || current.LifecycleState is not ("superseded" or "settled")
            || !IsMentionAllowed(current) || !IsNativeSupplyAllowed(current) || SuppressedTextMatches(current.Text)) return false;
        var normalized = current.Copy(); normalized.LifecycleState = original.LifecycleState;
        return B1Hash(normalized.Text) == source.Hash && SourceVersion(source.Kind, normalized).Fingerprint == source.Fingerprint;
    }
    private bool ReflectionReasonBasisFullyTracked(ReflectionReasonBasis? basis) => basis is { Complete: true }
        && CheckMethodSourceContinuity(basis.Dependencies) == SourceContinuity.Same
        && ReflectionExpressionSourcesCurrent(basis.GeneratedSources);

    internal static bool ReflectionIntentReferences(ReflectionPendingIntent intent, HashSet<string> ids) =>
        ids.Contains(intent.Origin.UserEventId) || intent.TargetEventIds.Any(ids.Contains)
        || intent.ReasonBasis?.Dependencies.Any(d => ids.Contains(d.Id)) == true
        || intent.ReasonBasis?.GeneratedSources?.Any(d=>ids.Contains(d.Key))==true;
}
