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
        if (view.Block.Length == 0 || view.ProgramBlockId.Length == 0 || view.Units.Count == 0 || view.Bodies.Count == 0) return;
        lock (_b1Ownership)
            if (!_contextBodyViews.Any(v => v.Block == view.Block && v.ProgramBlockId == view.ProgramBlockId))
                _contextBodyViews.Add(view with { Bodies = view.Bodies.ToArray(), Units = view.Units.ToArray(), DirectEventIds = view.DirectEventIds.ToArray(), AnchorEventIds = view.AnchorEventIds.ToArray(), SparkEventIds = view.SparkEventIds.ToArray() });
    }

    internal IReadOnlyList<RequestSourceBody> ContextSourceBodiesFor(string actualContext)
    {
        lock (_b1Ownership)
        {
            var result = new List<RequestSourceBody>();
            foreach (var view in _contextBodyViews.Where(v => actualContext.Contains(v.Block, StringComparison.Ordinal)))
            {
                string blockId = view.ProgramBlockId;
                if (blockId.Length == 0
                    || actualContext.IndexOf(view.Block, StringComparison.Ordinal)
                        != actualContext.LastIndexOf(view.Block, StringComparison.Ordinal)
                    || _contextBodyViews.Any(other => !ReferenceEquals(other, view) && other.Block != view.Block
                        && actualContext.Contains(other.Block, StringComparison.Ordinal)
                        && other.ProgramBlockId == blockId)) continue;
                var units = view.Units;
                var verified = new List<RequestSourceBody>();
                foreach (var body in view.Bodies)
                {
                    if (!body.Qualified) continue;
                    int index = -1;
                    for (int i = 0; i < units.Count; i++)
                    {
                        if (units[i].SourceSpans.Any(span => span.EventId == body.EventId))
                        { index = i; break; }
                    }
                    if (index < 0 || body.Location != "runtime_context.experience_blocks[" + blockId + "].messages[" + index + "].text")
                        continue;
                    var unit = units[index];
                    if (body.TextStart > unit.Text.Length || body.Chars > unit.Text.Length - body.TextStart
                        || unit.Text.Substring(body.TextStart, body.Chars) != body.Text) continue;
                    var spans = unit.SourceSpans.Where(span =>
                        span.EventId == body.EventId
                        && string.Equals(span.TextHash, body.Sha256, StringComparison.OrdinalIgnoreCase)
                        && span.OffsetInUnit == body.TextStart
                        && span.SourceOffset == 0
                        && span.Chars == body.Chars).ToArray();
                    var members = unit.Members.Where(member =>
                        member.EventId == body.EventId
                        && RequestSourceBody.Hash(member.Text) == body.Sha256
                        && member.Role == body.Role
                        && member.Origin == body.Origin
                        && member.DeliveryState == body.DeliveryState).ToArray();
                    bool host = B1HostFootprint.Any(t => t.EventId == body.EventId && t.Text == body.Text
                        && t.Role == body.Role && t.Origin == body.Origin && t.DeliveryState == body.DeliveryState);
                    if (spans.Length == 1 && members.Length == 1 && host) verified.Add(body);
                }
                result.AddRange(verified);
            }
            return result.Distinct().ToArray();
        }
    }
}
