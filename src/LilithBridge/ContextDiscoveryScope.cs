using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LilithBridge;

// Optional read-side location conditions. They never resolve entities, infer
// event dates, create permissions, or alter the existing record/message owners.
internal sealed record ContextDiscoveryScope(string ContainsText = "",
    DateTimeOffset? RecordedAfter = null, DateTimeOffset? RecordedBefore = null)
{
    internal static readonly string[] FieldNames = {
        "discovery_contains", "discovery_recorded_after", "discovery_recorded_before"
    };
    internal bool HasTime => RecordedAfter.HasValue || RecordedBefore.HasValue;
    internal bool Valid => ContainsText.Length <= 160
        && (ContainsText.Length == 0 || !string.IsNullOrWhiteSpace(ContainsText))
        && (ContainsText.Length > 0 || HasTime)
        && (!RecordedAfter.HasValue || !RecordedBefore.HasValue || RecordedAfter < RecordedBefore);

    private bool AllowsTime(DateTimeOffset at) => !HasTime || at != default
        && (!RecordedAfter.HasValue || at >= RecordedAfter.Value)
        && (!RecordedBefore.HasValue || at < RecordedBefore.Value);

    internal ContextDiscoveryMatch? Match(ContextDiscoveryItem item,
        IReadOnlyDictionary<string, DateTimeOffset?> recordedTimes)
    {
        if (!Valid) return null;
        DateTimeOffset? KnownTime(string id) => recordedTimes.TryGetValue(id, out var at) && at.HasValue && at.Value != default ? at : null;
        var questionAt = item.SourceQuestion is {} question ? KnownTime(question.AnchorEventId) : null;
        if (item.Unit is {} unit)
        {
            // Require all conditions on the SAME actual source record. A name
            // in one member and a date in another must not manufacture a hit.
            // Only original spans are searched: program gap markers and joins
            // between fragments never become literal source matches.
            var members = unit.Members.ToDictionary(m => m.EventId, StringComparer.Ordinal);
            foreach (var span in unit.SourceSpans)
            {
                if (!members.TryGetValue(span.EventId, out var raw)) continue;
                // Use the existing archive's explicit timestamp map. A missing
                // JSON timestamp must not inherit MemoryTurn's constructor clock.
                var recorded = KnownTime(raw.EventId).GetValueOrDefault();
                if (!AllowsTime(recorded)) continue;
                string original = raw.Text.Substring(span.SourceOffset, span.Chars);
                int at = ContainsText.Length == 0 ? 0 : original.IndexOf(ContainsText, StringComparison.OrdinalIgnoreCase);
                if (at >= 0) return new(span.OffsetInUnit + at, raw.EventId, recorded,
                    "original_utterance_record_time", KnownTime(unit.AnchorEventId), questionAt);
            }
            return null;
        }
        if (item.Generated is {} generated && AllowsTime(generated.Metadata.CapturedAtUtc))
        {
            int at = ContainsText.Length == 0 ? 0 : generated.BodyText.IndexOf(ContainsText, StringComparison.OrdinalIgnoreCase);
            if (at >= 0) return new(at, "", generated.Metadata.CapturedAtUtc,
                "received_public_text_captured_at", generated.Metadata.CapturedAtUtc == default ? null : generated.Metadata.CapturedAtUtc, questionAt);
        }
        return null;
    }

    internal object Wire() => new {
        contains_text = ContainsText.Length == 0 ? null : ContainsText,
        recorded_after = RecordedAfter?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        recorded_before = RecordedBefore?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        context = "Requested scope only; message/question context may be outside. Time range [after,before)."
    };

    internal static object Attach(object payload, string key, object value)
    {
        var row = JsonSerializer.SerializeToElement(payload, DialogueMaterialEnvelope.DataJson)
            .EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone(), StringComparer.Ordinal);
        row.Add(key, value);
        return row;
    }

    internal static bool TryParse(JsonElement root, out ContextDiscoveryScope? scope, out string error)
    {
        scope = null; error = "";
        if (!FieldNames.Any(name => root.TryGetProperty(name, out _))) return true;
        string text = "";
        if (root.TryGetProperty("discovery_contains", out var literal)
            && (literal.ValueKind != JsonValueKind.String || (text = literal.GetString() ?? "").Length is < 1 or > 160
                || string.IsNullOrWhiteSpace(text)))
        { error = "discovery_contains must be a nonblank original-text string of 1..160 UTF-16 units"; return false; }
        bool Time(string name, out DateTimeOffset? result)
        {
            result = null;
            if (!root.TryGetProperty(name, out var value)) return true;
            if (value.ValueKind != JsonValueKind.String) return false;
            string raw = value.GetString() ?? "";
            if (!Regex.IsMatch(raw, @"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]{1,7})?(?:Z|[+-][0-9]{2}:[0-9]{2})$",
                    RegexOptions.CultureInvariant)
                || !DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) return false;
            result = parsed.ToUniversalTime(); return true;
        }
        if (!Time("discovery_recorded_after", out var after) || !Time("discovery_recorded_before", out var before))
        { error = "discovery recorded bounds require full ISO8601 timestamps with explicit Z or UTC offset"; return false; }
        var candidate = new ContextDiscoveryScope(text, after, before);
        if (!candidate.Valid) { error = "discovery recorded bounds must form a nonempty forward interval"; return false; }
        scope = candidate; return true;
    }
}

internal sealed record ContextDiscoveryMatch(int Offset, string EventId, DateTimeOffset At, string Basis,
    DateTimeOffset? ViewAt, DateTimeOffset? QuestionAt)
{
    internal object Wire() => new {
        event_id = EventId.Length == 0 ? null : EventId,
        recorded_at = At == default ? null : At.ToString("O", CultureInfo.InvariantCulture),
        time_basis = At == default ? "recorded_time_unknown" : Basis
    };
}
