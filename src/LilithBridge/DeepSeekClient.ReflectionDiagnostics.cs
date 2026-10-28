using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LilithBridge;

internal sealed partial class DeepSeekClient
{
    // Parse the complete response, then validate proposal shape atomically.
    // Provenance, persistence and pending consumption remain caller-owned.
    private async Task<ReflectionResult?> RunReflectionWithDiagnosticsAsync(
        BridgeConfig config, string source, CancellationToken cancellationToken,
        ReflectionDispatch? dispatch)
    {
        if (string.IsNullOrWhiteSpace(config.ApiKey) || string.IsNullOrWhiteSpace(source))
        {
            TraceReflectionParse(null, "precondition", string.IsNullOrWhiteSpace(config.ApiKey)
                ? "not_configured" : "no_source", 0, 0, "", null, null);
            return null;
        }
        ApiResult? envelope = null;
        string stage = "send", code = "no_completion", exceptionType = "";
        int contentChars = 0, normalizedChars = 0;
        long? jsonLine = null, jsonByte = null;
        string? publicContent = null, parserInput = null;
        try
        {
            // These are the sender's real identities, not a second diagnostic
            // request or a correlation inferred from log timing.
            envelope = new ApiResult();
            string? text = await SendTextAsync(config, config.ReflectionModel,
                BuildReflectionPrompt(), source, config.ReflectionMaxTokens, 0.2, false,
                cancellationToken, "reflection", reflectionDispatch: dispatch,
                envelopeOverride: envelope).ConfigureAwait(false);
            contentChars = envelope.Response.ContentChars;
            if (string.IsNullOrWhiteSpace(text)) return null;
            contentChars = text.Length;
            publicContent = text;
            stage = "strip_fence";
            text = StripJsonFences(text).Trim();
            parserInput = text;
            normalizedChars = text.Length;
            if (text.Length == 0) { code = "empty_after_fence"; return null; }
            stage = "root";
            if (!text.StartsWith('{')) { code = "non_object_prefix"; return null; }
            using JsonDocument doc = JsonDocument.Parse(text);
            stage = "shape";
            string? shapeFailure = ReflectionResponseShape.RejectCode(doc.RootElement);
            if (shapeFailure != null) { code = shapeFailure; return null; }
            var result = new ReflectionResult();
            stage = "note";
            if (doc.RootElement.TryGetProperty("note", out JsonElement note) && note.ValueKind == JsonValueKind.Object)
            {
                var proposal = new ReflectionNoteProposal();
                if (note.TryGetProperty("text",out JsonElement noteText) && noteText.ValueKind == JsonValueKind.String)
                    proposal.Text = MemoryStore.Clean(noteText.GetString());
                if (note.TryGetProperty("revises",out JsonElement revises) && revises.ValueKind == JsonValueKind.String)
                    proposal.Revises = revises.GetString() ?? "";
                if (note.TryGetProperty("source_event_ids",out JsonElement ids) && ids.ValueKind == JsonValueKind.Array)
                    proposal.SourceEventIds = ids.EnumerateArray().Where(id => id.ValueKind == JsonValueKind.String)
                        .Select(id => id.GetString() ?? "").ToArray();
                if (note.TryGetProperty("source_day_ids",out JsonElement dayIds) && dayIds.ValueKind == JsonValueKind.Array)
                    proposal.SourceDayIds = dayIds.EnumerateArray().Where(id => id.ValueKind == JsonValueKind.String)
                        .Select(id => id.GetString() ?? "").ToArray();
                if (note.TryGetProperty("settles",out JsonElement settles) && settles.ValueKind == JsonValueKind.String)
                    proposal.Settles = settles.GetString() ?? "";
                if (note.TryGetProperty("settle_reason",out JsonElement settleReason) && settleReason.ValueKind == JsonValueKind.String)
                    proposal.SettleReason = MemoryStore.Clean(settleReason.GetString());
                result.Note = proposal;
            }
            stage = "lesson";
            if (doc.RootElement.TryGetProperty("lesson", out JsonElement lesson)
                && lesson.ValueKind == JsonValueKind.Object)
            {
                var proposal = new ReflectionLessonProposal();
                if (lesson.TryGetProperty("text", out JsonElement lessonText)
                    && lessonText.ValueKind == JsonValueKind.String)
                    proposal.Text = MemoryStore.Clean(lessonText.GetString());
                if (lesson.TryGetProperty("revises", out JsonElement lessonRevises)
                    && lessonRevises.ValueKind == JsonValueKind.String)
                    proposal.Revises = lessonRevises.GetString() ?? "";
                if (lesson.TryGetProperty("source_note_ids", out JsonElement lessonIds)
                    && lessonIds.ValueKind == JsonValueKind.Array)
                    proposal.SourceNoteIds = lessonIds.EnumerateArray()
                        .Where(id => id.ValueKind == JsonValueKind.String)
                        .Select(id => id.GetString() ?? "").ToArray();
                result.Lesson = proposal;
            }
            stage = "relationship";
            if (doc.RootElement.TryGetProperty("relationship", out JsonElement relationship)
                && relationship.ValueKind == JsonValueKind.Object)
            {
                var proposal = new RelationshipReadingProposal();
                if (relationship.TryGetProperty("text", out JsonElement readingText)
                    && readingText.ValueKind == JsonValueKind.String)
                    proposal.Text = MemoryStore.Clean(readingText.GetString());
                if (relationship.TryGetProperty("revises", out JsonElement readingRevises)
                    && readingRevises.ValueKind == JsonValueKind.String)
                    proposal.Revises = readingRevises.GetString() ?? "";
                if (relationship.TryGetProperty("source_note_ids", out JsonElement readingIds)
                    && readingIds.ValueKind == JsonValueKind.Array)
                    proposal.SourceNoteIds = readingIds.EnumerateArray()
                        .Where(id => id.ValueKind == JsonValueKind.String)
                        .Select(id => id.GetString() ?? "").ToArray();
                result.Relationship = proposal;
            }
            stage = "summary";
            if (doc.RootElement.TryGetProperty("reflection", out JsonElement reflection)
                && reflection.ValueKind == JsonValueKind.String)
                result.Summary = MemoryStore.Clean(reflection.GetString());
            stage = "thought";
            if (doc.RootElement.TryGetProperty("thought", out JsonElement thought)
                && thought.ValueKind == JsonValueKind.String)
                result.Thought = MemoryStore.Clean(thought.GetString());
            stage = "legacy_memories";
            if (doc.RootElement.TryGetProperty("memories", out JsonElement memories)
                && memories.ValueKind == JsonValueKind.Array)
                ReadMemoryPatchArray(memories, result.Patches);
            code = result.Note == null && result.Lesson == null && result.Relationship == null
                ? "parsed_empty" : "parsed_proposal";
            stage = "complete";
            return result;
        }
        catch (Exception error)
        {
            exceptionType = error.GetType().Name;
            if (error is JsonException jsonError)
            {
                jsonLine = jsonError.LineNumber;
                jsonByte = jsonError.BytePositionInLine;
            }
            code = stage == "send" ? "completion_exception"
                : error is JsonException ? "json_parse_error" : "field_read_error";
            return null;
        }
        finally
        {
            TraceReflectionParse(envelope, stage, code, contentChars, normalizedChars,
                exceptionType, jsonLine, jsonByte);
            try
            {
                if (publicContent != null && parserInput != null && code is
                    "json_parse_error" or "field_read_error" or "non_object_prefix" or "empty_after_fence"
                    or "schema_type_error" or "schema_duplicate_field" or "schema_unknown_field")
                {
                    bool sent = envelope?.Response.HttpSendStartedMs != null;
                    ReflectionFailureEvidence.Observe(publicContent, parserInput,
                        sent ? envelope!.RequestId : "", sent ? envelope!.Response.AttemptId : "",
                        stage, code, exceptionType, jsonLine, jsonByte,
                        !string.IsNullOrEmpty(config.ApiKey) && publicContent.Contains(config.ApiKey, StringComparison.Ordinal));
                }
            }
            catch { /* Observation cannot affect parsing, pending or caller. */ }
        }
    }

    private static void TraceReflectionParse(ApiResult? envelope, string stage, string code,
        int contentChars, int normalizedChars, string exceptionType, long? jsonLine, long? jsonByte)
    {
        try
        {
            bool dispatched = envelope?.Response.HttpSendStartedMs != null;
            NativeEventLog.Trace("memory.reflection.parse", "mod", "system", "reflection result classified",
                JsonSerializer.Serialize(new
                {
                    request_id = dispatched ? envelope!.RequestId : "",
                    attempt_id = dispatched ? envelope!.Response.AttemptId : "",
                    link_state = dispatched ? "transport_attempt" : "not_dispatched",
                    stage, code, content_chars = contentChars, normalized_chars = normalizedChars,
                    exception_type = exceptionType, json_line = jsonLine, json_byte = jsonByte
                }));
        }
        catch
        {
            // Diagnostic failure must not change response acceptance or consumption.
        }
    }
}
