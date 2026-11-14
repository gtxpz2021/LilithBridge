using System;
using System.Collections.Generic;
using System.Linq;

namespace LilithBridge;

internal sealed partial class DeepSeekClient
{
    private static void ValidateSceneBodyReferences(DialogueRequest? request, string context,
        IReadOnlyList<RequestSourceBody> actualBodies)
    {
        if (request == null) return;
        foreach (var reference in request.SceneBodyReferencesFor(context))
        {
            var matches = actualBodies.Where(body => body.Qualified && reference.Qualified
                && body.EventId == reference.EventId && body.Kind == reference.Kind
                && body.Text == reference.Text && body.Role == reference.Role
                && body.Origin == reference.Origin && body.DeliveryState == reference.DeliveryState
                && body.Sha256 == reference.Sha256 && body.TextStart == reference.TextStart
                && body.Chars == reference.Chars
                && body.Location.StartsWith("history[", StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1)
                throw new SemanticMaterialException("source_body_reference_changed");
        }
    }

    // Locations are inside the actual source-envelope data message; character
    // ranges are UTF-16, matching the programme's immutable displayed strings.
    // This is deliberately not a map of the wider discovery/permission footprint.
    internal static IReadOnlyList<RequestSourceBody> SourceEnvelopeBodies(
        IEnumerable<ProjectedHistoryTurn> outgoingHistory, MemoryTurn? currentUser,
        string actualCurrentText, bool sourceEnvelope)
    {
        if (!sourceEnvelope) return Array.Empty<RequestSourceBody>();
        var bodies = new List<RequestSourceBody>();
        int index = 0;
        foreach (var entry in outgoingHistory)
        {
            bool current = currentUser != null && entry.Record?.EventId == currentUser.EventId
                && entry.Role == "user" && MemoryStore.Clean(entry.Text) == MemoryStore.Clean(actualCurrentText);
            if (current) continue;
            // Fail closed on a projection that transforms/inserts any text. A
            // reference must resolve to the exact source body, not an approximation.
            if (entry.Text == string.Concat(entry.Records.Select(t => t.Text)))
            {
                int start = 0;
                foreach (var record in entry.Records)
                {
                    bodies.Add(new RequestSourceBody("utterance", record.EventId, record.Text,
                        "history[" + index + "].text", record.Role, record.Origin,
                        record.DeliveryState, start, record.Text.Length));
                    start += record.Text.Length;
                }
            }
            index++;
        }
        // The source envelope intentionally excludes the current input from its
        // history. Its one complete body is the separate actual user message.
        if (currentUser != null && currentUser.Text == actualCurrentText)
            bodies.Add(new RequestSourceBody("utterance", currentUser.EventId, currentUser.Text,
                "current_input", currentUser.Role, currentUser.Origin,
                currentUser.DeliveryState, 0, currentUser.Text.Length));
        return bodies;
    }
}
