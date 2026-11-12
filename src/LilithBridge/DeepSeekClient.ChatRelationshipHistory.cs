using System.Text;
using System.Text.Json;

namespace LilithBridge;

internal sealed record ChatHistoryContinuation(string RootVersion,ChatHistoryReadOptions Options,
    int? NextBeforeRevision,string NextCursor,IReadOnlyList<ChatHistoryLocator> Deferred,int DeferredCount);

internal sealed partial class DeepSeekClient
{
    private ReviewToolOutcome ExecuteChatRelationshipHistory(BridgeConfig config,DialogueRequest request,
        ReviewToolArgs args,MemoryStore owner,CancellationToken token,MethodSource[] derived,MethodOffer? offer)
    {
        token.ThrowIfCancellationRequested();
        if(!request.ReviewToolEnabled)return ReviewProcess.Empty("unavailable","disabled");
        if(!ReferenceEquals(request.ReviewStore,owner)||request.ReviewSessionIdentity!=owner.ReviewSessionIdentity
            ||request.ReviewDataRoot!=DataPaths.Root()||request.ReviewConfigRoot!=config.DataDirectory)
            return ReviewProcess.Empty("unavailable","history_owner_mismatch");
        request.ChatHistoryBudgetActive=true;
        string query=args.Query.Length>0?args.Query:args.Question;
        var packet=owner.ReadChatRelationshipHistory(args.RootVersion,query,config,args.HistoryOptions??new());
        request.ChatHistoryLastPacket=ChatHistoryContinuationOf(packet);
        token.ThrowIfCancellationRequested();
        if(packet.Status!="ok"&&(packet.MetadataSources?.Count??0)==0)return new(ChatHistoryJson(packet,request),0,0,0,0,0,0,false,
            ReadDataVersion:packet.DataVersion,MethodSourcesTracked:false);
        var snapshot=new ReviewSourceSnapshot(config,request,owner,args.Question,Array.Empty<MemoryTurn>(),derived,offer,packet);
        if(!snapshot.IsPermitted())return ReviewProcess.Empty("unavailable","source_changed");
        return new(ChatHistoryJson(packet,request),0,0,0,0,0,0,false,
            SourceSnapshot:snapshot,ReadDataVersion:packet.DataVersion,ChatHistory:packet,MethodSourcesTracked:false);
    }

    // Only the explicit supplied range is serialized. Full records remain
    // private, request-local revalidation evidence and never enter write pools.
    private static string ChatHistoryJson(ChatHistoryPacket packet,DialogueRequest request)=>ReviewContext.Serialize(new {
        status=packet.Status,reason=packet.Reason,mode="history",history_scope=packet.Options?.Scope??"focus",
        requested_body_chars=packet.Options?.BodyChars??700,
        body_selection=packet.Options?.Find.Length>0?"first_literal_match_with_bounded_context; not semantic completeness":"prefix_or_identity_bound_continuation",
        unread_prefix_present=packet.Items.Any(i=>i.Offset>0),
        page_candidates_complete=packet.ScopeComplete,
        older_catalog_range_exhausted=packet.Options?.Scope=="versions"?packet.FamilyExhausted:(bool?)null,
        catalog_before_revision=packet.Options?.BeforeRevision??0,
        catalogue_domain="currently qualified versions only; unavailable or unknown members are not enumerated",
        family_coverage="not_asserted_by_this_page",
        returned_record_bodies_complete=packet.Items.Count>0&&packet.Items.All(i=>i.BodyIncluded&&i.Offset==0&&i.NextOffset==null),
        comparison_version_bodies=packet.Items.Count(i=>i.Material.Ref.Kind=="reading"&&i.BodyIncluded),
        comparison_context=packet.Options?.Scope=="focus"&&packet.Items.Count(i=>i.Material.Ref.Kind=="reading"&&i.BodyIncluded)<2
            ?"single_or_no_qualified_comparison_endpoint; a full comparison is not supplied":"not_a_claim_of_sufficient_context",
        root_version=ChatHistorySafeRoot(packet),family_id=packet.Items.Count>0?packet.FamilyId:null,
        next_before_revision=packet.NextBeforeRevision,next_cursor=packet.NextCursor,
        next_locator=ChatHistoryNextLocator(packet),continuation_note="Use next_locator as locating arguments and keep the original self-contained question. Cursor and before_revision are alternatives, not a pair. has_more_locators describes unexpanded locating paths, not guaranteed readable bodies or sufficient context.",
        records=packet.Items.Select(item=>new {
            history_ref=item.Material.Ref.Key,kind=item.Material.Ref.Kind,event_id=item.Turn.EventId,
            recall_id=item.Turn.RecallId,revision=item.Turn.Revision,role=item.Turn.Role,origin=item.Turn.Origin,
            time=item.Turn.Timestamp.ToString("O"),delivery=item.Turn.DeliveryState,
            lifecycle=item.Turn.LifecycleState,validity=item.Turn.Validity,reading_state=item.Material.State,
            text_sha256=item.Material.Ref.Sha256,version_fingerprint=item.Material.VersionFingerprint,
            record_kind=item.Turn.Kind,source_context=item.Material.Scope,
            body_included=item.BodyIncluded,offset=item.Offset,total_chars=item.Turn.Text.Length,
            text=item.Text,next_offset=item.NextOffset,next_cursor=item.NextCursor,
            lineage_status_at_read=item.LineageStatus,
            revises=item.Turn.SourceEventId,source_note_ids=item.Turn.SourceNoteIds.Take(12),
            source_event_ids=item.Turn.SourceEventIds.Take(12),source_day_ids=item.Turn.SourceDayIds.Take(12),
            declared_source_count=item.Turn.SourceNoteIds.Length+item.Turn.SourceEventIds.Length+item.Turn.SourceDayIds.Length,
            source_links_truncated=item.Turn.SourceNoteIds.Length>12||item.Turn.SourceEventIds.Length>12||item.Turn.SourceDayIds.Length>12
        }).ToArray(),
        omissions=packet.Omissions,
        locators=packet.Locators?.Take(packet.Reason=="request_budget"?4:24),
        not_expanded=packet.NotExpanded?.Take(packet.Reason=="request_budget"?4:24),
        omission_notice_count=packet.OmissionCount,locator_count=packet.LocatorCount,deferred_notice_count=packet.NotExpandedCount,
        gap_count_semantics="Distinct returned locating/gap notices; redacted notices may aggregate. These are not exact counts of all missing or unavailable materials.",
        locator_list_truncated=packet.LocatorCount>Math.Min(packet.Locators?.Count??0,packet.Reason=="request_budget"?4:24),
        not_expanded_list_truncated=packet.NotExpandedCount>Math.Min(packet.NotExpanded?.Count??0,packet.Reason=="request_budget"?4:24),
        coverage_note="Page completion only means the selected candidates were examined. It does not mean bodies, declared provenance, the whole family or the question are complete. older_catalog_range_exhausted applies below catalog_before_revision only. Missing, denied, not expanded and budget-omitted differ. Expression/interpretation is not proof that its content happened. Links do not prove causation. Other dependencies are checked for permission, not automatically expanded. Cursor binds source identity and range; it is not a secret token or permission.",
        used_calls=request.ReviewCalls,remaining_calls=Math.Max(0,ReviewContext.MaxCallsPerTurn-request.ReviewCalls),
        remaining_reconsider=Math.Max(0,ReviewContext.MaxReconsiderPerTurn-request.ReconsiderCalls),
        can_rephrase=packet.Status=="not_found",
        has_more_locators=packet.NotExpanded?.Count>0||packet.NextBeforeRevision!=null||packet.NextCursor.Length>0,
        can_continue=request.CanStartReviewRead&&(packet.NotExpanded?.Count>0||packet.NextBeforeRevision!=null||packet.NextCursor.Length>0)
    });

    private static ChatHistoryContinuation ChatHistoryContinuationOf(ChatHistoryPacket packet)
        =>new(ChatHistorySafeRoot(packet)??"",packet.Options??new(),packet.NextBeforeRevision,packet.NextCursor,
            packet.NotExpanded??Array.Empty<ChatHistoryLocator>(),packet.NotExpandedCount);

    private static string? ChatHistorySafeRoot(ChatHistoryPacket packet)=>packet.RootExplicit
        ||packet.Items.Any(i=>i.Material.Ref.Kind=="reading"&&i.Turn.EventId==packet.RootVersion)
        ?packet.RootVersion:packet.Items.FirstOrDefault(i=>i.Material.Ref.Kind=="reading")?.Turn.EventId;

    private static object? ChatHistoryNextLocator(ChatHistoryPacket packet)
    {
        string? root=ChatHistorySafeRoot(packet);
        if(root==null)return null;
        if(packet.Options?.Scope=="record"&&packet.Items.FirstOrDefault(i=>i.NextOffset!=null) is { } item)
            return new {mode="history",root_version=root,history_scope="record",history_ref=item.Material.Ref.Key,
                offset=item.NextOffset,history_cursor=item.NextCursor,history_body_chars=packet.Options?.BodyChars??700};
        if(packet.Options?.Scope=="versions"&&packet.NextBeforeRevision is { } boundary)
            return new {mode="history",root_version=root,history_scope="versions",before_revision=boundary};
        if(packet.NextCursor.Length>0)
        {
            string scope=packet.NextCursor.StartsWith("sources:",StringComparison.Ordinal)?"sources":packet.Options?.Scope??"versions";
            if(scope=="sources")return new {mode="history",root_version=root,history_scope="sources",
                history_ref=packet.Options?.Reference.Length>0?packet.Options.Reference:"reading:"+root,
                history_cursor=packet.NextCursor};
            if(scope=="versions")return new {mode="history",root_version=root,history_scope="versions",history_cursor=packet.NextCursor};
            return null;
        }
        if(packet.NextBeforeRevision is { } before)
            return new {mode="history",root_version=root,history_scope="versions",before_revision=before};
        return null;
    }

    private static bool TryUseReviewAndChatHistory(ApiResult envelope,ReviewSourceSnapshot? review,Func<bool> use)
        =>review!=null&&(envelope.DialogueRequest is {HasChatHistorySupply:true} owner
            ?owner.TryUseChatHistory(use,review):review.TryUse(use));

    private static string AdaptHistoryBudgetPayload(BridgeConfig config,string json,string kind="main")
    {
        if(!config.IsOpenAI)return json;
        json=OpenAIProtocol.BuildRequest(config,json,kind);
        return config.UsesChatGPT?ChatGPTProtocol.Adapt(json):json;
    }
    private static bool ChatHistoryFits(BridgeConfig config,Dictionary<string,object> payload,List<object> messages,
        string callId,string result,DialogueRequest request)
    {
        try
        {
            var prospective=new Dictionary<string,object>(payload){["messages"]=messages.Concat(new object[]{
                new {role="tool",tool_call_id=callId,content=result}}).ToArray()};
            long bytes=Encoding.UTF8.GetByteCount(AdaptHistoryBudgetPayload(config,SerializeTimed(prospective)));
            return request.ChatHistoryRequestFits(bytes);
        }
        catch{return false;}
    }

    private static ReviewToolOutcome FitChatHistoryOutcome(BridgeConfig config,Dictionary<string,object> payload,
        List<object> messages,PendingNativeCall call,DialogueRequest request,MemoryStore owner,
        ReviewToolOutcome outcome,NativeReadReceipts receipts)
    {
        string Preview(ReviewToolOutcome result)=>result.ReadDataVersion.Length==0?result.Json:
            receipts.Project(ReviewContext.ToolName,JsonSerializer.Serialize(call.Review),result.ReadDataVersion,call.Id,result.Json,remember:false);
        if(ChatHistoryFits(config,payload,messages,call.Id,Preview(outcome),request))return outcome;
        if(call.Review?.Mode=="history"&&outcome.ChatHistory is { } original)
        {
            // Keep useful nearby context first. Limits are unchanged; only the
            // selected page/ranges shrink, with executable continuation locators.
            var variants=new[]{12,6,3,2,1}.SelectMany(count=>new[]{original.Options?.BodyChars??700,700,350,120}.Distinct().Select(chars=>(Count:count,Chars:chars)))
                .Concat(new[]{12,6,3,2,1}.Select(count=>(Count:count,Chars:0)));
            foreach(var variant in variants)
            {
                var packet=MemoryStore.ProjectChatHistoryPacket(original,variant.Count,variant.Chars);
                if(packet.Items.Count==0)continue;
                var snapshot=new ReviewSourceSnapshot(config,request,owner,call.Review.Question,
                    Array.Empty<MemoryTurn>(),outcome.DerivedMethodSources??Array.Empty<MethodSource>(),call.MethodSnapshot??request.MethodHttpOffer,packet);
                if(!snapshot.IsPermitted())return ReviewProcess.Empty("unavailable","source_changed");
                var projected=outcome with {Json=ChatHistoryJson(packet,request),ChatHistory=packet,
                    SourceSnapshot=snapshot,ReadDataVersion=packet.DataVersion,
                    SourceRecords=null,SourceViews=null,SourceVersions=null,MethodSourcesTracked=false};
                if(!ChatHistoryFits(config,payload,messages,call.Id,Preview(projected),request))continue;
                request.ChatHistoryLastPacket=ChatHistoryContinuationOf(packet);return projected;
            }
        }
        // No body was registered or cached. Preserve the locating request in
        // the request owner; the compact result does not assert source absence.
        return ReviewProcess.Empty("unavailable","request_budget_omitted") with {
            Json=ReviewContext.Serialize(new {status="unavailable",reason="request_budget_omitted",mode="history",
                scope_complete=false,family_exhausted=false,records=Array.Empty<object>(),
                note="The requested page did not fit this request. No material was supplied; this does not mean it is absent. The locating request is retained locally.",
                used_calls=request.ReviewCalls,remaining_calls=Math.Max(0,ReviewContext.MaxCallsPerTurn-request.ReviewCalls),can_rephrase=false})};
    }

    // Optional reconsider must leave space for an ordinary main continuation.
    // Use the existing payload builders and final protocol adapters; no model
    // is called to estimate, and neither byte limit is increased.
    private static bool CanBudgetReconsider(BridgeConfig config,DialogueRequest request,
        Dictionary<string,object> payload,List<object> messages)
    {
        if(!request.ChatHistoryBudgetActive)return true;
        try
        {
            var prospective=new Dictionary<string,object>(payload){["messages"]=messages.ToArray()};
            long main=Encoding.UTF8.GetByteCount(AdaptHistoryBudgetPayload(config,SerializeTimed(prospective)));
            var worst=Enumerable.Range(0,Math.Clamp(config.ReviewMaxRecords,1,ReviewContext.MaxRecordsLimit))
                .Select(i=>new ReviewMaterialView("a:budget"+i,"lilith",DateTimeOffset.UnixEpoch,"delivered",0,700,new string('界',700),false)).ToArray();
            var args=new ReviewToolArgs(new string('界',ReviewContext.MaxQuestionChars),"reconsider","",Array.Empty<string>(),false);
            long child=Encoding.UTF8.GetByteCount(AdaptHistoryBudgetPayload(config,BuildReviewPayload(config,request,args,worst),"review"));
            long continuation=main+48000; // conservative result plus note/protocol allowance
            return child<=DialogueRequest.ChatHistoryMaxRequestBytes&&continuation<=DialogueRequest.ChatHistoryMaxRequestBytes
                &&request.ChatHistoryReservedBytes+main+child+continuation<=DialogueRequest.ChatHistoryMaxTotalBytes;
        }
        catch{return false;}
    }
}
