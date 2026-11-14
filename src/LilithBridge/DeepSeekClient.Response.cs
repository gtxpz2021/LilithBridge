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
    public string Provider { get; set; } = "deepseek";
    public string AuthenticationMode { get; set; } = "";
    public string RequestedModel { get; set; } = "";
    public string ReasoningEffort { get; set; } = "";
    public int MaxOutputTokens { get; set; }
    public string TerminalEvent { get; set; } = "";
    public string IncompleteReason { get; set; } = "";
    public string ApiErrorCode { get; set; } = "";
    public string ApiErrorType { get; set; } = "";
    public string ApiErrorParam { get; set; } = "";
    public string ApiErrorBodyShape { get; set; } = "";
    public string ResponseMediaType { get; set; } = "";
    public string ResponseContentEncoding { get; set; } = "";
    public string ResponseBodyFormat { get; set; } = "";
    public long? FirstEventMs { get; set; }
    public long? FirstTextDeltaMs { get; set; }
    public long? LastEventMs { get; set; }
    public int TextDeltaChars { get; set; }
    public int StreamEvents { get; set; }
    public int BackgroundFailures { get; set; }
    public DateTimeOffset? BackgroundResumeAtUtc { get; set; }
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
    public string FinalOutboundObservationState { get; set; } = "off";
    public string FinalOutboundObservationFile { get; set; } = "";
    public bool SameAsLastAssistantRecord { get; set; }
    public string Phase { get; set; } = "not_sent";
    public bool HeadersReceived { get; set; }
    public bool BodyComplete { get; set; }
    public bool UsageReceived { get; set; }
    // Per-attempt observations: false also covers absent/null/non-integer fields.
    // Legacy integer counters remain unchanged for existing aggregate consumers.
    public bool CacheHitTokensReported { get; set; }
    public bool CacheMissTokensReported { get; set; }
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
        HistoryProjectionSummary? historyProjection = null,ReviewSourceSnapshot? reviewSources = null,
        FinalOutboundTicket? outboundObservation = null)
    {
        ApiResponseDiagnostic diagnostic = envelope.Response;
        var stopwatch = Stopwatch.StartNew();
        if (config.ResponseObservationEnabled && kind == "main" && envelope.DialogueRequest != null)
            diagnostic.ObservationOutcome = "no_complete_response";
        try
        {
            if (!envelope.SelectionCurrent()) return RejectCompletion(envelope, "selection_changed", "Creator selection snapshot changed; response or request is stale");
            if(!envelope.RequestSourcesPermitted())return RejectCompletion(envelope,"recall_source_changed","补充资料已撤回或变更，本轮未继续发送");
            if(config.SendPermission is {Allowed:false} permission)
                return RejectCompletion(envelope,"authorization_revoked",RequestSendPermissions.Notice(permission.Reason));
            if(config.UsesChatGPT)
            {
                if(requestUrl != OpenAISettings.ResponsesUrl) throw new InvalidOperationException("Unexpected OAuth resource");
                using var auth = _chatgptAuthFactory?.Invoke(config.DataDirectory) ?? new ChatGPTAuth(config.DataDirectory);
                apiKey = await auth.TokenForModelAsync(config.ChatGPTBinding, model, budgetToken).ConfigureAwait(false);
                if(config.SendPermission is {Allowed:false} changed)
                    return RejectCompletion(envelope,"authorization_revoked",RequestSendPermissions.Notice(changed.Reason));
            }
            using var request = new HttpRequestMessage(HttpMethod.Post, requestUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            diagnostic.TimeoutSeconds=timeoutSeconds;
            budgetToken.ThrowIfCancellationRequested();
            if (kind == "main" && envelope.MemoResultInput?.Permitted(envelope.MemoResultSources) == false)
                return RejectCompletion(envelope, "source_permission_changed", "Undelivered memo draft source permission changed before send");
            if(kind=="main" && envelope.DialogueRequest is { } reviewOwner
                && !reviewOwner.ReviewSupplyPermitted())
                return RejectCompletion(envelope,"source_permission_changed","Review source permission changed before send");
            if(kind=="main" && envelope.DialogueRequest is { } methodRequest && !MethodProtocolPermitted(methodRequest))
                return RejectCompletion(envelope,"method_source_changed","Method source changed before send");
            if (envelope.DialogueRequest?.TryBeginHttp(kind=="review") == false)
                return RejectCompletion(envelope, "attempt_limit", "Dialogue HTTP attempt limit reached");
            cancellationToken.ThrowIfCancellationRequested();
            using(PreparationTiming.Measure("diagnostic.prompt_dump"))
                if(!config.IsOpenAI && !config.SessionReadMethodsEnabled) PromptDumpSink.Write(config, kind, json, runtimeBlocks, purpose, envelope.RequestId,
                    diagnostic.AttemptId, historyProjection, envelope.HistoryBoundary);
            Task<HttpResponseMessage> DispatchCore()
            {
                if(!envelope.RequestSourcesPermitted(includeChatHistory:false))throw new SemanticMaterialException("recall_source_changed");
                if(config.SendPermission is {Allowed:false})throw new OperationCanceledException(budgetToken);
                if (kind == "main" && envelope.MemoResultInput?.Permitted(envelope.MemoResultSources) == false)
                    throw new SemanticMaterialException("source_permission_changed");
                diagnostic.Outcome = "started";
                diagnostic.Phase = "sending_headers";
                if(kind!="review" && !(kind=="reflection" && envelope.ReflectionDispatch!=null)
                    &&envelope.DialogueRequest?.HasChatHistorySupply!=true)TraceCompletion("model.request", model, kind, purpose, envelope);
                diagnostic.HttpSendStartedMs = stopwatch.ElapsedMilliseconds;
                envelope.ReflectionSend?.Entered();
                if(kind=="main"&&envelope.DialogueRequest is { } materialOwner)
                {
                    materialOwner.CommitReflectionPurposeInput();
                    BindDispatchedReflectionExpressionBasis(materialOwner,envelope);
                    if(materialOwner.PendingPayloadIncludesB1)materialOwner.MarkB1Supplied();
                    materialOwner.SemanticMaterial?.ObserveHttp(diagnostic.AttemptId,"started");
                }
                PreparationTiming.Current?.Mark("http.dispatch");
                if(kind=="main")envelope.DialogueRequest?.WaitStatus.WaitingForReply();
                if(config.IsOpenAI&&kind=="main")envelope.DialogueRequest?.WaitStatus.ReportStream("connecting",0);
                FinalOutboundObservation.DispatchStarted(outboundObservation,json);
                if(kind=="main")envelope.DialogueRequest?.ObserveB1ReadIndexDispatch(json,diagnostic.AttemptId);
                if(kind=="main")envelope.DialogueRequest?.ObserveWorkReferencePositionDispatch(json);
                return (config.UsesChatGPT?_planHttp:_http).SendAsync(request,HttpCompletionOption.ResponseHeadersRead,budgetToken);
            }
            Task<HttpResponseMessage> Dispatch()
            {
                SelectionBoundaryForTests?.Invoke("before_dispatch");
                Task<HttpResponseMessage>? started = null;
                if (!envelope.TryUseSelection(() =>
                {
                    var snapshot = envelope.SelectionSnapshot;
                    if (snapshot != null && !snapshot.Store.ObserveB1SelectionRequest(snapshot,
                        diagnostic.AttemptId, "supplied")) return false;
                    // Prepared historical union uses the same synchronous
                    // dispatch boundary, not merely an earlier boolean probe.
                    if(envelope.DialogueRequest is {} preparedOwner)
                    {
                        if(!preparedOwner.TryUseInitialExperienceDispatch(()=>{started=DispatchCore();return true;}))
                            throw new SemanticMaterialException("recall_source_changed");
                    }
                    else started=DispatchCore(); // Never await network under a source boundary.
                    if (snapshot != null && !snapshot.Store.ObserveB1SelectionRequest(snapshot,
                        diagnostic.AttemptId, "started")) throw new SemanticMaterialException("selection_evidence_failed");
                    return true;
                })) throw new SemanticMaterialException("selection_changed");
                return started!;
            }
            Task<HttpResponseMessage>? dispatched=null;
            if(kind=="review")
            {
                ReviewSourceBoundaryForTests?.Invoke("before_dispatch");
                if(!TryUseReviewAndChatHistory(envelope,reviewSources,()=>
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
                if (envelope.SharedSupply != null || envelope.SharedDispatch != null || envelope.DialogueRequest?.HasChatHistorySupply==true)
                    return RejectCompletion(envelope, "reflection_source_changed", "Mismatched reflection and shared-activity owners");
                envelope.ReflectionSend?.HandedOff();
                dispatched = await envelope.ReflectionDispatch(Dispatch, budgetToken).ConfigureAwait(false);
                if (dispatched == null)
                {
                    envelope.ReflectionSend?.ReturnedWithoutSend();
                    return RejectCompletion(envelope, "reflection_source_changed", "Reflection source changed before send");
                }
            }
            else
            {
                if(envelope.SharedSupply != null || envelope.SharedDispatch != null) SharedDispatchBoundaryForTests?.Invoke();
                // Prepare configuration outside both source and shared-object locks.
                // Acquire the shared gate first: its source adapter may itself
                // consult MemoryStore. Reenter it under the source lock for the
                // final shared/source check and synchronous start.
                if(envelope.DialogueRequest?.HasChatHistorySupply==true)LegacyArchiveBoundaryForTests?.Invoke("before_main_dispatch");
                var historyUse=envelope.DialogueRequest is {HasChatHistorySupply:true} historyOwner
                    ?historyOwner.PrepareChatHistoryUse():null;
                if(!(historyUse!=null
                    ?TrySharedDispatch(envelope,()=>historyUse(()=>TrySharedDispatch(envelope, () => { dispatched=Dispatch(); return true; })))
                    : TrySharedDispatch(envelope, () => { dispatched=Dispatch(); return true; })))
                    return envelope.DialogueRequest?.HasDayArtifactSupply==true&&envelope.SharedSupply==null&&envelope.SharedDispatch==null
                        ?RejectCompletion(envelope,"reading_material_changed","Reading material permission, version or request ownership changed before send")
                        :RejectCompletion(envelope,"shared_activity_changed","Shared activity permission or revision changed before send");
            }
            // Logging loads configuration. Keep it outside the review source /
            // capability locks (configuration publication takes the reverse lock).
            if(kind=="review" || kind=="reflection" && envelope.ReflectionDispatch!=null
                || envelope.DialogueRequest?.HasChatHistorySupply==true)TraceCompletion("model.request", model, kind, purpose, envelope);
            using HttpResponseMessage response = await dispatched!.ConfigureAwait(false);
            diagnostic.HeadersReceived = true;
            diagnostic.HeadersReceivedMs = stopwatch.ElapsedMilliseconds;
            diagnostic.Phase = "headers_received";
            diagnostic.HttpStatus = (int)response.StatusCode;
            if (response.Headers.TryGetValues("x-request-id", out var ids))
                diagnostic.HeaderRequestId = DiagnosticToken(ids.FirstOrDefault());
            if (!response.IsSuccessStatusCode)
            {
                if(diagnostic.HttpStatus==401)config.SendPermission?.Revoke("credentials_invalid");
                if(config.IsOpenAI) await OpenAIProtocol.ReadHttpErrorAsync(response,envelope,budgetToken).ConfigureAwait(false);
                if(config.UsesChatGPT && diagnostic.ApiErrorCode is "subscription_sharing_usage_limit_exceeded" or "subscription_sharing_usage_unavailable")
                    return RejectCompletion(envelope,"chatgpt_usage_limit","ChatGPT 计划用量不可用或达到限制；请打开认证账户页的 Manage usage。没有改用付费 API。");
                if(config.UsesChatGPT)return RejectCompletion(envelope,"http_error",ChatGPTProtocol.HttpError(diagnostic.HttpStatus));
                return RejectCompletion(envelope, "http_error", config.IsOpenAI
                    ? OpenAISettings.HttpError(diagnostic.HttpStatus) : "HTTP " + diagnostic.HttpStatus);
            }
            if(config.IsOpenAI && kind=="main")envelope.DialogueRequest?.WaitStatus.ReportStream("headers",0);
            diagnostic.Phase = "reading_body";
            string raw = config.IsOpenAI
                ? await OpenAIProtocol.ReadResponseAsync(response, budgetToken, envelope,
                    kind=="main" ? envelope.DialogueRequest?.WaitStatus : null, () => stopwatch.ElapsedMilliseconds,
                    requireStreamCompletion:config.UsesChatGPT,publicStream:BeginPublicItemStream(config,kind,envelope)).ConfigureAwait(false)
                : await response.Content.ReadAsStringAsync(budgetToken).ConfigureAwait(false);
            diagnostic.BodyComplete = true;
            SelectionBoundaryForTests?.Invoke("response_received");
            if (envelope.SelectionSnapshot is { } receivedSnapshot
                && !receivedSnapshot.Store.ObserveB1SelectionRequest(receivedSnapshot, diagnostic.AttemptId, "response_received"))
                return RejectCompletion(envelope, "selection_evidence_failed", "Response arrived but selection evidence could not be recorded");
            if(config.UsesChatGPT) ChatGPTProtocol.ValidateCompletion(raw);
            diagnostic.BodyCompleteMs = stopwatch.ElapsedMilliseconds;
            if(!config.IsOpenAI)diagnostic.BodyUtf8Bytes = Encoding.UTF8.GetByteCount(raw);
            diagnostic.Phase = "validating_body";
            if (!envelope.SelectionCurrent()) return RejectCompletion(envelope, "selection_changed", "Creator selection snapshot changed; response or request is stale");
            if(!envelope.RequestSourcesPermitted())return RejectCompletion(envelope,"recall_source_changed","补充资料已撤回或变更，回复未交付");
            if(config.IsOpenAI && kind=="main")envelope.DialogueRequest?.WaitStatus.ReportStream("validating",diagnostic.TextDeltaChars);
            cancellationToken.ThrowIfCancellationRequested();
            if(config.SendPermission is {Allowed:false} revoked)
                return RejectCompletion(envelope,"authorization_revoked",RequestSendPermissions.Notice(revoked.Reason));
            try
            {
                if(kind=="main" && envelope.DialogueRequest is { } methodOwner && !MethodProtocolPermitted(methodOwner))
                    return RejectCompletion(envelope,"method_source_changed","Method source changed during response");
                if(!TrySharedDispatch(envelope, () => true))
                    return RejectCompletion(envelope,"shared_activity_changed","Shared activity changed during response");
                string? text=null;
                if(kind=="review")
                {
                    if(!TryUseReviewAndChatHistory(envelope,reviewSources,()=>
                    {text=ReadCompletion(raw,envelope,history);return true;}))
                        return RejectCompletion(envelope,"review_source_changed","Review source changed during response");
                }
                else text=ReadCompletion(raw,envelope,history);
                if (!envelope.TryUseSelection(() => true))
                    return RejectCompletion(envelope, "selection_changed", "Creator selection changed during response; result was not accepted");
                if (text != null && kind == "main") envelope.RawText = config.IsOpenAI ? OpenAIProtocol.WithoutReplay(raw) : raw;
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
                if(!config.IsOpenAI && !config.SessionReadMethodsEnabled) ResponseObservationSink.Write(config, kind, purpose, json, raw, envelope);
            }
        }
        catch (OperationCanceledException)
        {
            if(config.SendPermission is {Allowed:false} revoked)
                return RejectCompletion(envelope,"authorization_revoked",RequestSendPermissions.Notice(revoked.Reason));
            bool cancelled = cancellationToken.IsCancellationRequested;
            return RejectCompletion(envelope, cancelled ? "cancelled" : "timeout",
                cancelled ? "request cancelled" : "request timeout");
        }
        catch (SemanticMaterialException error)
        {return RejectCompletion(envelope,error.Outcome,"资料来源已变更，本轮未继续发送或交付");}
        catch (JsonException)
        {
            return RejectCompletion(envelope, "invalid_json", "API response was not valid JSON");
        }
        catch (OpenAIProtocolException error)
        {
            if(config.UsesChatGPT&&error.Code=="openai_incomplete")
                return RejectCompletion(envelope,"chatgpt_incomplete","ChatGPT 本轮未完整返回，未交付不完整回复；请检查模型选择、思考强度或计划用量。此路线不设置 API 输出上限。");
            return RejectCompletion(envelope, error.Code, error.Message);
        }
        catch (ChatGPTAuthException error)
        {
            return RejectCompletion(envelope, error.Code, error.Message);
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
            try{envelope.FinalPublicDraft?.End();}catch{try{NativeEventLog.Trace("work.final_progress.end_failed","mod","system","Final public progress checkpoint was not confirmed","");}catch{}}
            FinishPublicItemStream(envelope);
            if (envelope.SelectionSnapshot is { } finalSnapshot
                && diagnostic.Outcome is not ("accepted" or "accepted_without_finish_reason"))
            {
                try { finalSnapshot.Store.ObserveB1SelectionRequest(finalSnapshot, diagnostic.AttemptId,
                    diagnostic.Outcome == "cancelled" ? "cancelled" : "failed"); }
                catch { /* Failed evidence never marks a notice consumed; subsequent requests still include it. */ }
            }
            envelope.LatencyMs = stopwatch.ElapsedMilliseconds;
            if (diagnostic.BodyCompleteMs is { } bodyEnd)
                diagnostic.ValidationAndObservationMs = Math.Max(0, envelope.LatencyMs - bodyEnd);
            TraceCompletion("model.response", model, kind, purpose, envelope);
            FinalOutboundObservation.Terminal(outboundObservation,diagnostic);
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
                provider=d.Provider,authentication_mode=d.AuthenticationMode,requested_reasoning_effort=d.ReasoningEffort,max_output_tokens=d.MaxOutputTokens,
                terminal_event=d.TerminalEvent,incomplete_reason=d.IncompleteReason,api_error_code=d.ApiErrorCode,api_error_type=d.ApiErrorType,
                api_error_param=d.ApiErrorParam,api_error_body_shape=d.ApiErrorBodyShape,
                response_media_type=d.ResponseMediaType,response_content_encoding=d.ResponseContentEncoding,response_body_format=d.ResponseBodyFormat,
                first_stream_event_ms=d.FirstEventMs,first_text_delta_ms=d.FirstTextDeltaMs,last_stream_event_ms=d.LastEventMs,
                text_delta_chars=d.TextDeltaChars,stream_events=d.StreamEvents,
                background_failures=d.BackgroundFailures,background_resume_at_utc=d.BackgroundResumeAtUtc,
                recovery_attempt = d.RecoveryAttempt, recovery_of_attempt_id = d.RecoveryOfAttemptId,
                source_user_event_id = result.SourceUserEventId, request_kind = kind, request_purpose = purpose,
                dialogue_snapshot_mode = result.DialogueRequest?.Generation.Effort,
                dialogue_snapshot_selection = result.DialogueRequest?.Generation.Selection,
                dialogue_snapshot_budget = result.DialogueRequest?.Generation.MaxTokens,
                material_revision=result.DialogueRequest?.SemanticMaterial?.Current?.Revision,
                recall_supplements=result.DialogueRequest?.SemanticMaterial?.Current?.RecallSupplementCount,
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
                final_outbound_observation = d.FinalOutboundObservationState, final_outbound_manifest_file = d.FinalOutboundObservationFile,
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
                cache_miss = result.PromptCacheMissTokens,
                cache_hit_reported = d.CacheHitTokensReported, cache_miss_reported = d.CacheMissTokensReported
            }));
        }
        catch { /* Telemetry must not decide whether a response is delivered. */ }
    }
}
