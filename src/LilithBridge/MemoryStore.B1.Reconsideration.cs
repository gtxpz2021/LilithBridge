using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;

namespace LilithBridge;

internal sealed partial class MemoryStore
{
    // No scheduler or transport: one explicitly supplied pre-existing B1 only.
    // The object owns its capture, accepted output and lifetime; it is neither a
    // DialogueRequest nor an utterance, and cannot be moved to a fresh store.
    internal sealed class B1ReconsiderationOwner : IDisposable
    {
        private readonly object _gate = new();
        internal readonly MemoryStore Store;
        internal readonly BridgeConfig Config;
        internal readonly B1CapabilityOwner Capability;
        internal readonly long Generation;
        internal readonly long ControlGeneration;
        internal readonly B1Packet Packet;
        internal readonly string OpportunityId;
        internal readonly string StepId;
        internal readonly string SessionId;
        private readonly CancellationToken _token;
        private CancellationTokenRegistration _registration;
        private bool _closed, _invalidated, _accepted;
        internal B1Choice? Choice { get; private set; }
        internal string AttemptId { get; private set; } = "";
        internal int HttpAttempts { get; private set; }
        internal string SourceRef => Packet.ShortRefs.Single().Key;
        internal string Material => B1Continuity.RenderReadOnly(Packet);
        internal const string Contract = "Optional reconsideration of exactly this existing judgment. null maintains it without a new entry. An exact repeat of text, scope, nature and conditions records an unchanged receipt, not a new revision. Otherwise v=3, relation=revises, target=the supplied source_ref, sources=[that same source_ref], about=[], nature=hypothesis/inference/question. Text, scope and conditions stay finite. The old judgment is prior reasoning, not independent external evidence, a new observation, or proof of a world fact. No retention is required.";

        internal B1ReconsiderationOwner(MemoryStore store, BridgeConfig config, string opportunityId,
            B1Packet packet, CancellationToken cancellation)
        {
            Store = store; Config = config; Capability = config.B1CapabilityOwner!;
            Generation = config.B1CapabilityGeneration; OpportunityId = opportunityId;
            ControlGeneration = store.CurrentB1SelectionGeneration();
            Packet = packet; StepId = packet.Id; SessionId = store._sessionId; _token = cancellation;
        }
        internal void StartLifetime() => _registration = _token.Register(Cancel);
        internal bool Live()
        {
            if (_closed || _invalidated) return false;
            if (SessionId != Store._sessionId || _token.IsCancellationRequested || !B1CapabilityOwner.Enabled(Config)
                || !Capability.Permits(Generation)
                || !ReferenceEquals(Config.B1CapabilityOwner, Capability)
                || Config.B1CapabilityGeneration != Generation
                || !string.Equals(Path.GetFullPath(Config.DataDirectory), Store._root, StringComparison.OrdinalIgnoreCase))
                _invalidated = true;
            return !_invalidated;
        }
        internal T Boundary<T>(Func<T> body) { lock (_gate) return Capability.WithBoundary(body); }
        // Caller supplies actual accepted response metadata. This method never
        // invents an HTTP attempt or retries until a non-null choice appears.
        internal bool Accept(B1Choice? choice, string outcome, bool localFallback, string attemptId, int httpAttempts)
            => Boundary(() =>
            {
                if (!Live() || _accepted || outcome != "accepted" || localFallback || attemptId == null || httpAttempts < 0)
                    return false;
                Choice = choice == null ? null : JsonSerializer.Deserialize<B1Choice>(JsonSerializer.Serialize(choice));
                AttemptId = attemptId; HttpAttempts = httpAttempts; _accepted = true;
                return true;
            });
        internal bool HasAccepted => _accepted;
        internal void Cancel() { lock (_gate) _closed = true; }
        public void Dispose() { Cancel(); _registration.Dispose(); }
    }

    internal B1ReconsiderationOwner? CaptureB1Reconsideration(BridgeConfig config, string opportunityId,
        B1Ref supplied, CancellationToken cancellation)
    {
        var capability = config.B1CapabilityOwner;
        if (capability == null || supplied == null || supplied.Kind != "b1"
            || string.IsNullOrWhiteSpace(opportunityId)) return null;
        return capability.WithBoundary(() =>
        {
            if (!B1CapabilityOwner.Enabled(config) || !capability.Permits(config.B1CapabilityGeneration)
                || cancellation.IsCancellationRequested
                || !string.Equals(Path.GetFullPath(config.DataDirectory), _root, StringComparison.OrdinalIgnoreCase)) return null;
            lock (_experienceLock)
            {
                using var context = BeginB1ReadOnlyContext("", config);
                BeginB1Read();
                var doc = ReadB1Storage(true, initializeMissing: false);
                if (doc == null) return null;
                string step = "b1-reconsideration/" + B1Hash(opportunityId);
                bool replay = doc.Receipts.Any(r => r.StepId == step);
                var target = FindB1(doc, supplied.Version);
                if (target == null || !replay && doc.Current.GetValueOrDefault(target.LogicalId) != target.Version
                    || B1ManagementState(doc, target.Version) is "suppressed" or "withdrawn" or "deleting" or "deleted") return null;
                var resolved = ResolveB1(supplied with { Purpose = "history" }, doc, new());
                if (!resolved.Found || resolved.Material is not {} material || !B1ReadOnlyMaterialAllowed(material)) return null;
                // Operation identity is stable for this actual opportunity. The
                // original opportunity is provenance, never a fabricated EventId.
                var packet = new B1Packet { Id = step,
                    SearchMode = "explicit_existing_b1_reconsideration", FocusStatus = "prior_judgment_not_external_evidence",
                    Materials = new() { material }, Footprint = new() { material.Ref with { Purpose = "permission" } } };
                B1Continuity.AssignShortRefs(packet);
                var owner = new B1ReconsiderationOwner(this, config, opportunityId, packet, cancellation);
                owner.StartLifetime();
                if (owner.Live()) return owner;
                owner.Dispose();
                return null;
            }
        });
    }

    internal B1Commit CommitB1Reconsideration(B1ReconsiderationOwner owner)
        => owner.Boundary<B1Commit>(() =>
        {
            if (!ReferenceEquals(owner.Store, this)) return new(false, "store_owner_mismatch");
            if (!owner.Live()) return new(false, "owner_cancelled_or_revoked");
            if (!owner.HasAccepted) return new(false, "accepted_choice_required");
            lock (_experienceLock)
            {
                using var context = BeginB1ReadOnlyContext("", owner.Config);
                BeginB1Read();
                try
                {
                    using var fileLock = new FileStream(B1Path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    var doc = LoadB1(true);
                    if (doc == null) return new(false, "store_" + B1StorageStatus);
                    // Receipt comes before CAS: exact replay after a successful
                    // revision is not rejected because its head already moved.
                    var prior = doc.Receipts.FirstOrDefault(r => r.StepId == owner.StepId);
                    if (prior != null) return new(true, "already_committed_" + prior.Outcome, prior.ResultVersion);
                    if (doc.ControlGeneration != owner.ControlGeneration) return new(false, "selection_generation_changed");
                    var material = owner.Packet.Materials.Single();
                    var target = FindB1(doc, material.Ref.Version);
                    if (target == null || doc.Current.GetValueOrDefault(target.LogicalId) != target.Version)
                        return new(false, "target_version_conflict");
                    if (!B1ReadOnlyPacketPermitted(owner.Packet, "", owner.Config))
                        return new(false, "source_permission_or_version_changed");
                    var choice = owner.Choice;
                    B1Entry? entry = null;
                    string unchangedVersion = "";
                    if (choice != null)
                    {
                        // Strict subset of B1 v3, intentionally no general MAIN
                        // validator fork, mixed-source policy or object promotion.
                        if (choice.ProtocolVersion != 3 || choice.About == null || choice.About.Length != 0
                            || choice.Sources == null || choice.Sources.Length != 1 || choice.Sources[0] != owner.SourceRef
                            || choice.Relation != "revises" || choice.Target != owner.SourceRef
                            || choice.Nature is not ("hypothesis" or "inference" or "question")
                            || string.IsNullOrWhiteSpace(choice.Text) || choice.Text.Trim().Length > B1Continuity.MaxResultChars
                            || string.IsNullOrWhiteSpace(choice.Scope) || choice.Scope.Trim().Length > B1Continuity.MaxScopeChars
                            || choice.Conditions == null || choice.Conditions.Length > B1Continuity.MaxConditionsChars)
                            return new(false, "invalid_reconsideration_choice");
                        // This route supplies exactly the prior judgment, not
                        // new observations or independent support. Compare only
                        // after validation, head CAS and permission revalidation.
                        // Exact retained meaning is no revision; preserve origin,
                        // anchors, sources and the old packet rather than replace
                        // them with a self-supported duplicate. The opportunity
                        // still gets its own durable, replayable audit receipt.
                        bool unchanged = target.Kind == (choice.Nature == "question" ? "question" : "observation")
                            && target.Text == choice.Text.Trim() && target.Scope == choice.Scope.Trim()
                            && target.Nature == choice.Nature && target.Conditions == choice.Conditions.Trim();
                        if (unchanged) unchangedVersion = target.Version;
                        else
                        {
                        string version = Guid.NewGuid().ToString("N");
                        entry = new B1Entry { Version = version, LogicalId = target.LogicalId, Revision = NextB1FamilyRevision(doc, target.LogicalId),
                            Kind = choice.Nature == "question" ? "question" : "observation", Text = choice.Text.Trim(),
                            Scope = choice.Scope.Trim(), Nature = choice.Nature, Conditions = choice.Conditions.Trim(),
                            Relation = "revises", Target = material.Ref.Key, StepId = owner.StepId, AttemptId = owner.AttemptId,
                            At = DateTimeOffset.UtcNow, GeneratedBy = "b1_reconsideration", Selection = "explicit_retain",
                            AnchorStatus = "unbound_limited", Sources = new() { material.Ref with { Purpose = "history" } },
                            Packet = owner.Packet };
                        }
                    }
                    var next = new B1Document { Entries = entry == null ? doc.Entries : doc.Entries.Append(entry).ToList(),
                        Receipts = doc.Receipts.ToList(), Current = new(doc.Current, StringComparer.Ordinal), Deliveries = doc.Deliveries };
                    CopyB1State(doc, next);
                    if (entry != null) next.Current[entry.LogicalId] = entry.Version;
                    string outcome = entry == null ? unchangedVersion.Length > 0 ? "unchanged" : "no_retention" : "retained";
                    string resultVersion = entry?.Version ?? unchangedVersion;
                    next.Receipts.Add(new(owner.StepId, resultVersion, outcome, owner.AttemptId, owner.HttpAttempts, DateTimeOffset.UtcNow));
                    if (!owner.Live()) return new(false, "owner_cancelled_or_revoked");
                    return SaveB1(next) ? new(true, outcome, resultVersion) : new(false, "save_failed_or_uncertain");
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new(false, "store_busy_or_io"); }
            }
        });
}
