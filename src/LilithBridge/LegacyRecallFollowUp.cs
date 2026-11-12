using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace LilithBridge;

internal sealed record LegacyRecallDecision(bool NeedsGeneration, string Reason, int SelectedRecords);

// Adjudicates only the old text-tag handoff after a valid final reply. It does
// not cancel a model-selected native read or restrict reformulation/expansion.
// No cross-request cache and no approximate query/result equivalence is used.
internal static class LegacyRecallFollowUp
{
    internal static LegacyRecallDecision Decide(ApiResult reply,
        IReadOnlyList<MemoryTurn> facts, IReadOnlyList<MemoryTurn> experiences)
    {
        int selected = facts.Count + experiences.Count;
        // A control-only draft still needs a final expression. Do not manufacture
        // local speech, reinterpret silence, or weaken any outgoing-source gate.
        if (reply.Error.Length > 0 || reply.Response.Outcome != "accepted"
            || string.IsNullOrWhiteSpace(reply.Text) && !reply.ExplicitEmptyText)
            return new(true, "no_accepted_final_expression", selected);
        if (!reply.RequestSourcesPermitted() || reply.DialogueRequest?.ReviewSupplyPermitted() == false)
            return new(true, "source_permission_uncertain", selected);
        if (selected == 0) return new(false, "no_additional_records", 0);

        // A read footprint is not proof that the model saw the complete body.
        // Only validated full native review views qualify, never a paper source,
        // a matching word, a short ID alone, a partial page, or a prior turn.
        var request = reply.DialogueRequest;
        if (request == null) return new(true, "additional_or_unproven_records", selected);
        try
        {
            foreach (var turn in facts.Concat(experiences))
            {
                if (turn.EventId.Length == 0) return new(true, "unaddressed_record", selected);
                string id = ReviewContext.ShortId(turn);
                if (!request.ReviewSourceRecords.TryGetValue(id, out var supplied)
                    || !request.ReviewSourceVersions.ContainsKey(id)
                    || !request.ReviewSourceViews.TryGetValue(id, out var views)
                    || JsonSerializer.Serialize(turn, DialogueMaterialEnvelope.DataJson)
                        != JsonSerializer.Serialize(supplied, DialogueMaterialEnvelope.DataJson))
                    return new(true, "additional_or_changed_record", selected);
                if (!views.Any(view => {
                    string text = view.RawTextPreserved ? turn.Text : ReviewContext.Clean(turn.Text);
                    return view.Offset == 0 && !view.Truncated
                        && view.TotalChars == text.Length && view.Text == text;
                }))
                    return new(true, "partially_supplied_record", selected);
            }
            return new(false, "complete_records_already_supplied", selected);
        }
        catch { return new(true, "supply_identity_uncertain", selected); }
    }
}
