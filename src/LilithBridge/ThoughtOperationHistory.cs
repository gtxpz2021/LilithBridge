using System.Text.Json;

namespace LilithBridge;

// Rebuilt from the exact note bodies already admitted to this tool page.
// This is an observation about one saved operation, never a graph edge used for
// discovery, qualification, revision authority, or lifecycle changes.
internal static class ThoughtOperationHistory
{
    internal static string Append(string json, IReadOnlyList<ChatHistoryItem> items)
    {
        try
        {
            var notes = items.Where(i => i.BodyIncluded && i.Text.Length > 0
                    && i.Material.Ref.Kind == "note" && i.Turn.Kind == "reflection.note"
                    && i.Turn.Origin == "reflection")
                .Select(i => i.Turn).GroupBy(t => t.EventId, StringComparer.Ordinal)
                .Where(g => g.Key.Length > 0 && g.Count() == 1).Select(g => g.Single()).ToArray();
            var operations = new List<object>();
            foreach (var created in notes.Where(t => !string.IsNullOrWhiteSpace(t.OperationId))
                .GroupBy(t => t.OperationId, StringComparer.Ordinal))
            {
                var createdIds = created.Select(t => t.EventId).ToHashSet(StringComparer.Ordinal);
                var settled = notes.Where(t => !createdIds.Contains(t.EventId)
                    && t.ReflectionSettlementOperationId == created.Key
                    && t.SettledAt != null && !string.IsNullOrWhiteSpace(t.SettleReason)).ToArray();
                if (settled.Length == 0) continue;
                operations.Add(new {
                    operation_id = created.Key,
                    created_note_refs = created.Select(t => "note:" + t.EventId).ToArray(),
                    settled_notes = settled.Select(t => new {
                        history_ref = "note:" + t.EventId,
                        settled_at = t.SettledAt, original_settle_reason = t.SettleReason
                    }).ToArray()
                });
            }
            if (operations.Count == 0) return json;
            using var document = JsonDocument.Parse(json);
            var payload = document.RootElement.EnumerateObject()
                .ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
            payload["thought_operation_history"] = JsonSerializer.SerializeToElement(new {
                meaning = "Creation and settlement were recorded in the same operation. This does not say that the new note corrects, replaces, agrees with, or caused settlement of the other note. The settlement reason is the original recorded reason for that settled note, not a new explanation or a user fact.",
                coverage = "Only qualified note bodies actually included on this page. Missing context is not proof of no relationship. This adds no source/citation authority, reachable records, or lifecycle change.",
                operations
            }, DialogueMaterialEnvelope.DataJson);
            return ReviewContext.Serialize(payload);
        }
        catch (Exception) { return json; } // Optional history never blocks the lawful page.
    }
}
