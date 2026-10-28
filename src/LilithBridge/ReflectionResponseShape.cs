using System.Text.Json;

namespace LilithBridge;

// Validate the whole response before constructing any proposal. Do not extract
// a valid prefix, repair nesting, discard a wrong-typed proposal, or pick a duplicate.
// Empty/absent proposals remain legal; provenance and persistence stay with MemoryStore.
internal static class ReflectionResponseShape
{
    private static readonly HashSet<string> KnownFields = new(StringComparer.Ordinal)
    {
        "note", "lesson", "relationship", "reflection", "thought", "memories",
        "text", "revises", "settles", "settle_reason", "source_event_ids", "source_day_ids", "source_note_ids"
    };

    internal static string? RejectCode(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return "schema_type_error";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (!seen.Add(property.Name)) return "schema_duplicate_field";
            if (property.Name is "note" or "lesson" or "relationship")
            {
                if (property.Value.ValueKind == JsonValueKind.Null) continue;
                if (property.Value.ValueKind != JsonValueKind.Object) return "schema_type_error";
                string? failure = Proposal(property.Value, property.Name == "note");
                if (failure != null) return failure;
            }
            // Reject known protocol fields in the wrong scope; ignore inert
            // extensions exactly as the consumer does. Never promote or walk them.
            // Existing legacy readers retain their current semantics.
            else if (property.Name is not ("reflection" or "thought" or "memories")
                && KnownFields.Contains(property.Name))
                return "schema_unknown_field";
        }
        return null;
    }

    private static string? Proposal(JsonElement proposal, bool note)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in proposal.EnumerateObject())
        {
            if (!seen.Add(property.Name)) return "schema_duplicate_field";
            bool scalar = property.Name is "text" or "revises"
                || (note && property.Name is ("settles" or "settle_reason"));
            bool ids = note ? property.Name is "source_event_ids" or "source_day_ids"
                : property.Name == "source_note_ids";
            if (!scalar && !ids)
            {
                if (KnownFields.Contains(property.Name)) return "schema_unknown_field";
                continue; // An unknown extension has no proposal or provenance meaning.
            }
            if (scalar && property.Value.ValueKind != JsonValueKind.String) return "schema_type_error";
            if (ids && (property.Value.ValueKind != JsonValueKind.Array
                || property.Value.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String)))
                return "schema_type_error";
        }
        return null;
    }
}
