using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace LilithBridge;

internal static class TransportRetryPolicy
{
    internal const int MinimumRemainingMs = 1000;
    internal const int PauseMs = 250;
    internal static bool Retryable(ApiResponseDiagnostic d) => d.Outcome == "transport_error"
        && !d.BodyComplete && d.ContentChars == 0 && d.FinishReason == "missing"
        && (d.HttpStatus == 0 || d.HttpStatus is >= 200 and < 300)
        && d.TransportErrorType == "HttpRequestException"
        && d.InnerErrorType is "HttpRequestException" or "IOException" or "SocketException";
}

internal sealed partial class DeepSeekClient
{
    private async Task<string?> SendCompletionAsync(
        BridgeConfig config, string model, string json, string kind, string purpose,
        ApiResult envelope, CancellationToken cancellationToken,
        IReadOnlyList<string>? runtimeBlocks = null, IReadOnlyList<MemoryTurn>? history = null,
        HistoryProjectionSummary? historyProjection = null,ReviewSourceSnapshot? reviewSources = null)
    {
        // Serialize/build/retrieve once in the caller. Both sends use these exact bytes
        // and endpoint/credential values, even if the next request's config changes.
        if(kind=="main")config=envelope.DialogueRequest?.RequestConfig??envelope.GenerationConfig??config;
        envelope.SendPermission=config.SendPermission;
        if(config.SendPermission is {Allowed:false} denied)
            return RejectCompletion(envelope,"authorization_revoked",RequestSendPermissions.Notice(denied.Reason));
        if(config.SendPermission==null&&!_injectedTransport)
            return RejectCompletion(envelope,"authorization_revoked","请求没有当前有效的发送授权");
        // Replaced for each actual main payload, including native tool rounds;
        // remains attached to the response source-use checks.
        if(kind=="main")
        {
            var request=envelope.DialogueRequest; var readable=request?.ReadableB1;
            // Generated presentation proof is a pre-send check only. Its live
            // source/permission guard remains the existing generated supply guard;
            // do not widen the generic post-response B1 coupling here.
            if(request?.CurrentGeneratedReadable is {} generated&&!generated.ValidatePayload(json))
                return RejectCompletion(envelope,"readable_generated_source_changed","接收原文视图与实际请求不对应，本轮未发送");
            envelope.ReadableB1SourcePermission=null;
            envelope.ReadableB1SentView=null;
            if(readable!=null)
            {
                if(!readable.ValidatePayload(json,request!.B1Packet))
                    return RejectCompletion(envelope,"readable_source_changed","可读材料与实际请求不对应，本轮未发送");
                // Rendering/label consistency is checked above and at B1 commit.
                // Ordinary response use depends on the actually supplied sources,
                // not later mutation of this request's diagnostic or alias table.
                if(request!.ReadableB1SourcePermission==null)
                    return RejectCompletion(envelope,"readable_source_owner_unavailable","可读材料缺少原来源owner，本轮未发送");
                envelope.ReadableB1SourcePermission=request.ReadableB1SourcePermission;
                envelope.ReadableB1SentView=readable;
            }
        }
        CaptureSelection(config, envelope);
        try { json = IncludeSelectionNotice(json, envelope); }
        catch (Exception) { return RejectCompletion(envelope, "selection_changed", "Creator selection snapshot unavailable or changed; request was not sent"); }
        string url = config.ChatCompletionsUrl, apiKey = config.ApiKey;
        OpenAIRequestProfile? openAIProfile=null;
        if (config.IsOpenAI)
        {
            openAIProfile=OpenAITaskPolicy.Capture(config,kind);
            model=openAIProfile.Model;
            envelope.Response.Provider="openai";envelope.Response.RequestedModel=model;
            envelope.Response.AuthenticationMode=config.UsesChatGPT?"chatgpt":"api_key";
            envelope.Response.ReasoningEffort=openAIProfile.Effort;envelope.Response.MaxOutputTokens=openAIProfile.MaxOutputTokens;
            if (!config.OpenAIDataConsent)
                return RejectCompletion(envelope, "openai_consent_required", "OpenAI 数据发送尚未启用；请在模型设置查看说明后启用");
            if(kind!="main")
            {
                if(!config.OpenAIBackgroundEnabled || config.UsesChatGPT && !config.ChatGPTAllowBackground)
                    return RejectCompletion(envelope,"background_disabled","此认证方式的后台模型任务已关闭");
                if(!_openAIBackground.Allows(kind,openAIProfile,out int failures,out var until))
                {
                    envelope.Response.BackgroundFailures=failures;envelope.Response.BackgroundResumeAtUtc=until;
                    RejectCompletion(envelope,"background_backoff","后台任务失败后正在冷却，不再发起模型请求");
                    TraceCompletion("model.background_deferred",model,kind,purpose,envelope);
                    return null;
                }
            }
            try { json = OpenAIProtocol.BuildRequest(config, json, kind); if(config.UsesChatGPT)json=ChatGPTProtocol.Adapt(json); }
            catch (Exception) { return RejectCompletion(envelope, "openai_configuration", "OpenAI 模型或思考强度配置无效"); }
            url = OpenAISettings.ResponsesUrl;
        }
        long finalWireBytes=System.Text.Encoding.UTF8.GetByteCount(json);
        string sourceRejection=!envelope.SelectionCurrent()?"selection_changed":!envelope.RequestSourcesPermitted()?"recall_source_changed":"";
        if(sourceRejection.Length>0)
        {
            FinalOutboundObservation.Plan(config,json,kind,purpose,envelope,history,historyProjection,
                rejection:sourceRejection,rejectionStage:"rejected_at_source");
            ObserveFinalOutboundRejectionForTests(sourceRejection,finalWireBytes,null,"rejected_at_source");
            return RejectCompletion(envelope,sourceRejection,"资料来源已变更，本轮未继续发送");
        }
        int finalWireLimit=Math.Max(16384,config.FinalOutboundMaxUtf8Bytes);
        if(config.FinalOutboundMaxUtf8Bytes>0 && finalWireBytes>finalWireLimit)
        {
            FinalOutboundObservation.Plan(config,json,kind,purpose,envelope,history,historyProjection,
                budgetAccepted:false,rejection:"final_request_utf8_budget",sourceVerified:true);
            ObserveFinalOutboundRejectionForTests("final_request_utf8_budget",finalWireBytes,finalWireLimit);
            return RejectCompletion(envelope,"final_request_budget","最终请求超过本地资源预算，本轮未发送");
        }
        if(kind is "main" or "review" && envelope.DialogueRequest is { } historyBudget)
        {
            long bytes=System.Text.Encoding.UTF8.GetByteCount(json);
            if(!historyBudget.ObserveChatHistoryRequestBytes(bytes))
            {
                FinalOutboundObservation.Plan(config,json,kind,purpose,envelope,history,historyProjection,
                    budgetAccepted:false,rejection:"history_request_budget",sourceVerified:true);
                ObserveFinalOutboundRejectionForTests("history_request_budget",bytes,DialogueRequest.ChatHistoryMaxRequestBytes);
                return RejectCompletion(envelope,"history_request_budget","历史读取的整请求预算不足，本轮未继续发送");
            }
        }
        var outboundObservation=FinalOutboundObservation.Plan(config,json,kind,purpose,envelope,history,historyProjection,sourceVerified:true);
        if(StopAtFinalOutboundForTests(json))
        {
            RejectCompletion(envelope,"offline_observation","Offline final outbound capture; no attempt, authentication or HTTP was started");
            FinalOutboundObservation.Terminal(outboundObservation,envelope.Response);
            return null;
        }
        int seconds = openAIProfile?.TimeoutSeconds ?? Math.Max(kind == "main" ? 1 : 10, config.RequestTimeoutSeconds);
        DialogueRequest? owner = envelope.DialogueRequest;
        bool eligible = !config.IsOpenAI && kind == "main" && purpose == "dialogue" && owner != null
            && owner.HttpAttempts == 0 && !owner.NativeTools && !owner.ScreenRequested
            && !envelope.NativeToolsSent;
        var elapsed = Stopwatch.StartNew();
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        using var linked = config.SendPermission is { } permission
            ?CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,budget.Token,permission.Token)
            :CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budget.Token);
        try
        {
            string? text = await SendCompletionAttemptAsync(config, model, json, kind, purpose,
                envelope, cancellationToken, linked.Token, url, apiKey, seconds,
                runtimeBlocks, history, historyProjection,reviewSources,outboundObservation).ConfigureAwait(false);
            if(kind=="main")owner?.SemanticMaterial?.ObserveHttp(envelope.Response.AttemptId,envelope.Response.Outcome);
            cancellationToken.ThrowIfCancellationRequested();
            if(text!=null&&config.SendPermission is {Allowed:false} revoked)
                return RejectCompletion(envelope,"authorization_revoked",RequestSendPermissions.Notice(revoked.Reason));
            if (!eligible || !TransportRetryPolicy.Retryable(envelope.Response)) return text;
            if (seconds * 1000L - elapsed.ElapsedMilliseconds < TransportRetryPolicy.MinimumRemainingMs + TransportRetryPolicy.PauseMs)
            { envelope.RecoverySkipReason = "remaining_budget"; return text; }
            // One pause and one resend only. Cancellation applies during the pause,
            // sending headers, and reading the body; the original deadline is shared.
            await Task.Delay(TransportRetryPolicy.PauseMs, linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            if(owner?.SemanticMaterial is { } materialState)
            {
                int before=materialState.Current?.Revision??0;
                var current=await materialState.PrepareAsync(cancellationToken).ConfigureAwait(false);
                if(current.Revision!=before){envelope.RecoverySkipReason="materials_changed_before_same_payload_retry";return text;}
            }
            if (seconds * 1000L - elapsed.ElapsedMilliseconds < TransportRetryPolicy.MinimumRemainingMs)
            { envelope.RecoverySkipReason = "remaining_budget"; return text; }
            ApiResponseDiagnostic first = envelope.Response;
            envelope.PreviousTransportAttempts.Add(first);
            envelope.Response = new ApiResponseDiagnostic { RecoveryOfAttemptId = first.AttemptId, RecoveryAttempt = 1 };
            string retrySourceRejection=!envelope.SelectionCurrent()?"selection_changed":!envelope.RequestSourcesPermitted()?"recall_source_changed":"";
            if(retrySourceRejection.Length>0)
            {
                FinalOutboundObservation.Plan(config,json,kind,purpose,envelope,history,historyProjection,
                    rejection:retrySourceRejection,rejectionStage:"rejected_at_source");
                ObserveFinalOutboundRejectionForTests(retrySourceRejection,finalWireBytes,null,"rejected_at_source");
                return RejectCompletion(envelope,retrySourceRejection,"资料来源已变更，本轮未继续发送");
            }
            var retryObservation=FinalOutboundObservation.Plan(config,json,kind,purpose,envelope,history,historyProjection,sourceVerified:true);
            if(StopAtFinalOutboundForTests(json))
            {
                RejectCompletion(envelope,"offline_observation","Offline final outbound capture; retry was not started");
                FinalOutboundObservation.Terminal(retryObservation,envelope.Response);
                return null;
            }
            envelope.Error = "";
            owner!.WaitStatus.Retry();
            NativeEventLog.Trace("api.transport_retry", "api", "system", "one same-payload resend",
                "request_id=" + envelope.RequestId + ";first_attempt_id=" + first.AttemptId
                + ";attempt_id=" + envelope.Response.AttemptId + ";remaining_ms=" + Math.Max(0, seconds * 1000L - elapsed.ElapsedMilliseconds));
            string? retried=await SendCompletionAttemptAsync(config, model, json, kind, purpose,
                envelope, cancellationToken, linked.Token, url, apiKey, seconds,
                runtimeBlocks, history, historyProjection,reviewSources,retryObservation).ConfigureAwait(false);
            if(kind=="main")owner?.SemanticMaterial?.ObserveHttp(envelope.Response.AttemptId,envelope.Response.Outcome);
            return retried;
        }
        catch (OperationCanceledException)
        {
            if(config.SendPermission is {Allowed:false} revoked)
                return RejectCompletion(envelope,"authorization_revoked",RequestSendPermissions.Notice(revoked.Reason));
            bool cancelled = cancellationToken.IsCancellationRequested;
            return RejectCompletion(envelope, cancelled ? "cancelled" : "timeout",
                cancelled ? "request cancelled" : "request timeout");
        }
        finally
        {
            if(openAIProfile!=null&&kind!="main")
            {
                _openAIBackground.Complete(kind,openAIProfile,envelope.Response);
                if(envelope.Response.BackgroundFailures>0)
                    TraceCompletion("model.background_cooldown",model,kind,purpose,envelope);
            }
            if (eligible)
                NativeEventLog.Trace("api.transport_recovery_end", "api", "system", "transport recovery settled",
                    "request_id=" + envelope.RequestId + ";attempt_id=" + envelope.Response.AttemptId
                    + ";retry_count=" + envelope.PreviousTransportAttempts.Count + ";outcome=" + envelope.Response.Outcome
                    + ";skip=" + envelope.RecoverySkipReason + ";elapsed_ms=" + elapsed.ElapsedMilliseconds
                    + ";budget_ms=" + seconds * 1000L);
        }
    }
}
