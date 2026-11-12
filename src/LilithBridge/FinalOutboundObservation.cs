using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LilithBridge;

// Local opt-in transport evidence. Never a memory source, provider reasoning
// reader, credential reader, or proof that an observed request was dispatched.
internal sealed class FinalOutboundTicket
{
    internal string Directory { get; init; } = "";
    internal string Stem { get; init; } = "";
    internal string Mode { get; init; } = "off";
    internal string WireHash { get; init; } = "";
    internal string RequestId { get; init; } = "";
    internal string AttemptId { get; init; } = "";
    internal ApiResponseDiagnostic Diagnostic { get; init; } = null!;
    internal bool DispatchStarted { get; set; }
    internal bool BodySaved { get; set; }
    internal bool ManifestSaved { get; set; }
    internal string FailureStage { get; set; } = "";
    internal string ErrorType { get; set; } = "";
}

internal static class FinalOutboundObservation
{
    private static readonly UTF8Encoding Utf8 = new(false);
    internal static string LastFailure { get; private set; } = "";
    internal static Action<string>? FailureForTests { get; set; }

    internal static string NormalizeMode(string? mode) => mode?.Trim().ToLowerInvariant() is "manifest" or "body"
        ? mode.Trim().ToLowerInvariant() : "off";
    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(value))).ToLowerInvariant();
    // Identity fields are program-owned. Unexpected values are represented by
    // hash rather than being allowed to smuggle arbitrary text into a manifest.
    internal static string Identity(string? value) => string.IsNullOrEmpty(value) ? "" : value.Length <= 160
        && value.All(c => c <= 127 && (char.IsLetterOrDigit(c) || "-_.:/".Contains(c))) ? value : "sha256:" + Hash(value);

    internal static FinalOutboundTicket? Plan(BridgeConfig config, string json, string kind, string purpose,
        ApiResult envelope, IReadOnlyList<MemoryTurn>? history, HistoryProjectionSummary? historyProjection,
        bool budgetAccepted = true, string rejection = "", string rejectionStage = "rejected_at_budget", bool sourceVerified = false)
    {
        string mode = NormalizeMode(config.FinalOutboundObservationMode);
        if (mode == "off") return null;
        var ticket = new FinalOutboundTicket
        {
            Directory = Path.Combine(config.DataDirectory ?? "", "telemetry", "final_outbound"),
            Stem = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffffffZ") + "-" + Guid.NewGuid().ToString("N"),
            Mode = mode, WireHash = Hash(json), RequestId = Identity(envelope.RequestId),
            AttemptId = Identity(envelope.Response.AttemptId), Diagnostic = envelope.Response
        };
        try
        {
            if (string.IsNullOrWhiteSpace(config.DataDirectory)) throw new InvalidOperationException();
            var structure = FinalOutboundStructure.Describe(json, envelope, history);
            bool accepted = budgetAccepted && rejection.Length == 0;
            bool bodySaved = accepted && mode == "body";
            System.IO.Directory.CreateDirectory(ticket.Directory);
            if (bodySaved)
            {
                WriteNew(Path.Combine(ticket.Directory, ticket.Stem + ".wire.json"), Utf8.GetBytes(json));
                ticket.BodySaved = true;
            }
            var owner = envelope.DialogueRequest;
            var manifest = new
            {
                schema_version = 1, observation_id = ticket.Stem, timestamp_utc = DateTimeOffset.UtcNow,
                stage = accepted ? "planned" : rejectionStage, dispatch_started = false,
                source_precheck_verified = sourceVerified,
                request_id = ticket.RequestId, attempt_id = ticket.AttemptId,
                logical_owner_id = Identity(owner?.Id), owner_http_attempts_before_send = owner?.HttpAttempts,
                logical_owner_user_event_id = Identity(owner?.UserEventId), source_user_event_id = Identity(envelope.SourceUserEventId),
                recovery_attempt = envelope.Response.RecoveryAttempt,
                recovery_of_attempt_id = Identity(envelope.Response.RecoveryOfAttemptId),
                request_kind = Identity(kind), request_purpose = Identity(purpose), child_request = kind == "review",
                continuation_hint = purpose.Contains(":tool", StringComparison.Ordinal),
                provider = config.IsOpenAI ? "openai" : "deepseek", authentication_mode = config.UsesChatGPT ? "chatgpt" : "api_key",
                adapters = config.IsOpenAI ? config.UsesChatGPT ? new[] { "openai_responses", "chatgpt_plan" } : new[] { "openai_responses" } : Array.Empty<string>(),
                selection_generation = envelope.SelectionSnapshot?.Generation,
                wire = new { sha256 = ticket.WireHash, utf8_bytes = Utf8.GetByteCount(json), utf16_chars = json.Length,
                    body_file = bodySaved ? ticket.Stem + ".wire.json" : null, body_complete = bodySaved, redacted = false,
                    completeness = bodySaved ? "exact_serialized_application_body; excludes_headers_and_auth" : "metadata_only; no_body_saved" },
                budget = new { accepted = rejectionStage == "rejected_at_source" ? (bool?)null : budgetAccepted, rejection = Identity(rejection),
                    generic_limit_enabled = config.FinalOutboundMaxUtf8Bytes > 0,
                    generic_max_utf8_bytes = config.FinalOutboundMaxUtf8Bytes > 0 ? (int?)Math.Max(16384,config.FinalOutboundMaxUtf8Bytes) : null,
                    history_budget_enforced = owner?.ChatHistoryBudgetActive == true,
                    history_max_request_bytes = owner == null ? (long?)null : DialogueRequest.ChatHistoryMaxRequestBytes,
                    history_max_total_bytes = owner == null ? (long?)null : DialogueRequest.ChatHistoryMaxTotalBytes,
                    history_reserved_bytes_after_check = owner?.ChatHistoryReservedBytes },
                history_projection = historyProjection == null ? null : new {mode=Identity(historyProjection.Mode),
                    available_records=historyProjection.AvailableRecords,selected_records=historyProjection.SelectedRecords,
                    grouped_replies=historyProjection.GroupedReplies,projected_messages=historyProjection.ProjectedMessages,
                    projected_entries=historyProjection.ProjectedEntries,text_chars=historyProjection.TextChars},
                generated_public_answer_sources = owner?.GeneratedReplySupply.Select(source=>new {
                    key=Identity(source.Key), request_id=Identity(source.RequestId), attempt_id=Identity(source.AttemptId),
                    body_sha256=source.BodyHash, body_chars=source.BodyText.Length, version=source.VersionFingerprint,
                    scope="captured_supply_plan;actual_body_or_refs_are_in_final_wire;not_a_player_read_receipt",
                    source.Completeness, source.EpistemicStatus,
                    shown_records=source.DisplayRanges.Count, delivery_outcome=Identity(source.Metadata.DeliveryOutcome)
                }).ToArray(),
                history_read_window = DescribeReadWindow(history),
                structure,
                evidence_scope = "final_application_body_after_selection_notice_adapters_and_budget_before_attempt_auth; planned_is_not_sent"
            };
            WriteNew(Path.Combine(ticket.Directory, ticket.Stem + ".manifest.json"), Utf8.GetBytes(JsonSerializer.Serialize(manifest)));
            ticket.ManifestSaved = true;
            ticket.Diagnostic.FinalOutboundObservationState = accepted ? "planned_" + mode : rejectionStage + "_manifest";
            ticket.Diagnostic.FinalOutboundObservationFile = ticket.Stem + ".manifest.json";
            Event(ticket, accepted ? "planned" : rejectionStage, null, ticket.WireHash);
        }
        catch (Exception error) { Fail(ticket, "planned", error); }
        return ticket;
    }

    private static object DescribeReadWindow(IReadOnlyList<MemoryTurn>? history)
    {
        if (history is not HistoryWindowRecords view)
            return new { coverage = "unknown", scope = "no_prior_window_provenance_on_history_argument" };
        var p = view.Provenance;
        return new { coverage = "recorded_native_aware_selection", stage = p.Stage, scope = p.Scope,
            recent_read_records = p.RecentReadRecords, ordinary_eligible_records = p.OrdinaryEligibleRecords,
            ordinary_message_budget = p.OrdinaryBudget, reserved_recall_records = p.ReservedRecallRecords,
            ordinary_selected_records = p.OrdinarySelectedRecords, initially_returned_records = p.InitiallyReturnedRecords,
            current_records = p.CurrentRecords, source_root_overflow = p.SourceRootOverflow,
            revalidation_removed_records = p.RevalidationRemovedRecords,
            final_selection_removed_records = p.FinalSelectionRemovedRecords,
            window_reason = p.WindowReason, reasons = p.Reasons };
    }

    internal static void DispatchStarted(FinalOutboundTicket? ticket, string json)
    {
        if (ticket == null) return;
        ticket.DispatchStarted = true;
        Event(ticket, "dispatch_started", null, Hash(json));
    }
    internal static void Terminal(FinalOutboundTicket? ticket, ApiResponseDiagnostic diagnostic)
    {
        if (ticket != null) Event(ticket, "terminal", diagnostic, ticket.WireHash);
    }
    private static void Event(FinalOutboundTicket ticket, string stage, ApiResponseDiagnostic? diagnostic, string currentHash)
    {
        try
        {
            string line = JsonSerializer.Serialize(new
            {
                schema_version = 1, timestamp_utc = DateTimeOffset.UtcNow, observation_id = ticket.Stem,
                request_id = ticket.RequestId, attempt_id = ticket.AttemptId, stage,
                dispatch_started = ticket.DispatchStarted, wire_sha256 = currentHash,
                observation_manifest_saved = ticket.ManifestSaved, observation_body_saved = ticket.BodySaved,
                observation_failure_stage = ticket.FailureStage, observation_error_type = ticket.ErrorType,
                matches_planned_wire = currentHash == ticket.WireHash,
                outcome = diagnostic == null ? null : Identity(diagnostic.Outcome),
                http_status = diagnostic?.HttpStatus, headers_received = diagnostic?.HeadersReceived,
                response_body_complete = diagnostic?.BodyComplete,
                evidence_scope = stage == "dispatch_started" ? "synchronous_http_send_invocation_about_to_start; not_server_receipt" : "local_observation"
            });
            File.AppendAllText(Path.Combine(ticket.Directory, ticket.Stem + ".events.jsonl"), line + "\n", Utf8);
        }
        catch (Exception error) { Fail(ticket, stage, error); }
    }
    private static void WriteNew(string path, byte[] bytes)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        file.Write(bytes); file.Flush(true);
    }
    private static void Fail(FinalOutboundTicket ticket, string stage, Exception error)
    {
        LastFailure = "stage=" + stage + ";request=" + ticket.RequestId + ";attempt=" + ticket.AttemptId
            + ";error_type=" + error.GetType().Name;
        ticket.FailureStage = stage; ticket.ErrorType = error.GetType().Name;
        ticket.Diagnostic.FinalOutboundObservationState = "write_failed:" + stage + ":" + error.GetType().Name;
        // No ConfigStore.Load/NativeEventLog call here: even diagnostic failure
        // on an offline capture must not trigger configuration or auth loading.
        try { Plugin.PluginLog?.LogWarning("LilithBridge final outbound observation failed; " + LastFailure); } catch { }
        try { FailureForTests?.Invoke(LastFailure); } catch { }
    }
}

internal sealed partial class DeepSeekClient
{
    // Test-only fail-closed seam. A non-null callback ALWAYS ends this send
    // before SendCompletionAttemptAsync, including callback failure. Never UI/config.
    internal static Action<string>? FinalOutboundCaptureForTests { get; set; }
    internal static Action<string>? FinalOutboundRejectedForTests { get; set; }
    private static void ObserveFinalOutboundRejectionForTests(string reason,long bytes,long? limit,string stage="rejected_at_budget")
    {
        try { FinalOutboundRejectedForTests?.Invoke(JsonSerializer.Serialize(new
            {stage,reason,utf8_bytes=bytes,limit_bytes=limit,body_included=false})); } catch { }
    }
    private static bool StopAtFinalOutboundForTests(string json)
    {
        var capture = FinalOutboundCaptureForTests;
        if (capture == null) return false;
        try { capture(json); } catch { /* Still stop: never fall through to auth/network. */ }
        return true;
    }
}
