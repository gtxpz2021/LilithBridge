using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json;
using System.Text;
using System.Net.Http;
using System.Threading.Tasks;

namespace LilithBridge;

internal sealed record B1ReflectionWork(string Kind, string OperationId, string DataRoot,
    string OriginalSession, long OriginalGeneration, string InputStamp, string[] WindowIds,
    B1Ref Target, string TargetFingerprint, string[] PermissionIds, string LogicalId,
    B1ReflectionAccepted? Accepted = null, long ControlGeneration = 0);
internal sealed record B1ReflectionJournal(int Version = 1, bool NormalTurnDue = false,
    string LastLogicalId = "", string LastOperationId = "", string LastOutcome = "",
    B1ReflectionWork? Work = null, Dictionary<string, long>? UsedProgress = null, long UsedModelSerial = 0);

internal sealed partial class MemoryStore
{
    private string B1ReflectionPath => Path.Combine(_root, "v4-b1-reflection-work.json");
    private readonly HashSet<string> _invalidatedB1Reflections = new(StringComparer.Ordinal);
    private SemanticRecallResult? _lastDialogueSemanticRecall;
    internal static bool InvalidatePersistedB1Reflection(string root, string reason)
    {
        if (string.IsNullOrWhiteSpace(root)) return true;
        string journal = Path.Combine(root, "v4-b1-reflection-work.json");
        if (!File.Exists(journal)) return true;
        try
        {
            var state = JsonSerializer.Deserialize<B1ReflectionJournal>(File.ReadAllText(journal));
            if (state == null || state.Version != 1) return false;
            if (state.Work is not {} work) return true;
            string marker = Path.Combine(root, "v4-b1-reflection-invalidated.json");
            File.WriteAllText(marker + ".tmp", JsonSerializer.Serialize(new { operation = work.OperationId, reason }));
            File.Move(marker + ".tmp", marker, true);
            return true;
        }
        catch { return false; }
    }
    internal bool B1ReflectionWasInvalidated(string operation)
    {
        if (_invalidatedB1Reflections.Contains(operation)) return true;
        string path = Path.Combine(_root, "v4-b1-reflection-invalidated.json");
        if (!File.Exists(path)) return false;
        try
        {
            using var marker = JsonDocument.Parse(File.ReadAllText(path));
            return marker.RootElement.GetProperty("operation").GetString() == operation;
        }
        catch { return true; }
    }
    internal bool InvalidateB1Reflection(string operation, string reason, string originalRoot)
    {
        _invalidatedB1Reflections.Add(operation);
        bool marked = InvalidatePersistedB1Reflection(originalRoot, reason);
        bool ended = originalRoot == _root && EndB1ReflectionWork(operation, "invalidated_" + reason);
        return marked || ended;
    }
    internal B1ReflectionJournal? ReadB1ReflectionJournal()
    {
        lock (_experienceLock)
        {
            if (!File.Exists(B1ReflectionPath)) return new();
            try
            {
                if (new FileInfo(B1ReflectionPath).Length > 131072) return null;
                var state = JsonSerializer.Deserialize<B1ReflectionJournal>(File.ReadAllText(B1ReflectionPath));
                if (state == null || state.Version != 1 || state.LastLogicalId == null || state.LastOutcome == null
                    || state.Work is {} w && (w.Kind != "b1_only_reconsideration" || string.IsNullOrWhiteSpace(w.OperationId)
                        || w.DataRoot != _root || w.WindowIds == null || w.WindowIds.Length == 0
                        || w.Target == null || w.Target.Kind != "b1" || w.PermissionIds == null
                        || w.TargetFingerprint == null || w.InputStamp == null || !state.NormalTurnDue
                        || w.Accepted is {} a && (a.Outcome != "accepted" || a.HttpAttempts != 1
                            || string.IsNullOrWhiteSpace(a.RequestId) || string.IsNullOrWhiteSpace(a.AttemptId)
                            || a.RawContent == null || a.RawContent.Length > 32768))) return null;
                return state;
            }
            catch { return null; }
        }
    }
    private bool WriteB1ReflectionJournal(B1ReflectionJournal state)
    {
        try
        {
            Directory.CreateDirectory(_root);
            string serialized = JsonSerializer.Serialize(state);
            if (Encoding.UTF8.GetByteCount(serialized) > 131072) return false;
            File.WriteAllText(B1ReflectionPath + ".tmp", serialized);
            File.Move(B1ReflectionPath + ".tmp", B1ReflectionPath, true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
    // Session sequence high-water marks, not a hash of the currently permitted
    // set. Deletion, withdrawal, reordering, or the B1 write cannot advance them.
    private Dictionary<string, long> B1ReflectionProgress() => ReadExperiences(includeSuppressed: true)
        .Where(t => t.Role is "user" or "assistant" && t.DeliveryState is "received" or "delivered"
            && t.SessionId.Length > 0 && t.Sequence > 0)
        .GroupBy(t => t.SessionId).ToDictionary(g => g.Key, g => g.Max(t => t.Sequence), StringComparer.Ordinal);
    private bool B1ReflectionProgressAdvanced(B1ReflectionJournal state, ReflectionWindowCursor cursor)
        => state.UsedProgress == null || cursor.ModelRequestVersion > state.UsedModelSerial
            || B1ReflectionProgress().Any(p => p.Value > state.UsedProgress.GetValueOrDefault(p.Key));
    internal bool B1ReflectionOpportunityAlreadyUsed(ReflectionWindowCursor cursor)
    {
        var state = ReadB1ReflectionJournal();
        // A corrupt/unreadable journal pauses new reflection scheduling; a completed
        // B1 pass (NormalTurnDue=true, Work=null) yields the next idle pass to normal
        // 3-chain reflection, which then clears NormalTurnDue and locks UsedProgress.
        return state == null;
    }
    internal bool B1ReflectionMaySelect(ReflectionWindowCursor cursor)
    {
        var state = ReadB1ReflectionJournal();
        return state is { NormalTurnDue: false, Work: null } && B1ReflectionProgressAdvanced(state, cursor);
    }
    internal string B1ReflectionInputStamp() => SemanticTextHash(string.Join("\n", ReadExperiences(includeSuppressed: true)
        .Where(t => t.Role == "user" && t.DeliveryState == "received").Select(t => t.EventId)));

    internal B1ReconsiderationOwner? SelectB1Reflection(BridgeConfig config, ReflectionPreparation preparation,
        string lastLogicalId)
    {
        if (config.B1CapabilityOwner == null) return null;
        return config.B1CapabilityOwner.WithBoundary(() =>
        {
          lock (_experienceLock)
          {
            var doc = ReadB1Storage(true, initializeMissing: false);
            if (doc == null) return null;
            var window = preparation.Window;
            if (window == null || window.Count == 0)
            {
                // Legacy fallback when no dialogue window is supplied: deterministic rotation over Current heads.
                var candidates = doc.Entries.Where(e => doc.Current.GetValueOrDefault(e.LogicalId) == e.Version)
                    .OrderBy(e => string.CompareOrdinal(e.LogicalId, lastLogicalId) <= 0 ? 1 : 0)
                    .ThenBy(e => e.LogicalId, StringComparer.Ordinal).Take(32);
                foreach (var entry in candidates)
                {
                    var captured = CaptureB1Reconsideration(config, preparation.Id,
                        new("b1", entry.Version, "", "history"), preparation.Token);
                    if (captured == null) continue;
                    if (B1ReflectionMaterial(captured).Length <= 8000) return captured;
                    captured.Dispose();
                }
                return null;
            }

            // Experience-anchored selection:
            // 1. Collect user and model utterances in the reflection window (+ any main_reply B1 created on those turns)
            var userTurns = window.Where(t => t.Role == "user" && t.DeliveryState == "received"
                && !string.IsNullOrWhiteSpace(t.Text)).ToList();
            if (userTurns.Count == 0) return null;
            var assistantTurns = window.Where(t => t.Role == "assistant" && t.Origin == "model"
                && t.DeliveryState == "delivered" && !string.IsNullOrWhiteSpace(t.Text)).ToList();
            var windowEventIds = window.Select(t => t.EventId).ToHashSet(StringComparer.Ordinal);
            var windowNewB1s = doc.Entries.Where(e => windowEventIds.Contains(e.Packet.UserEventId)
                && B1ManagementState(doc, e.Version) is not ("suppressed" or "withdrawn" or "deleting" or "deleted")).ToList();
            string userQuery = string.Join(" ", userTurns.Select(t => t.Text)
                .Concat(windowNewB1s.Select(B1DiscoveryText)));
            string windowQuery = string.Join(" ", userTurns.Select(t => t.Text)
                .Concat(assistantTurns.Select(t => t.Text))
                .Concat(windowNewB1s.Select(B1DiscoveryText)));

            // 2. Dual-lane association search (lexical + Conditions meaning + semantic)
            //    and mandatory resolution to each family's latest Current head (.43 R-01).
            FindB1(doc, "");
            var userDiscoveryTerms = B1SearchTokens(userQuery).Where(B1DiscoveryContent).ToHashSet(StringComparer.Ordinal);
            var discoveryTerms = B1SearchTokens(windowQuery).Where(B1DiscoveryContent).ToHashSet(StringComparer.Ordinal);
            bool allFiller = userTurns.All(t => MemorySemantics.IsLowInfoGreetingOrFiller(t.Text));
            var semantic = preparation.SemanticResult;
            if (semantic == null && _lastDialogueSemanticRecall is { Unavailable.Length: 0 } cached
                && userTurns.Any(t => SemanticTextHash(t.Text) == cached.QueryHash))
                semantic = cached;
            if (allFiller && userDiscoveryTerms.Count < 2 && semantic == null)
            {
                NativeEventLog.Trace("b1.retrospective.salience_skipped", "mod", "system", "B1 reflection skipped by salience gate",
                    "operation=" + preparation.Id + ";user_turns=" + userTurns.Count + ";content_tokens=" + userDiscoveryTerms.Count);
                return null;
            }
            var semanticScores = new Dictionary<string, double>(StringComparer.Ordinal);
            if (semantic != null)
            {
                foreach (var c in semantic.Candidates.Where(c => c.Kind == "b1"
                    && c.Score >= MinB1SemanticScore
                    && ((c.DomainRank > 0 ? c.DomainRank : c.Rank) <= 0
                        || (c.DomainRank > 0 ? c.DomainRank : c.Rank) <= MaxB1SemanticGlobalRank)))
                {
                    if (!semanticScores.TryGetValue(c.EventId, out double prev) || c.Score > prev)
                        semanticScores[c.EventId] = c.Score;
                }
            }

            var familyBest = new Dictionary<string, (B1Entry Head, double Score, string Channel)>(StringComparer.Ordinal);
            foreach (var e in doc.Entries)
            {
                if (B1ManagementState(doc, e.Version) is "suppressed" or "withdrawn" or "deleting" or "deleted") continue;
                // Always resolve to the family's current head and exclude B1 families created inside this very window
                if (!doc.Current.TryGetValue(e.LogicalId, out string? headVersion) || string.IsNullOrEmpty(headVersion)) continue;
                var head = FindB1(doc, headVersion);
                if (head == null || windowEventIds.Contains(head.Packet.UserEventId)
                    || B1ManagementState(doc, head.Version) is "suppressed" or "withdrawn" or "deleting" or "deleted") continue;

                var tokens = _b1SearchIndex[e.Version];
                var meaning = _b1MeaningSearchIndex[e.Version];
                var objects = _b1SourceSearchIndex[e.Version];
                int userTextOverlap = userDiscoveryTerms.Count(tokens.Contains);
                int userMeaningOverlap = userDiscoveryTerms.Count(meaning.Contains);
                int userObjOverlap = userDiscoveryTerms.Count(objects.Contains);
                int textOverlap = discoveryTerms.Count(tokens.Contains);
                int meaningOverlap = discoveryTerms.Count(meaning.Contains);
                int objOverlap = discoveryTerms.Count(objects.Contains);
                static double UserLexScore(int overlap, int userCount, int docCount)
                {
                    if (overlap <= 0 || userCount <= 0 || docCount <= 0) return 0d;
                    double minSide = Math.Min(userCount, docCount);
                    double docDamp = docCount > 36 && docCount > userCount * 2
                        ? Math.Sqrt(docCount) - 6d : 0d;
                    return Math.Min(1d, overlap / Math.Max(3d, minSide + docDamp));
                }
                static double OchiaiScore(int overlap, int queryCount, int docCount)
                {
                    if (overlap <= 0 || queryCount <= 0 || docCount <= 0) return 0d;
                    double denom = Math.Max(4d, Math.Sqrt((double)queryCount * docCount));
                    return Math.Min(1d, overlap / denom);
                }
                double userTextScore = UserLexScore(userTextOverlap, userDiscoveryTerms.Count, tokens.Count);
                double userMeaningScore = UserLexScore(userMeaningOverlap, userDiscoveryTerms.Count, meaning.Count);
                double userObjScore = UserLexScore(userObjOverlap, userDiscoveryTerms.Count, objects.Count);
                double textScore = OchiaiScore(textOverlap, discoveryTerms.Count, tokens.Count);
                double meaningScore = OchiaiScore(meaningOverlap, discoveryTerms.Count, meaning.Count);
                double objScore = OchiaiScore(objOverlap, discoveryTerms.Count, objects.Count);
                const double MinWindowOchiaiScore = 0.18d;
                bool lexHit = (userTextScore >= MemorySemantics.MinRecallSimilarity
                        && (userTextOverlap >= MemorySemantics.MinRecallOverlap || (userDiscoveryTerms.Count <= 2 && userTextScore >= 0.5)))
                    || (userObjScore >= MemorySemantics.MinRecallSimilarity && userObjOverlap >= MemorySemantics.MinRecallOverlap)
                    || (textScore >= MinWindowOchiaiScore && textOverlap >= 4)
                    || (objScore >= MinWindowOchiaiScore && objOverlap >= 4);
                bool condHit = e.Conditions.Length > 0
                    && ((userMeaningScore >= MemorySemantics.MinRecallSimilarity && userMeaningOverlap >= MemorySemantics.MinRecallOverlap)
                        || (meaningScore >= MinWindowOchiaiScore && meaningOverlap >= 3));
                bool semHit = semanticScores.TryGetValue(e.Version, out double semScore);
                if (!lexHit && !condHit && !semHit) continue;

                double lexCombined = Math.Max(Math.Max(userTextScore, userObjScore), Math.Max(textScore, objScore));
                double condCombined = condHit ? Math.Max(userMeaningScore, meaningScore) : 0d;
                double combined = Math.Max(lexCombined, Math.Max(condCombined, semHit ? semScore : 0d));
                string channel = lexHit && semHit ? "lexical+semantic" : condHit ? "conditions" : lexHit ? "lexical" : "semantic";
                if (!familyBest.TryGetValue(head.LogicalId, out var prior) || combined > prior.Score)
                    familyBest[head.LogicalId] = (head, combined, channel);
            }

            var rankedHeads = familyBest.Values
                .OrderBy(x => x.Head.LogicalId == lastLogicalId ? 1 : 0)
                .ThenByDescending(x => x.Score)
                .ThenByDescending(x => x.Head.At)
                .ToList();

            foreach (var candidate in rankedHeads)
            {
                var captured = CaptureB1Reconsideration(config, preparation.Id,
                    new("b1", candidate.Head.Version, "", "history"), preparation.Token, window);
                if (captured == null) continue;
                if (B1ReflectionMaterial(captured).Length <= 8000)
                {
                    NativeEventLog.Trace("b1.retrospective.selected", "mod", "system", "Experience-anchored B1 retrospective target selected",
                        "operation=" + preparation.Id + ";target=" + candidate.Head.Version + ";logical_id=" + candidate.Head.LogicalId
                        + ";channel=" + candidate.Channel + ";score=" + candidate.Score.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
                    return captured;
                }
                captured.Dispose();
            }

            // 3. Salience Gate for new formation when no existing B1 family matched:
            //    Skip pure greetings / low-info filler windows with zero HTTP cost.
            if (allFiller || userDiscoveryTerms.Count < 2)
            {
                NativeEventLog.Trace("b1.retrospective.salience_skipped", "mod", "system", "B1 reflection skipped by salience gate",
                    "operation=" + preparation.Id + ";user_turns=" + userTurns.Count + ";content_tokens=" + userDiscoveryTerms.Count);
                return null;
            }

            var formation = CaptureB1Reconsideration(config, preparation.Id, null, preparation.Token, window);
            if (formation != null)
            {
                if (B1ReflectionMaterial(formation).Length <= 8000)
                {
                    NativeEventLog.Trace("b1.retrospective.selected", "mod", "system", "Experience-anchored B1 formation window selected",
                        "operation=" + preparation.Id + ";target=none;content_tokens=" + userDiscoveryTerms.Count);
                    return formation;
                }
                formation.Dispose();
            }
            return null;
          }
        });
    }
    internal static string B1ReflectionMaterial(B1ReconsiderationOwner owner)
    {
        if (!owner.ExperienceAnchored)
        {
            var m = owner.Packet.Materials.Single();
            return JsonSerializer.Serialize(new { work_kind = "b1_only_reconsideration", source_ref = owner.SourceRef,
                original_identity = new { m.Ref.Kind, m.Ref.Version, m.LogicalId, m.Revision, m.Origin, m.At },
                text = m.Text, scope = m.Scope, conditions = m.Conditions, nature = m.Nature,
                qualification = "prior_reasoning_not_new_external_evidence" });
        }
        var byKey = owner.Packet.ShortRefs.ToDictionary(kv => kv.Value.Key, kv => kv.Key, StringComparer.Ordinal);
        var targetMat = owner.TargetMaterial;
        return JsonSerializer.Serialize(new
        {
            work_kind = "experience_anchored_b1_retrospective",
            target_source_ref = targetMat != null ? byKey.GetValueOrDefault(targetMat.Ref.Key) : null,
            prior_b1_lineage = owner.Packet.Materials.Where(m => m.Ref.Kind == "b1").Select(m => new
            {
                source_ref = byKey[m.Ref.Key],
                role = m.DependencyRole,
                logical_id = m.LogicalId,
                revision = m.Revision,
                at = m.At,
                text = m.Text,
                scope = m.Scope,
                conditions = m.Conditions,
                nature = m.Nature,
                relation = m.Relation,
                target = m.Target.Length > 0 ? byKey.GetValueOrDefault(m.Target, m.Target) : "",
                qualification = "prior_reasoning_not_new_external_evidence"
            }).ToArray(),
            recent_utterances = owner.Packet.Materials.Where(m => m.Ref.Kind == "utterance").Select(m => new
            {
                source_ref = byKey[m.Ref.Key],
                role = m.DependencyRole,
                speaker = m.Origin == "user" ? "user" : "assistant",
                at = m.At,
                text = m.Text
            }).ToArray()
        }, DialogueMaterialEnvelope.DataJson);
    }
    internal bool B1ReflectionSourceCurrent(B1ReconsiderationOwner owner)
        => owner.Boundary(() =>
        {
            if (!owner.Live()) return false;
            lock (_experienceLock)
            {
                return owner.ControlGeneration == CurrentB1SelectionGeneration()
                    && B1ReadOnlyPacketPermitted(owner.Packet, "", owner.Config);
            }
        });
    private string[] B1ReflectionPermissionIds(B1ReconsiderationOwner owner)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<B1Ref>(owner.Packet.Footprint);
        var doc = ReadB1Storage(true, initializeMissing: false);
        if (doc == null) return Array.Empty<string>();
        while (pending.Count > 0)
        {
            var reference = pending.Dequeue();
            if (!ids.Add(reference.Version) || reference.Kind != "b1") continue;
            var entry = FindB1(doc, reference.Version);
            if (entry != null) foreach (var source in entry.Sources.Concat(entry.ObjectAnchors).Concat(entry.Packet.Footprint).Concat(B1DisplayFootprint(entry.Packet))) pending.Enqueue(source);
        }
        return ids.OrderBy(v => v, StringComparer.Ordinal).ToArray();
    }
    internal bool BeginB1ReflectionWork(ReflectionPreparation preparation, B1ReconsiderationOwner owner, ReflectionWindowCursor cursor)
    {
        return owner.Boundary(() =>
        {
          lock (_experienceLock)
          {
            var state = ReadB1ReflectionJournal();
            if (state == null || state.NormalTurnDue || state.Work != null || !ReflectionPreparationCurrent(preparation)
                || !B1ReflectionSourceCurrent(owner)) return false;
            var targetRef = owner.TargetMaterial?.Ref ?? new B1Ref("b1", "", "", "history");
            string targetLogicalId = owner.TargetMaterial?.LogicalId ?? "";
            var work = new B1ReflectionWork("b1_only_reconsideration", preparation.Id, _root, _sessionId,
                preparation.Generation, B1ReflectionInputStamp(), preparation.Window.Select(t => t.EventId).ToArray(),
                targetRef, owner.PacketFingerprint, B1ReflectionPermissionIds(owner), targetLogicalId,
                ControlGeneration: owner.ControlGeneration);
            return WriteB1ReflectionJournal(state with { NormalTurnDue = true, Work = work,
                UsedProgress = B1ReflectionProgress(), UsedModelSerial = cursor.ModelRequestVersion });
          }
        });
    }
    internal Task<HttpResponseMessage>? DispatchB1Reflection(ReflectionPreparation preparation,
        B1ReconsiderationOwner owner, ReflectionWindowCursor cursor, string expectedInput, Func<Task<HttpResponseMessage>> send)
        => owner.Boundary(() =>
        {
            lock (_experienceLock)
            {
                if (expectedInput != B1ReflectionInputStamp()
                    || !owner.Capability.ReflectionPermits(owner.Config.B1ReflectionCapabilityGeneration)
                    || !BeginB1ReflectionWork(preparation, owner, cursor)) return null;
                var targetRef = owner.TargetMaterial?.Ref ?? new B1Ref("b1", "", "", "history");
                ReflectionIntentTrace.Emit(() => ReflectionIntentTrace.B1Dispatch(preparation,
                    targetRef, owner.PacketFingerprint,
                    B1ReflectionMaterial(owner)));
                return send();
            }
        });
    internal bool SaveB1ReflectionAccepted(string operationId, B1ReflectionAccepted accepted)
    {
        lock (_experienceLock)
        {
            var state = ReadB1ReflectionJournal();
            if (state?.Work is not {} work || work.OperationId != operationId || work.Accepted != null
                || work.ControlGeneration != CurrentB1SelectionGeneration()
                || accepted.SelectionGeneration != work.ControlGeneration) return false;
            return WriteB1ReflectionJournal(state with { Work = work with { Accepted = accepted } });
        }
    }
    internal bool EndB1ReflectionWork(string operationId, string outcome)
    {
        lock (_experienceLock)
        {
            var state = ReadB1ReflectionJournal();
            if (state?.Work is not {} work || work.OperationId != operationId) return false;
            return WriteB1ReflectionJournal(state with { Work = null, LastLogicalId = work.LogicalId,
                LastOperationId = operationId, LastOutcome = outcome, NormalTurnDue = true });
        }
    }
    internal bool CompleteB1ReflectionNormalTurn(ReflectionWindowCursor cursor)
    {
        lock (_experienceLock)
        {
            var state = ReadB1ReflectionJournal();
            if (state == null) return false;
            if (!state.NormalTurnDue || state.Work != null) return true;
            return WriteB1ReflectionJournal(state with { NormalTurnDue = false, UsedProgress = B1ReflectionProgress(), UsedModelSerial = cursor.ModelRequestVersion });
        }
    }
    internal IReadOnlyList<MemoryTurn> B1ReflectionRecoveryWindow(B1ReflectionWork work)
    {
        lock (_experienceLock)
        {
            if (work.DataRoot != _root || work.InputStamp != B1ReflectionInputStamp()) return Array.Empty<MemoryTurn>();
            var byId = ReadExperiences().ToDictionary(t => t.EventId, StringComparer.Ordinal);
            return work.WindowIds.All(byId.ContainsKey) ? work.WindowIds.Select(id => byId[id].Copy()).ToArray() : Array.Empty<MemoryTurn>();
        }
    }
    internal B1ReconsiderationOwner? RecaptureB1Reflection(BridgeConfig config,
        ReflectionPreparation preparation, B1ReflectionWork work)
    {
        if (B1ReflectionWasInvalidated(work.OperationId) || !ReflectionPreparationCurrent(preparation) || work.InputStamp != B1ReflectionInputStamp()
            || work.ControlGeneration != CurrentB1SelectionGeneration()) return null;
        var window = B1ReflectionRecoveryWindow(work);
        B1Ref? targetRef = work.Target.Version.Length > 0 ? work.Target : null;
        var captured = CaptureB1Reconsideration(config, work.OperationId, targetRef, preparation.Token,
            window.Count > 0 ? window : null);
        if (captured != null && captured.PacketFingerprint == work.TargetFingerprint) return captured;
        // Fallback for legacy single-B1 work items that stored only the single target fingerprint
        if (captured != null) captured.Dispose();
        if (targetRef != null)
        {
            var legacyCaptured = CaptureB1Reconsideration(config, work.OperationId, targetRef, preparation.Token);
            if (legacyCaptured != null && legacyCaptured.PacketFingerprint == work.TargetFingerprint) return legacyCaptured;
            legacyCaptured?.Dispose();
        }
        return null;
    }
    internal B1Commit ApplyB1ReflectionAccepted(B1ReconsiderationOwner owner, B1ReflectionWork work, ReflectionPreparation preparation)
    {
        return owner.Boundary(() =>
        {
          lock (_experienceLock)
          {
        if (B1ReflectionWasInvalidated(work.OperationId) || work.InputStamp != B1ReflectionInputStamp() || !ReflectionPreparationCurrent(preparation)
            || !owner.Capability.ReflectionPermits(owner.Config.B1ReflectionCapabilityGeneration))
            return new B1Commit(false, "opportunity_or_mode_invalidated");
        if (work.ControlGeneration != owner.ControlGeneration
            || work.ControlGeneration != CurrentB1SelectionGeneration()) return new(false, "selection_generation_changed");
        if (work.Accepted is not {} accepted || work.OperationId != owner.OpportunityId
            || accepted.SelectionGeneration != work.ControlGeneration)
            return new(false, "accepted_journal_required");
        if (!accepted.ParsedChoice) return new(false, "invalid_response_shape");
        if (!owner.Accept(accepted.Choice, accepted.Outcome, false, accepted.AttemptId, accepted.HttpAttempts))
            return new(false, "accepted_owner_invalidated");
        return CommitB1Reconsideration(owner);
          }
        });
    }
    // Physical deletion removes response copies too, even if this feature is
    // disabled. Permission IDs are body-free graph indices, not supplied text.
    private bool PurgeB1ReflectionWork(HashSet<string> ids)
    {
        foreach (string path in new[] { B1ReflectionPath, B1ReflectionPath + ".tmp" })
        {
            if (!File.Exists(path)) continue;
            try
            {
                var state = JsonSerializer.Deserialize<B1ReflectionJournal>(File.ReadAllText(path));
                if (state == null || state.Version != 1) return false;
                if (state.Work is {} work && (work.PermissionIds.Any(ids.Contains) || work.WindowIds.Any(ids.Contains)))
                {
                    if (path.EndsWith(".tmp", StringComparison.Ordinal)) File.Delete(path);
                    else if (!WriteB1ReflectionJournal(state with { Work = null, NormalTurnDue = true,
                        LastOperationId = work.OperationId, LastOutcome = "source_deleted" })) return false;
                }
            }
            catch { return false; }
        }
        return true;
    }
}
