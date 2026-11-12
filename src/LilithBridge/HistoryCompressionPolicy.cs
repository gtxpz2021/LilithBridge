using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LilithBridge;

// This policy affects only the recent-history producer. It does not change
// MemorySemantics.CompressConversation or any day/reflection producer.
internal static class HistoryCompressionPolicy
{
    internal const string RetainRaw = "retain_raw";
    internal const string AuxiliaryExtracts = "auxiliary_extracts";
    internal const string ExtractKind = "conversation.summary";
    internal const string ExtractOrigin = "history_extract";
    internal static string NormalizeMode(string? mode) => mode == AuxiliaryExtracts ? AuxiliaryExtracts : RetainRaw;

    internal static bool SameRaw(MemoryTurn a, MemoryTurn b) => a.SchemaVersion == b.SchemaVersion
        && a.EventId == b.EventId && a.SourceEventId == b.SourceEventId
        && a.SourceEventIds != null && b.SourceEventIds != null && a.SourceEventIds.SequenceEqual(b.SourceEventIds)
        && a.SessionId == b.SessionId && a.Sequence == b.Sequence && a.Timestamp == b.Timestamp
        && a.Role == b.Role && a.Kind == b.Kind && a.Origin == b.Origin
        && a.DeliveryState == b.DeliveryState && a.Text == b.Text;

    // Fresh physical confirmation, deliberately independent of the ordinary
    // recall cache/first-wins policy. Conflicting copies never prove durability.
    internal static bool ConfirmDurableRaw(string directory, IReadOnlyList<MemoryTurn> required, out string reason)
    {
        reason = "";
        if (required.Count == 0) return true;
        try
        {
            var targets = new Dictionary<string, MemoryTurn>(StringComparer.Ordinal);
            foreach (var turn in required)
            {
                if (turn == null || string.IsNullOrEmpty(turn.EventId) || turn.SourceEventIds == null)
                { reason = "required_raw_identity_invalid"; return false; }
                if (targets.TryGetValue(turn.EventId, out var prior) && !SameRaw(turn, prior))
                { reason = "required_raw_same_id_conflict"; return false; }
                targets[turn.EventId] = turn;
            }
            var found = new HashSet<string>(StringComparer.Ordinal);
            if (!Directory.Exists(directory)) { reason = "archive_directory_missing"; return false; }
            foreach (string path in Directory.EnumerateFiles(directory, "session-*.jsonl").OrderBy(p => p, StringComparer.Ordinal))
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var reader = new StreamReader(file))
            {
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    using var doc = JsonDocument.Parse(line);
                    if (!doc.RootElement.TryGetProperty("EventId", out var id) || id.ValueKind != JsonValueKind.String)
                    { reason = "archive_record_identity_invalid"; return false; }
                    if (!targets.TryGetValue(id.GetString() ?? "", out var target)) continue;
                    var stored = doc.RootElement.Deserialize<MemoryTurn>();
                    if (stored == null || !SameRaw(target, stored))
                    { reason = "archive_same_id_conflict"; return false; }
                    found.Add(target.EventId);
                }
            }
            if (found.Count != targets.Count) { reason = "archive_required_raw_missing"; return false; }
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        { reason = "archive_read_unconfirmed"; return false; }
    }

    private static readonly JsonSerializerOptions Json = new() {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // Newest whole messages compete first. A body is either supplied exactly
    // or omitted as a unit; no substring, clause guessing, or 44-char clipping.
    internal static string BuildAuxiliary(IReadOnlyList<MemoryTurn> raw, int maxChars,
        Func<ProjectedHistoryTurn, bool> wholeMessageAllowed)
    {
        var units = HistoryProjection.Build(raw, int.MaxValue, true, true).Turns;
        if (units.Count == 0) return "";
        var chosen = new List<ProjectedHistoryTurn>();
        var reasons = new Dictionary<ProjectedHistoryTurn, string>();
        foreach (var u in units)
            if (!wholeMessageAllowed(u)) reasons[u] = "whole_message_extent_unverified";
        object Row(ProjectedHistoryTurn u) => new {
            speaker = u.Role, origin = u.Records[0].Origin,
            source_ref = "utterance:" + u.Records[0].EventId,
            last_source_ref = u.Records.Count == 1 ? null : "utterance:" + u.Records[^1].EventId,
            source_records = u.Records.Count, text = u.Text,
            coverage = "whole_record_or_verified_terminal_reply"
        };
        string Render()
        {
            var omitted = units.Where(u => !chosen.Contains(u)).ToArray();
            return JsonSerializer.Serialize(new {
                kind = ExtractKind, format = "whole_message_extract_v1",
                coverage = "auxiliary_only;originals_not_fully_supplied",
                auxiliary_budget_chars = maxChars,
                selected = units.Where(chosen.Contains).Select(Row).ToArray(),
                omitted = new {
                    messages = omitted.Length, source_records = omitted.Sum(u => u.Records.Count),
                    reasons = omitted.GroupBy(u => reasons.GetValueOrDefault(u, "auxiliary_budget_whole_message_not_selected"))
                        .ToDictionary(g => g.Key, g => g.Count()),
                    first_source_ref = omitted.Length == 0 ? null : "utterance:" + omitted[0].Records[0].EventId,
                    last_source_ref = omitted.Length == 0 ? null : "utterance:" + omitted[^1].Records[^1].EventId
                },
                source_index = "this_record.SourceEventIds;batch_provenance_not_sentence_support"
            }, Json);
        }
        foreach (var unit in units.Reverse())
        {
            if (reasons.ContainsKey(unit)) continue;
            chosen.Add(unit);
            if (Render().Length <= maxChars) continue;
            chosen.Remove(unit); reasons[unit] = "auxiliary_budget_whole_message_not_selected";
        }
        string result = Render();
        if (result.Length <= maxChars) return result;
        // Extremely long malformed identities cannot force either a cut source
        // pointer or a cut claim. The complete source index remains on the record.
        result = JsonSerializer.Serialize(new {
            kind = ExtractKind, format = "whole_message_extract_v1", coverage = "no_body_selected",
            omitted_source_records = raw.Count, reason = "auxiliary_metadata_budget",
            source_index = "this_record.SourceEventIds;batch_provenance_not_sentence_support"
        }, Json);
        return result.Length <= maxChars ? result : "";
    }
}
