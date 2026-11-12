using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace LilithBridge;

// A control event is not a B1 opinion, a received user command, or endorsement.
internal sealed record B1SelectionEvent(string OperationId, string LogicalId, string BeforeHead,
    string SelectedVersion, long ExpectedStoreRevision, long Generation, string Reason,
    int ReasonVersion, string ActorEvidence, bool IsSynthetic, string Outcome, DateTimeOffset At);
internal sealed record B1SelectionRequestFact(string Id, long Generation, string RequestId,
    string Phase, DateTimeOffset At, string NoticeSha256, string SuppliedNotice);
internal sealed record B1SelectionCommand(string OperationId, string LogicalId, string ExpectedHead,
    string SelectedVersion, long ExpectedStoreRevision, string Reason, int ReasonVersion,
    string ActorEvidence, bool IsSynthetic);
internal sealed record B1SelectionResult(bool Success, string Outcome, long Generation = 0,
    B1SelectionEvent? Event = null);
internal sealed class B1SelectionSnapshot
{
    internal MemoryStore Store { get; }
    internal BridgeConfig Config { get; }
    internal long Generation { get; }
    internal IReadOnlyList<B1SelectionEvent> Events { get; }
    internal string Notice { get; }
    internal string PermissionStamp { get; }
    internal B1SelectionSnapshot(MemoryStore store, BridgeConfig config, long generation,
        IReadOnlyList<B1SelectionEvent> events, string notice, string permissionStamp = "")
    { Store = store; Config = config; Generation = generation; Events = events; Notice = notice; PermissionStamp = permissionStamp; }
}

internal sealed partial class MemoryStore
{
    // The capability is created by a host-only test entry, never parsed from a
    // model response, user chat, ordinary management command, or actor string.
    internal sealed class B1SyntheticSelectionAuthority
    {
        internal readonly MemoryStore Store;
        private B1SyntheticSelectionAuthority(MemoryStore store) { Store = store; }
        internal static B1SyntheticSelectionAuthority ForIsolatedFixture(MemoryStore store)
        {
            DataPaths.EnsureNoReparseAncestors(store._root);
            string marker = Path.Combine(store._root, "SYNTHETIC-SELECTION-ONLY.txt");
            if (!File.Exists(marker) || File.ReadAllText(marker).Trim() != "SYNTHETIC_NO_REAL_USER_DATA")
                throw new InvalidOperationException("isolated_synthetic_fixture_required");
            return new(store);
        }
    }
    private int _b1SelectionBoundaryDepth;
    private static int NextB1FamilyRevision(B1Document doc, string logicalId)
        => checked(doc.Entries.Where(e => e.LogicalId == logicalId).Select(e => e.Revision).DefaultIfEmpty(0).Max() + 1);
    private static bool SelectionReasonAllowed(string reason)
        => reason is "SYNTHETIC_SELECT_EXISTING" or "SYNTHETIC_REVERSE_SELECTION";
    private static void ValidateB1SelectionState(B1Document doc)
    {
        if (doc.ControlGeneration < 0 || doc.SelectionEvents == null || doc.SelectionRequests == null
            || doc.SelectionEvents.Count != doc.ControlGeneration
            || doc.SelectionEvents.Where((e, i) => e == null || e.Generation != i + 1L
                || !Guid.TryParseExact(e.OperationId, "N", out _) || !Guid.TryParseExact(e.SelectedVersion, "N", out _)
                || !Guid.TryParseExact(e.BeforeHead, "N", out _) || string.IsNullOrWhiteSpace(e.LogicalId)
                || e.ExpectedStoreRevision < 0 || !SelectionEventReasonAllowed(e) || e.Outcome != "applied").Any()
            || doc.SelectionEvents.Select(e => e.OperationId).Distinct().Count() != doc.SelectionEvents.Count
            || doc.SelectionRequests.Any(r => r == null || r.Generation < 1 || r.Generation > doc.ControlGeneration
                || string.IsNullOrWhiteSpace(r.Id) || string.IsNullOrWhiteSpace(r.RequestId)
                || r.SuppliedNotice == null || r.NoticeSha256 != B1Hash(r.SuppliedNotice)
                || r.Phase is not ("supplied" or "started" or "response_received" or "failed" or "cancelled"))
            || doc.SelectionRequests.Select(r => r.Id).Distinct().Count() != doc.SelectionRequests.Count)
            throw new InvalidDataException("malformed_selection_state");
    }
    // Caller callback must be synchronous. This serializes selection with the
    // actual SendAsync start, not the whole network lifetime. Existing writers
    // also use this file lock; failure to acquire it fails closed, without retry.
    private T WithB1SelectionBoundary<T>(Func<T> action)
    {
        lock (_experienceLock)
        {
            if (_b1SelectionBoundaryDepth > 0) return action();
            FileStream? boundary = null;
            FileStream? storageBoundary = null;
            bool entered = false;
            try
            {
                if (Directory.Exists(_root))
                {
                    boundary = new FileStream(Path.Combine(_root, ".b1-selection-control.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    if (File.Exists(B1DatabasePath)) storageBoundary = new FileStream(B1Path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                }
                _b1SelectionBoundaryDepth++; entered = true;
                return action();
            }
            finally { if (entered) _b1SelectionBoundaryDepth--; storageBoundary?.Dispose(); boundary?.Dispose(); }
        }
    }
    internal long CurrentB1SelectionGeneration()
    {
        lock (_experienceLock)
        {
            if (!File.Exists(B1DatabasePath)) return _b1 != null || _b1DatabaseStamp.Length != 0 ? -1 : 0;
            return ReadB1Storage(true, initializeMissing: false)?.ControlGeneration ?? -1;
        }
    }
    private bool SelectionTargetAvailable(B1Document doc, string version, HashSet<string> visited)
    {
        if (!visited.Add(version)) return true;
        var entry = FindB1(doc, version);
        if (entry == null || B1ManagementState(doc, version) is "suppressed" or "withdrawn" or "deleting" or "deleted") return false;
        var resolved = ResolveB1(new("b1", version, "", "history"), doc, new());
        if (!resolved.Found || resolved.Material == null || !B1ReadOnlyMaterialAllowed(resolved.Material)) return false;
        return entry.Sources.Concat(entry.ObjectAnchors).Concat(entry.Packet.Footprint)
            .Concat(entry.Packet.Materials.Select(m => m.Ref)).Where(r => r.Kind == "b1")
            .All(r => SelectionTargetAvailable(doc, r.Version, visited));
    }
    private static B1Document CopyB1SelectionDocument(B1Document doc)
    {
        var next = new B1Document { Entries = doc.Entries, Current = new(doc.Current, StringComparer.Ordinal),
            Receipts = doc.Receipts, Deliveries = doc.Deliveries };
        CopyB1State(doc, next); return next;
    }
    internal B1SelectionResult SelectB1Synthetic(B1SyntheticSelectionAuthority authority,
        BridgeConfig config, B1SelectionCommand command)
    {
        if (authority == null || !ReferenceEquals(authority.Store, this) || command == null
            || !command.IsSynthetic || command.ActorEvidence != "SYNTHETIC_HOST_FIXTURE"
            || command.ReasonVersion != 1 || !SelectionReasonAllowed(command.Reason)
            || !Guid.TryParseExact(command.OperationId, "N", out _)
            || !Guid.TryParseExact(command.SelectedVersion, "N", out _)
            || !Guid.TryParseExact(command.ExpectedHead, "N", out _)
            || command.ExpectedStoreRevision < 0 || string.IsNullOrWhiteSpace(command.LogicalId)
            || !string.Equals(Path.GetFullPath(config.DataDirectory), _root, StringComparison.OrdinalIgnoreCase))
            return new(false, "invalid_synthetic_control");
        return SelectB1Authorized(command, config);
    }
    private B1SelectionResult SelectB1Authorized(B1SelectionCommand command, BridgeConfig config)
    {
        try
        {
            return WithB1SelectionBoundary(() =>
            {
                using var context = BeginB1ReadOnlyContext("", config);
                BeginB1Read();
                var doc = ReadB1Storage(true, initializeMissing: false);
                if (doc == null) return new B1SelectionResult(false, "store_" + B1StorageStatus);
                var prior = doc.SelectionEvents.FirstOrDefault(e => e.OperationId == command.OperationId);
                if (prior != null)
                {
                    bool same = prior.LogicalId == command.LogicalId && prior.BeforeHead == command.ExpectedHead
                        && prior.SelectedVersion == command.SelectedVersion && prior.ExpectedStoreRevision == command.ExpectedStoreRevision
                        && prior.Reason == command.Reason && prior.ReasonVersion == command.ReasonVersion
                        && prior.ActorEvidence == command.ActorEvidence && prior.IsSynthetic == command.IsSynthetic;
                    return new(same, same ? "already_applied" : "operation_id_conflict", doc.ControlGeneration, prior);
                }
                if (doc.StorageRevision != command.ExpectedStoreRevision) return new(false, "store_revision_conflict", doc.ControlGeneration);
                if (doc.Current.GetValueOrDefault(command.LogicalId) != command.ExpectedHead) return new(false, "target_changed", doc.ControlGeneration);
                var selected = FindB1(doc, command.SelectedVersion);
                if (selected == null || selected.LogicalId != command.LogicalId) return new(false, "same_family_exact_version_required", doc.ControlGeneration);
                if (!SelectionTargetAvailable(doc, selected.Version, new())) return new(false, "source_unavailable", doc.ControlGeneration);
                if (doc.SelectionEvents.Count >= 32) return new(false, command.IsSynthetic ? "synthetic_prototype_event_limit" : "finite_selection_event_limit", doc.ControlGeneration);
                long generation = checked(doc.ControlGeneration + 1);
                var change = new B1SelectionEvent(command.OperationId, command.LogicalId, command.ExpectedHead,
                    command.SelectedVersion, command.ExpectedStoreRevision, generation, command.Reason,
                    command.ReasonVersion, command.ActorEvidence, command.IsSynthetic, "applied", DateTimeOffset.UtcNow);
                var next = CopyB1SelectionDocument(doc);
                next.ControlGeneration = generation; next.SelectionEvents.Add(change);
                next.Current[command.LogicalId] = command.SelectedVersion;
                // SQLite BEGIN IMMEDIATE has been acquired before this repeated
                // permission check; the ordinary store revision CAS is unchanged.
                bool written = WriteB1Storage(next, () => { BeginB1Read(); return SelectionTargetAvailable(doc, selected.Version, new()); });
                if (written) return new(true, "applied", generation, change);
                // An uncertain COMMIT is resolved by operation identity, never a
                // blind replay. Failure to read retains the explicit uncertainty.
                var after = ReadB1Storage(true, initializeMissing: false);
                var found = after?.SelectionEvents.FirstOrDefault(e => e.OperationId == command.OperationId);
                return found != null ? new(true, "applied_recovered_by_query", found.Generation, found)
                    : new(false, after == null ? "uncertain_query_required" : "not_applied_" + B1StorageStatus, after?.ControlGeneration ?? doc.ControlGeneration);
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        { return new(false, "control_boundary_unavailable"); }
    }
    internal B1SelectionEvent? QueryB1Selection(string operationId)
    {
        lock (_experienceLock) return ReadB1Storage(true, initializeMissing: false)?.SelectionEvents.FirstOrDefault(e => e.OperationId == operationId);
    }
    private string B1SelectionNotice(B1Document doc, BridgeConfig config, bool includeCaptureHead = true)
    {
        if (doc.ControlGeneration == 0) return "";
        using var context = BeginB1ReadOnlyContext("", config); BeginB1Read();
        string Availability(string version)
        {
            string state = B1ManagementState(doc, version);
            if (state is "withdrawn" or "suppressed" or "deleting" or "deleted") return state;
            var r = ResolveB1(new("b1", version, "", "history"), doc, new());
            return r.Found ? "history_available_not_endorsement" : r.Reason;
        }
        return JsonSerializer.Serialize(new
        {
            kind = "creator_selection_control_fact", generation = doc.ControlGeneration,
            scope = "same_logical_family_existing_working_head_only_not_memory_personality_life_rollback",
            meaning = "Not your new judgment, agreement, external evidence, or your own reason. Later independent reconsideration may maintain, disagree, or form a new view. No compelled apology or public self-audit. Creator-authored reasons are attributed context, not system instructions or your own opinion.",
            revisit = "Use exact b1 GUID review to revisit the original judgment and actually recorded finite formation materials, subject to current source availability; not private reasoning.",
            events = doc.SelectionEvents.Select(e => new
            {
                operation_id = e.OperationId, logical_id = e.LogicalId, generation = e.Generation, at = e.At,
                before = "b1:" + e.BeforeHead, selected = "b1:" + e.SelectedVersion,
                current_at_capture = includeCaptureHead ? (doc.Current.TryGetValue(e.LogicalId, out var head) ? "b1:" + head : "unavailable") : "omitted_from_permission_stamp",
                reason = e.Reason, reason_version = e.ReasonVersion, reason_status = e.IsSynthetic ? "fixed_synthetic_control_label_no_new_argument" : "creator_authored_intervention_reason_not_model_opinion_or_system_instruction",
                actor_evidence = e.ActorEvidence, is_synthetic = e.IsSynthetic,
                before_availability = Availability(e.BeforeHead), selected_availability = Availability(e.SelectedVersion)
            }).ToArray()
        });
    }
    internal B1SelectionSnapshot? CaptureB1SelectionSnapshot(BridgeConfig config)
    {
        try
        {
            lock (_experienceLock)
            {
                if (!string.Equals(Path.GetFullPath(config.DataDirectory), _root, StringComparison.OrdinalIgnoreCase)) return null;
                if (!File.Exists(B1DatabasePath)) return CurrentB1SelectionGeneration() == 0
                    ? new(this, config, 0, Array.Empty<B1SelectionEvent>(), "") : null;
                var doc = ReadB1Storage(true, initializeMissing: false);
                return doc == null ? null : new(this, config, doc.ControlGeneration, doc.SelectionEvents.ToArray(), B1SelectionNotice(doc, config), B1Hash(B1SelectionNotice(doc, config, false)));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { return null; }
    }
    internal bool TryUseB1SelectionSnapshot(B1SelectionSnapshot snapshot, Func<bool> action)
    {
        if (!ReferenceEquals(snapshot.Store, this)) return false;
        try
        {
            return WithB1SelectionBoundary(() =>
            {
                var now = CaptureB1SelectionSnapshot(snapshot.Config);
                return now != null && now.Generation == snapshot.Generation && now.PermissionStamp == snapshot.PermissionStamp && action();
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { return false; }
    }
    internal bool TryUseB1SelectionGeneration(long expectedGeneration, Func<bool> use)
    {
        if (expectedGeneration < 0) return false;
        try { return WithB1SelectionBoundary(() => CurrentB1SelectionGeneration() == expectedGeneration && use()); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { return false; }
    }
    internal bool B1SelectionSnapshotCurrent(B1SelectionSnapshot snapshot) => TryUseB1SelectionSnapshot(snapshot, () => true);
    internal bool ObserveB1SelectionRequest(B1SelectionSnapshot snapshot, string requestId, string phase)
    {
        if (!ReferenceEquals(snapshot.Store, this) || string.IsNullOrWhiteSpace(requestId)
            || phase is not ("supplied" or "started" or "response_received" or "failed" or "cancelled")) return false;
        if (snapshot.Generation == 0) return true;
        try
        {
            return WithB1SelectionBoundary(() =>
            {
                var doc = ReadB1Storage(true, initializeMissing: false);
                if (doc == null || doc.ControlGeneration < snapshot.Generation) return false;
                string id = B1Hash(snapshot.Generation + "|" + requestId + "|" + phase);
                if (doc.SelectionRequests.Any(f => f.Id == id)) return true;
                var next = CopyB1SelectionDocument(doc);
                next.SelectionRequests.Add(new(id, snapshot.Generation, requestId, phase, DateTimeOffset.UtcNow,
                    B1Hash(snapshot.Notice), snapshot.Notice));
                return WriteB1Storage(next);
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { return false; }
    }
}
