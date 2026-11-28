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
        internal B1Material? TargetMaterial => Packet.Materials.FirstOrDefault(m => m.Ref.Kind == "b1" && m.DependencyRole is "target_current" or "context")
            ?? Packet.Materials.FirstOrDefault(m => m.Ref.Kind == "b1");
        internal bool ExperienceAnchored => Packet.Materials.Any(m => m.Ref.Kind == "utterance");
        internal string PacketFingerprint => Packet.Materials.Count == 1
            ? Packet.Materials[0].VersionFingerprint
            : B1Hash(string.Join("|", Packet.Materials.Select(m => m.Ref.Key + ":" + m.VersionFingerprint)));
        internal string SourceRef => TargetMaterial is { } target
            && Packet.ShortRefs.FirstOrDefault(kv => kv.Value == target.Ref).Key is { Length: > 0 } key
            ? key : Packet.ShortRefs.FirstOrDefault().Key ?? "";
        internal string Material => B1Continuity.RenderReadOnly(Packet);
        internal const string Contract = "Optional reconsideration of exactly this existing judgment. null maintains it without a new entry. An exact repeat of text, scope, nature and conditions records an unchanged receipt, not a new revision. Otherwise v=3, relation=revises, target=the supplied source_ref, sources=[that same source_ref], about=[], nature=hypothesis/inference/question. Text, scope and conditions stay finite. The old judgment is prior reasoning, not independent external evidence, a new observation, or proof of a world fact. No retention is required.";
        internal const string ExperienceAnchoredContract =
            "你是莉莉丝，在对话间隙安静回看刚发生的一段真实交流（recent_utterances），以及可能一并提供的过往保留想法（prior_b1_lineage）。这不是对玩家说话，也不是替玩家做档案摘抄，不必为了产出而硬写。\n" +
            "你可以任选以下一种结果，它们同等正当：\n" +
            "1. 无需保留或维持原样：如果这只是日常寒暄、试音闲聊、明面事实没有新触动，或者旧想法依然适用，直接返回 {\"b1\":null}。\n" +
            "2. 留下一个新判断或未解问题（relation:\"\", target:\"\"）：如果刚才聊的事让你冒出没想通的怪问题（question）、对他潜台词的小揣测或自己嘴硬没说破的后觉心意（hypothesis）、或是两人看法分歧与相处门道（inference），可新建一条 B1（sources 至少引用一条 recent_utterances 的 source_ref）。\n" +
            "3. 与旧想法并存对照或补充限定（relation:\"compare\" 或 \"qualify\", target:旧想法source_ref）：当 prior_b1_lineage 提供了旧想法，而近期交流带来了反例、另一面或新的适用条件，且旧想法在原范围内仍可并存时使用。\n" +
            "4. 明确修订旧想法（relation:\"revises\", target:旧想法source_ref）：当近期交流、玩家更正、或同时看到的两个矛盾旧分支让你想通了新说法时，用新表述替代该旧头。\n" +
            "5. 温和收起已翻篇的旧想法（relation:\"settles\", target:旧想法source_ref）：当某个旧问题已经聊透解开、旧猜测已不再适用、或你自己觉得这桩旧念头已经翻篇时，在 text 写一句简短收起理由将其归架。\n" +
            "约束：用你自己的少女口吻（“我”或“莉莉丝”）写具体平实的短句；严守说话人归属：recent_utterances 中 speaker=user（玩家）是玩家讲的，只有 speaker=assistant（莉莉丝）才是你说的话，绝不把玩家讲的故事或原话写成自己讲的；禁止把明面原话重抄成干瘪摘要，禁止写“下次别猜/先核实”之类的自我审查家规、心理分析标签、破折号总结金句、“我先收着/记一笔”记账腔或“不是A，是B”套话；严守外部事实边界（不虚构未发生的外部事件或离线等待，允许对真实交流做内心回味）。" +
            "仅输出单根 JSON {\"b1\":null} 或 {\"b1\":{\"v\":3,\"text\":\"具体判断、问题或收起理由\",\"scope\":\"具体对象与适用范围\",\"conditions\":\"可选适用条件，无则空串\",\"nature\":\"question|hypothesis|inference（禁止reported）\",\"about\":[\"可选对象source_ref，最多2项，无则[]\"],\"sources\":[\"本次实际依据的source_ref，1至8项\"],\"relation\":\"\"或\"compare\"或\"qualify\"或\"revises\"或\"settles\",\"target\":\"\"或prior_b1_lineage中的活跃头source_ref}}。只引用输入里实际提供的 source_ref，不捏造未提供的事实。";

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
        B1Ref? supplied, CancellationToken cancellation, IReadOnlyList<MemoryTurn>? window = null)
    {
        var capability = config.B1CapabilityOwner;
        bool hasWindow = window is { Count: > 0 };
        if (capability == null || string.IsNullOrWhiteSpace(opportunityId)) return null;
        if (!hasWindow && (supplied == null || supplied.Kind != "b1" || string.IsNullOrWhiteSpace(supplied.Version))) return null;
        if (supplied != null && supplied.Kind != "b1") return null;
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
                if (!hasWindow)
                {
                    var target = FindB1(doc, supplied!.Version);
                    if (target == null || !replay && doc.Current.GetValueOrDefault(target.LogicalId) != target.Version
                        || (!replay ? IsB1ManagedInactiveState(B1ManagementState(doc, target.Version))
                            : B1ManagementState(doc, target.Version) is "suppressed" or "withdrawn" or "deleting" or "deleted")) return null;
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

                var materials = new List<B1Material>();
                var addedKeys = new HashSet<string>(StringComparer.Ordinal);
                B1Entry? targetEntry = null;
                if (supplied != null && supplied.Version.Length > 0)
                {
                    targetEntry = FindB1(doc, supplied.Version);
                    if (targetEntry == null || !replay && doc.Current.GetValueOrDefault(targetEntry.LogicalId) != targetEntry.Version
                        || (!replay ? IsB1ManagedInactiveState(B1ManagementState(doc, targetEntry.Version))
                            : B1ManagementState(doc, targetEntry.Version) is "suppressed" or "withdrawn" or "deleting" or "deleted")) return null;
                    var resolvedTarget = ResolveB1(supplied with { Purpose = "history" }, doc, new());
                    if (!resolvedTarget.Found || resolvedTarget.Material is not { } targetMaterial
                        || !B1ReadOnlyMaterialAllowed(targetMaterial)) return null;

                    // Optional 1-hop predecessor (placed chronologically before target_current)
                    if (targetEntry.Target.StartsWith("b1:", StringComparison.Ordinal))
                    {
                        string predVersion = targetEntry.Target[3..];
                        if (B1ManagementState(doc, predVersion) is not ("suppressed" or "withdrawn" or "deleting" or "deleted"))
                        {
                            var resolvedPred = ResolveB1(new("b1", predVersion, "", "history"), doc, new());
                            if (resolvedPred.Found && resolvedPred.Material is { } predMat
                                && B1ReadOnlyMaterialAllowed(predMat) && predMat.Text.Length <= 1200
                                && addedKeys.Add(predMat.Ref.Key))
                                materials.Add(predMat with { DependencyRole = "target_predecessor" });
                        }
                    }

                    if (addedKeys.Add(targetMaterial.Ref.Key))
                        materials.Add(targetMaterial with { DependencyRole = "target_current" });

                    // Optional 1 active sibling branch (compare/qualify) or corroborated 2nd active family head
                    FindB1(doc, "");
                    string targetRefKey = "b1:" + targetEntry.Version;
                    B1Entry? siblingBranch = doc.Entries
                        .Where(s => s.Version != targetEntry.Version
                            && doc.Current.GetValueOrDefault(s.LogicalId) == s.Version
                            && !IsB1ManagedInactiveState(B1ManagementState(doc, s.Version))
                            && !window!.Any(w => w.EventId == s.Packet.UserEventId)
                            && (s.Target == targetRefKey
                                || (targetEntry.Target.StartsWith("b1:", StringComparison.Ordinal)
                                    && s.Target == targetEntry.Target)))
                        .OrderByDescending(s => s.At)
                        .ThenBy(s => s.Version, StringComparer.Ordinal)
                        .FirstOrDefault();
                    string siblingRole = "sibling_branch";
                    if (siblingBranch == null)
                    {
                        var winTerms = B1SearchTokens(string.Join(" ", window!.Where(t => t.Role is "user" or "assistant").Select(t => t.Text)))
                            .Where(B1DiscoveryContent).ToHashSet(StringComparer.Ordinal);
                        if (winTerms.Count >= 2)
                        {
                            siblingBranch = doc.Entries
                                .Where(s => s.LogicalId != targetEntry.LogicalId
                                    && doc.Current.GetValueOrDefault(s.LogicalId) == s.Version
                                    && !IsB1ManagedInactiveState(B1ManagementState(doc, s.Version))
                                    && !window!.Any(w => w.EventId == s.Packet.UserEventId))
                                .Select(s =>
                                {
                                    int overlap = winTerms.Count(_b1MeaningSearchIndex[s.Version].Contains);
                                    double score = overlap == 0 ? 0d : overlap / Math.Max(3d, Math.Min(winTerms.Count, _b1MeaningSearchIndex[s.Version].Count));
                                    return (Entry: s, Overlap: overlap, Score: score);
                                })
                                .Where(x => x.Overlap >= 2 && x.Score >= MemorySemantics.MinRecallSimilarity)
                                .OrderByDescending(x => x.Score)
                                .ThenByDescending(x => x.Entry.At)
                                .ThenBy(x => x.Entry.Version, StringComparer.Ordinal)
                                .Select(x => x.Entry)
                                .FirstOrDefault();
                            siblingRole = "related_current";
                        }
                    }
                    if (siblingBranch != null)
                    {
                        var resolvedSibling = ResolveB1(new("b1", siblingBranch.Version, "", "history"), doc, new());
                        if (resolvedSibling.Found && resolvedSibling.Material is { } sibMat
                            && B1ReadOnlyMaterialAllowed(sibMat) && sibMat.Text.Length <= 900
                            && addedKeys.Add(sibMat.Ref.Key))
                            materials.Add(sibMat with { DependencyRole = siblingRole });
                    }

                    // Optional 1 prior source utterance anchor so old B1 is not misread out of context
                    var priorAnchorRef = targetEntry.ObjectAnchors.Concat(targetEntry.Sources)
                        .FirstOrDefault(r => r.Kind == "utterance" && !window!.Any(w => w.EventId == r.Version));
                    if (priorAnchorRef != null)
                    {
                        var resolvedPrior = ResolveB1(priorAnchorRef with { Purpose = "history" }, doc, new());
                        if (resolvedPrior.Found && resolvedPrior.Material is { } priorMat
                            && B1ReadOnlyMaterialAllowed(priorMat) && priorMat.Text.Length <= 600
                            && addedKeys.Add(priorMat.Ref.Key))
                            materials.Add(priorMat with { DependencyRole = "prior_source_anchor" });
                    }
                }

                int windowChars = 0;
                int triggerUsers = 0;
                var eligibleTurns = window!.Where(t => t.EventId.Length > 0
                        && t.Role is "user" or "assistant"
                        && t.Origin is "user" or "model"
                        && t.DeliveryState is "received" or "delivered")
                    .GroupBy(t => t.EventId, StringComparer.Ordinal).Select(g => g.First())
                    .ToList();
                var userIndices = new List<int>();
                for (int i = 0; i < eligibleTurns.Count; i++)
                    if (eligibleTurns[i].Role == "user") userIndices.Add(i);
                int startIndex = userIndices.Count > 3 ? userIndices[^3] : (userIndices.Count > 0 ? userIndices[0] : 0);
                var boundedWindow = eligibleTurns.Skip(startIndex).Take(24).ToList();
                foreach (var turn in boundedWindow)
                {
                    if (windowChars + turn.Text.Length > 3600 && triggerUsers > 0) break;
                    var uRef = new B1Ref("utterance", turn.EventId, B1Hash(turn.Text), "history");
                    var resolvedTurn = ResolveB1(uRef, doc, new());
                    if (!resolvedTurn.Found || resolvedTurn.Material is not { } turnMat
                        || !B1ReadOnlyMaterialAllowed(turnMat)) continue;
                    string role = turn.Role == "user" ? "trigger" : "context";
                    if (addedKeys.Add(turnMat.Ref.Key))
                    {
                        materials.Add(turnMat with { DependencyRole = role });
                        windowChars += turnMat.Text.Length;
                        if (turn.Role == "user") triggerUsers++;
                    }
                }
                if (triggerUsers == 0) return null;

                var expPacket = new B1Packet
                {
                    Id = step,
                    SearchMode = "association_driven_b1_retrospective",
                    FocusStatus = targetEntry != null ? "experience_anchored_retrospective" : "experience_anchored_formation",
                    Materials = materials,
                    Footprint = materials.Select(m => m.Ref with { Purpose = "permission" }).Distinct().ToList()
                };
                B1Continuity.AssignShortRefs(expPacket);
                var expOwner = new B1ReconsiderationOwner(this, config, opportunityId, expPacket, cancellation);
                expOwner.StartLifetime();
                if (expOwner.Live()) return expOwner;
                expOwner.Dispose();
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
                    var targetMat = owner.TargetMaterial;
                    B1Entry? target = targetMat == null ? null : FindB1(doc, targetMat.Ref.Version);
                    if (targetMat != null && (target == null || doc.Current.GetValueOrDefault(target.LogicalId) != target.Version
                        || IsB1ManagedInactiveState(B1ManagementState(doc, target.Version))))
                        return new(false, "target_version_conflict");
                    foreach (var activeHeadMat in owner.Packet.Materials.Where(m => m.Ref.Kind == "b1"
                        && m.DependencyRole is "sibling_branch" or "related_current"))
                    {
                        var activeHead = FindB1(doc, activeHeadMat.Ref.Version);
                        if (activeHead == null || doc.Current.GetValueOrDefault(activeHead.LogicalId) != activeHead.Version
                            || IsB1ManagedInactiveState(B1ManagementState(doc, activeHead.Version)))
                            return new(false, "target_version_conflict");
                    }
                    if (!B1ReadOnlyPacketPermitted(owner.Packet, "", owner.Config))
                        return new(false, "source_permission_or_version_changed");
                    var choice = owner.Choice;
                    B1Entry? entry = null;
                    string unchangedVersion = "";
                    string settledTargetVersion = "";
                    string settledStateValue = "";
                    if (!owner.ExperienceAnchored)
                    {
                        var material = owner.Packet.Materials.Single();
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
                            bool unchanged = target!.Kind == (choice.Nature == "question" ? "question" : "observation")
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
                    }
                    else if (choice != null)
                    {
                        string normalizedNature = choice.Nature?.Trim().ToLowerInvariant() ?? "";
                        string relation = choice.Relation?.Trim() ?? "";
                        string rawTarget = choice.Target?.Trim() ?? "";

                        B1Material? MapMaterial(string shortOrKey)
                        {
                            if (string.IsNullOrWhiteSpace(shortOrKey)) return null;
                            if (owner.Packet.ShortRefs.TryGetValue(shortOrKey, out var actual))
                                return owner.Packet.Materials.FirstOrDefault(m => m.Ref == actual);
                            return owner.Packet.Materials.FirstOrDefault(m => m.Ref.Key == shortOrKey);
                        }

                        if (relation == "settles")
                        {
                            if (choice.ProtocolVersion != 3
                                || string.IsNullOrWhiteSpace(choice.Text) || choice.Text.Trim().Length > B1Continuity.MaxResultChars
                                || (choice.Scope != null && choice.Scope.Length > B1Continuity.MaxScopeChars)
                                || (choice.Conditions != null && choice.Conditions.Length > B1Continuity.MaxConditionsChars)
                                || (choice.About != null && (choice.About.Length > B1Continuity.MaxAboutReferences
                                    || choice.About.Any(k => string.IsNullOrWhiteSpace(k) || k != k.Trim() || MapMaterial(k) == null)))
                                || (choice.Sources != null && (choice.Sources.Length > B1Continuity.MaxSourceReferences
                                    || choice.Sources.Any(k => string.IsNullOrWhiteSpace(k) || k != k.Trim() || MapMaterial(k) == null))))
                                return new(false, "invalid_reconsideration_choice");
                            var settleTargetMat = MapMaterial(rawTarget);
                            if (settleTargetMat == null || settleTargetMat.Ref.Kind != "b1"
                                || settleTargetMat.DependencyRole is not ("target_current" or "sibling_branch" or "related_current"))
                                return new(false, "invalid_reconsideration_choice");
                            var settleEntry = FindB1(doc, settleTargetMat.Ref.Version);
                            if (settleEntry == null || doc.Current.GetValueOrDefault(settleEntry.LogicalId) != settleEntry.Version
                                || IsB1ManagedInactiveState(B1ManagementState(doc, settleEntry.Version)))
                                return new(false, "target_version_conflict");
                            string cleanReason = Clean(choice.Text);
                            if (cleanReason.Length > 240) cleanReason = cleanReason[..240];
                            settledTargetVersion = settleEntry.Version;
                            settledStateValue = cleanReason.Length > 0 ? "settled:" + cleanReason : "settled";
                        }
                        else
                        {
                            string normalizedKind = normalizedNature == "question" ? "question" : "observation";
                            if (choice.ProtocolVersion != 3
                                || choice.About == null || choice.About.Length > B1Continuity.MaxAboutReferences
                                || choice.About.Any(k => string.IsNullOrWhiteSpace(k) || k != k.Trim())
                                || choice.Sources == null || choice.Sources.Length < 1 || choice.Sources.Length > B1Continuity.MaxSourceReferences
                                || choice.Sources.Any(k => string.IsNullOrWhiteSpace(k) || k != k.Trim())
                                || relation is not ("" or "compare" or "qualify" or "revises")
                                || normalizedNature is not ("hypothesis" or "inference" or "question")
                                || string.IsNullOrWhiteSpace(choice.Text) || choice.Text.Trim().Length > B1Continuity.MaxResultChars
                                || string.IsNullOrWhiteSpace(choice.Scope) || choice.Scope.Trim().Length > B1Continuity.MaxScopeChars
                                || choice.Conditions == null || choice.Conditions.Length > B1Continuity.MaxConditionsChars)
                                return new(false, "invalid_reconsideration_choice");

                            var mappedSources = choice.Sources.Select(MapMaterial).ToArray();
                            if (mappedSources.Any(m => m == null)) return new(false, "invalid_reconsideration_choice");
                            var sourceMaterials = mappedSources.Cast<B1Material>().GroupBy(m => m.Ref.Key, StringComparer.Ordinal).Select(g => g.First()).ToList();

                            var mappedAbout = choice.About.Select(MapMaterial).ToArray();
                            if (mappedAbout.Any(m => m == null)) return new(false, "invalid_reconsideration_choice");
                            var aboutMaterials = mappedAbout.Cast<B1Material>().GroupBy(m => m.Ref.Key, StringComparer.Ordinal).Select(g => g.First()).ToList();

                            string targetKey = "";
                            B1Entry? effectiveTarget = target;
                            if (relation.Length == 0)
                            {
                                if (rawTarget.Length != 0) return new(false, "invalid_reconsideration_choice");
                                // New formation in background reflection must cite at least one window utterance
                                if (!sourceMaterials.Any(m => m.Ref.Kind == "utterance"))
                                    return new(false, "invalid_reconsideration_choice");
                            }
                            else
                            {
                                var mappedTarget = MapMaterial(rawTarget);
                                if (mappedTarget == null || mappedTarget.Ref.Kind != "b1"
                                    || mappedTarget.DependencyRole is not ("target_current" or "sibling_branch" or "related_current"))
                                    return new(false, "invalid_reconsideration_choice");
                                effectiveTarget = FindB1(doc, mappedTarget.Ref.Version);
                                if (effectiveTarget == null || doc.Current.GetValueOrDefault(effectiveTarget.LogicalId) != effectiveTarget.Version
                                    || IsB1ManagedInactiveState(B1ManagementState(doc, effectiveTarget.Version)))
                                    return new(false, "target_version_conflict");
                                targetKey = mappedTarget.Ref.Key;
                                if (!sourceMaterials.Any(m => m.Ref.Key == targetKey))
                                    sourceMaterials.Add(mappedTarget);
                            }

                            string text = choice.Text.Trim();
                            string scope = choice.Scope.Trim();
                            string conditions = choice.Conditions.Trim();
                            bool exactTargetRepeat = effectiveTarget != null
                                && effectiveTarget.Kind == normalizedKind && effectiveTarget.Text == text
                                && effectiveTarget.Scope == scope && effectiveTarget.Nature == normalizedNature
                                && effectiveTarget.Conditions == conditions;
                            var existingDup = relation.Length == 0
                                ? doc.Entries.FirstOrDefault(e => doc.Current.GetValueOrDefault(e.LogicalId) == e.Version
                                    && !IsB1ManagedInactiveState(B1ManagementState(doc, e.Version))
                                    && e.Kind == normalizedKind && e.Text == text && e.Scope == scope
                                    && e.Nature == normalizedNature && e.Conditions == conditions)
                                : relation is "compare" or "qualify"
                                    ? doc.Entries.FirstOrDefault(e => doc.Current.GetValueOrDefault(e.LogicalId) == e.Version
                                        && !IsB1ManagedInactiveState(B1ManagementState(doc, e.Version))
                                        && e.Relation == relation && e.Target == targetKey
                                        && e.Kind == normalizedKind && e.Text == text && e.Scope == scope
                                        && e.Nature == normalizedNature && e.Conditions == conditions)
                                    : null;
                            if (exactTargetRepeat) unchangedVersion = effectiveTarget!.Version;
                            else if (existingDup != null) unchangedVersion = existingDup.Version;
                            else
                            {
                                bool isRevises = relation == "revises";
                                var objectAnchors = aboutMaterials.Select(m => isRevises && m.Ref.Key == targetKey
                                    ? m.Ref with { Purpose = "history" } : m.Ref).ToList();
                                string anchorStatus = objectAnchors.Count == 0 ? "unbound_limited"
                                    : objectAnchors.All(r => r.Kind == "utterance"
                                        && owner.Packet.Materials.Any(m => m.Ref.Key == r.Key && m.DependencyRole == "trigger"))
                                        ? "turn_only" : "model_declared_object_binding_not_semantically_verified";
                                var sources = sourceMaterials.Select(m => isRevises && m.Ref.Key == targetKey
                                    ? m.Ref with { Purpose = "history" } : m.Ref).ToList();
                                string version = Guid.NewGuid().ToString("N");
                                entry = new B1Entry
                                {
                                    Version = version,
                                    LogicalId = isRevises ? effectiveTarget!.LogicalId : version,
                                    Revision = isRevises ? NextB1FamilyRevision(doc, effectiveTarget!.LogicalId) : 1,
                                    Kind = normalizedKind,
                                    Text = text,
                                    Scope = scope,
                                    Nature = normalizedNature,
                                    Conditions = conditions,
                                    Relation = relation,
                                    Target = targetKey,
                                    StepId = owner.StepId,
                                    AttemptId = owner.AttemptId,
                                    At = DateTimeOffset.UtcNow,
                                    GeneratedBy = "b1_reconsideration",
                                    Selection = "explicit_retain",
                                    ObjectAnchors = objectAnchors,
                                    AnchorStatus = anchorStatus,
                                    Sources = sources,
                                    Packet = owner.Packet
                                };
                            }
                        }
                    }
                    var next = new B1Document { Entries = entry == null ? doc.Entries : doc.Entries.Append(entry).ToList(),
                        Receipts = doc.Receipts.ToList(), Current = new(doc.Current, StringComparer.Ordinal), Deliveries = doc.Deliveries };
                    CopyB1State(doc, next);
                    if (entry != null) next.Current[entry.LogicalId] = entry.Version;
                    if (settledTargetVersion.Length > 0) next.Management[settledTargetVersion] = settledStateValue;
                    string outcome = entry != null ? "retained"
                        : settledTargetVersion.Length > 0 ? "settled"
                        : unchangedVersion.Length > 0 ? "unchanged" : "no_retention";
                    string resultVersion = entry?.Version ?? (settledTargetVersion.Length > 0 ? settledTargetVersion : unchangedVersion);
                    next.Receipts.Add(new(owner.StepId, resultVersion, outcome, owner.AttemptId, owner.HttpAttempts, DateTimeOffset.UtcNow));
                    if (!owner.Live()) return new(false, "owner_cancelled_or_revoked");
                    return SaveB1(next) ? new(true, outcome, resultVersion) : new(false, "save_failed_or_uncertain");
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new(false, "store_busy_or_io"); }
            }
        });
}
