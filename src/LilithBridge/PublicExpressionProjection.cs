using System.Text.Json;

namespace LilithBridge;

internal static class PublicExpressionProjection
{
    internal static object Enrich(object ordinary,GeneratedReplySnapshot source)
    {
        var metadata=source.Metadata;var state=metadata.PublicExpression!;
        var record=JsonSerializer.SerializeToElement(ordinary,DialogueMaterialEnvelope.DataJson).EnumerateObject()
            .ToDictionary(p=>p.Name,p=>p.Value.Clone(),StringComparer.Ordinal);
        if(state.StreamItem is {} stream)record["stream_component"]=JsonSerializer.SerializeToElement(new {
            response_id=stream.ResponseId,item_id=stream.ItemId,output_index=stream.OutputIndex,content_index=stream.ContentIndex,phase=stream.Phase,
            extent=state.FinalProgress==null?"one_closed_public_message_item;not_complete_response_or_hidden_reasoning":"received_public_prefix_checkpoint;item_may_still_be_open;not_independent_message_or_hidden_reasoning",network_body_complete=metadata.NetworkBodyComplete,
            terminal_outcome=metadata.NetworkOutcome,terminal_event=metadata.NetworkTerminalEvent,
            reconciliation="separate_final_answer_uses_original_terminal_validation;no_automatic_replay"
        },DialogueMaterialEnvelope.DataJson);
        if(state.FinalProgress is {} progress)record["public_final_progress"]=JsonSerializer.SerializeToElement(new {
            generation_id=progress.GenerationId,checkpoint_revision=progress.Revision,state=progress.State,ready_bubbles=progress.ReadyChunks,
            reason=progress.Reason,observed_appearance=state.StreamAppearances,
            meaning="One provider answer generation with versioned received extents. Checkpoints and bubbles are not independent memories. Latest qualified/canonical version is the default; older versions are audit material, never automatically replayed."
        },DialogueMaterialEnvelope.DataJson);
        if(metadata.PublicResponseItems!=null)record["response_public_item_receipts"]=JsonSerializer.SerializeToElement(metadata.PublicResponseItems,DialogueMaterialEnvelope.DataJson);
        record["source_kind"]=JsonSerializer.SerializeToElement("generated_public_expression");
        record["public_expression"]=JsonSerializer.SerializeToElement(new {
            kind=state.Kind,segment_id=state.SegmentId,operation_id=source.RequestId,attempt_id=source.AttemptId,
            writer_session_id=state.WriterSessionId,receipt_revision=state.ReceiptRevision,work_outcome=state.WorkOutcome,
            superseded_by=state.SupersededBy,source_tracking=state.Sources.Complete?"captured_and_currently_revalidated":state.Sources.Reason,
            epistemic_status="model_generated_public_expression;not_hidden_reasoning;not_player_speech;not_new_fact_or_authorization",
            resumption="historical_material_only;no_old_http_work_or_playback_resumed"
        },DialogueMaterialEnvelope.DataJson);
        record["display_evidence"]=JsonSerializer.SerializeToElement(new {
            kind="temporary_expression_appearance_receipt;not_raw_delivered_history",
            plan_version=metadata.DisplayPlanVersion,coordinate_unit="UTF-16",
            ranges=state.Ranges.Select(r=>new {
                bubble_index=r.Index,generated_start_char=r.GeneratedStartChar,generated_chars=r.GeneratedChars,
                display_start_char=r.DisplayStartChar,display_chars=r.DisplayChars,
                recorded_state=r.State,qualification=r.State switch {
                    "shown"=>"whole_bubble_appearance_observed;player_read_unknown",
                    "not_presented"=>"not_told;no_observed_display_submission",
                    "accepted_pending"=>"appearance_unconfirmed;after_interruption_or_restart_remains_unknown;not_shown",
                    _=>"appearance_unknown;not_a_confirmed_utterance" }
            }),
            whitespace_omissions=metadata.WhitespaceOmissions,
            player_read="unknown",meaning="Only shown proves program appearance. Omitted whitespace is not displayed; other ranges never become player knowledge or facts. No automatic replay of a received tail."
        },DialogueMaterialEnvelope.DataJson);
        return record;
    }
}
