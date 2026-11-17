using System.Text.Json;

namespace LilithBridge;

internal sealed partial class DeepSeekClient
{
    private ReviewToolOutcome ExecuteDayArtifacts(BridgeConfig config,DialogueRequest request,ReviewToolArgs args,
        MemoryStore owner,CancellationToken token,MethodSource[] derived,MethodOffer? offer)
    {
        token.ThrowIfCancellationRequested();
        if(!request.ReviewToolEnabled||!ReferenceEquals(request.ReviewStore,owner)
            ||request.ReviewSessionIdentity!=owner.ReviewSessionIdentity||request.ReviewDataRoot!=DataPaths.Root()
            ||request.ReviewConfigRoot!=config.DataDirectory)return ReviewProcess.Empty("unavailable","artifact_owner_mismatch");
        request.ChatHistoryBudgetActive=true;
        var packet=owner.ReadDayArtifacts(args.RootVersion,args.Query,args.HistoryOptions??new(),request.ReviewCurrentText,config);
        if(packet.Items.Count==0)return new(DayArtifactJson(packet,request),0,0,0,0,0,0,false,ReadDataVersion:packet.DataVersion);
        var snapshot=new ReviewSourceSnapshot(config,request,owner,args.Question,Array.Empty<MemoryTurn>(),derived,offer,dayArtifacts:packet);
        if(!snapshot.IsPermitted())return ReviewProcess.Empty("unavailable","source_changed");
        return new(DayArtifactJson(packet,request),0,0,0,0,0,0,false,SourceSnapshot:snapshot,ReadDataVersion:packet.DataVersion,DayArtifacts:packet);
    }
    private static string DayArtifactJson(DayArtifactReadPacket p,DialogueRequest request)
    {
        string? rootAlias = p.RootVersion.Length > 0
            ? request.RegisterReviewHandle("artifact", "day:" + p.RootVersion, rootVersion: p.RootVersion)
            : null;
        return ReviewContext.Serialize(new{
            status=p.Status,reason=p.Reason,mode="artifact",history_scope=p.Options.Scope,
            use="reading_material_only; generated/expressed history is distinct from truth of described events; may reinterpret, ignore, or leave no note",
            discovery="qualified artifacts only; optional literal query, otherwise newest first; this page is not all history or a statement of current interests",
            root_version=rootAlias,scope_complete=p.ScopeComplete,
            next_cursor=p.NextCursor.Length>0?p.NextCursor:null,
            next_locator=p.NextCursor.Length>0?(object)new{mode="artifact",root_version=rootAlias,
                history_scope=p.RootVersion.Length>0?"sources":"focus",history_cursor=p.NextCursor,query=p.Query}:null,
            records=p.Items.Select(i=>{
                string itemAlias=request.RegisterReviewHandle("artifact",i.SourceKind+":"+i.Turn.EventId,rootVersion:i.OwnerId);
                string ownerAlias=i.OwnerId.Length>0?request.RegisterReviewHandle("artifact","day:"+i.OwnerId,rootVersion:i.OwnerId):"";
                string dayRootAlias=i.SourceKind=="day"?request.RegisterReviewHandle("artifact","day:"+i.Turn.EventId,rootVersion:i.Turn.EventId):"";
                return new{
                    history_ref=itemAlias,artifact_owner=ownerAlias,kind=i.Turn.Kind,
                    material_type=i.SourceKind,role=i.Turn.Role,origin=i.Turn.Origin,time=i.Turn.Timestamp.ToString("O"),
                    delivery=i.Turn.DeliveryState,validity=i.Turn.Validity,lifecycle=i.Turn.LifecycleState,revision=i.Turn.Revision,
                    label=i.Turn.Kind==MemoryStore.DayDreamKind?"你当时生成的梦／想象；描写不证明外部事件发生":
                        i.Turn.Kind==MemoryStore.DayConsolidationKind?"你当时的归拢；是整理而非新事实":
                        i.SourceKind=="utterance"?"当时实际收到/说过的原话；命题不因此属实":"当时留下的解释；不代表当前赞同",
                    text=i.Text,offset=i.Offset,total_chars=i.Turn.Text.Length,
                    unread_prefix=i.Offset>0,next_offset=i.NextOffset,next_cursor=i.NextCursor.Length>0?i.NextCursor:null,
                    next_locator=i.NextOffset.HasValue?new{mode="artifact",root_version=ownerAlias,history_scope="record",
                        history_ref=itemAlias,offset=i.NextOffset,history_cursor=i.NextCursor}:null,
                    declared_sources=i.SourceKind=="day"?i.Turn.SourceEventIds.Select(x=>request.RegisterReviewHandle("artifact","utterance:"+x,rootVersion:i.Turn.EventId))
                        .Concat(i.Turn.SourceNoteIds.Select(x=>request.RegisterReviewHandle("artifact","note:"+x,rootVersion:i.Turn.EventId)))
                        .Concat(i.Turn.SourceDayIds.Select(x=>request.RegisterReviewHandle("artifact","day:"+x,rootVersion:i.Turn.EventId))).ToArray():null,
                    read_sources_with=i.SourceKind=="day"?new{mode="artifact",root_version=dayRootAlias,history_scope="sources"}:null
                };
            }),omissions=p.Omissions,
            used_calls=request.ReviewCalls,remaining_calls=Math.Max(0,ReviewContext.MaxCallsPerTurn-request.ReviewCalls),
            write_references_added=false,can_continue=request.CanStartReviewRead
        });
    }
    private static ReviewToolOutcome FitDayArtifacts(BridgeConfig config,Dictionary<string,object> payload,
        List<object> messages,PendingNativeCall call,DialogueRequest request,MemoryStore owner,ReviewToolOutcome outcome,NativeReadReceipts receipts)
    {
        string Preview(ReviewToolOutcome result)=>receipts.Project(ReviewContext.ToolName,JsonSerializer.Serialize(call.Review),result.ReadDataVersion,call.Id,result.Json,remember:false);
        bool Fits(ReviewToolOutcome value)=>System.Text.Encoding.UTF8.GetByteCount(value.Json)<=MemoryStore.ChatHistoryMaterialBytes
            &&ChatHistoryFits(config,payload,messages,call.Id,Preview(value),request);
        if(Fits(outcome))return outcome;
        if(outcome.DayArtifacts is { } original)
            foreach(int count in new[]{12,6,3,1})foreach(int chars in new[]{700,350,120})
            {
                var p=MemoryStore.ProjectDayArtifacts(original,count,chars);
                var snapshot=new ReviewSourceSnapshot(config,request,owner,call.Review!.Question,Array.Empty<MemoryTurn>(),
                    outcome.DerivedMethodSources??Array.Empty<MethodSource>(),call.MethodSnapshot??request.MethodHttpOffer,dayArtifacts:p);
                if(!snapshot.IsPermitted())return ReviewProcess.Empty("unavailable","source_changed");
                var smaller=outcome with{Json=DayArtifactJson(p,request),DayArtifacts=p,SourceSnapshot=snapshot,ReadDataVersion=p.DataVersion};
                if(Fits(smaller))return smaller;
            }
        return ReviewProcess.Empty("unavailable","request_budget_omitted") with{Json=ReviewContext.Serialize(new{
            mode="artifact",status="unavailable",reason="request_budget_omitted",scope_complete=false,
            records=Array.Empty<object>(),note="No artifact body fitted this request; this is not source absence."})};
    }
}
