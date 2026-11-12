using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace LilithBridge;

// Exact read footprint only. Full public text already lives in the generated
// store and the captured Source; it is not duplicated in this dependency.
internal sealed record ReflectionGeneratedDependency(string Key, string Version, string BodyHash, string[] SourceEventIds);
internal sealed record ReflectionGeneratedCapture(JsonElement Projection, IReadOnlyList<ReflectionGeneratedDependency> Dependencies);

internal sealed partial class MemoryStore
{
    private const int ReflectionGeneratedByteLimit = 16384;
    private const int ReflectionGeneratedOwnerLimit = 6;

    // Called only after an actual immutable public answer was durably captured.
    // The request owner is verified, not inferred from time, text or adjacency.
    // Failure to persist this optional locator does not fail public-answer save.
    private void LinkReflectionGeneratedPublic(ApiResult reply, string key)
    {
        string owner = reply.DialogueRequest?.Id ?? reply.RequestId;
        if (!ReflectionDeliveries().TryGetValue(reply.SourceUserEventId, out var receipt)
            || receipt.SessionId != _sessionId || receipt.RequestId != owner || !ValidGeneratedKey(key)) return;
        if (receipt.GeneratedPublicKey == key) return;
        ReflectionDeliveries()[receipt.UserId] = receipt with { GeneratedPublicKey = key };
        SaveReflectionDeliveries();
    }

    // No generated-directory scan or semantic/model pass. Only explicit keys
    // bound to the selected window's existing delivery owners are resolved.
    // Legacy receipts without a key remain unavailable, never guessed.
    private ReflectionGeneratedCapture CaptureReflectionGeneratedPublic(IReadOnlyList<MemoryTurn> window,
        IReadOnlyList<MemoryTurn> evidence)
    {
        lock (_experienceLock)
        {
            var owners = window.Select(t => t.Role == "user" ? t.EventId
                : t.Role == "assistant" ? t.SourceEventId : "").Where(id => id.Length > 0)
                .Distinct(StringComparer.Ordinal).ToArray();
            var records = new List<object>();
            var omitted = new List<object>();
            var dependencies = new List<ReflectionGeneratedDependency>();
            bool budgetLimited = false;
            IReadOnlyList<MemoryTurn>? raw = null;
            const string contract = "Read-only received public answers for selected exchanges only, not a complete generation inventory. "
                + "Not hidden reasoning, not player evidence, not a note source or revision pool. "
                + "Body parts in order reconstruct the complete received public text; text_ref resolves the exact supplied utterances.text. "
                + "A confirmed display is not proof the player read it. Other or unconfirmed ranges are not claimed spoken, unseen or read. "
                + "Do not assume unexpressed text is known to the player or must be said. Missing records prove no absence of generation. Exact same-text ranges require verified body text and raw identity; other displays do not locate spoken text in the generated body. Integer display source indices refer to the supplied utterances array, zero based; string sources are explicit event IDs. Receipt timestamps are local observation times, distinct from public capture time and player reading.";
            JsonElement Project() => JsonSerializer.SerializeToElement(new { contract,
                display_columns = new { exact_same_text_ranges = new[] { "utterance_index_or_event_id", "generated_offset", "chars", "receipt_recorded_at_utc" },
                    other_display_records = new[] { "utterance_index_or_event_id", "display_chars", "mapping_state", "receipt_recorded_at_utc" } },
                records, omitted, budget_limited = budgetLimited }, DialogueMaterialEnvelope.DataJson);
            int Bytes() => JsonSerializer.SerializeToUtf8Bytes(Project(), DialogueMaterialEnvelope.DataJson).Length;
            // Omission metadata is bounded by the existing finite selected owner
            // window. Do not expand it with identities from the entire archive.
            foreach (string owner in owners.Take(ReflectionGeneratedOwnerLimit))
            {
                if (!ReflectionDeliveries().TryGetValue(owner, out var receipt)
                    || string.IsNullOrEmpty(receipt.GeneratedPublicKey))
                { omitted.Add(new { source_user_event_id = owner, reason = "no_verified_generated_locator" }); continue; }
                var source = GetExactGeneratedReply(receipt.GeneratedPublicKey);
                if (source == null || source.SourceUserId != owner || source.SessionId != receipt.SessionId)
                { omitted.Add(new { source_user_event_id = owner, reason = "generated_body_or_permission_unavailable" }); continue; }
                records.Add(ReflectionGeneratedRecord(source, evidence));
                if (Bytes() > ReflectionGeneratedByteLimit)
                {
                    records.RemoveAt(records.Count - 1); budgetLimited = true;
                    omitted.Add(new { source_user_event_id = owner, reason = "whole_public_answer_byte_budget" });
                    continue;
                }
                var m = source.Metadata;
                raw ??= ReadExperiences(includeSuppressed: true);
                var ids = m.ShownRanges.Select(r => r.EventId).Append(source.SourceUserId)
                    .Concat(raw.Where(t => t.SessionId == source.SessionId && t.SourceEventId == owner && t.Origin == source.Origin
                        && HistoryBoundary.TryReplyBubble(t, out string request, out _) && request == source.RequestId).Select(t => t.EventId))
                    .Where(id => id.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
                dependencies.Add(new(source.Key, source.VersionFingerprint, source.BodyHash, ids));
            }
            if (owners.Length > ReflectionGeneratedOwnerLimit) omitted.Add(new { reason = "selected_owner_limit" });
            // Reserve omission metadata too. Bodies are never truncated to fit.
            while (Bytes() > ReflectionGeneratedByteLimit && records.Count > 0)
            { records.RemoveAt(records.Count - 1); dependencies.RemoveAt(dependencies.Count - 1); budgetLimited = true; }
            while (Bytes() > ReflectionGeneratedByteLimit && omitted.Count > 0) omitted.RemoveAt(omitted.Count - 1);
            return new(Project(), dependencies);
        }
    }

    private static object ReflectionGeneratedRecord(GeneratedReplySnapshot source, IReadOnlyList<MemoryTurn> evidence)
    {
        var parts = new List<object>(); int at = 0;
        var exact = new List<object[]>();
        var other = new List<object[]>();
        var metadata = source.Metadata;
        foreach (var range in source.DisplayRanges.OrderBy(r => r.GeneratedStartChar ?? int.MaxValue).ThenBy(r => r.ChunkIndex))
        {
            var rows = evidence.Where(t => t.EventId == range.EventId && t.Role == "assistant"
                && t.DeliveryState == "delivered" && t.SessionId == source.SessionId && t.SourceEventId == source.SourceUserId).ToArray();
            int suppliedIndex = -1;
            if (rows.Length == 1) for (int i = 0; i < evidence.Count; i++)
                if (ReferenceEquals(evidence[i], rows[0])) { suppliedIndex = i; break; }
            object sourceRef = suppliedIndex >= 0 ? (object)suppliedIndex : range.EventId;
            // The safe generated reader already checked all archived raw
            // fingerprints, even if this reflection did not supply their bodies.
            // When a host is supplied, check it too before reusing its text.
            bool rawMatches = rows.Length == 0 || rows.Length == 1
                && LogicalDialogueCatalog.RawFingerprint(rows[0]) == range.RawFingerprint
                && RequestSourceBody.Hash(rows[0].Text).Equals(range.ActualTextHash, StringComparison.OrdinalIgnoreCase);
            if (!rawMatches || !TryReflectionDisplayPieces(source, metadata, range, out var pieces))
            {
                if (range.EventId.Length > 0) other.Add(new object[] { sourceRef, range.Chars,
                    range.MappingState, range.RecordedAtUtc });
                continue;
            }
            foreach (var piece in pieces) exact.Add(new object[] { sourceRef, piece.Start, piece.Chars, range.RecordedAtUtc });
            // Multi-piece mappings contain renderer-omitted whitespace in the
            // original body. Preserve that whitespace inline; do not splice the
            // concatenated displayed text into an allegedly contiguous source.
            if (pieces.Count != 1 || suppliedIndex < 0) continue;
            int start = pieces[0].Start, chars = pieces[0].Chars;
            if (start < at || chars != rows[0].Text.Length) continue;
            if (start > at) parts.Add(new { generated_offset = at, text = source.Body[at..start] });
            parts.Add(new { generated_offset = start, chars,
                text_ref = new { event_id = range.EventId, field = "utterances.text" } });
            at = start + chars;
        }
        if (at < source.Body.Length) parts.Add(new { generated_offset = at, text = source.Body[at..] });
        var inline = new object[] { new { generated_offset = 0, text = source.Body } };
        // Many tiny bubbles make exact-reference metadata larger than the whole
        // public answer. Compare complete body representations, retaining the
        // exact body and independent display evidence whichever one wins.
        object body = JsonSerializer.SerializeToUtf8Bytes(parts, DialogueMaterialEnvelope.DataJson).Length
            < JsonSerializer.SerializeToUtf8Bytes(inline, DialogueMaterialEnvelope.DataJson).Length ? parts : inline;
        var m = source.Metadata;
        return new {
            generated_ref = source.Key, source.SessionId, source.RequestId, source.AttemptId, source.SourceUserId,
            at = m.CapturedAtUtc, source.BodyHash, source.VersionFingerprint, total_chars = source.Body.Length,
            public_extent = m.PublicTextExtent, network_body_complete = m.NetworkBodyComplete,
            body_parts = body,
            display = new { terminal = m.DeliveryTerminal, outcome = m.DeliveryOutcome,
                ended_at_utc = m.DeliveryEndedAtUtc, exact_same_text_ranges = exact, other_display_records = other,
                unknown_chunks = m.UnknownChunks, unknown_chars = m.UnknownChars,
                abandoned_chunks = m.AbandonedChunks, abandoned_chars = m.AbandonedChars,
                player_reading = "unknown;not_inferred_from_display" }
        };
    }

    // Same renderer-declared whitespace validation used by the independently
    // tested compact public-answer projection. Only declared pure whitespace
    // may be excluded; a rewrite is never repaired by text normalization.
    private static bool TryReflectionDisplayPieces(GeneratedReplySnapshot source, GeneratedReplyMetadata metadata,
        GeneratedReplyDisplayRange range, out List<(int Start, int Chars)> pieces)
    {
        pieces = new();
        int start = range.GeneratedStartChar ?? -1, count = range.GeneratedChars ?? range.Chars;
        if (range.MappingState != "exact_display_plan" || range.EventId.Length == 0
            || range.RawFingerprint.Length == 0 || start < 0 || count <= 0 || range.Chars <= 0
            || start > source.Body.Length || count > source.Body.Length - start) return false;
        int end = start + count, at = start;
        foreach (var omission in metadata.WhitespaceOmissions.OrderBy(o => o.GeneratedStartChar))
        {
            int omitStart = omission.GeneratedStartChar;
            long omitEnd = (long)omitStart + omission.GeneratedChars;
            if (omitEnd <= start || omitStart >= end) continue;
            if (range.PlanVersion.Length == 0 || range.PlanVersion != metadata.DisplayPlanVersion
                || omitStart < 0 || omission.GeneratedChars <= 0 || omitEnd > source.Body.Length
                || !source.Body.Substring(omitStart, omission.GeneratedChars).All(char.IsWhiteSpace)) return false;
            int left = Math.Max(start, omitStart), right = Math.Min(end, (int)omitEnd);
            if (left > at) pieces.Add((at, left - at));
            at = Math.Max(at, right);
        }
        if (at < end) pieces.Add((at, end - at));
        string actual = string.Concat(pieces.Select(p => source.Body.Substring(p.Start, p.Chars)));
        if (actual.Length == range.Chars && RequestSourceBody.Hash(actual).Equals(range.ActualTextHash, StringComparison.OrdinalIgnoreCase)) return true;
        pieces.Clear(); return false;
    }

    private bool ReflectionGeneratedPublicCurrent(ReflectionRequestSnapshot request)
    {
        var sources = request.GeneratedPublicContext;
        if (sources == null) return false;
        bool hasBodies = request.Source.Contains("\"generated_ref\":", StringComparison.Ordinal);
        if (sources.Count == 0 && !hasBodies) return true; // unchanged legacy snapshots
        try
        {
            using var document = JsonDocument.Parse(request.Source);
            if (!document.RootElement.TryGetProperty("generated_public_context", out var context)
                || !context.TryGetProperty("records", out var records) || records.ValueKind != JsonValueKind.Array) return false;
            var keys = records.EnumerateArray().Select(r => r.GetProperty("generated_ref").GetString()).ToArray();
            if (sources.Any(s => s == null || s.SourceEventIds == null) || keys.Length != sources.Count
                || sources.Select(s => s.Key).Distinct(StringComparer.Ordinal).Count() != sources.Count
                || !keys.ToHashSet(StringComparer.Ordinal).SetEquals(sources.Select(s => s.Key))) return false;
            var utterances = document.RootElement.GetProperty("utterances").EnumerateArray().ToArray();
            if (utterances.Length != request.Evidence.Count) return false;
            for (int i = 0; i < utterances.Length; i++)
                if (utterances[i].GetProperty("event_id").GetString() != request.Evidence[i].EventId
                    || utterances[i].GetProperty("text").GetString() != request.Evidence[i].Text) return false;
            foreach (var prior in sources)
            {
                var current = GetExactGeneratedReply(prior.Key);
                if (current == null || current.VersionFingerprint != prior.Version || current.BodyHash != prior.BodyHash
                    || !prior.SourceEventIds.Contains(current.SourceUserId, StringComparer.Ordinal)
                    || current.DisplayRanges.Any(r => r.EventId.Length > 0 && !prior.SourceEventIds.Contains(r.EventId, StringComparer.Ordinal))) return false;
                var supplied = records.EnumerateArray().Single(r => r.GetProperty("generated_ref").GetString() == prior.Key);
                // Bind the persisted body/refs/state/times to the exact qualified
                // snapshot, not merely to a separately stored dependency list.
                var expected = JsonSerializer.SerializeToElement(ReflectionGeneratedRecord(current, request.Evidence), DialogueMaterialEnvelope.DataJson);
                if (supplied.GetRawText() != expected.GetRawText()) return false;
            }
            return true;
        }
        catch { return false; }
    }

}

// This is a deliberate reader barrier, not a belief/version stamp. Only work
// containing new read-only public bodies uses the object form. Old readers
// expect Source to be a string and fail closed, preserving the journal rather
// than sending or applying context whose dependency they cannot validate.
// New readers accept all legacy string Sources unchanged.
internal sealed class ReflectionSourceJsonConverter : System.Text.Json.Serialization.JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String) return reader.GetString();
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("Unsupported reflection source format");
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (root.EnumerateObject().Count() != 3
            || !root.TryGetProperty("format", out var format)
            || format.GetString() is not ("reflection_generated_public_v1" or "reflection_request_origin_v1" or "reflection_generated_public_request_origin_v1" or "reflection_purpose_v1")
            || !root.TryGetProperty("version", out var version) || version.GetInt32() != 1
            || !root.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
            throw new JsonException("Unsupported reflection source reader requirement");
        return text.GetString();
    }
    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        bool required = false;
        if (value.Contains("\"generated_public_context\"", StringComparison.Ordinal))
        {
            using var document = JsonDocument.Parse(value);
            required = document.RootElement.TryGetProperty("generated_public_context", out var context)
                && context.TryGetProperty("records", out var records) && records.GetArrayLength() > 0;
        }
        bool originRequired = false;
        if (value.Contains("\"reflection_request_context\"", StringComparison.Ordinal))
        {
            using var document = JsonDocument.Parse(value);
            originRequired = document.RootElement.TryGetProperty("reflection_request_context", out _);
        }
        bool purposeRequired = value.Contains("\"reflection_requests\"", StringComparison.Ordinal);
        if (!required && !originRequired && !purposeRequired) { writer.WriteStringValue(value); return; }
        writer.WriteStartObject();
        writer.WriteString("format", purposeRequired ? "reflection_purpose_v1" : required && originRequired ? "reflection_generated_public_request_origin_v1"
            : originRequired ? "reflection_request_origin_v1" : "reflection_generated_public_v1");
        writer.WriteNumber("version", 1);
        writer.WriteString("text", value);
        writer.WriteEndObject();
    }
}
