using System.Text.Json;
using System.Threading;

namespace LilithBridge;

internal enum ReflectionLocalSendState { Unobserved, NotStarted, HandoffUnresolved, Entered }
internal enum ReflectionLocalOutcomeKind { Unobserved, NotStarted, HandoffUnresolved, EnteredOutcomeUnknown, ResponseRejected, AcceptedResult }

// One live invocation's monotonic observations, not a dispatch permit or a
// durable attempt counter. Entered means the existing final local send region;
// it does not establish remote receipt, inference or billing.
internal sealed class ReflectionSendObservation
{
    private int _facts;
    internal void HandedOff() => Interlocked.Or(ref _facts, 1);
    internal void Entered() => Interlocked.Or(ref _facts, 2);
    // Only the existing dispatch contract's completed null return records this.
    // Cancellation/exception may win while its queued callback is still pending.
    internal void ReturnedWithoutSend() => Interlocked.Or(ref _facts, 4);
    internal ReflectionLocalSendState Snapshot()
    {
        int facts = Volatile.Read(ref _facts);
        if ((facts & 2) != 0) return ReflectionLocalSendState.Entered;
        return (facts & 1) != 0 && (facts & 4) == 0
            ? ReflectionLocalSendState.HandoffUnresolved : ReflectionLocalSendState.NotStarted;
    }
}

internal sealed partial class ApiResult
{
    internal ReflectionSendObservation? ReflectionSend { get; set; }
}

// Immutable at return. AcceptedResult means the existing parser supplied a
// usable value (including all-empty); source acceptance, save and coverage are
// still separate. Lost process memory and old Work have no such observation.
internal sealed record ReflectionLocalOutcome(
    ReflectionLocalOutcomeKind Kind, ReflectionLocalSendState SendState,
    ReflectionResult? Result = null, string TransportRequestId = "", string AttemptId = "",
    string TransportOutcome = "", string ParseStage = "", string ParseCode = "",
    bool HeadersReceived = false, bool BodyComplete = false, int HttpStatus = 0)
{
    internal static ReflectionLocalOutcome Unobserved { get; } = new(
        ReflectionLocalOutcomeKind.Unobserved, ReflectionLocalSendState.Unobserved);

    internal static ReflectionLocalOutcome Capture(ReflectionSendObservation send, ApiResult? envelope,
        ReflectionResult? result, string stage, string code)
    {
        var state = send.Snapshot();
        var diagnostic = envelope?.Response;
        bool headers = diagnostic?.HeadersReceived == true, complete = diagnostic?.BodyComplete == true;
        int status = diagnostic?.HttpStatus ?? 0;
        // A complete but unusable body, or an observed non-success HTTP status,
        // is distinct from losing the response. Neither proves no remote work.
        bool rejectedResponse = complete || headers && status > 0 && (status < 200 || status >= 300);
        var kind = result != null ? ReflectionLocalOutcomeKind.AcceptedResult
            : rejectedResponse ? ReflectionLocalOutcomeKind.ResponseRejected
            : state == ReflectionLocalSendState.Entered ? ReflectionLocalOutcomeKind.EnteredOutcomeUnknown
            : state == ReflectionLocalSendState.NotStarted ? ReflectionLocalOutcomeKind.NotStarted
            : ReflectionLocalOutcomeKind.HandoffUnresolved;
        return new(kind, state, result, envelope?.RequestId ?? "", diagnostic?.AttemptId ?? "",
            diagnostic?.Outcome ?? "", stage, code, headers, complete, status);
    }

    internal void Trace(string workId, string ownerId)
    {
        try
        {
            NativeEventLog.Trace("memory.reflection.local_outcome", "mod", "system", "live reflection return facts",
                JsonSerializer.Serialize(new { work_id = workId, owner_id = ownerId,
                    transport_request_id = TransportRequestId, attempt_id = AttemptId,
                    kind = Kind.ToString(), send_state = SendState.ToString(), transport_outcome = TransportOutcome,
                    parse_stage = ParseStage, parse_code = ParseCode, headers_received = HeadersReceived,
                    body_complete = BodyComplete, http_status = HttpStatus,
                    scope = "live_return_only;accepted_is_not_persisted;retry_policy_unchanged" }));
        }
        catch { /* Observing a return must not affect save, coverage or retry. */ }
    }
}
