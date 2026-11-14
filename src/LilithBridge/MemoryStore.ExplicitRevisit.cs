using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace LilithBridge;

// An explicit control invocation or actual MAIN action, never a fabricated reflect tag.
internal sealed record ReflectionRevisitInvocation(string EntryPoint, string CauseId,
    DateTimeOffset RequestedAt, string DataRoot, string SessionId, DaySourceDependency Target,
    [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] MainRevisitOrigin? Main = null);
internal sealed record ReflectionRevisitCompletion(string OperationId, ReflectionRevisitInvocation Invocation,
    DateTimeOffset CompletedAt, string Outcome, string Reason, string[] ResultIds);

internal sealed partial class MemoryStore
{
    private const string ExplicitRevisitEntry = "explicit_control_call";
    internal Action<string>? ExplicitRevisitCheckpointForTests { get; set; }

    private static bool ValidRevisitInvocation(ReflectionRevisitInvocation r) => r != null
        && (r.EntryPoint == ExplicitRevisitEntry && r.Main == null
            || r.EntryPoint == MainRevisitEntry && r.Main != null && ValidMainRevisitOrigin(r.Main) && r.CauseId == r.Main.RequestId)
        && !string.IsNullOrWhiteSpace(r.CauseId) && r.CauseId.Length <= 256
        && r.RequestedAt != default && !string.IsNullOrWhiteSpace(r.DataRoot) && !string.IsNullOrWhiteSpace(r.SessionId)
        && r.Target is { Kind: "note" } && !string.IsNullOrWhiteSpace(r.Target.EventId) && r.Target.EventId.Length <= 256
        && r.Target.Fingerprint is { Length: 64 } && r.Target.Fingerprint.All(Uri.IsHexDigit);

    private static bool ValidRevisitCompletion(ReflectionRevisitCompletion c) => c != null
        && !string.IsNullOrWhiteSpace(c.OperationId) && c.OperationId.Length <= 256 && ValidRevisitInvocation(c.Invocation)
        && c.CompletedAt != default && c.Outcome is "completed_no_output" or "completed_with_output" or "blocked" or "result_unavailable" or "rejected"
        && !string.IsNullOrWhiteSpace(c.Reason) && c.Reason.Length <= 256
        && c.ResultIds != null && c.ResultIds.Length <= 4 && c.ResultIds.All(id => !string.IsNullOrWhiteSpace(id) && id.Length <= 256);

    private bool ValidReflectionWorkPurpose(ReflectionWork work) => work.Purpose switch
    {
        ReflectionWorkPurpose.Dialogue => work.Request.RevisitContext == null && work.Window.Length > 0,
        ReflectionWorkPurpose.ExplicitRevisit => work.Window.Length == 0 && work.Target == ReflectionWindowSnapshot.Empty
            && work.Request.RevisitContext is {} r && ValidRevisitInvocation(r)
            && string.Equals(r.DataRoot, _root, StringComparison.OrdinalIgnoreCase)
            && work.Request.RequestOriginContext == null && work.Request.PurposeContexts.Length == 0
            && ReflectionRevisitContextBound(work.Request),
        _ => false
    };

    private static object RevisitProjection(ReflectionRevisitInvocation r) => r.Main is {} main ? new {
        entry_point = r.EntryPoint, cause_id = r.CauseId, requested_at = r.RequestedAt,
        main_request_id = main.RequestId, user_event_id = main.UserEventId, tool_call_id = main.ToolCallId,
        reason = main.Reason, reason_basis = main.Basis?.Complete == true ? "tracked" : "partially_tracked",
        target = new { kind = r.Target.Kind, id = r.Target.EventId, version = r.Target.Fingerprint }
    } : new {
        entry_point = r.EntryPoint, cause_id = r.CauseId, requested_at = r.RequestedAt,
        target = new { kind = r.Target.Kind, id = r.Target.EventId, version = r.Target.Fingerprint }
    };

    private bool ReflectionRevisitContextBound(ReflectionRequestSnapshot request)
    {
        if (request.RevisitContext == null) return true;
        var r = request.RevisitContext;
        if (!ValidRevisitInvocation(r)
            || !string.Equals(r.DataRoot, _root, StringComparison.OrdinalIgnoreCase)
            || request.RequestOriginContext != null || request.PurposeContexts.Length != 0
            || request.Dependencies == null || !request.Dependencies.Contains(r.Target)
            || request.Notes.Concat(request.Settled).Count(t => t.EventId == r.Target.EventId) != 1) return false;
        try
        {
            using var doc = JsonDocument.Parse(request.Source);
            return doc.RootElement.TryGetProperty("explicit_revisit", out var projection)
                && projection.GetRawText() == JsonSerializer.SerializeToElement(RevisitProjection(r), DialogueMaterialEnvelope.DataJson).GetRawText();
        }
        catch (JsonException) { return false; }
    }

    private MemoryTurn? ReadExplicitRevisitNote(string id)
    {
        var matches = _reflectionNotes.Where(t => t.EventId == id).Take(2).ToArray();
        if (matches.Length != 1 || matches[0].Kind != "reflection.note" || matches[0].Origin != "reflection") return null;
        var note = matches[0];
        var available = IsActiveLifecycle(note) ? SelectAvailableReflectionNotes(int.MaxValue, null, null)
            : IsSettledLifecycle(note) ? SelectAvailableSettledNotes(int.MaxValue, null) : Array.Empty<MemoryTurn>();
        return available.Any(t => t.EventId == id) && RecordPermitted(note) && IsNativeSupplyAllowed(note) ? note.Copy() : null;
    }

    // Read-only exact lookup for an explicit caller. No target search or ranking.
    internal DaySourceDependency? CaptureExplicitRevisitTarget(string id)
    {
        lock (_experienceLock)
        {
            if (!_longTermEnabled || !_experienceArchiveEnabled) return null;
            var note = ReadExplicitRevisitNote(id);
            return note == null ? null : SourceVersion("note", note);
        }
    }

    internal ReflectionRevisitInvocation ExplicitRevisitInvocation(DaySourceDependency target, string causeId) =>
        new(ExplicitRevisitEntry, causeId, DateTimeOffset.UtcNow, _root, _sessionId, target);

    internal ReflectionRequestSnapshot? CaptureExplicitRevisitRequest(string operationId,
        ReflectionRevisitInvocation invocation, out string reason)
    {
        lock (_experienceLock)
        {
            reason = "invalid_invocation";
            if (string.IsNullOrWhiteSpace(operationId) || !ValidRevisitInvocation(invocation)
                || invocation.DataRoot != _root || invocation.SessionId != _sessionId) return null;
            reason = "reason_source_changed"; if (!MainRevisitOriginPermitted(invocation.Main)) return null;
            reason = "memory_disabled";
            if (!_longTermEnabled || !_experienceArchiveEnabled) return null;
            long generation = CurrentB1SelectionGeneration();
            reason = "selection_unavailable"; if (generation < 0) return null;
            var target = ReadExplicitRevisitNote(invocation.Target.EventId);
            reason = "target_unavailable"; if (target == null) return null;
            reason = "target_version_changed"; if (SourceVersion("note", target) != invocation.Target) return null;
            var ids = target.SourceEventIds.ToHashSet(StringComparer.Ordinal);
            var evidence = GetExperienceValidationTurns().Where(t => ids.Contains(t.EventId)
                && IsMentionAllowed(t) && IsNativeSupplyAllowed(t)).Select(t => t.Copy()).ToArray();
            reason = "source_unavailable";
            if (ids.Count == 0 || evidence.Length != ids.Count) return null;
            var dayIds = target.SourceDayIds.ToHashSet(StringComparer.Ordinal);
            var allowedDays = AvailableDayArtifactIds();
            var days = _dreams.Where(t => dayIds.Contains(t.EventId) && allowedDays.Contains(t.EventId)).Select(t => t.Copy()).ToArray();
            if (days.Length != dayIds.Count) return null;
            reason = "material_budget";
            if (evidence.Sum(t => (long)t.Text.Length) + target.Text.Length + days.Sum(t => (long)t.Text.Length) > 24000) return null;
            var notes = IsActiveLifecycle(target) ? new[] { target } : Array.Empty<MemoryTurn>();
            var settled = IsSettledLifecycle(target) ? new[] { target } : Array.Empty<MemoryTurn>();
            var history = CaptureNoteHistory(new[] { target });
            object Note(MemoryTurn t) => new { note_id = t.EventId, text = t.Text, state = t.LifecycleState,
                revision = t.Revision, revises = t.SourceEventId, source_event_ids = t.SourceEventIds,
                at = t.Timestamp, settled_at = t.SettledAt, settle_reason = t.SettleReason,
                source_context = DescribeSourceAttribution(t) };
            string source = JsonSerializer.Serialize(new {
                work_kind = "explicit_thought_revisit", explicit_revisit = RevisitProjection(invocation),
                context = "这是一次对指定旧想法的重访，没有新交流。对象来自本次调用，不表示它重要或必须改变；可维持、联想、修订、放下或无新产物。",
                now = DateTimeOffset.UtcNow, window_event_ids = Array.Empty<string>(),
                window_start = (DateTimeOffset?)null, window_end = (DateTimeOffset?)null, turn_count = evidence.Length,
                utterances = evidence.Select(t => new { event_id = t.EventId, speaker = t.Role, origin = t.Origin,
                    text = t.Text, at = t.Timestamp, source_revision_context = NaturalMemoryContext(t),
                    reported_speech = UtteranceAttribution.Reported(t.Role, t.Text) }),
                previous_notes = notes.Select(Note), settled_notes = settled.Select(Note), note_history = history.Projection,
                day_artifacts = days.Select(t => new { day_id = t.EventId, kind = t.Kind, at = t.Timestamp,
                    text = t.Text, source_context = DescribeSourceAttribution(t) })
            }, DialogueMaterialEnvelope.DataJson);
            var request = new ReflectionRequestSnapshot(operationId, source, evidence, notes,
                Array.Empty<MemoryTurn>(), Array.Empty<MemoryTurn>(), days, settled) {
                RevisitContext = invocation, SelectionGeneration = generation,
                Dependencies = CaptureReflectionVersions(evidence, new[] { target }, Array.Empty<MemoryTurn>(), Array.Empty<MemoryTurn>(), days),
                NoteHistory = history.Notes, NoteHistoryPermissions = history.Permissions
            };
            reason = "captured"; return request;
        }
    }

    internal bool BeginExplicitRevisitWork(ReflectionWindowCursor cursor, ReflectionRequestSnapshot request)
    {
        lock (_experienceLock)
        {
            if (!cursor.InitializationSucceeded || cursor.PassActive || cursor.Snapshot.Work != null
                || ReadB1ReflectionJournal() is not { Work: null } || request.RevisitContext == null
                || !ReflectionMaterialsCurrent(request)) return false;
            var work = new ReflectionWork(request, Array.Empty<MemoryTurn>(), ReflectionWindowSnapshot.Empty) {
                Purpose = ReflectionWorkPurpose.ExplicitRevisit, StartedInSessionId = _sessionId
            };
            if (!ValidReflectionWorkPurpose(work) || !SaveReflectionWindow(cursor.DurableSnapshot with { Work = work })) return false;
            _reflectionCursors.Add(cursor); cursor.SetWork(work); return true;
        }
    }

    private string[] RevisitResults(string operationId) => _reflectionNotes.Where(t => t.OperationId == operationId
            || t.ReflectionSettlementOperationId == operationId).Concat(_reflectionLessons.Where(t => t.OperationId == operationId))
        .Concat(_relationshipReadings.Where(t => t.OperationId == operationId)).Select(t => t.EventId).Distinct(StringComparer.Ordinal).ToArray();

    internal bool RecordBlockedExplicitRevisit(ReflectionWindowCursor cursor, string operationId,
        ReflectionRevisitInvocation invocation, string reason)
    {
        lock (_experienceLock)
        {
            if (!cursor.InitializationSucceeded || cursor.PassActive || cursor.Snapshot.Work != null
                || !ValidRevisitInvocation(invocation)) return false;
            var completed = new ReflectionRevisitCompletion(operationId, invocation, DateTimeOffset.UtcNow, "blocked", reason, Array.Empty<string>());
            if (!ValidRevisitCompletion(completed)) return false;
            var next = cursor.DurableSnapshot with { LastRevisitCompletion = completed };
            if (!SaveReflectionWindow(next)) return false;
            cursor.Restore(next); return true;
        }
    }

    private bool FinishExplicitRevisit(ReflectionWindowCursor cursor, ReflectionWork work, string outcome, string reason)
    {
        if (cursor.PassActive || cursor.Snapshot.Work?.Request.RequestId != work.Request.RequestId) return false;
        var completed = new ReflectionRevisitCompletion(work.Request.RequestId, work.Request.RevisitContext!,
            DateTimeOffset.UtcNow, outcome, reason, RevisitResults(work.Request.RequestId));
        if (!ValidRevisitCompletion(completed)) return false;
        // Use the live receipt, never work.Target: unrelated/later requests stay.
        var next = cursor.DurableSnapshot with { Work = null, LastRevisitCompletion = completed };
        if (!SaveReflectionWindow(next)) return false;
        cursor.Restore(next); return true;
    }

    // Local recovery only: no dispatch delegate and no return to normal retry.
    internal bool ResumeExplicitRevisit(ReflectionWindowCursor cursor, out string reason)
    {
        lock (_experienceLock)
        {
            reason = "no_explicit_revisit";
            if (cursor.PassActive || cursor.Snapshot.Work is not { Purpose: ReflectionWorkPurpose.ExplicitRevisit } work) return false;
            if (!ValidReflectionWorkPurpose(work)) { reason = "invalid_revisit_work"; return false; }
            if (work.Result == null)
            {
                reason = "send_outcome_unknown";
                return FinishExplicitRevisit(cursor, work, "result_unavailable", reason);
            }
            if (!ReflectionMaterialsCurrent(work.Request, recovering: true))
            {
                reason = "source_or_version_changed";
                return FinishExplicitRevisit(cursor, work, "blocked", reason);
            }
            ApplyReflectionNote(work.Result, work.Request, work.Progress, out reason, out var outcome);
            ExplicitRevisitCheckpointForTests?.Invoke("after_artifacts_before_completion");
            bool saveFailed = outcome.NoteReason == "persistence_failed" || outcome.LessonReason == "persistence_failed"
                || outcome.ReadingReason == "persistence_failed";
            if (saveFailed)
            {
                var pending = work with { Progress = outcome };
                if (SaveReflectionWindow(cursor.DurableSnapshot with { Work = pending })) cursor.SetWork(pending);
                reason = "persistence_pending"; return false;
            }
            string completion = ReflectionWindow.CompletionReason(reason, outcome);
            string status = ReflectionWindow.RetainsForRetry(completion) ? "rejected"
                : RevisitResults(work.Request.RequestId).Length == 0 ? "completed_no_output" : "completed_with_output";
            // Invalid proposals terminate this one call; only persistence retries
            // replay its accepted result. No implicit additional model attempt.
            return FinishExplicitRevisit(cursor, work, status, completion);
        }
    }

    internal static bool RevisitCompletionReferences(ReflectionRevisitCompletion? completion, HashSet<string> ids) =>
        completion != null && (ids.Contains(completion.Invocation.Target.EventId) || ids.Contains(completion.Invocation.CauseId)
            || MainRevisitOriginReferences(completion.Invocation.Main, ids) || completion.ResultIds.Any(ids.Contains));
    internal static bool SnapshotRevisitCompletionReferences(ReflectionWindowSnapshot snapshot, HashSet<string> ids) =>
        RevisitCompletionReferences(snapshot.LastRevisitCompletion, ids)
            || RevisitCompletionReferences(snapshot.Work?.Target.LastRevisitCompletion, ids);

    internal static ReflectionWindowSnapshot ForgetRevisitCompletion(ReflectionWindowSnapshot snapshot, HashSet<string> ids)
    {
        if (RevisitCompletionReferences(snapshot.LastRevisitCompletion, ids)) snapshot = snapshot with { LastRevisitCompletion = null };
        // A later ordinary pass may have captured this metadata in its recovery
        // target. Clear that reference without cancelling its unrelated work.
        if (snapshot.Work is {} work && RevisitCompletionReferences(work.Target.LastRevisitCompletion, ids))
            snapshot = snapshot with { Work = work with { Target = work.Target with { LastRevisitCompletion = null } } };
        return snapshot;
    }
}
