using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace LilithBridge;

internal sealed partial class DialogueRequest
{
    // Only the program's renderer can install this transient view. No model
    // response, user text, stored free text or parser can grant a body alias.
    private readonly List<ExperienceBodyView> _contextBodyViews = new();
    internal void BindContextSourceBodies(ExperienceBodyView view)
    {
        if (view.Block.Length == 0 || view.DataJson.Length == 0 || view.Bodies.Count == 0
            || !view.Block.Contains(view.DataJson, StringComparison.Ordinal)) return;
        lock (_b1Ownership)
            if (!_contextBodyViews.Any(v => v.Block == view.Block))
                _contextBodyViews.Add(view with { Bodies = view.Bodies.ToArray(), DirectEventIds = view.DirectEventIds.ToArray(), AnchorEventIds = view.AnchorEventIds.ToArray() });
    }

    internal IReadOnlyList<RequestSourceBody> ContextSourceBodiesFor(string actualContext)
    {
        lock (_b1Ownership)
        {
            var result = new List<RequestSourceBody>();
            foreach (var view in _contextBodyViews.Where(v => actualContext.Contains(v.Block, StringComparison.Ordinal)))
            {
                try
                {
                    // Parse the renderer-owned exact JSON, not a guessed JSON
                    // substring discovered in arbitrary conversation text.
                    using var doc = JsonDocument.Parse(view.DataJson);
                    var root = doc.RootElement;
                    string blockId = root.GetProperty("program_block_id").GetString()!;
                    // A locator must identify one actual block. Several
                    // individually valid blocks with the same program ID are
                    // not resolved by picking a convenient body or version.
                    if (actualContext.IndexOf(view.Block, StringComparison.Ordinal)
                            != actualContext.LastIndexOf(view.Block, StringComparison.Ordinal)
                        || _contextBodyViews.Any(other => other.Block != view.Block
                            && actualContext.Contains(other.Block, StringComparison.Ordinal)
                            && ContextBlockIdentity(other.DataJson) == blockId)) continue;
                    var units = root.GetProperty("messages").EnumerateArray().ToArray();
                    var verified = new List<RequestSourceBody>();
                    foreach (var body in view.Bodies)
                    {
                        if (!body.Qualified) continue;
                        int index = Array.FindIndex(units, unit => unit.GetProperty("source_spans").EnumerateArray()
                            .Any(span => span.GetProperty("event_id").GetString() == body.EventId));
                        if (index < 0 || body.Location != "runtime_context.experience_blocks[" + blockId + "].messages[" + index + "].text")
                            continue;
                        var unit = units[index];
                        string text = unit.GetProperty("text").GetString()!;
                        if (body.TextStart > text.Length || body.Chars > text.Length - body.TextStart
                            || text.Substring(body.TextStart, body.Chars) != body.Text) continue;
                        var spans = unit.GetProperty("source_spans").EnumerateArray().Where(span =>
                            span.GetProperty("event_id").GetString() == body.EventId
                            && span.GetProperty("text_sha256").GetString() == body.Sha256
                            && span.GetProperty("offset_in_unit").GetInt32() == body.TextStart
                            && span.GetProperty("source_offset").GetInt32() == 0
                            && span.GetProperty("chars").GetInt32() == body.Chars).ToArray();
                        var members = unit.GetProperty("member_index").EnumerateArray().Where(member =>
                            member.GetProperty("event_id").GetString() == body.EventId
                            && member.GetProperty("text_sha256").GetString() == body.Sha256
                            && member.GetProperty("role").GetString() == body.Role
                            && member.GetProperty("origin").GetString() == body.Origin
                            && member.GetProperty("delivery_state").GetString() == body.DeliveryState).ToArray();
                        // The actual preparation footprint must own the source,
                        // in addition to the body being present in this carrier.
                        bool host = B1HostFootprint.Any(t => t.EventId == body.EventId && t.Text == body.Text
                            && t.Role == body.Role && t.Origin == body.Origin && t.DeliveryState == body.DeliveryState);
                        if (spans.Length == 1 && members.Length == 1 && host) verified.Add(body);
                    }
                    result.AddRange(verified);
                }
                catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
                {
                    // Missing/invalid map is not permission to guess a source.
                    // B1 rendering falls back to its ordinary inline body.
                }
            }
            return result.Distinct().ToArray();
        }
    }

    private static string? ContextBlockIdentity(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("program_block_id").GetString();
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException)
        { return null; }
    }
}
