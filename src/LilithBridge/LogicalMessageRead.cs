using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LilithBridge;

internal sealed record ExperienceBodyView(string Block, string DataJson, IReadOnlyList<RequestSourceBody> Bodies)
{
    // Renderer-owned selection receipt. A rendered neighbour is body supply,
    // not another semantic seed. Never reconstruct this from free JSON/text.
    internal IReadOnlyList<string> DirectEventIds {get;init;} = Array.Empty<string>();
    internal IReadOnlyList<string> AnchorEventIds {get;init;} = Array.Empty<string>();
}
internal sealed record LogicalMessageReadOptions(string Reference = "", string Cursor = "", int Offset = 0, int BodyChars = 0);
internal sealed record LogicalMessagePage([property: JsonIgnore] LogicalDialogueUnit Unit,
    int Offset, string Text, bool BodyIncluded, int? NextOffset, string NextCursor,
    [property: JsonIgnore] IReadOnlyList<ReviewMaterialView> SourceViews);

// Full units and raw versions are private revalidation evidence. Render only the
// explicit page projection below; never serialize a unit's private member bodies.
internal sealed record LogicalMessageReadPacket(string Status, string Reason,
    [property: JsonIgnore] IReadOnlyList<LogicalDialogueUnit> Units,
    [property: JsonIgnore] IReadOnlyList<LogicalMessagePage> Pages,
    [property: JsonIgnore] IReadOnlyDictionary<string, string> MemberVersions,
    [property: JsonIgnore] IReadOnlyDictionary<string, string> SourceContexts,
    bool CrossSessionRead = true, bool WritableCurrentSession = false)
{
    internal IReadOnlyList<MemoryTurn> ActualSourceRecords => Pages.Where(p => p.BodyIncluded)
        .SelectMany(page => {
            var members = page.Unit.Members.ToDictionary(ReviewContext.ShortId, StringComparer.Ordinal);
            return page.SourceViews.Select(view => members[view.Id]);
        })
        .DistinctBy(t => t.EventId).ToArray();
    internal IReadOnlyList<ReviewMaterialView> ActualSourceViews => Pages.Where(p => p.BodyIncluded)
        .SelectMany(p => p.SourceViews).Distinct().ToArray();
    internal string Json => LogicalMessageProjection.PacketJson(this);
    internal LogicalMessageReadPacket ProjectBodies(IReadOnlyCollection<string> bodyKeys, string reason = "logical_message_count_budget") => this with {
        Reason = Pages.Any(page => !bodyKeys.Contains(page.Unit.Key)) ? reason : Reason,
        Pages = Pages.Select(page => bodyKeys.Contains(page.Unit.Key) ? page : page with { Text = "", BodyIncluded = false,
            NextOffset = page.Offset, NextCursor = LogicalMessageProjection.Cursor(page.Unit, this, page.Offset),
            SourceViews = Array.Empty<ReviewMaterialView>() }).ToArray()
    };
    internal LogicalMessageReadPacket ProjectOmitted(string reason = "existing_request_budget") => this with {
        Status = "body_omitted", Reason = reason,
        Pages = Pages.Select(page => page with { Text = "", BodyIncluded = false,
            NextOffset = page.Offset, NextCursor = LogicalMessageProjection.Cursor(page.Unit, this, page.Offset),
            SourceViews = Array.Empty<ReviewMaterialView>() }).ToArray()
    };
}

internal static class LogicalMessageProjection
{
    internal static string StateStamp(LogicalDialogueUnit unit, LogicalMessageReadPacket packet) =>
        MemoryStore.SemanticTextHash(JsonSerializer.Serialize(new { unit.VersionFingerprint,
            members = unit.Members.Select(t => new { t.EventId,
                version = packet.MemberVersions.GetValueOrDefault(t.EventId),
                context = packet.SourceContexts.GetValueOrDefault(t.EventId) }).ToArray() }, DialogueMaterialEnvelope.DataJson));

    internal static string Cursor(LogicalDialogueUnit unit, LogicalMessageReadPacket packet, int offset)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new {
            v = 1, reference = unit.Key, fingerprint = StateStamp(unit, packet), offset }, DialogueMaterialEnvelope.DataJson));
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    internal static bool ValidCursor(LogicalDialogueUnit unit, LogicalMessageReadPacket packet, string cursor, int offset)
    {
        if (cursor.Length == 0) return offset == 0;
        if (cursor.Length > 4096) return false;
        try
        {
            string value = cursor.Replace('-', '+').Replace('_', '/');
            value += new string('=', (4 - value.Length % 4) % 4);
            using var doc = JsonDocument.Parse(Convert.FromBase64String(value)); var root = doc.RootElement;
            return root.GetProperty("v").GetInt32() == 1 && root.GetProperty("reference").GetString() == unit.Key
                && root.GetProperty("fingerprint").GetString() == StateStamp(unit, packet)
                && root.GetProperty("offset").GetInt32() == offset;
        }
        catch (Exception error) when (error is FormatException or JsonException or InvalidOperationException or KeyNotFoundException) { return false; }
    }

    internal static IReadOnlyList<ReviewMaterialView> PageViews(LogicalDialogueUnit unit, int offset, int chars)
    {
        var byId = unit.Members.ToDictionary(t => t.EventId, StringComparer.Ordinal);
        var result = new List<ReviewMaterialView>();
        foreach (var span in unit.SourceSpans)
        {
            int start = Math.Max(offset, span.OffsetInUnit), end = Math.Min(offset + chars, span.OffsetInUnit + span.Chars);
            if (end <= start || !byId.TryGetValue(span.EventId, out var raw)) continue;
            int sourceStart = span.SourceOffset + start - span.OffsetInUnit, length = end - start;
            result.Add(new(ReviewContext.ShortId(raw), raw.Role == "user" ? "player" : "lilith", raw.Timestamp,
                raw.DeliveryState, sourceStart, raw.Text.Length, raw.Text.Substring(sourceStart, length),
                sourceStart != 0 || sourceStart + length != raw.Text.Length, RawTextPreserved: true));
        }
        return result;
    }

    internal static object Message(LogicalDialogueUnit unit, int offset, string text, bool bodyIncluded,
        int? nextOffset, string nextCursor, IReadOnlyList<ReviewMaterialView>? views = null, bool writable = false,
        IReadOnlyCollection<string>? revisionAssociations = null)
    {
        var members = unit.Members;
        var byId = members.ToDictionary(raw => raw.EventId, StringComparer.Ordinal);
        return new {
        message_ref = unit.Key, version_fingerprint = unit.VersionFingerprint, text_sha256 = unit.TextHash,
        role = unit.Role, origin = unit.Origin, session_id = unit.SessionId, request_id = unit.RequestId,
        source_event_id = unit.SourceEventId, unit_kind = unit.IsMessage ? "message" : "independent_utterance",
        coverage = unit.Coverage, head_known = unit.HeadKnown, tail_known = unit.TailKnown, has_gaps = unit.HasGaps,
        offset, total_chars = unit.Text.Length, text, body_included = bodyIncluded,
        supplied_view_complete = bodyIncluded && offset == 0 && nextOffset == null,
        next_offset = nextOffset, next_cursor = nextCursor,
        member_index = members.Select(raw => new {
            event_id = raw.EventId, text_sha256 = RequestSourceBody.Hash(raw.Text),
            record_fingerprint = LogicalDialogueCatalog.RawFingerprint(raw), role = raw.Role, origin = raw.Origin,
            delivery_state = raw.DeliveryState, session_id = raw.SessionId, sequence = raw.Sequence,
            source_event_id = raw.SourceEventId, revision = raw.Revision, time = raw.Timestamp.ToString("O"),
            source_ref = writable && bodyIncluded && views?.Any(v => v.Id == ReviewContext.ShortId(raw)) == true ? ReviewContext.ShortId(raw) : null,
            source_revision_association = revisionAssociations?.Contains(raw.EventId) == true
                ? "natural_revision_link_present; earlier_wording_is_history_not_current_fact" : null,
            reported_speech = UtteranceAttribution.Reported(raw.Role, raw.Text)
        }).ToArray(),
        source_spans = unit.SourceSpans.Select(span => new {
            event_id = span.EventId, text_sha256 = RequestSourceBody.Hash(byId[span.EventId].Text),
            offset_in_unit = span.OffsetInUnit, source_offset = span.SourceOffset, chars = span.Chars }).ToArray(),
        program_gaps = unit.Gaps.Select(gap => new { offset_in_unit = gap.OffsetInUnit, chars = gap.Chars,
            reason = gap.Reason, before_event_id = gap.BeforeEventId, after_event_id = gap.AfterEventId,
            qualification = "program_marker_not_source_text" }).ToArray(),
        supplied_source_ranges = (views ?? Array.Empty<ReviewMaterialView>()).Select(view => new {
            source_ref = writable ? view.Id : null, offset = view.Offset, chars = view.Text.Length,
            total_chars = view.TotalChars, text_basis = "raw_utf16", truncated = view.Truncated }).ToArray()
        };
    }

    internal static string PacketJson(LogicalMessageReadPacket packet, string mode = "message") => ReviewContext.Serialize(new {
        status = packet.Status, reason = packet.Reason, mode,
        read_domain = packet.CrossSessionRead ? "qualified_archive_logical_messages_read_only" : "current_session_logical_messages",
        messages = packet.Pages.Select(page => Message(page.Unit, page.Offset, page.Text, page.BodyIncluded,
            page.NextOffset, page.NextCursor, page.SourceViews, packet.WritableCurrentSession,
            packet.SourceContexts.Where(p => p.Value.Length > 0).Select(p => p.Key).ToArray())).ToArray(),
        omitted_messages = OmissionLocators(packet),
        completeness_note = "Complete means the supplied qualified view/range only; gaps and unknown generation tail remain explicit. Member index and a whole-view hash do not grant citation to unseen text. message_ref/cursor locate data, not permission."
    });

    internal static object[] OmissionLocators(LogicalMessageReadPacket packet) => packet.Pages.Where(p => !p.BodyIncluded)
        .Select(p => (object)new { message_ref = p.Unit.Key,
            version_fingerprint = p.Unit.VersionFingerprint, reason = packet.Reason,
            next_read = new { mode = "message", message_ref = p.Unit.Key, message_cursor = p.NextCursor,
                message_offset = p.Offset, message_body_chars = 0 } }).ToArray();

    internal static string OmissionJson(LogicalMessageReadPacket packet, string mode = "message", string reason = "existing_request_budget")
        => PacketJson(packet.ProjectOmitted(reason), mode);
}

internal sealed partial class MemoryStore
{
    internal static ExperienceBodyView RenderExperienceBodyView(IReadOnlyList<MemoryTurn> turns, string blockId = "experience_recall", IEnumerable<string>? directEventIds = null, IEnumerable<string>? anchorEventIds = null)
    {
        if (turns.Count == 0) return new("", "", Array.Empty<RequestSourceBody>());
        var units = turns is LogicalDialogueSelection selected ? selected.Units : LogicalDialogueCatalog.Build(turns).Units;
        var revisions = turns.Where(t => t.NaturalMemoryReadContext.Length > 0).Select(t => t.EventId).ToArray();
        string data = JsonSerializer.Serialize(new { program_block_id = blockId,
            qualification = "historical_utterances_not_current_facts; program_gap_markers_are_not_source_text; no_required_use_or_mention",
            messages = units.Select(unit => LogicalMessageProjection.Message(unit, 0, unit.Text, true, null, "",
                revisionAssociations: revisions)).ToArray() }, DialogueMaterialEnvelope.DataJson);
        var bodies = new List<RequestSourceBody>();
        for (int at = 0; at < units.Count; at++)
        {
            var unit = units[at]; var byId = unit.Members.ToDictionary(t => t.EventId, StringComparer.Ordinal);
            foreach (var span in unit.SourceSpans)
                if (byId.TryGetValue(span.EventId, out var raw) && span.SourceOffset == 0 && span.Chars == raw.Text.Length
                    && unit.Text.Substring(span.OffsetInUnit, span.Chars) == raw.Text)
                {
                    var body = new RequestSourceBody("utterance", raw.EventId, raw.Text,
                        "runtime_context.experience_blocks[" + blockId + "].messages[" + at + "].text",
                        raw.Role, raw.Origin, raw.DeliveryState, span.OffsetInUnit, span.Chars);
                    if (body.Qualified) bodies.Add(body);
                }
        }
        return new("[找回的原始发言（按消息组织）]\n" + data + "\n", data, bodies) {
            AnchorEventIds=(anchorEventIds??Array.Empty<string>()).Where(id=>turns.Any(t=>t.EventId==id)).Distinct(StringComparer.Ordinal).ToArray(),
            DirectEventIds=(directEventIds ?? (turns as LogicalDialogueSelection)?.DirectHitEventIds ?? Array.Empty<string>())
                .Where(id=>turns.Any(t=>t.EventId==id)).Distinct(StringComparer.Ordinal).ToArray()
        };
    }

    private bool LogicalMessageReadAllowed(string query, BridgeConfig config, bool crossSession) =>
        !MemorySemantics.DeclinesArchiveRecall(query)
        && string.Equals(System.IO.Path.GetFullPath(config.DataDirectory), _root, StringComparison.OrdinalIgnoreCase)
        && (!crossSession || _longTermEnabled && _experienceArchiveEnabled && config.LongTermMemoryEnabled && config.ExperienceArchiveEnabled);

    private LogicalDialogueCatalog CurrentLogicalMessageCatalog() => GetExperienceLogicalCatalog(dialogueOnly: true);

    private LogicalMessageReadPacket LogicalMessagePacket(IReadOnlyList<LogicalDialogueUnit> units, bool crossSession, bool writable)
    {
        var raw = units.SelectMany(u => u.Members).DistinctBy(t => t.EventId).ToArray();
        var packet = new LogicalMessageReadPacket("ok", "qualified_logical_message_view", units, Array.Empty<LogicalMessagePage>(),
            raw.ToDictionary(t => t.EventId, t => SourceVersion("utterance", t).Fingerprint, StringComparer.Ordinal),
            raw.ToDictionary(t => t.EventId, NaturalMemoryContext, StringComparer.Ordinal), crossSession, writable);
        return packet with { Pages = units.Select(unit => new LogicalMessagePage(unit, 0, unit.Text, true, null, "",
            LogicalMessageProjection.PageViews(unit, 0, unit.Text.Length))).ToArray() };
    }

    internal LogicalMessageReadPacket? CaptureLogicalMessageSupply(IReadOnlyList<MemoryTurn> records, string query, BridgeConfig config)
    {
        lock (_experienceLock)
        {
            if (!LogicalMessageReadAllowed(query, config, crossSession: false)) return null;
            using var mention = BeginMentionRequest(query, true);
            using var native = BeginNativeDialogueRequest(query, true, config);
            var catalog = CurrentLogicalMessageCatalog(); var ids = records.Select(t => t.EventId).ToHashSet(StringComparer.Ordinal);
            var units = records is LogicalDialogueSelection selected ? selected.Units
                : records.Select(t => catalog.FindByEvent(t.EventId)).Where(u => u != null).Cast<LogicalDialogueUnit>().DistinctBy(u => u.Key).ToArray();
            var current = new List<LogicalDialogueUnit>();
            foreach (var old in units)
            {
                var unit = catalog.FindByKey(old.Key);
                if (unit == null || unit.VersionFingerprint != old.VersionFingerprint || unit.SessionId != _sessionId
                    || unit.Members.Any(t => !ids.Contains(t.EventId))) return null;
                current.Add(unit);
            }
            if (current.SelectMany(u => u.Members).Select(t => t.EventId).Distinct().Count() != ids.Count) return null;
            return LogicalMessagePacket(current, crossSession: false, writable: true);
        }
    }

    internal LogicalMessageReadPacket ReadLogicalMessage(string query, BridgeConfig config, LogicalMessageReadOptions options)
    {
        LogicalMessageReadPacket Failure(string reason) => new("unavailable", reason, Array.Empty<LogicalDialogueUnit>(),
            Array.Empty<LogicalMessagePage>(), new Dictionary<string, string>(), new Dictionary<string, string>());
        lock (_experienceLock)
        {
            if (!LogicalMessageReadAllowed(query, config, crossSession: true)) return Failure("logical_message_read_not_allowed");
            if (options.Reference.Length == 0 || options.Reference.Length > 256 || options.Offset < 0 || options.BodyChars < 0)
                return Failure("invalid_message_read_arguments");
            using var mention = BeginMentionRequest(query, true);
            using var native = BeginNativeDialogueRequest(query, true, config);
            var unit = CurrentLogicalMessageCatalog().FindByKey(options.Reference);
            if (unit == null) return Failure("message_source_unavailable");
            var packet = LogicalMessagePacket(new[] { unit }, crossSession: true, writable: false);
            if (!LogicalMessageProjection.ValidCursor(unit, packet, options.Cursor, options.Offset)) return Failure("message_cursor_stale_or_invalid");
            if (options.Offset > unit.Text.Length || options.Offset > 0 && options.Offset < unit.Text.Length && char.IsLowSurrogate(unit.Text[options.Offset]))
                return Failure("invalid_message_offset");
            int chars = options.BodyChars == 0 ? unit.Text.Length - options.Offset : Math.Min(options.BodyChars, unit.Text.Length - options.Offset);
            if (chars > 0 && options.Offset + chars < unit.Text.Length && char.IsHighSurrogate(unit.Text[options.Offset + chars - 1])) chars--;
            if (chars == 0 && options.Offset < unit.Text.Length && options.BodyChars > 0)
                return Failure("message_page_too_small_for_codepoint");
            int? next = options.Offset + chars < unit.Text.Length ? options.Offset + chars : null;
            var page = new LogicalMessagePage(unit, options.Offset, unit.Text.Substring(options.Offset, chars), true,
                next, next.HasValue ? LogicalMessageProjection.Cursor(unit, packet, next.Value) : "",
                LogicalMessageProjection.PageViews(unit, options.Offset, chars));
            return packet with { Pages = new[] { page }, Reason = next.HasValue || options.Offset > 0 ? "qualified_logical_message_page" : "qualified_logical_message_view" };
        }
    }

    internal bool TryUseLogicalMessageRead(LogicalMessageReadPacket packet, string query, BridgeConfig config, Func<bool> use)
        => TryUseLogicalMessageReadWithCatalog(packet,query,config,use,null);

    private bool TryUseLogicalMessageReadWithCatalog(LogicalMessageReadPacket packet,string query,BridgeConfig config,
        Func<bool> use,LogicalDialogueCatalog? catalog)
    {
        lock (_experienceLock)
        {
            if (!LogicalMessageReadAllowed(query, config, packet.CrossSessionRead)) return false;
            using var mention = BeginMentionRequest(query, true);
            using var native = BeginNativeDialogueRequest(query, true, config);
            catalog ??= CurrentLogicalMessageCatalog();
            foreach (var old in packet.Units)
            {
                var current = catalog.FindByKey(old.Key);
                if (current == null || current.VersionFingerprint != old.VersionFingerprint
                    || !packet.CrossSessionRead && current.SessionId != _sessionId) return false;
                foreach (var raw in current.Members)
                    if (!packet.MemberVersions.TryGetValue(raw.EventId, out string? version) || SourceVersion("utterance", raw).Fingerprint != version
                        || !packet.SourceContexts.TryGetValue(raw.EventId, out string? context) || NaturalMemoryContext(raw) != context) return false;
            }
            foreach (var page in packet.Pages)
            {
                if (!packet.Units.Any(u => u.Key == page.Unit.Key && u.VersionFingerprint == page.Unit.VersionFingerprint)
                    || page.Offset < 0 || page.Offset > page.Unit.Text.Length || page.Text.Length > page.Unit.Text.Length - page.Offset) return false;
                if (page.BodyIncluded && page.Unit.Text.Substring(page.Offset, page.Text.Length) != page.Text
                    || !page.BodyIncluded && page.Text.Length != 0) return false;
                if (page.BodyIncluded && !LogicalMessageProjection.PageViews(page.Unit, page.Offset, page.Text.Length).SequenceEqual(page.SourceViews)) return false;
            }
            return use();
        }
    }
}
