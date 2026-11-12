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
        return state == null || state is { NormalTurnDue: true, Work: null } && !B1ReflectionProgressAdvanced(state, cursor);
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
            // Existing current finite judgments only. A bounded deterministic
            // rotation avoids inventing relevance, interest, or evidence from raw.
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
        });
    }
    internal static string B1ReflectionMaterial(B1ReconsiderationOwner owner)
    {
        var m = owner.Packet.Materials.Single();
        return JsonSerializer.Serialize(new { work_kind = "b1_only_reconsideration", source_ref = owner.SourceRef,
            original_identity = new { m.Ref.Kind, m.Ref.Version, m.LogicalId, m.Revision, m.Origin, m.At },
            text = m.Text, scope = m.Scope, conditions = m.Conditions, nature = m.Nature,
            qualification = "prior_reasoning_not_new_external_evidence" });
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
            var target = owner.Packet.Materials.Single();
            var work = new B1ReflectionWork("b1_only_reconsideration", preparation.Id, _root, _sessionId,
                preparation.Generation, B1ReflectionInputStamp(), preparation.Window.Select(t => t.EventId).ToArray(),
                target.Ref, target.VersionFingerprint, B1ReflectionPermissionIds(owner), target.LogicalId,
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
                ReflectionIntentTrace.Emit(() => ReflectionIntentTrace.B1Dispatch(preparation,
                    owner.Packet.Materials.Single().Ref, owner.Packet.Materials.Single().VersionFingerprint,
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
        var captured = CaptureB1Reconsideration(config, work.OperationId, work.Target, preparation.Token);
        if (captured != null && captured.Packet.Materials.Single().VersionFingerprint == work.TargetFingerprint) return captured;
        captured?.Dispose(); return null;
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
