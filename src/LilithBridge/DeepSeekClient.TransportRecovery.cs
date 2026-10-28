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
        string url = config.ChatCompletionsUrl, apiKey = config.ApiKey;
        int seconds = Math.Max(kind == "main" ? 1 : 10, config.RequestTimeoutSeconds);
        DialogueRequest? owner = envelope.DialogueRequest;
        bool eligible = kind == "main" && purpose == "dialogue" && owner != null
            && owner.HttpAttempts == 0 && !owner.NativeTools && !owner.ScreenRequested
            && !envelope.NativeToolsSent;
        var elapsed = Stopwatch.StartNew();
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budget.Token);
        try
        {
            string? text = await SendCompletionAttemptAsync(config, model, json, kind, purpose,
                envelope, cancellationToken, linked.Token, url, apiKey, seconds,
                runtimeBlocks, history, historyProjection,reviewSources).ConfigureAwait(false);
            if(kind=="main")owner?.SemanticMaterial?.ObserveHttp(envelope.Response.AttemptId,envelope.Response.Outcome);
            cancellationToken.ThrowIfCancellationRequested();
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
            envelope.Error = "";
            owner!.WaitStatus.Retry();
            NativeEventLog.Trace("api.transport_retry", "api", "system", "one same-payload resend",
                "request_id=" + envelope.RequestId + ";first_attempt_id=" + first.AttemptId
                + ";attempt_id=" + envelope.Response.AttemptId + ";remaining_ms=" + Math.Max(0, seconds * 1000L - elapsed.ElapsedMilliseconds));
            string? retried=await SendCompletionAttemptAsync(config, model, json, kind, purpose,
                envelope, cancellationToken, linked.Token, url, apiKey, seconds,
                runtimeBlocks, history, historyProjection,reviewSources).ConfigureAwait(false);
            if(kind=="main")owner?.SemanticMaterial?.ObserveHttp(envelope.Response.AttemptId,envelope.Response.Outcome);
            return retried;
        }
        catch (OperationCanceledException)
        {
            bool cancelled = cancellationToken.IsCancellationRequested;
            return RejectCompletion(envelope, cancelled ? "cancelled" : "timeout",
                cancelled ? "request cancelled" : "request timeout");
        }
        finally
        {
            if (eligible)
                NativeEventLog.Trace("api.transport_recovery_end", "api", "system", "transport recovery settled",
                    "request_id=" + envelope.RequestId + ";attempt_id=" + envelope.Response.AttemptId
                    + ";retry_count=" + envelope.PreviousTransportAttempts.Count + ";outcome=" + envelope.Response.Outcome
                    + ";skip=" + envelope.RecoverySkipReason + ";elapsed_ms=" + elapsed.ElapsedMilliseconds
                    + ";budget_ms=" + seconds * 1000L);
        }
    }
}
