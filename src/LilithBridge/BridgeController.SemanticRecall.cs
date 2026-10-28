using System.Diagnostics;
using System.Threading;

namespace LilithBridge;

internal sealed class SemanticMaterialException : Exception
{
    internal string Outcome { get; }
    internal SemanticMaterialException(string outcome):base(outcome)=>Outcome=outcome;
}

internal sealed partial class BridgeController
{
    private SemanticRecallWorker? _semanticWorker;
    private string _semanticWorkerKey="",_semanticLastStatus="";
    private bool _semanticDestroyed;
    private bool _semanticCreationAttempted;
    internal IContextEnvironment? SemanticEnvironmentForTests { get; set; }
    internal Action<DialogueRequest>? SemanticBoundaryHookForTests { get; set; }
    internal bool SemanticWorkerTestMode { get; set; }
    internal Func<string,string,SemanticRecallResult>? SemanticResultForIsolatedTests {get;set;}
    internal SemanticRecallWorker? SemanticWorkerForTests=>_semanticWorker;
    internal double LastSemanticOwnerMs { get; private set; }
    internal double LastRequestComposeMs { get; private set; }
    internal double LastSemanticSnapshotMs { get; private set; }

    private void TickSemanticRuntime()
    {
        if(_semanticDestroyed)return;
        string key=string.Join("|",_memory.SemanticDataRoot,_config.SemanticPythonPath,_config.SemanticWorkerScript,_config.SemanticModelPath,_config.SemanticIndexDirectory);
        if(!_config.SemanticRecallEnabled||key!=_semanticWorkerKey)
        {
            if(_semanticWorker!=null){CancelReflectionPreparation("semantic_configuration_changed");if(_activeDialogue?.SemanticMaterial!=null)CancelPendingDialogue();_semanticWorker.Dispose();_semanticWorker=null;_memory.SemanticDiscovery=null;}
            _semanticWorkerKey=key;
            _semanticCreationAttempted=false;
        }
        if(_config.SemanticRecallEnabled&&_semanticWorker==null&&!_semanticCreationAttempted)
        {
            _semanticCreationAttempted=true;
            try
            {
                IReadOnlyList<SemanticIndexRecord> snapshot;
                try{snapshot=_memory.CaptureSemanticIndexSnapshot();}
                catch(SemanticSnapshotUnavailableException)
                {
                    // Model prewarming is independent of source availability.
                    // Every real query captures again and must report failure;
                    // this empty warmup is never its completed source snapshot.
                    snapshot=Array.Empty<SemanticIndexRecord>();
                }
                _semanticWorker=new(_config,_memory.SemanticDataRoot,SemanticWorkerTestMode);
                _semanticWorker.Start(snapshot);_memory.SemanticDiscovery=_semanticWorker.DiscoverAsync;
            }
            catch(Exception error){_semanticLastStatus="unavailable:"+error.GetType().Name;}
        }
        string status=_semanticWorker?.Status??(_config.SemanticRecallEnabled?_semanticLastStatus:"disabled");
        if(status!=_semanticLastStatus){_semanticLastStatus=status;NativeEventLog.Trace("semantic.state","mod","system","local semantic worker state",status);}
    }
    private void StopSemanticRuntime()
    {CancelReflectionPreparation("shutdown");_semanticDestroyed=true;_memory.SemanticDiscovery=null;_memory.ReadMethods.Sync("",false);_semanticWorker?.Dispose();}

    // Called before any request scope is opened. No using/Unity/MemoryStore
    // mutable object is carried into the worker task.
    private void QueueSemanticDialogue(DialogueRequest owner,long generation,string query,Action continuation)
    {
        if(SemanticWorkerTestMode&&SemanticResultForIsolatedTests!=null)
        {
            owner.SemanticMaterial=new(owner.Id,generation,query,_memory.SemanticDataRoot);
            owner.SemanticMaterial.Result=SemanticResultForIsolatedTests(owner.Id,query);continuation();return;
        }
        long semanticStarted=Stopwatch.GetTimestamp();
        int timeout=_config.SemanticQueryTimeoutMs;
        var deadline=DateTimeOffset.UtcNow.AddMilliseconds(timeout);
        owner.WaitStatus.Prepare();
        owner.PreparationTrace?.Value("semantic_deadline_budget_ms",timeout);
        // Establish state/deadline before the first synchronous prewarm snapshot.
        // Sync locks/I/O and the later owner queue are measured, not preempted.
        using(PreparationTiming.MeasureOn(owner.PreparationTrace,"semantic.runtime_tick"))TickSemanticRuntime();
        owner.PreparationTrace?.Value("semantic_ready",_semanticWorker?.Ready==true?1:0);
        var state=new SemanticRequestMaterial(owner.Id,generation,query,_memory.SemanticDataRoot);owner.SemanticMaterial=state;
        var worker=_semanticWorker;
        SemanticRecallResult Unavailable(string reason)=>new(owner.Id,MemoryStore.SemanticTextHash(query),SemanticRecallWorker.ModelRevision,worker?.Epoch??"none",Array.Empty<SemanticRecallCandidate>(),reason,false,
            _memory.SemanticDataRoot,_memory.ReviewSessionIdentity,Generation:generation);
        if(worker==null)
        {
            state.Result=Unavailable("semantic_unavailable");owner.PreparationTrace?.Mark("semantic.unavailable");
            NativeEventLog.Trace("semantic.query","mod","system","local semantic query unavailable","semantic_unavailable");
            owner.PreparationTrace?.Record("semantic.owner_resume_total",semanticStarted,Stopwatch.GetTimestamp());
            continuation();return;
        }
        // Capture on the owner before yielding; the worker receives copies only.
        var snapshotWatch=Stopwatch.StartNew();
        Task<SemanticRecallResult> discovery;
        using(PreparationTiming.Use(owner.PreparationTrace))
            discovery=_memory.DiscoverSemanticAsync(owner.Id,generation,query,SemanticDomain.All,deadline,owner.Token);
        LastSemanticSnapshotMs=snapshotWatch.Elapsed.TotalMilliseconds;
        Task.Run(async()=>
        {
            var diagnostic=owner.PreparationTrace;
            using var timing=PreparationTiming.Use(diagnostic);
            try
            {
                var result=await discovery.ConfigureAwait(false);
                long completed=Stopwatch.GetTimestamp();diagnostic?.Mark("semantic.result_complete");
                _mainThread.Enqueue(()=>
                {
                    using var ownerTiming=PreparationTiming.Use(diagnostic);
                    diagnostic?.Record("semantic.owner_queue",completed,Stopwatch.GetTimestamp());
                    diagnostic?.Record("semantic.owner_resume_total",semanticStarted,Stopwatch.GetTimestamp());
                    diagnostic?.Value("index_complete",result.IndexComplete?1:0);
                    diagnostic?.Value("candidate_count",result.Candidates.Count);
                    diagnostic?.Value("semantic_unavailable",result.Unavailable.Length>0?1:0);
                    ObservePreparationOwner(diagnostic,"worker_continuation");
                    if(_semanticDestroyed||!IsCurrentDialogue(owner)||generation!=_conversationGeneration||owner.IsCancelled)return;
                    state.Result=ReferenceEquals(worker,_semanticWorker)&&(result.Unavailable.Length>0||result.IndexVersion.StartsWith(worker.Epoch,StringComparison.Ordinal))
                        && result.QueryId==owner.Id&&result.QueryHash==state.QueryHash&&result.Generation==generation
                        ?result:Unavailable("semantic_scope_changed");
                    NativeEventLog.Trace("semantic.query","mod","system","local semantic query completed",state.Result.Unavailable.Length>0?state.Result.Unavailable:state.Result.IndexComplete?"available":"partial");
                    continuation();
                });
            }
            catch(OperationCanceledException){ }
        });
    }
    private void BindSemanticMaterial(DialogueRequest owner,long generation,string query,BridgeConfig config,string extraContext,
        string context,IReadOnlyList<MemoryTurn> history,SemanticRecallTrace trace,IReadOnlyList<MemoryTurn> recalled,
        ThoughtRecallBoundary thought,ClaimRecallBoundary claim)
    {
        owner.PreparationTrace ??= PreparationTiming.Current;
        var state=owner.SemanticMaterial!;
        state.Accept(new(1,owner.Id,generation,MemoryStore.SemanticTextHash(query),context,history,trace,
            owner.B1HostFootprint.Select(t=>t.Copy()).ToArray(),recalled,thought,claim,
            owner.MethodRuntimeSources,owner.MethodRuntimeTracked),owner,"initial");
        state.RefreshOnOwner=token=>
        {
            var completion=new TaskCompletionSource<SemanticPreparedMaterial>(TaskCreationOptions.RunContinuationsAsynchronously);
            var registration=token.Register(()=>completion.TrySetCanceled(token));
            var diagnostic=owner.PreparationTrace;int ordinal=diagnostic?.NextPrepare()??0;
            long enqueued=Stopwatch.GetTimestamp();
            _mainThread.Enqueue(()=>
            {
                long started=Stopwatch.GetTimestamp();
                diagnostic?.Record("prepare."+ordinal+".queue",enqueued,started);
                ObservePreparationOwner(diagnostic,"prepare."+ordinal);
                using var timing=PreparationTiming.Use(diagnostic);
                var sw=Stopwatch.StartNew();
                try
                {
                    token.ThrowIfCancellationRequested();
                    if(_semanticDestroyed||!IsCurrentDialogue(owner)||generation!=_conversationGeneration||state.DataRoot!=_memory.SemanticDataRoot)throw new OperationCanceledException();
                    using var mention=PreparationTiming.Run("owner.mention_scope",()=>_memory.BeginMentionRequest(query,true));
                    using var native=PreparationTiming.Run("owner.native_scope",()=>_memory.BeginNativeDialogueRequest(query,true,config));
                    SemanticBoundaryHookForTests?.Invoke(owner);
                    var current=state.Current!;
                    bool b1Current,materialCurrent;
                    using(_memory.BeginB1ReadSession())
                    {
                        if(!_memory.SemanticCurrentInputAllowed(owner,query))throw new OperationCanceledException("current player source is no longer available");
                        if(!_memory.SemanticProtocolPermitted(owner,query))throw new SemanticMaterialException("protocol_source_changed");
                        b1Current=!owner.B1Participates||owner.B1DisabledForRequest&&state.ReplacementAccepted
                            ||owner.B1CanWrite&&(owner.B1Packet==null||_memory.B1PacketPermitted(owner.B1Packet,query));
                        materialCurrent=_memory.SemanticMaterialCurrent(owner,query,current);
                    }
                    if(materialCurrent&&b1Current){completion.TrySetResult(current);return;}
                    // One completed refresh for this boundary, never a loop that
                    // retries until a moving source happens to pass.
                    if(state.RebasedAtBoundary(owner.HttpAttempts))throw new SemanticMaterialException("material_changed_after_one_refresh");
                    var fresh=BuildFreshSemanticMaterial(owner,generation,query,config,extraContext,current.Revision+1);
                    if(!_memory.SemanticMaterialCurrent(owner,query,fresh)||!_memory.SemanticProtocolPermitted(owner,query))
                        throw new SemanticMaterialException("material_changed_during_refresh");
                    if(owner.B1Participates)owner.DisableB1("materials_rebased");
                    state.Accept(fresh,owner,"materials_rebased");completion.TrySetResult(fresh);
                }
                catch(OperationCanceledException){completion.TrySetCanceled(token.IsCancellationRequested?token:new CancellationToken(true));}
                catch(Exception error){completion.TrySetException(error);}
                finally{LastSemanticOwnerMs=sw.Elapsed.TotalMilliseconds;diagnostic?.Record("prepare."+ordinal+".owner",started,Stopwatch.GetTimestamp());registration.Dispose();}
            });
            return completion.Task;
        };
    }
    private SemanticPreparedMaterial BuildFreshSemanticMaterial(DialogueRequest owner,long generation,string query,BridgeConfig config,string extra,int revision)
    {
        var footprint=new List<MemoryTurn>();var recalled=new List<MemoryTurn>();var trace=new SemanticRecallTrace();trace.BindQuery(query);
        var history=_memory.RevalidateRequestHistory(_memory.GetRequestHistory(config));
        using var capture=B1Continuity.BeginCapture(footprint);
        using var methodRuntime=MethodRuntimeEvidence.Begin(config.SessionReadMethodsEnabled);
        string context=ContextBuilder.Build(_memory,_sessionState,config,list=>recalled.AddRange(list),environment:SemanticEnvironmentForTests,
            allowReflectionChatProjection:true,recallQueryOverride:query,imageAttached:owner.Frame is {DataUrl.Length:>0},playerDirected:true,
            requestHistory:history,includeVisionStatus:false,semanticRecall:owner.SemanticMaterial!.Result,semanticTrace:trace);
        context+=Environment.NewLine+_sessionState.VisualSupply.Describe(_memory.GetRequestHistorySelection(config,history));
        if(!string.IsNullOrWhiteSpace(extra))context+=Environment.NewLine+"[程序自动观察] "+extra;
        context+=Environment.NewLine+"[本轮材料更新] 以当前来源和状态为准，旧B1编号不再作为可写引用；继续原交流，不重复已经完成的工具。";
        var thought=_memory.CaptureThoughtRecallBoundary(query,config.ReflectionEnabled&&config.RuntimeContextCleanupEnabled,owner.SemanticMaterial?.Result);
        var claim=_memory.CaptureClaimBoundary(query);
        recalled.AddRange(claim.Sources);
        return new(revision,owner.Id,generation,MemoryStore.SemanticTextHash(query),context,history,trace,footprint,recalled,thought,claim,
            methodRuntime.Sources,methodRuntime.Available&&string.IsNullOrWhiteSpace(extra));
    }
}
