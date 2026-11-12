using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace LilithBridge;

// Only candidates already resolved and qualified by CaptureB1View enter here.
// This pass neither discovers sources nor grants permission to an omitted body.
internal sealed record B1ProjectionUnitDecision(string Key, string Root,
    IReadOnlyList<string> SourceKeys, int TextChars)
{
    internal string Outcome { get; set; } = "supplied";
}

internal sealed class B1ProjectionBudgetObservation
{
    internal List<B1ProjectionUnitDecision> Units { get; } = new();
    internal int SourceRecords => Units.Sum(u => u.SourceKeys.Count);
    internal int TextChars => Units.Sum(u => u.TextChars);
    internal int InlineRenderedUtf8Bytes { get; set; }
}

internal static class B1ProjectionBudget
{
    internal static void Select(B1Packet packet, IReadOnlyList<B1Material> plan, bool readOnly)
    {
        var materials = packet.Materials;
        materials.Clear();
        var observation = packet.ProjectionBudget = new B1ProjectionBudgetObservation();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var groups = plan.Where(m => m.ReplyRunFirst != null)
            .GroupBy(m => m.ReplyRunFirst!.Key)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        int chars = 0;
        foreach (var item in plan)
        {
            if (!visited.Add(item.Ref.Key)) continue;
            var unit = item.ReplyRunFirst == null ? new List<B1Material> { item }
                : groups[item.ReplyRunFirst.Key];
            foreach (var member in unit) visited.Add(member.Ref.Key);
            var decision = new B1ProjectionUnitDecision(item.ReplyRunFirst?.Key ?? item.Ref.Key,
                item.ReplyRoot?.Key ?? "", unit.Select(m => m.Ref.Key).ToArray(), unit.Sum(m => m.Text.Length));
            observation.Units.Add(decision);
            if (item.ReplyRunFirst != null && !materials.Any(m => m.Ref.Key == item.ReplyRoot!.Key))
            {
                decision.Outcome = "public_reply_source_not_supplied";
                packet.Omissions.Add(new(item.ReplyRoot!, decision.Outcome));
                continue;
            }
            if (chars + decision.TextChars > B1Continuity.MaxPacketChars)
            {
                decision.Outcome = "body_char_budget";
                packet.Omissions.Add(new(item.ReplyRoot ?? item.Ref,
                    item.ReplyRunFirst == null ? decision.Outcome : "historical_public_reply_group_body_char_budget"));
                packet.Diagnostic = "view_partial_budget";
                continue;
            }
            materials.AddRange(unit);
            chars += decision.TextChars;
        }
        B1Continuity.AssignShortRefs(packet);
        // The same inline representation as the existing capture budget. Every
        // original field, reference, omission and budget counter is included.
        int Bytes() => Encoding.UTF8.GetByteCount(readOnly
            ? B1Continuity.RenderReadOnly(packet) : B1Continuity.Render(packet));
        int bytes = Bytes();
        while (bytes > B1Continuity.MaxPacketUtf8Bytes && materials.Count > 0)
        {
            var last = materials[^1];
            string key = last.ReplyRunFirst?.Key ?? last.Ref.Key;
            var decision = observation.Units.Single(u => u.Key == key);
            decision.Outcome = "rendered_utf8_byte_budget";
            foreach (var removed in materials.Where(m => (m.ReplyRunFirst?.Key ?? m.Ref.Key) == key).ToList())
            {
                materials.Remove(removed);
                packet.Omissions.Add(new(removed.Ref, decision.Outcome));
            }
            packet.Diagnostic = "view_partial_budget";
            B1Continuity.AssignShortRefs(packet);
            bytes = Bytes();
        }
        observation.InlineRenderedUtf8Bytes = bytes;
        packet.SuppliedChars = materials.Sum(m => m.Text.Length);
    }

    internal static void Trace(B1Packet packet)
    {
        if (packet.ProjectionBudget is not { } observation) return;
        // Local telemetry only: identities and execution facts, never source
        // text. Denied/unresolved candidates never entered the plan. These ids
        // are not sent as new readable/citable material to the model.
        NativeEventLog.Trace("b1.projection_budget", "mod", "system", "B1 material projection budget",
            JsonSerializer.Serialize(new {
                request_id = packet.Id, candidate_units = observation.Units.Count,
                candidate_source_records = observation.SourceRecords,
                supplied_units = B1Continuity.SelectionUnits(packet.Materials),
                supplied_source_records = packet.Materials.Count, text_chars = packet.SuppliedChars,
                text_char_limit = B1Continuity.MaxPacketChars,
                rendered_utf8_byte_limit = B1Continuity.MaxPacketUtf8Bytes,
                budget_inline_rendered_utf8_bytes = observation.InlineRenderedUtf8Bytes,
                units = observation.Units.Select(u => new {
                    unit_key = u.Key, root_key = u.Root, source_keys = u.SourceKeys,
                    text_chars = u.TextChars, outcome = u.Outcome })
            }));
    }
}
