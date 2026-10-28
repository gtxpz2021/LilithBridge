using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
namespace LilithBridge;

internal enum ReviewSupplyAssessment { Allowed, Withheld, Unknown, Invalid }

// One bounded, session-local review process. It never owns speech, commits or a
// scheduler. Only an already dispatched read/note may outlive its main owner.
internal sealed class ReviewProcess
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    internal string Id { get; } = Guid.NewGuid().ToString("N");
    internal string OriginEvent { get; }
    internal string Session { get; }
    internal string Root { get; } = DataPaths.Root();
    internal string ConfigRoot {get;}
    private readonly string _originQuery;
    private readonly BridgeConfig _originConfig;
    private IReadOnlyList<MemoryTurn> _inFlightSources=Array.Empty<MemoryTurn>();
    private MethodSource[] _inFlightDerivedSources=Array.Empty<MethodSource>();
    internal void CaptureDerivedSources(MethodSource[] sources){lock(_gate)_inFlightDerivedSources=sources.ToArray();}
    internal void CaptureSources(IReadOnlyList<MemoryTurn> records) {lock(_gate)_inFlightSources=records.Select(t=>t.Copy()).ToArray();}
    internal void RevalidateInFlight(MemoryStore store)
    {lock(_gate)if(Running&&(!store.ReviewTransferPermitted(_inFlightSources,"",_originQuery,_originConfig)
        ||!store.MethodSourcesPermitted(_inFlightDerivedSources,_originQuery,_originConfig)))Stop("source_changed");}
    internal DateTimeOffset Deadline { get; }
    internal string Question { get; private set; } = "";
    internal string State { get; private set; } = "ready";
    internal int Calls { get; private set; }
    internal int Reconsiders { get; private set; }
    internal int Http { get; private set; }
    internal int NoteMaxTokens {get;}
    internal bool NoticeOffered { get; set; }
    private int _step;

    private ReviewToolOutcome? _result;
    private readonly List<ReviewToolOutcome> _steps = new();
    internal CancellationToken Token => _stop.Token;
    internal bool Closed { get { lock(_gate) return State is "stopped" or "expired" or "replaced" or "source_changed" or "permission_revoked" or "scope_changed"; } }
    internal bool Running { get { lock(_gate) return State == "running"; } }
    internal ReviewProcess(DialogueRequest request, MemoryStore store, BridgeConfig config)
    {
        OriginEvent=request.UserEventId; Session=store.ReviewSessionIdentity;ConfigRoot=config.DataDirectory;_originQuery=request.ReviewCurrentText;_originConfig=config;
        Calls=request.ReviewCalls; Reconsiders=request.ReconsiderCalls; Http=request.HttpAttempts;NoteMaxTokens=request.ReviewNoteMaxTokens;
        Deadline=DateTimeOffset.UtcNow.AddSeconds(Math.Max(10,config.RequestTimeoutSeconds));
        _stop.CancelAfter(Deadline-DateTimeOffset.UtcNow);
    }
    internal bool Join(DialogueRequest request)
    {
        lock(_gate)
        {
            if(Closed) return false;
            if(ReferenceEquals(request.ReviewBudget,this))return true;
            if(Http+request.HttpAttempts>=DialogueRequest.MaxHttpAttempts)return false;
            Http+=request.HttpAttempts;
            request.ReviewBudget=this; return true;
        }
    }
    internal int RemainingHttp {get {lock(_gate)return Math.Max(0,DialogueRequest.MaxHttpAttempts-Http);}}
    internal bool AllowsNewSteps {get {lock(_gate)return !Closed&&!Token.IsCancellationRequested&&DateTimeOffset.UtcNow<Deadline;}}
    internal bool BeginHttp(bool reviewExecution=false)
    {lock(_gate){if(Http>=DialogueRequest.MaxHttpAttempts || Closed&&(reviewExecution||State!="expired"))return false;Http++;return true;}}
    internal bool Accept(bool reconsider,out string reason)
    {
        lock(_gate)
        {
            reason=Closed?"operation_closed":Running?"operation_running":DateTimeOffset.UtcNow>=Deadline?"operation_expired"
                :Calls>=ReviewContext.MaxCallsPerTurn?"limit_calls":reconsider&&Reconsiders>=ReviewContext.MaxReconsiderPerTurn?"limit_reconsider"
                :Http+(reconsider?2:1)>DialogueRequest.MaxHttpAttempts?"http_budget":"";
            if(reason.Length>0)return false;
            Calls++;if(reconsider)Reconsiders++;return true;
        }
    }
    internal Task<ReviewToolOutcome> Start(ReviewToolArgs args,Func<CancellationToken,Task<ReviewToolOutcome>> execute)
    {
        lock(_gate)
        {
            if(Closed||Running||Token.IsCancellationRequested)return Task.FromResult(Status());
            State="running";Question=args.Question;int step=++_step;
            // execute starts synchronously while the request's ownership lock is
            // held. Cancellation cannot authorize a previously unissued step.
            Task<ReviewToolOutcome> dispatched=execute(Token);
            return Complete(dispatched,step);
        }
    }
    private async Task<ReviewToolOutcome> Complete(Task<ReviewToolOutcome> dispatched,int step)
    {
        ReviewToolOutcome outcome;
        try { outcome=await dispatched.ConfigureAwait(false); }
        catch(OperationCanceledException) { outcome=Empty("cancelled",DateTimeOffset.UtcNow>=Deadline?"deadline":"cancelled"); }
        catch { outcome=Empty("unavailable","review_failed"); }
        lock(_gate)
        {
            if(Closed||step!=_step)return Status();
            if(Token.IsCancellationRequested) { Stop("expired");return Status(); }
            bool Register()
            {
                _result=StripThought(outcome);_steps.Add(_result);
                using var doc=JsonDocument.Parse(outcome.Json);
                string status=doc.RootElement.TryGetProperty("status",out var v)?v.GetString()??"unknown":"unknown";
                State=status is "ok" or "truncated" or "not_found" ? "completed" : status;
                return true;
            }
            if(outcome.SourceSnapshot is { } snapshot)
            {
                if(!snapshot.TryUse(Register))
                {
                    // The dispatched step is rejected, not a Take of a retained
                    // result. Preserve its accepted question/dependency snapshot.
                    State="source_changed";_result=null;_stop.Cancel();return Status();
                }
            }
            else Register();
            return outcome;
        }
    }
    internal ReviewToolOutcome Take(MemoryStore store,DialogueRequest? request=null)
    {
        lock(_gate)
        {
            if(Closed||_result==null)return Status();
            if(StopIfExpired())return Status();
            var assessment=InspectSupply(store,request);
            if(assessment==ReviewSupplyAssessment.Invalid){Stop("source_changed");return Status();}
            if(assessment!=ReviewSupplyAssessment.Allowed)return WithheldReceipt(assessment);
            var taken = _result with { IsControlReceipt=false, Json=ReviewContext.Serialize(new { operation_id=Id,status=State,question=Question,
                source_versions=_steps.SelectMany(s=>s.SourceVersions??Array.Empty<DaySourceDependency>()).Distinct().ToArray(),
                steps=_steps.Select(s=>JsonSerializer.Deserialize<JsonElement>(s.Json)).ToArray(),
                used_calls=Calls,remaining_calls=Math.Max(0,ReviewContext.MaxCallsPerTurn-Calls),
                remaining_reconsider=Math.Max(0,ReviewContext.MaxReconsiderPerTurn-Reconsiders),http_used=Http,deadline=Deadline }),
                SourceRecords=_steps.SelectMany(s=>s.SourceRecords??Array.Empty<MemoryTurn>()).GroupBy(t=>t.EventId).Select(g=>g.First()).ToArray(),
                SourceViews=_steps.SelectMany(s=>s.SourceViews??Array.Empty<ReviewMaterialView>()).Distinct().ToArray(),
                SourceVersions=_steps.SelectMany(s=>s.SourceVersions??Array.Empty<DaySourceDependency>()).Distinct().ToArray(),
                DerivedMethodSources=_steps.SelectMany(s=>s.DerivedMethodSources??Array.Empty<MethodSource>()).Distinct().ToArray(),
                MethodSourcesTracked=_steps.All(s=>s.MethodSourcesTracked) };
            return taken with { SourceSnapshot=request==null?null
                :ReviewSourceSnapshot.ForTransfer(_steps.Select(s=>s.SourceSnapshot),request,taken.Json) };
        }
    }
    // Caller holds _gate. This is inspection only: no payload registration,
    // lifecycle change or material transfer. Invalid outranks any unknown step.
    private ReviewSupplyAssessment InspectSupply(MemoryStore store,DialogueRequest? request)
    {
        var assessment=ReviewSupplyAssessment.Allowed;
        foreach(var step in _steps)
        {
            bool permitted,permissionUnknown=false;
            try { permitted=store.ReviewOutcomePermitted(step,request); }
            catch { permitted=false;permissionUnknown=true; }
            if(permitted)continue;
            SourceContinuity continuity;
            try { continuity=store.CheckReviewOutcomeContinuity(step); }
            catch { continuity=SourceContinuity.Unknown; }
            if(continuity==SourceContinuity.KnownInvalid)return ReviewSupplyAssessment.Invalid;
            if(continuity==SourceContinuity.Unknown||permissionUnknown)assessment=ReviewSupplyAssessment.Unknown;
            else if(assessment==ReviewSupplyAssessment.Allowed)assessment=ReviewSupplyAssessment.Withheld;
        }
        return assessment;
    }
    private bool StopIfExpired()
    {
        if(DateTimeOffset.UtcNow<Deadline&&!Token.IsCancellationRequested)return false;
        Stop("expired");return true;
    }
    private ReviewToolOutcome WithheldReceipt(ReviewSupplyAssessment assessment)
        => Empty("unavailable",assessment==ReviewSupplyAssessment.Unknown?"source_check_unavailable":"not_available_this_turn")
            with { Json=ReviewContext.Serialize(new {operation_id=Id,status="unavailable",
                reason=assessment==ReviewSupplyAssessment.Unknown?"source_check_unavailable":"not_available_this_turn",
                used_calls=Calls,remaining_calls=Math.Max(0,ReviewContext.MaxCallsPerTurn-Calls),
                remaining_reconsider=Math.Max(0,ReviewContext.MaxReconsiderPerTurn-Reconsiders),http_used=Http,deadline=Deadline,
                records=Array.Empty<object>()}) };
    internal ReviewToolOutcome OfferResult(MemoryStore store,DialogueRequest request)
    {
        lock(_gate)
        {
            if(Closed||StopIfExpired()||_result==null)return Status();
            var assessment=InspectSupply(store,request);
            if(assessment==ReviewSupplyAssessment.Invalid){Stop("source_changed");return Status();}
            // Only an allowed inspection can attempt Take, which revalidates.
            return assessment==ReviewSupplyAssessment.Allowed?Take(store,request):WithheldReceipt(assessment);
        }
    }
    internal bool HasPermittedCompletedResult(MemoryStore store, DialogueRequest request)
    {
        lock (_gate)
        {
            if(Closed||State!="completed"||_result==null)return false;
            // Declaration probing asks only whether this turn may see it.
            // A later explicit query can permit a temporarily hidden result.
            return !Token.IsCancellationRequested&&DateTimeOffset.UtcNow<Deadline
                &&InspectSupply(store,request)==ReviewSupplyAssessment.Allowed;
        }
    }
    internal ReviewToolOutcome Status() => Empty(State,Id) with { Json=ReviewContext.Serialize(new {
        operation_id=Id,status=State,used_calls=Calls,
        remaining_calls=Math.Max(0,ReviewContext.MaxCallsPerTurn-Calls),remaining_reconsider=Math.Max(0,ReviewContext.MaxReconsiderPerTurn-Reconsiders),
        http_used=Http,deadline=Deadline,records=Array.Empty<object>() }) };
    internal void Stop(string reason="stopped") { lock(_gate) { if(Closed)return;State=reason;Question="";_result=null;_steps.Clear();_inFlightSources=Array.Empty<MemoryTurn>();_inFlightDerivedSources=Array.Empty<MethodSource>();_stop.Cancel(); } }
    internal static ReviewToolOutcome Empty(string status,string reason)=>new(ReviewContext.Serialize(new {status,reason,records=Array.Empty<object>()}),0,0,0,0,0,0,false,IsControlReceipt:true);
    private static ReviewToolOutcome StripThought(ReviewToolOutcome outcome)
    {
        var node=JsonNode.Parse(outcome.Json)!.AsObject();
        foreach(string key in new[]{"prior_thought","prior_thought_note","prior_thought_basis"})node.Remove(key);
        node["prior_thought_status"]="not_transferred";
        return outcome with {Json=node.ToJsonString(ReviewContext.Json),SuppliesPriorThought=false};
    }
}

internal sealed class ReviewHandoff
{
    private readonly object _gate=new();
    internal ReviewProcess? Current {get;private set;}
    internal Func<BridgeConfig>? Configuration;
    internal BridgeConfig Config(DialogueRequest request)=>Configuration?.Invoke()??request.B1ReadConfig;
    internal ReviewProcess? Find(DialogueRequest request,MemoryStore store,string id="")
    {
        lock(_gate)
        {
            var p=Current;if(p==null)return null;
            if(p.Root!=DataPaths.Root()||p.ConfigRoot!=Config(request).DataDirectory||p.Session!=store.ReviewSessionIdentity)
            {p.Stop("scope_changed");Current=null;return null;}
            if(!store.GetRequestHistory(Config(request)).Any(t=>t.EventId==p.OriginEvent))
            {p.Stop("expired");Current=null;return null;}
            if(id.Length>0&&id!=p.Id)return null;
            return p;
        }
    }
    internal ReviewProcess? Cancellable(DialogueRequest request,MemoryStore store)
    {
        if(ReviewContextModes.Normalize(Config(request).ReviewContextMode)==ReviewContextModes.Off)return null;
        var p=Find(request,store);return p is {Closed:false}&&!p.Token.IsCancellationRequested?p:null;
    }
    internal void InvalidateScope(MemoryStore store,BridgeConfig config)
    {
        lock(_gate)if(Current is { } p && (p.Root!=DataPaths.Root()||p.ConfigRoot!=config.DataDirectory||p.Session!=store.ReviewSessionIdentity))
        {p.Stop("scope_changed");Current=null;}
    }
    internal ReviewProcess Start(DialogueRequest request,MemoryStore store)
    {
        lock(_gate)
        {
            if(request.ReviewBudget is { } existing&&ReferenceEquals(Current,existing))return existing;
            Current?.Stop("replaced");Current=new ReviewProcess(request,store,Config(request));
            request.ReviewBudget=Current;return Current;
        }
    }
    internal void Stop(string reason="stopped") {lock(_gate)Current?.Stop(reason);}
    internal void Trim(MemoryStore store,BridgeConfig config)
    {lock(_gate)if(Current is { } p && !store.GetRequestHistory(config).Any(t=>t.EventId==p.OriginEvent)){p.Stop("expired");Current=null;}}
    internal bool HasRunning {get {lock(_gate)return Current?.Running==true;}}
    internal string Offer(DialogueRequest request,MemoryStore store)
    {
        var p=Find(request,store);
        if(p==null||p.OriginEvent==request.UserEventId||p.NoticeOffered||p.Closed)return "";
        p.NoticeOffered=true;
        if(!request.ReviewToolEnabled)return "";
        var taken=p.OfferResult(store,request);
        if(!p.Running && !taken.IsControlReceipt && taken.SourceViews!=null)
        {
            string supplied="";
            if(request.RegisterReviewOutcome(taken,()=>
            {
                request.ReviewHandoffSupplied=true;request.ReviewSuppliedText.Add(taken.Json);
                supplied="\n[已取得的回看材料·当前供给] "+taken.Json;
            }))return supplied;
        }
        string status=p.Status().Json;
        request.ReviewControlReceipts.Add(status);
        return "\n[近期回看操作] "+status+"。可直接回应当前话题；需要时以 operation_id 调用 review_context 的 take/status/stop，或在原余额内 read/reconsider；不自动续步。";
    }
}

internal sealed partial class MemoryStore
{
    internal string ReviewSessionIdentity=>_sessionId;
    internal bool ReviewOutcomePermitted(ReviewToolOutcome outcome,DialogueRequest? request=null)
    {
        return ReviewTransferPermitted(outcome.SourceRecords??Array.Empty<MemoryTurn>(),outcome.Json,request?.ReviewCurrentText??"",request?.B1ReadConfig)
            &&MethodSourcesPermitted(outcome.DerivedMethodSources??Array.Empty<MethodSource>(),request?.ReviewCurrentText??"",request?.B1ReadConfig);
    }
    internal bool ReviewTransferPermitted(IReadOnlyList<MemoryTurn> records,string text,string query,BridgeConfig? config)
    {
        var previousMention=_mentionReadPolicy.Value;var previousNative=_nativeDialoguePolicy.Value;
        _mentionReadPolicy.Value=null;_nativeDialoguePolicy.Value=null;
        try
        {
            using var mention=BeginMentionRequest(query,true);
            using var native=BeginNativeDialogueRequest(query,true,config);
            return ReviewRecordsStillPermitted(records)
                &&IsMentionTextAllowed(text)&&!SuppressedTextMatches(text);
        }
        finally {_mentionReadPolicy.Value=previousMention;_nativeDialoguePolicy.Value=previousNative;}
    }
    internal bool ReviewOutboundPermitted(DialogueRequest request)
    {
        var previousMention=_mentionReadPolicy.Value;var previousNative=_nativeDialoguePolicy.Value;
        _mentionReadPolicy.Value=null;_nativeDialoguePolicy.Value=null;
        try
        {
            using var mention=BeginMentionRequest(request.ReviewCurrentText,true);
            using var native=BeginNativeDialogueRequest(request.ReviewCurrentText,true,request.B1ReadConfig);
            return B1ProtocolInputsPermitted(request,request.ReviewCurrentText)
                &&MethodSourcesPermitted(request.ReviewDerivedMethodSources,request)
                &&request.ReviewSuppliedText.All(t=>IsMentionTextAllowed(t)&&!SuppressedTextMatches(t));
        }
        finally {_mentionReadPolicy.Value=previousMention;_nativeDialoguePolicy.Value=previousNative;}
    }
}
