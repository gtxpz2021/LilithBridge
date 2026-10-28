using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LilithBridge;

// Request-scoped transport evidence, owned by DeepSeekClient; never stored as memory.
internal sealed class ApiResponseDiagnostic
{
    public int RecoveryAttempt { get; set; }
    public string RecoveryOfAttemptId { get; set; } = "";
    public string AttemptId { get; } = Guid.NewGuid().ToString("N");
    public string ResponseId { get; set; } = "";
    public string ResponseModel { get; set; } = "";
    public string ModelFingerprint { get; set; } = "";
    public string HeaderRequestId { get; set; } = "";
    public string FinishReason { get; set; } = "missing";
    public string Outcome { get; set; } = "not_sent";
    public int HttpStatus { get; set; }
    public int ContentChars { get; set; }
    public string ContentSha256 { get; set; } = "";
    public string ObservationOutcome { get; set; } = "disabled";
    public string ObservationFile { get; set; } = "";
    public bool SameAsLastAssistantRecord { get; set; }
    public string Phase { get; set; } = "not_sent";
    public bool HeadersReceived { get; set; }
    public bool BodyComplete { get; set; }
    public bool UsageReceived { get; set; }
    public long? HttpSendStartedMs { get; set; }
    public long? HeadersReceivedMs { get; set; }
    public long? BodyCompleteMs { get; set; }
    public long? ValidationAndObservationMs { get; set; }
    public int? BodyUtf8Bytes { get; set; }
    public int TimeoutSeconds { get; set; }
    public string TransportErrorType { get; set; } = "";
    public int TransportHResult { get; set; }
    public string InnerErrorType { get; set; } = "";
    public int InnerHResult { get; set; }
}

internal sealed partial class DeepSeekClient
{
    private static string SerializeTimed(object payload)
    {using var timing=PreparationTiming.Measure("payload.serialize");return JsonSerializer.Serialize(payload);}
    // Both dialogue and auxiliary tasks must validate transport completion before
    // interpreting text as speech, actions, memories, reflections or day products.
    private async Task<string?> SendCompletionAttemptAsync(
        BridgeConfig config, string model, string json, string kind, string purpose,
        ApiResult envelope, CancellationToken cancellationToken,
        CancellationToken budgetToken, string requestUrl, string apiKey, int timeoutSeconds,
        IReadOnlyList<string>? runtimeBlocks = null, IReadOnlyList<MemoryTurn>? history = null,
        HistoryProjectionSummary? historyProjection = null,ReviewSourceSnapshot? reviewSources = null)
    {
        ApiResponseDiagnostic diagnostic = envelope.Response;
        var stopwatch = Stopwatch.StartNew();
        if (config.ResponseObservationEnabled && kind == "main" && envelope.DialogueRequest != null)
            diagnostic.ObservationOutcome = "no_complete_response";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, requestUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            diagnostic.TimeoutSeconds=timeoutSeconds;
            budgetToken.ThrowIfCancellationRequested();
            if(kind=="main" && envelope.DialogueRequest is { } reviewOwner
                && !reviewOwner.ReviewSupplyPermitted())
                return RejectCompletion(envelope,"source_permission_changed","Review source permission changed before send");
            if(kind=="main" && envelope.DialogueRequest is { } methodRequest && !MethodProtocolPermitted(methodRequest))
                return RejectCompletion(envelope,"method_source_changed","Method source changed before send");
            if (envelope.DialogueRequest?.TryBeginHttp(kind=="review") == false)
                return RejectCompletion(envelope, "attempt_limit", "Dialogue HTTP attempt limit reached");
            cancellationToken.ThrowIfCancellationRequested();
            using(PreparationTiming.Measure("diagnostic.prompt_dump"))
                if(!config.SessionReadMethodsEnabled) PromptDumpSink.Write(config, kind, json, runtimeBlocks, purpose, envelope.RequestId,
                    diagnostic.AttemptId, historyProjection, envelope.HistoryBoundary);
            Task<HttpResponseMessage> Dispatch()
            {
                diagnostic.Outcome = "started";
                diagnostic.Phase = "sending_headers";
                if(kind!="review" && !(kind=="reflection" && envelope.ReflectionDispatch!=null))TraceCompletion("model.request", model, kind, purpose, envelope);
                diagnostic.HttpSendStartedMs = stopwatch.ElapsedMilliseconds;
                if(kind=="main"&&envelope.DialogueRequest is { } materialOwner)
                {
                    if(materialOwner.PendingPayloadIncludesB1)materialOwner.MarkB1Supplied();
                    materialOwner.SemanticMaterial?.ObserveHttp(diagnostic.AttemptId,"started");
                }
                PreparationTiming.Current?.Mark("http.dispatch");
                if(kind=="main")envelope.DialogueRequest?.WaitStatus.WaitingForReply();
                return _http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,budgetToken);
            }
            Task<HttpResponseMessage>? dispatched=null;
            if(kind=="review")
            {
                ReviewSourceBoundaryForTests?.Invoke("before_dispatch");
                if(reviewSources==null||!reviewSources.TryUse(()=>
                {
                    budgetToken.ThrowIfCancellationRequested();
                    dispatched=Dispatch();
                    return true;
                }))return RejectCompletion(envelope,"review_source_changed","Review source changed before send");
            }
            else if (kind == "reflection" && envelope.ReflectionDispatch != null)
            {
                // Reflection has its own source owner; shared-object material is
                // supplied only to its explicit shared/main request consumers.
                if (envelope.SharedSupply != null || envelope.SharedDispatch != null)
                    return RejectCompletion(envelope, "reflection_source_changed", "Mismatched reflection and shared-activity owners");
                dispatched = await envelope.ReflectionDispatch(Dispatch, budgetToken).ConfigureAwait(false);
                if (dispatched == null) return RejectCompletion(envelope, "reflection_source_changed", "Reflection source changed before send");
            }
            else
            {
                if(envelope.SharedSupply != null || envelope.SharedDispatch != null) SharedDispatchBoundaryForTests?.Invoke();
                if(!TrySharedDispatch(envelope, () => { dispatched=Dispatch(); return true; }))
                    return RejectCompletion(envelope,"shared_activity_changed","Shared activity permission or revision changed before send");
            }
            // Logging loads configuration. Keep it outside the review source /
            // capability locks (configuration publication takes the reverse lock).
            if(kind=="review" || kind=="reflection" && envelope.ReflectionDispatch!=null)TraceCompletion("model.request", model, kind, purpose, envelope);
            using HttpResponseMessage response = await dispatched!.ConfigureAwait(false);
            diagnostic.HeadersReceived = true;
            diagnostic.HeadersReceivedMs = stopwatch.ElapsedMilliseconds;
            diagnostic.Phase = "headers_received";
            diagnostic.HttpStatus = (int)response.StatusCode;
            if (response.Headers.TryGetValues("x-request-id", out var ids))
                diagnostic.HeaderRequestId = DiagnosticToken(ids.FirstOrDefault());
            if (!response.IsSuccessStatusCode)
                return RejectCompletion(envelope, "http_error", "HTTP " + diagnostic.HttpStatus);
            diagnostic.Phase = "reading_body";
            string raw = await response.Content.ReadAsStringAsync(budgetToken).ConfigureAwait(false);
            diagnostic.BodyComplete = true;
            diagnostic.BodyCompleteMs = stopwatch.ElapsedMilliseconds;
            diagnostic.BodyUtf8Bytes = Encoding.UTF8.GetByteCount(raw);
            diagnostic.Phase = "validating_body";
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if(kind=="main" && envelope.DialogueRequest is { } methodOwner && !MethodProtocolPermitted(methodOwner))
                    return RejectCompletion(envelope,"method_source_changed","Method source changed during response");
                if(!TrySharedDispatch(envelope, () => true))
                    return RejectCompletion(envelope,"shared_activity_changed","Shared activity changed during response");
                string? text=null;
                if(kind=="review")
                {
                    if(reviewSources==null||!reviewSources.TryUse(()=>
                    {text=ReadCompletion(raw,envelope,history);return true;}))
                        return RejectCompletion(envelope,"review_source_changed","Review source changed during response");
                }
                else text=ReadCompletion(raw,envelope,history);
                if (text != null && kind == "main") envelope.RawText = raw;
                return text;
            }
            catch (JsonException)
            {
                return RejectCompletion(envelope, "invalid_json", "API response was not valid JSON");
            }
            finally
            {
                // Observe the provider's returned fields even when completion
                // validation rejects them. Never feed observation text back.
                if(!config.SessionReadMethodsEnabled) ResponseObservationSink.Write(config, kind, purpose, json, raw, envelope);
            }
        }
        catch (OperationCanceledException)
        {
            bool cancelled = cancellationToken.IsCancellationRequested;
            return RejectCompletion(envelope, cancelled ? "cancelled" : "timeout",
                cancelled ? "request cancelled" : "request timeout");
        }
        catch (JsonException)
        {
            return RejectCompletion(envelope, "invalid_json", "API response was not valid JSON");
        }
        catch (HttpRequestException error)
        {
            diagnostic.TransportErrorType=DiagnosticToken(error.GetType().Name);
            diagnostic.TransportHResult=error.HResult;
            Exception inner=error.GetBaseException();
            diagnostic.InnerErrorType=DiagnosticToken(inner.GetType().Name);
            diagnostic.InnerHResult=inner.HResult;
            if(diagnostic.HttpStatus==0 && error.StatusCode.HasValue)diagnostic.HttpStatus=(int)error.StatusCode.Value;
            return RejectCompletion(envelope, "transport_error", "API transport failed");
        }
        catch (Exception)
        {
            return RejectCompletion(envelope, "response_error", "API response could not be processed");
        }
        finally
        {
            envelope.LatencyMs = stopwatch.ElapsedMilliseconds;
            if (diagnostic.BodyCompleteMs is { } bodyEnd)
                diagnostic.ValidationAndObservationMs = Math.Max(0, envelope.LatencyMs - bodyEnd);
            TraceCompletion("model.response", model, kind, purpose, envelope);
        }
    }

    private static string? ReadCompletion(string raw, ApiResult envelope, IReadOnlyList<MemoryTurn>? history)
    {
        using JsonDocument doc = JsonDocument.Parse(raw);
        JsonElement root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return RejectCompletion(envelope, "invalid_response", "API response was not an object");
        ApiResponseDiagnostic diagnostic = envelope.Response;
        diagnostic.ResponseId = ReadDiagnosticToken(root, "id");
        diagnostic.ResponseModel = ReadDiagnosticToken(root, "model");
        diagnostic.ModelFingerprint = ReadDiagnosticToken(root, "system_fingerprint");
        diagnostic.UsageReceived=root.TryGetProperty("usage",out var observedUsage) && observedUsage.ValueKind==JsonValueKind.Object;
        ApplyUsage(root, envelope);
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0 || choices[0].ValueKind != JsonValueKind.Object)
            return RejectCompletion(envelope, "invalid_response", "API response contained no choice");
        JsonElement choice = choices[0];
        string? reason = null;
        if (choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind != JsonValueKind.Null)
        {
            if (finish.ValueKind != JsonValueKind.String)
            {
                diagnostic.FinishReason = "invalid_type";
                return RejectCompletion(envelope, "invalid_response", "API finish_reason was not a string");
            }
            reason = finish.GetString();
        }
        diagnostic.FinishReason = string.IsNullOrEmpty(reason) ? "missing" : DiagnosticToken(reason);
        string? text = ExtractText(root);
        if (text != null)
        {
            diagnostic.ContentChars = text.Length;
            diagnostic.ContentSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
            MemoryTurn? previous = history?.LastOrDefault(t => t.Role == "assistant");
            // Observation only: a legitimate repetition must not be rewritten or dropped.
            diagnostic.SameAsLastAssistantRecord = previous != null && !string.IsNullOrWhiteSpace(text)
                && MemoryStore.Clean(text) == MemoryStore.Clean(previous.Text);
        }
        // Only stop establishes a supported completed response. Missing metadata is
        // explicitly unknown but stays compatible with existing OpenAI-style providers.
        // No automatic retry: a rejected response must not multiply the boundary retry budget.
        if (reason == "tool_calls" && envelope.NativeToolsSent)
            return ReadNativeToolCall(choice, envelope);
        if (envelope.NativeToolsSent && string.IsNullOrEmpty(reason))
            return RejectCompletion(envelope, "invalid_response", "Native tool protocol requires a finish reason");
        if (!string.IsNullOrEmpty(reason) && reason != "stop")
            return RejectCompletion(envelope, "rejected_finish_reason", "API completion rejected: " + diagnostic.FinishReason);
        if (choice.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object
            && ((message.TryGetProperty("tool_calls", out var calls) && calls.ValueKind != JsonValueKind.Null
                 && (calls.ValueKind != JsonValueKind.Array || calls.GetArrayLength() > 0))
                || (message.TryGetProperty("function_call", out var call) && call.ValueKind != JsonValueKind.Null)))
            return RejectCompletion(envelope, "unsupported_tool_calls", "API returned unsupported tool calls");
        if (string.IsNullOrWhiteSpace(text))
        {
            if (reason == "stop" && message.ValueKind == JsonValueKind.Object
                && message.TryGetProperty("reasoning_content", out var reasoning)
                && reasoning.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(reasoning.GetString())
                && (!message.TryGetProperty("content", out var final) || final.ValueKind == JsonValueKind.Null
                    || (final.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(final.GetString()))))
                return RejectCompletion(envelope, "reasoning_only_stop", "API stopped with reasoning but no final content");
            return RejectCompletion(envelope, "empty_or_unsupported_content", "API response contained no supported message.content");
        }
        if (choice.TryGetProperty("message", out JsonElement acceptedMessage)
            && acceptedMessage.ValueKind == JsonValueKind.Object
            && acceptedMessage.TryGetProperty("reasoning_content", out JsonElement acceptedReasoning)
            && acceptedReasoning.ValueKind == JsonValueKind.String)
        {
            envelope.ReasoningContent = acceptedReasoning.GetString() ?? "";
        }
        diagnostic.Outcome = string.IsNullOrEmpty(reason) ? "accepted_without_finish_reason" : "accepted";
        return text;
    }

    private static string? RejectCompletion(ApiResult envelope, string outcome, string error)
    {
        envelope.Response.Outcome = outcome;
        envelope.Error = error;
        return null;
    }

    private static string ReadDiagnosticToken(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? DiagnosticToken(value.GetString()) : "";

    private static string DiagnosticToken(string? value) => new string((value ?? "").Take(128)
        .Select(c => c <= 0x7f && (char.IsLetterOrDigit(c) || "-_.:/".Contains(c)) ? c : '_').ToArray());

    private static void TraceCompletion(string eventKind, string model, string kind, string purpose, ApiResult result)
    {
        try
        {
            ApiResponseDiagnostic d = result.Response;
            // Deliberately exclude prompts, content, reasoning, credentials and HTTP error bodies.
            NativeEventLog.Trace(eventKind, "api", "system", "model transport", JsonSerializer.Serialize(new
            {
                schema_version = 1, request_id = result.RequestId, attempt_id = d.AttemptId,
                recovery_attempt = d.RecoveryAttempt, recovery_of_attempt_id = d.RecoveryOfAttemptId,
                source_user_event_id = result.SourceUserEventId, request_kind = kind, request_purpose = purpose,
                dialogue_snapshot_mode = result.DialogueRequest?.Generation.Effort,
                dialogue_snapshot_selection = result.DialogueRequest?.Generation.Selection,
                dialogue_snapshot_budget = result.DialogueRequest?.Generation.MaxTokens,
                requested_model = DiagnosticToken(model), response_model = d.ResponseModel,
                response_id = d.ResponseId, header_request_id = d.HeaderRequestId,
                http_status = d.HttpStatus, finish_reason = d.FinishReason, outcome = d.Outcome,
                response_phase=d.Phase,headers_received=d.HeadersReceived,body_complete=d.BodyComplete,
                usage_received=d.UsageReceived,timeout_seconds=d.TimeoutSeconds,
                transport_error_type=d.TransportErrorType,transport_hresult=d.TransportHResult,
                inner_error_type=d.InnerErrorType,inner_hresult=d.InnerHResult,
                content_chars = d.ContentChars, content_sha256 = d.ContentSha256,
                same_as_last_assistant_record = d.SameAsLastAssistantRecord,
                response_observation = d.ObservationOutcome, observation_file = d.ObservationFile,
                latency_ms = result.LatencyMs, prompt_tokens = result.PromptTokens,
                http_send_started_ms = d.HttpSendStartedMs, headers_received_ms = d.HeadersReceivedMs,
                body_complete_ms = d.BodyCompleteMs, validation_and_observation_ms = d.ValidationAndObservationMs,
                body_utf8_bytes = d.BodyUtf8Bytes,
                timing_scope = "per_attempt_elapsed; headers are not first-byte, thinking or content timestamps",
                completion_tokens = result.CompletionTokens, reasoning_tokens = result.ReasoningTokens,
                dialogue_effort = kind == "main" ? result.DialogueRequest?.Generation.Effort ?? "legacy" : "auxiliary",
                dialogue_carrier = kind == "main" && (purpose == "proactive" || purpose.StartsWith("proactive:", StringComparison.Ordinal))
                    ? "source_envelope" : result.DialogueRequest?.NativeTools == true ? "native_screen_tools"
                    : result.DialogueRequest?.SourceEnvelope == true ? "source_envelope" : "legacy",
                native_tools_sent = result.NativeToolsSent,
                model_fingerprint = result.Response.ModelFingerprint, cache_hit = result.PromptCacheHitTokens,
                cache_miss = result.PromptCacheMissTokens
            }));
        }
        catch { /* Telemetry must not decide whether a response is delivered. */ }
    }
}
