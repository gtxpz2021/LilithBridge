using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LilithBridge;

// A protocol request's origin is not its semantic object. No reason/body copy,
// target inference, priority, promise, new opportunity, or new write authority.
internal sealed record ReflectionRequestOrigin(string SessionId, string UserEventId, string RequestId);
internal sealed record ReflectionRequestOriginContext(long Serial, string Status,
    ReflectionRequestOrigin? Origin, DaySourceDependency? Permission, bool SourceBodySupplied);

internal sealed partial class MemoryStore
{
    private static bool ValidReflectionRequestOrigin(ReflectionRequestOrigin origin) =>
        !string.IsNullOrWhiteSpace(origin.SessionId) && !string.IsNullOrWhiteSpace(origin.UserEventId)
        && !string.IsNullOrWhiteSpace(origin.RequestId)
        && origin.SessionId.Length <= 256 && origin.UserEventId.Length <= 256 && origin.RequestId.Length <= 256;

    internal void MarkReflectionRequested(ReflectionWindowCursor cursor, ApiResult result)
    {
        lock (_experienceLock)
        {
            _reflectionCursors.Add(cursor);
            string request = result.DialogueRequest?.Id ?? result.RequestId;
            var origin = new ReflectionRequestOrigin(_sessionId, result.SourceUserEventId, request);
            // An unavailable origin does not revoke the existing opportunity.
            // Do not carry an earlier request's identity into an unbound request.
            if (!result.ReflectionRequested || result.IsLocalFallback || result.Response.Outcome != "accepted"
                || !ValidReflectionRequestOrigin(origin) || ResolveReflectionRequestOrigin(origin) == null) origin = null;
            cursor.MarkModelRequested(origin, origin == null ? null : ReadReflectionPurposes(result, origin));
        }
    }

    private MemoryTurn? ResolveReflectionRequestOrigin(ReflectionRequestOrigin origin)
    {
        if (!ValidReflectionRequestOrigin(origin)
            || !ReflectionDeliveries().TryGetValue(origin.UserEventId, out var receipt)
            || receipt.SessionId != origin.SessionId || receipt.RequestId != origin.RequestId) return null;
        var raw = ReadExperiences().FirstOrDefault(t => t.EventId == origin.UserEventId);
        return raw is { Role: "user", Origin: "user", Kind: "chat", DeliveryState: "received" }
            && raw.SessionId == origin.SessionId && IsMentionAllowed(raw) && IsNativeSupplyAllowed(raw) ? raw : null;
    }

    // Freeze beside the existing normal preparation, not in the backlog selector.
    // The complete reason is deliberately unavailable until its own source and
    // privacy contract is implemented; an origin cannot stand in for that reason.
    internal ReflectionRequestSnapshot BindReflectionRequestOrigin(ReflectionRequestSnapshot request,
        ReflectionRequestOrigin? origin, long serial, bool requested)
    {
        if (!requested) return request;
        lock (_experienceLock)
        {
            var raw = origin == null ? null : ResolveReflectionRequestOrigin(origin);
            var context = new ReflectionRequestOriginContext(serial,
                origin == null ? "origin_unknown" : raw == null ? "origin_unavailable" : "origin_qualified",
                raw == null ? null : origin, raw == null ? null : SourceVersion("utterance", raw),
                raw != null && request.Evidence.Any(t => t.EventId == raw.EventId && t.Text == raw.Text));
            var document = JsonNode.Parse(request.Source)?.AsObject() ?? throw new JsonException("Invalid reflection source");
            document["reflection_request_context"] = JsonSerializer.SerializeToNode(ReflectionRequestOriginProjection(context), DialogueMaterialEnvelope.DataJson);
            return request with { Source = document.ToJsonString(DialogueMaterialEnvelope.DataJson), RequestOriginContext = context };
        }
    }

    private static object ReflectionRequestOriginProjection(ReflectionRequestOriginContext context) => new {
        model_request_serial = context.Serial, status = context.Status,
        origin = context.Origin == null ? null : new { session_id = context.Origin.SessionId,
            source_user_event_id = context.Origin.UserEventId, request_id = context.Origin.RequestId },
        source_body = context.SourceBodySupplied ? "present_in_utterances" : "not_supplied",
        reason = "not_supplied", semantic_target = "unspecified;origin_is_not_target",
        contract = "此处只标识请求从哪次交流发出，不表示那次交流就是想反思的对象；理由未提供。没有自动增加或替换本轮材料，也不新增可写来源。可以维持原理解、不留产物，不能据此声称已解决原理由。"
    };

    private bool ReflectionRequestOriginCurrent(ReflectionRequestSnapshot request)
    {
        // Legacy callers may pass plain text, not a JSON object. Without either
        // new binding or a new context field, preserve the original path exactly.
        if (request.RequestOriginContext == null
            && !request.Source.Contains("\"reflection_request_context\"", StringComparison.Ordinal)) return true;
        try
        {
            using var document = JsonDocument.Parse(request.Source);
            bool supplied = document.RootElement.TryGetProperty("reflection_request_context", out var projection);
            var context = request.RequestOriginContext;
            if (context == null) return !supplied;
            if (!supplied || context.Serial < 0) return false;
            var expected = JsonSerializer.SerializeToElement(ReflectionRequestOriginProjection(context), DialogueMaterialEnvelope.DataJson);
            if (projection.GetRawText() != expected.GetRawText()) return false;
            if (context.Origin == null) return context.Permission == null && !context.SourceBodySupplied
                && context.Status is "origin_unknown" or "origin_unavailable";
            var current = ResolveReflectionRequestOrigin(context.Origin);
            return context.Status == "origin_qualified" && current != null && context.Permission == SourceVersion("utterance", current)
                && context.SourceBodySupplied == request.Evidence.Any(t => t.EventId == current.EventId && t.Text == current.Text);
        }
        catch { return false; }
    }
}
