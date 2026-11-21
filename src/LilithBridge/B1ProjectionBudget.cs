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
    internal static void Select(B1Packet packet, IReadOnlyList<B1Material> plan, bool readOnly,
        IReadOnlyList<RequestSourceBody>? hostBodies = null,
        IReadOnlyList<RequestSourceBody>? jsonFallbackHostBodies = null,
        bool naturalPresentation = false)
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
            bool folded = !readOnly && naturalPresentation && hostBodies is { Count: > 0 }
                && unit.All(m => RequestSourceBody.Match(hostBodies, m) != null);
            int effectiveChars = folded ? Math.Min(decision.TextChars, 64) : decision.TextChars;
            if (chars + effectiveChars > B1Continuity.MaxPacketChars)
            {
                decision.Outcome = "body_char_budget";
                packet.Omissions.Add(new(item.ReplyRoot ?? item.Ref,
                    item.ReplyRunFirst == null ? decision.Outcome : "historical_public_reply_group_body_char_budget"));
                packet.Diagnostic = "view_partial_budget";
                continue;
            }
            materials.AddRange(unit);
            chars += effectiveChars;
        }
        B1Continuity.AssignShortRefs(packet);
        // Keep JSON fallback UTF-8 byte ceiling honest using JSON-compatible host refs.
        int Bytes() => Encoding.UTF8.GetByteCount(readOnly
            ? B1Continuity.RenderReadOnly(packet)
            : (jsonFallbackHostBodies is { Count: > 0 }
                ? B1Continuity.Render(packet, jsonFallbackHostBodies)
                : B1Continuity.Render(packet)));
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
        OrderSurvivingB1LineageChronologically(materials);
        B1Continuity.AssignShortRefs(packet);
        observation.InlineRenderedUtf8Bytes = bytes;
        packet.SuppliedChars = materials.Sum(m => m.Text.Length);
    }

    // After budget selection has guaranteed that latest current heads survive
    // ahead of older historical versions, order surviving B1 entries within each
    // connected lineage chronologically (predecessor before successor) so
    // B1ReadableView and JSON views never present revisions before their targets.
    private static void OrderSurvivingB1LineageChronologically(List<B1Material> materials)
    {
        var b1Indices = new List<int>();
        var b1Items = new List<B1Material>();
        for (int i = 0; i < materials.Count; i++)
        {
            if (materials[i].Ref.Kind == "b1" && materials[i].ReplyRunFirst == null)
            {
                b1Indices.Add(i);
                b1Items.Add(materials[i]);
            }
        }
        if (b1Items.Count < 2) return;
        // Group connected B1 entries (by LogicalId or Target link among surviving B1 items)
        var parentOf = new int[b1Items.Count];
        for (int i = 0; i < b1Items.Count; i++) parentOf[i] = i;
        int Find(int x) => parentOf[x] == x ? x : (parentOf[x] = Find(parentOf[x]));
        void Union(int a, int b) { int ra = Find(a), rb = Find(b); if (ra != rb) parentOf[Math.Max(ra, rb)] = Math.Min(ra, rb); }
        for (int i = 0; i < b1Items.Count; i++)
        {
            for (int j = i + 1; j < b1Items.Count; j++)
            {
                bool sameLogical = b1Items[i].LogicalId.Length > 0 && b1Items[i].LogicalId == b1Items[j].LogicalId;
                bool targetLink = b1Items[i].Target == b1Items[j].Ref.Key || b1Items[j].Target == b1Items[i].Ref.Key;
                if (sameLogical || targetLink) Union(i, j);
            }
        }
        var groups = new List<(int Root, List<B1Material> Members)>();
        var byRoot = new Dictionary<int, List<B1Material>>();
        for (int i = 0; i < b1Items.Count; i++)
        {
            int r = Find(i);
            if (!byRoot.TryGetValue(r, out var list))
            {
                list = new List<B1Material>();
                byRoot[r] = list;
                groups.Add((r, list));
            }
            list.Add(b1Items[i]);
        }
        if (groups.All(g => g.Members.Count <= 1)) return;
        var reordered = new List<B1Material>(b1Items.Count);
        foreach (var g in groups)
        {
            if (g.Members.Count <= 1) { reordered.AddRange(g.Members); continue; }
            var remaining = g.Members.OrderBy(m => m.At).ThenBy(m => m.Revision).ToList();
            var sorted = new List<B1Material>(remaining.Count);
            while (remaining.Count > 0)
            {
                // Pick the earliest item whose Target is not among 'remaining'
                int pick = remaining.FindIndex(m => !remaining.Any(other => other.Ref.Key != m.Ref.Key && m.Target == other.Ref.Key));
                if (pick < 0) pick = 0;
                sorted.Add(remaining[pick]);
                remaining.RemoveAt(pick);
            }
            reordered.AddRange(sorted);
        }
        for (int i = 0; i < b1Indices.Count; i++)
            materials[b1Indices[i]] = reordered[i];
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
