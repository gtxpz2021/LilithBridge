using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace LilithBridge;

internal sealed partial class DeepSeekClient
{
    private static string GeneratedReplyCursor(GeneratedReplySnapshot source, int offset) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new {
            key = source.Key, version = source.VersionFingerprint, offset },DialogueMaterialEnvelope.DataJson)))
            .TrimEnd('=').Replace('+','-').Replace('/','_');

    private static ReviewToolOutcome ExecuteGeneratedReplyRead(BridgeConfig config, DialogueRequest request,
        ReviewToolArgs args, MemoryStore owner, CancellationToken token, MethodSource[] derived, MethodOffer? offer)
    {
        token.ThrowIfCancellationRequested();
        if (!request.ReviewToolEnabled || !ReferenceEquals(request.ReviewStore,owner)
            || request.ReviewSessionIdentity != owner.ReviewSessionIdentity
            || request.ReviewDataRoot != DataPaths.Root() || request.ReviewConfigRoot != config.DataDirectory
            || args.Message == null)
            return ReviewProcess.Empty("unavailable","generated_reply_owner_mismatch");
        var options = args.Message;
        var source = owner.GetExactGeneratedReply(options.Reference["generated/".Length..],request.ReviewCurrentText,config);
        if (source == null) return ReviewProcess.Empty("unavailable","generated_reply_source_unavailable");
        if (options.Offset > source.BodyText.Length || options.Offset > 0
            && (options.Cursor != GeneratedReplyCursor(source,options.Offset)
                || options.Offset < source.BodyText.Length && char.IsLowSurrogate(source.BodyText[options.Offset]))
            || options.Cursor.Length > 0 && options.Cursor != GeneratedReplyCursor(source,options.Offset))
            return ReviewProcess.Empty("unavailable","generated_reply_cursor_stale_or_invalid");
        int chars = options.BodyChars == 0 ? source.BodyText.Length-options.Offset
            : Math.Min(options.BodyChars,source.BodyText.Length-options.Offset);
        if (chars > 0 && options.Offset+chars < source.BodyText.Length
            && char.IsHighSurrogate(source.BodyText[options.Offset+chars-1])) chars--;
        if (chars == 0 && options.Offset < source.BodyText.Length)
            return ReviewProcess.Empty("unavailable","generated_reply_page_cannot_advance");
        var material = new GeneratedReplyMaterial("",new[]{source},Array.Empty<RequestSourceBody>());
        request.BindGeneratedReplyMaterial(owner,material,request.ReviewCurrentText,config,append:true);
        var snapshot = new ReviewSourceSnapshot(config,request,owner,args.Question,
            Array.Empty<MemoryTurn>(),derived,offer);
        if (!snapshot.IsPermitted()) return ReviewProcess.Empty("unavailable","generated_reply_source_changed");
        int? next = options.Offset+chars < source.BodyText.Length ? options.Offset+chars : null;
        string Json(bool body) => ReviewContext.Serialize(new {
            status = body ? next.HasValue ? "partial_page" : "ok" : "body_omitted",
            reason = body ? "qualified_received_public_answer" : "existing_history_request_budget",
            read_unit = "generated_public_answer", body_included = body,
            projection_version = 2, source_contract = GeneratedReplyProjection.Contract,
            record = body ? GeneratedReplyProjection.Record(source,offset:options.Offset,chars:chars) : null,
            message_ref = options.Reference,
            next_offset = body ? next : options.Offset,
            next_cursor = body ? next.HasValue ? GeneratedReplyCursor(source,next.Value) : ""
                : GeneratedReplyCursor(source,options.Offset),
            citation_eligibility = "read_only_generated_text;not_delivered_utterance_or_player_read_receipt"
        });
        string json = Json(true);
        // No new ceiling: only honour a history policy already activated by
        // another legacy read in this same request, with an explicit omission.
        return new ReviewToolOutcome(json,0,0,0,0,0,0,false,
            SourceSnapshot:snapshot,ReadDataVersion:MemoryStore.SemanticTextHash(json),GeneratedReply:source);
    }
    private static ReviewToolOutcome FitGeneratedReplyOutcome(BridgeConfig config,
        Dictionary<string,object> payload,List<object> messages,PendingNativeCall call,
        DialogueRequest request,ReviewToolOutcome outcome,NativeReadReceipts receipts)
    {
        if(outcome.GeneratedReply==null)return outcome;
        string Preview(ReviewToolOutcome result)=>result.ReadDataVersion.Length==0?result.Json:
            receipts.Project(ReviewContext.ToolName,JsonSerializer.Serialize(call.Review),result.ReadDataVersion,call.Id,result.Json,remember:false);
        var readable=ReadableGeneratedReplyOutcome(config,outcome);
        if(!request.ChatHistoryBudgetActive)return readable;
        string readablePreview=Preview(readable);
        // A duplicate receipt points only to an actual still-present result.
        // It supplies no new page and keeps the existing receipt/fit contract.
        if(!ReferenceEquals(readable,outcome))
        {
            using var preview=JsonDocument.Parse(readablePreview);
            if(preview.RootElement.GetProperty("status").GetString()=="already_supplied"
                &&ChatHistoryFits(config,payload,messages,call.Id,readablePreview,request))return readable;
        }
        // Preserve the original JSON page's admission/omission decision. A
        // larger natural presentation falls back to that exact admitted JSON.
        if(ChatHistoryFits(config,payload,messages,call.Id,Preview(outcome),request))
            return ReferenceEquals(readable,outcome)||ChatHistoryFits(config,payload,messages,call.Id,readablePreview,request)?readable:outcome;
        var source=outcome.GeneratedReply;int offset=call.Review?.Message?.Offset??0;
        string json=ReviewContext.Serialize(new {status="body_omitted",reason="existing_history_request_budget",
            read_unit="generated_public_answer",body_included=false,
            message_ref="generated/"+source.Key,source.VersionFingerprint,source.BodyHash,
            next_offset=offset,next_cursor=GeneratedReplyCursor(source,offset),
            note="No body was supplied. The existing history budget is active; this is not source absence. A permitted message read can request an explicit page."});
        return outcome with {Json=json,ReadDataVersion=MemoryStore.SemanticTextHash(json)};
    }

    private static ReviewToolOutcome ReadableGeneratedReplyOutcome(BridgeConfig config,ReviewToolOutcome outcome)
    {
        if(config.B1MaterialPresentation!="natural"||outcome.GeneratedReply is not {} source)return outcome;
        using var document=JsonDocument.Parse(outcome.Json);
        var root=document.RootElement;
        if(!root.TryGetProperty("body_included",out var included)||!included.GetBoolean()
            ||!root.TryGetProperty("record",out var record)||record.ValueKind!=JsonValueKind.Object)return outcome;
        int offset=record.GetProperty("offset").GetInt32(),chars=record.GetProperty("supplied_chars").GetInt32();
        if(!GeneratedReadableView.TryCreatePage(source,offset,chars,out var page,out _))return outcome;
        var result=root.EnumerateObject().ToDictionary(p=>p.Name,p=>p.Value.Clone(),StringComparer.Ordinal);
        var projected=record.EnumerateObject().Where(p=>p.Name is not ("body_parts" or "display_evidence"))
            .ToDictionary(p=>p.Name,p=>p.Value.Clone(),StringComparer.Ordinal);
        projected.Add("presentation",JsonSerializer.SerializeToElement("generated-readable-page-v1"));
        projected.Add("readable_page",JsonSerializer.SerializeToElement(page!.Text,DialogueMaterialEnvelope.DataJson));
        result["record"]=JsonSerializer.SerializeToElement(projected,DialogueMaterialEnvelope.DataJson);
        result["source_contract"]=JsonSerializer.SerializeToElement(GeneratedReadableView.PageContract);
        string json=ReviewContext.Serialize(result);
        return outcome with {Json=json,ReadDataVersion=MemoryStore.SemanticTextHash(json)};
    }

}
