using System.Text;
using System.Text.Json;

namespace LilithBridge;

internal sealed partial class DeepSeekClient
{
 internal Action<string>? LegacyArchiveBoundaryForTests {get;set;}
 private ReviewToolOutcome ExecuteLegacyArchive(BridgeConfig config,DialogueRequest request,ReviewToolArgs args,
  MemoryStore owner,CancellationToken token,MethodSource[] derived,MethodOffer? offer)
 {
  token.ThrowIfCancellationRequested();
  if(!request.ReviewToolEnabled||!ReferenceEquals(request.ReviewStore,owner)||request.ReviewSessionIdentity!=owner.ReviewSessionIdentity
   ||request.ReviewDataRoot!=DataPaths.Root()||request.ReviewConfigRoot!=config.DataDirectory)return ReviewProcess.Empty("unavailable","legacy_owner_or_permission_changed");
  request.ChatHistoryBudgetActive=true;
  var query=args.Archive??new LegacyArchiveQuery(args.Query);
  var packet=owner.ReadLegacyArchive(query,request.ReviewCurrentText,config);
  LegacyArchiveBoundaryForTests?.Invoke("after_read");
  return LegacyOutcome(config,request,args,owner,packet,derived,offer);
 }
 private static ReviewToolOutcome LegacyOutcome(BridgeConfig config,DialogueRequest request,ReviewToolArgs args,MemoryStore owner,
  LegacyArchivePacket packet,MethodSource[] derived,MethodOffer? offer)
 {
  var snapshot=new ReviewSourceSnapshot(config,request,owner,args.Question,Array.Empty<MemoryTurn>(),derived,offer,legacyArchive:packet);
  return new(LegacyArchiveWire(packet,request),0,0,0,0,0,0,false,SourceSnapshot:snapshot,ReadDataVersion:packet.DataVersion,MethodSourcesTracked:false,LegacyArchive:packet,LegacyOffer:offer);
 }
 private static object LegacyArchiveRecordWire(LegacyArchiveView r)=>new{archive_id=r.Id,version=r.Version,container=r.Family,source_ordinal=r.Ordinal,role=r.Role,time=r.Timestamp,
   timestamp_basis=r.TimestampBasis,evidence=r.Evidence,delivery="legacy_unknown",actual_native_delivery="unknown",
   content_identity="recorded_expression_not_asserted_world_fact",first_encounter="not_established",text_sha256=r.TextHash,
   record_kind=r.RecordKind,context_anchor=r.ContextAnchor.Length>0?r.ContextAnchor:null,context_relation=r.ContextRelation.Length>0?r.ContextRelation:null,
   offset_utf16=r.Offset,total_utf16=r.TotalChars,text=r.Text,next_offset_utf16=r.NextOffset,
   source=new{path=r.SourcePath,format=r.SourceFormat,original_container_sha256=r.SourceOriginalHash,observed_container_sha256=r.SourceObservedHash,
    container_changed_record_reverified=r.SourceContainerChanged,proof_kinds=r.ProofKinds}};
 private static string LegacyArchiveWire(LegacyArchivePacket p,DialogueRequest request)=>ReviewContext.Serialize(new {
  mode="archive",status=p.Status,identity="saved_legacy_record",complete=p.Status=="ok",matched_records=p.Matched,eligible_records=p.Eligible,
  records=p.Records.Select(LegacyArchiveRecordWire),
  related_context=(p.RelatedContext??Array.Empty<LegacyArchiveView>()).Select(LegacyArchiveRecordWire),
  context_complete=!p.ContextTruncated,context_omissions=(p.ContextOmissions??Array.Empty<LegacyArchiveGap>()).Select(o=>new{anchor=o.Id,reason=o.Reason}),
  context_scope="同一来源数组中已选交流的有界接续；不是新的检索命中、确认事件或完整显示。原记录优先，缺少后续回答不补造，不扩大原查询和读取权限。",
  
  omissions=p.Omissions.Select(o=>new{archive_id=o.Id,reason=o.Reason}),omissions_not_listed=p.OmissionsNotListed,next_cursor=p.NextCursor,
  scope="旧档记录可参与判断；交付不明不等于不可读，也不等于已完整显示。保留提交/回复接收/seed证据；说过、原作、事后叙述和实际事件须分别理解。缺项不抹除邻项，不扩大引用/修订权限。",
  continuation="仅确有需要时用archive_cursor翻页，或archive_id与archive_offset读长项后续；已有材料足够可直接回答。缺失/撤回项勿反复重试。",
  used_calls=request.ReviewCalls,remaining_calls=Math.Max(0,ReviewContext.MaxCallsPerTurn-request.ReviewCalls),
  can_continue=request.CanStartReviewRead&&(p.NextCursor.Length>0||p.Records.Concat(p.RelatedContext??Array.Empty<LegacyArchiveView>()).Any(r=>r.NextOffset!=null))});
 private static ReviewToolOutcome FitLegacyArchive(BridgeConfig config,DialogueRequest request,ReviewToolArgs args,MemoryStore owner,
  Dictionary<string,object> payload,List<object> messages,string callId,ReviewToolOutcome original,NativeReadReceipts receipts)
 {
  if(original.LegacyArchive==null)return original;
  var packet=original.LegacyArchive;
  string Preview(ReviewToolOutcome result)=>result.ReadDataVersion.Length==0?result.Json:
   receipts.Project(ReviewContext.ToolName,JsonSerializer.Serialize(args),result.ReadDataVersion,callId,result.Json,remember:false);
  if(original.SourceSnapshot?.IsPermitted()==true&&ChatHistoryFits(config,payload,messages,callId,Preview(original),request))return original;
  foreach(int budget in new[]{packet.MaterialBytes,24000,12000,6000,2400,700}.Distinct().OrderByDescending(x=>x))
  {
   var next=owner.ReadLegacyArchive(packet.Query,request.ReviewCurrentText,config,budget);
   var outcome=LegacyOutcome(config,request,args,owner,next,original.DerivedMethodSources??Array.Empty<MethodSource>(),original.LegacyOffer);
   if(ChatHistoryFits(config,payload,messages,callId,Preview(outcome),request))return outcome;
  }
  // Even ID-only gaps cannot fit an already-full surrounding request. The final
  // adapted hard gate remains authoritative; never hide this as a missing source.
  return ReviewProcess.Empty("unavailable","surrounding_request_budget") with {Json=ReviewContext.Serialize(new{
   mode="archive",status="unavailable",reason="surrounding_request_budget",records=Array.Empty<object>(),
   omitted_records=packet.Matched,remaining_calls=Math.Max(0,ReviewContext.MaxCallsPerTurn-request.ReviewCalls),can_continue=false})};
 }
}
