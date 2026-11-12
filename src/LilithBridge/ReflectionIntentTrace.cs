using System;
using System.Linq;
using System.Text.Json;

namespace LilithBridge;

// Observation only. This stamp never participates in eligibility, source authority,
// request consumption, prompt construction, or retries. Not persisted in work;
// existing operation IDs join observations. Missing/rotated traces remain unknown.
internal sealed record ReflectionServiceTrace(string PreparationId, long PreparationGeneration,
    long ModelRequestSerialAtCapture, bool ModelRequestedAtCapture, string ServiceKind,
    string SelectionLane, string[] GateWindowEventIds);

internal static class ReflectionIntentTrace
{
    private const string Qualification = "observation_only;opportunity_not_semantic_resolution;not_speech_or_read_receipt";

    // JSON text is normally Clean'ed before inline parsing. Reparse only into a
    // throwaway observation, then restore exact bodies only if every accepted
    // normalized reflect body maps in order. No operation or visible text changes.
    internal static void PreserveDecodedJsonReasons(ApiResult result, string? original, string? playerText,
        bool literalB1, bool literalMemo)
    {
        if (original == null || !result.ReflectionRequested) return;
        result.ReflectionRequestReasonBasis = "cleaned_json_parser_input;original_mapping_unknown";
        try
        {
            var observed = new ApiResult();
            if (!literalB1 && original.Contains("<b1>", StringComparison.Ordinal)) original = B1Continuity.Extract(original).Visible;
            original = NativeMemoReplySegments.Extract(observed, original, playerText, literalMemo);
            DeepSeekClient.ParseInlineTags(observed, original, playerText);
            if (!observed.ReflectionRequested || !result.ReflectionRequestReasons.Select(MemoryStore.Clean)
                .SequenceEqual(observed.ReflectionRequestReasons.Select(MemoryStore.Clean), StringComparer.Ordinal)) return;
            result.ReflectionRequestReasons.Clear();
            result.ReflectionRequestReasons.AddRange(observed.ReflectionRequestReasons);
            result.ReflectionRequestReasonBasis = "exact_decoded_json_text_tag_bodies";
        }
        catch { /* An uncertain observation never changes the accepted control signal. */ }
    }

    internal static object Request(ApiResult result, long serial, string session) => new {
        stage = "request_observed_before_display", session_id = session, model_request_serial = serial,
        request_id = result.RequestId, dialogue_request_id = result.DialogueRequest?.Id,
        attempt_id = result.Response.AttemptId, source_user_event_id = result.SourceUserEventId,
        conversation_generation = result.ConversationGeneration,
        generated_record_locator = result.Response.AttemptId.Length == 0 ? null
            : MemoryStore.GeneratedReplyKey(session, result.RequestId, result.Response.AttemptId, result.SourceUserEventId),
        generated_record_locator_status = "identity_only;storage_and_display_not_implied",
        applied_public_text_sha256 = MemoryStore.SemanticTextHash(result.Text),
        applied_public_text_basis = "current_parsed_reply_not_a_claim_of_stored_generation_bytes",
        reason_originals = result.ReflectionRequestReasons.Count > 0 ? result.ReflectionRequestReasons.ToArray()
            : new[] { result.ReflectionRequestReason },
        reason_basis = result.ReflectionRequestReasons.Count > 0 ? result.ReflectionRequestReasonBasis : "legacy_parsed_reason_only",
        qualification = Qualification
    };

    internal static object Delivery(ApiResult result, string session, string stage, string eventId, int chunkIndex, string? actualText) => new {
        stage, session_id = session, request_id = result.RequestId,
        attempt_id = result.Response.AttemptId, source_user_event_id = result.SourceUserEventId,
        generated_record_locator = result.Response.AttemptId.Length == 0 ? null
            : MemoryStore.GeneratedReplyKey(session, result.RequestId, result.Response.AttemptId, result.SourceUserEventId),
        displayed_event_id = eventId, chunk_index = chunkIndex,
        displayed_text_sha256 = actualText == null ? null : MemoryStore.SemanticTextHash(actualText),
        displayed_chars = actualText?.Length,
        delivery_terminal = result.Delivery?.IsTerminal ?? false,
        delivery_outcome = result.Delivery?.Outcome ?? result.DeliveryOutcome,
        qualification = Qualification
    };

    internal static object Dispatch(ReflectionRequestSnapshot request, ReflectionServiceTrace? capture) => new {
        stage = "dispatch_callback_invoked", operation_id = request.RequestId,
        capture,
        correlation = "serial_is_capture_fence_not_a_claim_that_any_reason_was_resolved;later_serials_not_in_capture",
        supplied_body_sha256 = MemoryStore.SemanticTextHash(request.Source),
        supplied = request.Evidence.Select(t => Material("utterance", t))
            .Concat(request.Notes.Select(t => Material("note", t)))
            .Concat(request.Lessons.Select(t => Material("lesson", t)))
            .Concat(request.Readings.Select(t => Material("reading", t)))
            .Concat(request.DayArtifacts.Select(t => Material("day_artifact", t)))
            .Concat(request.Settled.Select(t => Material("settled_note", t)))
            .Concat(request.RevisionTargets.Select(t => Material("revision_target", t)))
            .Concat(request.NoteHistory.Select(t => Material("note_history", t)))
            .Concat(request.RelationshipHistoryRecords.Select(t => Material("relationship_history", t))).ToArray(),
        generated_public_context = request.GeneratedPublicContext,
        permission_dependencies = request.Dependencies,
        note_history_permissions = request.NoteHistoryPermissions,
        relationship_history_versions = request.RelationshipHistoryVersions,
        qualification = Qualification
    };

    private static object Material(string role, MemoryTurn turn) => new {
        role, event_id = turn.EventId, revision = turn.Revision, origin = turn.Origin,
        at = turn.Timestamp, session_id = turn.SessionId,
        delivery_state = turn.DeliveryState, text_sha256 = MemoryStore.SemanticTextHash(turn.Text)
    };

    internal static object Outcome(ReflectionRequestSnapshot request, ReflectionResult? result,
        ReflectionChainOutcome? chains, string outcome, bool workPending) => new {
        stage = "service_outcome_observed", operation_id = request.RequestId,
        capture_status = "join_original_dispatch_by_operation_id;missing_trace_unknown",
        response_shape = result == null ? "no_usable_result" : "usable_result",
        proposed = result == null ? null : new { note = result.Note != null,
            lesson = result.Lesson != null, reading = result.Relationship != null },
        chains, outcome, work_pending = workPending,
        reason_resolution = "not_evaluated;actual_supplied_footprint_only",
        qualification = Qualification
    };

    internal static object B1Dispatch(ReflectionPreparation preparation, B1Ref reference, string fingerprint, string material) => new {
        stage = "dispatch_callback_invoked", operation_id = preparation.Id, capture = preparation.IntentTrace,
        service_kind = "b1_only_reconsideration", supplied = new { reference, fingerprint },
        supplied_body_sha256 = MemoryStore.SemanticTextHash(material),
        gate_window_event_ids = preparation.Window.Select(t => t.EventId).ToArray(),
        coverage = "one_prior_b1_only;gate_window_and_original_b1_sources_not_supplied",
        reason_resolution = "not_evaluated;opportunity_may_be_borrowed",
        qualification = Qualification
    };

    internal static object B1Outcome(B1ReflectionWork work, B1Commit committed) => new {
        stage = "service_outcome_observed", operation_id = work.OperationId,
        capture_status = "join_original_dispatch_by_operation_id;missing_trace_unknown",
        service_kind = "b1_only_reconsideration", request_id = work.Accepted?.RequestId,
        attempt_id = work.Accepted?.AttemptId,
        response_shape = work.Accepted == null ? "no_accepted_response"
            : !work.Accepted.ParsedChoice ? "invalid_response_shape"
            : work.Accepted.Choice == null ? "valid_explicit_null" : "choice_proposed",
        committed.Saved, committed.Outcome, committed.Version,
        coverage = "one_prior_b1_only;no_claim_to_cover_original_request_sources",
        qualification = Qualification
    };

    internal static void Emit(Func<object> observe)
    {
        try {
            if (!ConfigStore.Load().TraceEnabled) return;
            NativeEventLog.Trace("memory.reflection.intent_trace", "mod", "system",
            "reflection request and actual service observation", JsonSerializer.Serialize(observe())); }
        catch { /* Existing optional telemetry must never change the original path. */ }
    }
}

internal sealed partial class MemoryStore
{
    internal void TraceReflectionIntentRequest(ApiResult result, long serial)
        => ReflectionIntentTrace.Emit(() => ReflectionIntentTrace.Request(result, serial, _sessionId));
    internal void TraceReflectionIntentDelivery(ApiResult result, string stage, string eventId = "", int chunkIndex = -1, string? actualText = null)
    {
        if (result.ReflectionRequested)
            ReflectionIntentTrace.Emit(() => ReflectionIntentTrace.Delivery(result, _sessionId, stage, eventId, chunkIndex, actualText));
    }
}
