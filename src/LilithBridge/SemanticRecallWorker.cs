using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace LilithBridge;

internal sealed record SemanticIndexRecord(string EventId, string Text, string Role, string Origin, string Delivery,
    string Kind="utterance",string Version="")
{
    internal string WireId=>Kind=="utterance"?EventId:"derived/"+Kind+"/"+EventId;
    internal SemanticDomain Domain=>Kind switch {"b1"=>SemanticDomain.B1,"note" or "lesson"=>SemanticDomain.Thoughts,_=>SemanticDomain.Events};
}

// One private child per Bridge/data root. It owns derived vectors only. The
// worker never reads MemoryStore or the user's config/credentials itself.
internal sealed class SemanticRecallWorker : IDisposable
{
    internal const string ModelRevision = "7999e1d3359715c523056ef9478215996d62a620";
    private readonly string _python, _script, _model, _manifest, _index;
    private readonly string _dataRoot;
    private readonly bool _testMode;
    private readonly SemaphoreSlim _protocol = new(1,1), _synchronization = new(1,1), _discovery = new(1,1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string,string> _hashes = new(StringComparer.Ordinal);
    private TaskCompletionSource<JsonElement> _handshake = NewCompletion();
    private Process? _process;
    private long _indexGeneration;
    private int _restartUsed;
    private bool _needFullPrune=true;
    // Owned by _synchronization. Retain across a cancelled synchronization so
    // an unchanged next snapshot still persists previously acknowledged changes.
    private bool _indexDirty;
    // Separate from model readiness and pending durability: an unconfirmed
    // mutation may already have changed Python rows while _hashes is still old.
    internal bool IndexMirrorKnown {get;private set;}
    private Task _syncTask=Task.CompletedTask;
    private long _snapshotVersion,_indexedVersion;
    private volatile bool _disposed;
    private IReadOnlyList<SemanticIndexRecord> _lastSnapshot = Array.Empty<SemanticIndexRecord>();
    internal string Epoch { get; private set; } = Guid.NewGuid().ToString("N");
    private volatile bool _ready;
    internal bool Ready { get=>_ready; private set=>_ready=value; }
    internal string Status { get; private set; } = "not_started";
    internal int QueryCount { get; private set; }
    internal int LateResponses { get; private set; }
    internal double InitializationLoadMs {get;private set;}
    internal double InitialIndexMs {get;private set;}
    internal int TestQueryDelayMs { get; set; }
    internal int? ProcessId => _process is { HasExited:false } p ? p.Id : null;
    internal Task Initialization { get; private set; } = Task.CompletedTask;
    internal Task Shutdown { get; private set; } = Task.CompletedTask;
    internal Task IndexSynchronization => _syncTask;

    internal SemanticRecallWorker(BridgeConfig config,string dataRoot,bool testMode=false)
    {
        string root=Path.GetFullPath(dataRoot);
        _dataRoot=root;
        string Resolve(string value)=>Path.GetFullPath(Path.IsPathRooted(value)?value:Path.Combine(root,value));
        _python=Resolve(config.SemanticPythonPath);_script=Resolve(config.SemanticWorkerScript);
        _model=Resolve(config.SemanticModelPath);_manifest=Path.Combine(Path.GetDirectoryName(_script)!,"model-manifest.json");
        _index=Resolve(config.SemanticIndexDirectory);
        if(!_index.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Derived semantic index must be below its actual data root");
        _testMode=testMode;
    }
    private static TaskCompletionSource<JsonElement> NewCompletion()=>new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal void Start(IReadOnlyList<SemanticIndexRecord> snapshot)
    {
        if(Status!="not_started"||_disposed)return;
        Status="initializing";_lastSnapshot=snapshot;
        Initialization=Task.Run(()=>InitializeAsync(snapshot));
    }
    private async Task InitializeAsync(IReadOnlyList<SemanticIndexRecord> snapshot)
    {
        var readyWatch=Stopwatch.StartNew();
        try
        {
            _lifetime.Token.ThrowIfCancellationRequested();
            _handshake=NewCompletion();Epoch=Guid.NewGuid().ToString("N");
            var info=new ProcessStartInfo(_python){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,
                RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8,
                WorkingDirectory=Path.GetDirectoryName(_script)!};
            foreach(string arg in new[]{"-u",_script,"--model",_model,"--manifest",_manifest,"--index",_index})info.ArgumentList.Add(arg);
            if(_testMode)info.ArgumentList.Add("--test-mode");
            info.Environment["PYTHONUTF8"]="1";info.Environment["HF_HUB_OFFLINE"]="1";info.Environment["HF_HUB_DISABLE_TELEMETRY"]="1";info.Environment["HF_HUB_DISABLE_IMPLICIT_TOKEN"]="1";
            _lifetime.Token.ThrowIfCancellationRequested();
            _process=Process.Start(info)??throw new IOException("semantic child could not start");
            if(_disposed){StopOwnedProcess();throw new OperationCanceledException();}
            _process.StandardInput.NewLine="\n";
            _=ReadAsync(_process);_=DrainErrorsAsync(_process);
            var ready=await _handshake.Task.WaitAsync(TimeSpan.FromSeconds(60),_lifetime.Token).ConfigureAwait(false);
            if(ready.GetProperty("model_revision").GetString()!=ModelRevision)throw new InvalidDataException("semantic model identity mismatch");
            InitializationLoadMs=readyWatch.Elapsed.TotalMilliseconds;
            var indexWatch=Stopwatch.StartNew();
            await SynchronizeAsync(snapshot,_lifetime.Token,true,true).ConfigureAwait(false);
            InitialIndexMs=indexWatch.Elapsed.TotalMilliseconds;
            if(!_disposed){Status="ready";Ready=true;}
        }
        catch(Exception e)
        {
            Ready=false;Status=_disposed?"stopped":"unavailable:"+e.GetType().Name;
            StopOwnedProcess();
        }
    }
    private async Task ReadAsync(Process process)
    {
        try
        {
            while(await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if(!ReferenceEquals(process,_process))return;
                using var doc=JsonDocument.Parse(line);var value=doc.RootElement.Clone();
                if(value.TryGetProperty("index_generation",out var generation))Interlocked.Exchange(ref _indexGeneration,generation.GetInt64());
                if(value.TryGetProperty("event",out var kind)&&kind.GetString()=="ready"){_handshake.TrySetResult(value);continue;}
                string id=value.TryGetProperty("request_id",out var identity)?identity.GetString()??"":"";
                if(_pending.TryRemove(id,out var completion))completion.TrySetResult(value);else LateResponses++;
            }
            throw new EndOfStreamException("semantic child exited");
        }
        catch(Exception e)
        {
            if(!ReferenceEquals(process,_process))return;
            _handshake.TrySetException(e);
            foreach(var pair in _pending)if(_pending.TryRemove(pair.Key,out var c))c.TrySetException(e);
            bool wasReady=Ready;Ready=false;if(!_disposed)Status="unavailable:"+e.GetType().Name;
            if(wasReady)RestartOnce();
        }
    }
    private async Task DrainErrorsAsync(Process process)
    {
        // The child emits error types only. Never copy query/source text into
        // the host log, and drain stderr so it cannot fill and deadlock IPC.
        try{while(await process.StandardError.ReadLineAsync().ConfigureAwait(false) is not null){ }}catch{ }
    }
    private async Task<JsonElement> CommandAsync(string op,Dictionary<string,object?> data,CancellationToken token)
    {
        using(PreparationTiming.Measure(op=="query"?"semantic.protocol_queue":"semantic.index_protocol_queue"))
            await _protocol.WaitAsync(token).ConfigureAwait(false);
        using var ipcTiming=PreparationTiming.Measure(op=="query"?"semantic.ipc_roundtrip":"semantic.index_ipc_roundtrip");
        string id=Guid.NewGuid().ToString("N");var completion=NewCompletion();bool issued=false;
        try
        {
            token.ThrowIfCancellationRequested();if(_disposed)throw new OperationCanceledException();
            var process=_process;if(process==null||process.HasExited)throw new EndOfStreamException();
            data["op"]=op;data["request_id"]=id;
            if(op=="query")data["index_generation"]=Interlocked.Read(ref _indexGeneration);
            _pending[id]=completion;
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(data)).ConfigureAwait(false);
            await process.StandardInput.FlushAsync().ConfigureAwait(false);
            issued=true;
            return await completion.Task.WaitAsync(token).ConfigureAwait(false);
        }
        finally
        {
            // Cancelling the await does not stop Python. Retain the one occupied
            // protocol slot until its actual response/exit; do not accumulate IPC.
            if(issued&&_pending.ContainsKey(id)&&!completion.Task.IsCompleted)
                _=ReleaseAfterResponseAsync(id,completion.Task);
            else {_pending.TryRemove(id,out _);_protocol.Release();}
        }
    }
    private async Task ReleaseAfterResponseAsync(string id,Task response)
    {try{await response.ConfigureAwait(false);}catch{}finally{_pending.TryRemove(id,out _);_protocol.Release();}}
    private async Task SynchronizeAsync(IReadOnlyList<SemanticIndexRecord> snapshot,CancellationToken token,bool commit,bool reset=false)
    {
        await _synchronization.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if(reset){_hashes.Clear();_needFullPrune=true;IndexMirrorKnown=false;}
            _lastSnapshot=snapshot;
            var hashes=snapshot.ToDictionary(r=>r.WireId,r=>MemoryStore.SemanticTextHash(r.Text),StringComparer.Ordinal);
            var changes=snapshot.Where(r=>!_hashes.TryGetValue(r.WireId,out var hash)||hash!=hashes[r.WireId]).ToArray();
            for(int at=0;at<changes.Length;at+=16)
            {
                token.ThrowIfCancellationRequested();
                var batch=changes.Skip(at).Take(16).ToArray();
                var result=await CommandAsync("upsert",new(){["records"]=batch.Select(r=>new{event_id=r.WireId,text=r.Text,role=r.Role,origin=r.Origin,delivery=r.Delivery,text_hash=hashes[r.WireId]}).ToArray()},token).ConfigureAwait(false);
                if(result.GetProperty("status").GetString()!="ok")throw new InvalidDataException("semantic update rejected");
                _indexDirty=true;
                foreach(var r in batch)_hashes[r.WireId]=hashes[r.WireId];
                await Task.Yield(); // queries can enter between bounded batches
            }
            if(_needFullPrune||_hashes.Keys.Any(id=>!hashes.ContainsKey(id)))
            {
                var result=await CommandAsync("prune",new(){["event_ids"]=hashes.Keys.ToArray()},token).ConfigureAwait(false);
                if(result.GetProperty("status").GetString()!="ok")throw new InvalidDataException("semantic prune rejected");
                _indexDirty=true;
                _needFullPrune=false;
            }
            _hashes.Clear();foreach(var pair in hashes)_hashes[pair.Key]=pair.Value;
            if(commit&&_indexDirty)
            {
                using var commitTiming=PreparationTiming.Measure("semantic.commit_roundtrip");
                var result=await CommandAsync("commit",new(),token).ConfigureAwait(false);
                if(result.GetProperty("status").GetString()!="ok")throw new InvalidDataException("semantic commit rejected");
                _indexDirty=false;
            }
            IndexMirrorKnown=true;
            PreparationTiming.Current?.Value("semantic_index_mirror_known",1);
        }
        catch
        {
            // Cancellation only ends this await, not the issued Python command.
            // Invalidate the entire mirror before releasing synchronization. The
            // next operation must upsert its full current snapshot, prune all
            // other identities and confirm commit after the old IPC slot drains.
            // A dirty commit alone cannot restore a row removed by a late prune
            // or restore the prior hash after a late upsert.
            _hashes.Clear();_needFullPrune=true;_indexDirty=true;IndexMirrorKnown=false;
            PreparationTiming.Current?.Value("semantic_index_mirror_known",0);
            throw;
        }
        finally{_synchronization.Release();}
    }
    private void RestartOnce()
    {
        if(!_disposed&&Interlocked.CompareExchange(ref _restartUsed,1,0)==0)
        {StopOwnedProcess();Initialization=Task.Run(()=>InitializeAsync(_lastSnapshot));}
    }
    // Compatibility entry for isolated worker probes. Product callers supply
    // the actual operation identity, domains, scope and absolute deadline.
    internal Task<SemanticRecallResult> QueryAsync(string ownerId,long generation,string query,
        IReadOnlyList<SemanticIndexRecord> snapshot,int timeoutMs,CancellationToken token) =>
        DiscoverAsync(new(ownerId,generation,query,"","",SemanticDomain.All,snapshot,
            DateTimeOffset.UtcNow.AddMilliseconds(Math.Max(1,timeoutMs))),token);

    internal async Task<SemanticRecallResult> DiscoverAsync(SemanticDiscoveryRequest request,CancellationToken token)
    {
        using var queryTiming=PreparationTiming.Measure("semantic.query_total");
        string queryHash=MemoryStore.SemanticTextHash(request.Query),phase="ready";
        long target=0;
        SemanticRecallResult Unavailable(string why)=>new(request.OwnerId,queryHash,ModelRevision,Epoch,
            Array.Empty<SemanticRecallCandidate>(),why,false,request.DataRoot,request.SessionId,request.Domains,
            target,Interlocked.Read(ref _indexedVersion),request.Generation);
        if(request.DataRoot.Length>0&&Path.GetFullPath(request.DataRoot)!=_dataRoot)return Unavailable("semantic_scope_changed");
        var remaining=request.Deadline-DateTimeOffset.UtcNow;
        if(remaining<=TimeSpan.Zero)return Unavailable("semantic_deadline_ready");
        using var deadline=new CancellationTokenSource(remaining);
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(token,deadline.Token,_lifetime.Token);
        bool held=false;
        try
        {
            if(!Ready)
            {
                using var readyTiming=PreparationTiming.Measure("semantic.ready_wait");
                while(!Ready)
                {
                    linked.Token.ThrowIfCancellationRequested();
                    var initializing=Initialization;
                    if(initializing.IsCompleted)return Unavailable(Status=="stopped"?"semantic_worker_stopped":"semantic_unavailable");
                    await initializing.WaitAsync(linked.Token).ConfigureAwait(false);
                    if(!Ready&&ReferenceEquals(initializing,Initialization))return Unavailable("semantic_unavailable");
                }
            }
            string epoch=Epoch;
            PreparationTiming.Current?.Value("semantic_initialization_load_ms",InitializationLoadMs);
            PreparationTiming.Current?.Value("semantic_initial_index_ms",InitialIndexMs);
            phase="queue";
            using(PreparationTiming.Measure("semantic.operation_queue"))
                await _discovery.WaitAsync(linked.Token).ConfigureAwait(false);
            held=true;
            if(!Ready||epoch!=Epoch)return Unavailable("semantic_identity_changed");
            // Synchronize this immutable target, not the latest moving library.
            // A later operation cannot prune/replace it before its query returns.
            target=Interlocked.Increment(ref _snapshotVersion);
            PreparationTiming.Current?.Value("semantic_target_watermark",target);
            phase="index";
            using(PreparationTiming.Measure("semantic.index_wait"))
            {
                _syncTask=SynchronizeAsync(request.Snapshot,linked.Token,true);
                await _syncTask.ConfigureAwait(false);
                Interlocked.Exchange(ref _indexedVersion,target);
            }
            PreparationTiming.Current?.Value("semantic_completed_watermark",target);
            phase="query";
            var r=await CommandAsync("query",new(){["text"]=request.Query,["query_hash"]=queryHash,
                ["conversation_generation"]=request.Generation,["model_revision"]=ModelRevision,
                ["delay_ms"]=_testMode?TestQueryDelayMs:0},linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            if(r.GetProperty("status").GetString()=="error")return Unavailable("semantic_query_failed");
            if(epoch!=Epoch||r.GetProperty("status").GetString()!="ok"||r.GetProperty("model_revision").GetString()!=ModelRevision
                ||r.GetProperty("conversation_generation").GetInt64()!=request.Generation||r.GetProperty("query_hash").GetString()!=queryHash)
                return Unavailable("semantic_identity_changed");
            if(PreparationTiming.Current is { } trace)
            {
                foreach(string name in new[]{"elapsed_ms","records","chunks"})
                    if(r.TryGetProperty(name,out var number)&&number.TryGetDouble(out double value))trace.Value("worker_"+name,value);
            }
            QueryCount++;
            var byWire=request.Snapshot.ToDictionary(x=>x.WireId,StringComparer.Ordinal);
            var candidates=new List<SemanticRecallCandidate>();
            int rank=0;
            foreach(var c in r.GetProperty("candidates").EnumerateArray())
            {
                if(!byWire.TryGetValue(c.GetProperty("event_id").GetString()!,out var source)
                    ||(source.Domain&request.Domains)==0||c.GetProperty("text_hash").GetString()!=MemoryStore.SemanticTextHash(source.Text))continue;
                candidates.Add(new(source.EventId,c.GetProperty("text_hash").GetString()!,c.GetProperty("score").GetDouble(),
                    c.GetProperty("chunk_start").GetInt32(),c.GetProperty("chunk_end").GetInt32(),source.Kind,source.Version,++rank));
            }
            PreparationTiming.Current?.Value("semantic_event_candidates",candidates.Count(c=>c.Kind=="utterance"));
            PreparationTiming.Current?.Value("semantic_b1_hits",candidates.Count(c=>c.Kind=="b1"));
            PreparationTiming.Current?.Value("semantic_thought_hits",candidates.Count(c=>c.Kind is "note" or "lesson"));
            return new(request.OwnerId,queryHash,ModelRevision,epoch+":"+r.GetProperty("index_generation").GetInt64(),
                candidates,DataRoot:request.DataRoot,SessionId:request.SessionId,Domains:request.Domains,
                TargetWatermark:target,CompletedWatermark:target,Generation:request.Generation);
        }
        catch(OperationCanceledException)
        {
            token.ThrowIfCancellationRequested();
            return Unavailable(_lifetime.IsCancellationRequested?"semantic_worker_stopped":"semantic_deadline_"+phase);
        }
        catch(Exception)
        {
            Ready=false;Status="unavailable:worker_failure";RestartOnce();
            return Unavailable("semantic_worker_failed");
        }
        finally{if(held)_discovery.Release();}
    }
    internal Task<JsonElement> InspectAsync()=>CommandAsync("status",new(),_lifetime.Token);
    internal Task<JsonElement> ExitForTestAsync()=>CommandAsync("test_exit",new(),_lifetime.Token);
    private void StopOwnedProcess()
    {
        foreach(var pair in _pending)if(_pending.TryRemove(pair.Key,out var c))c.TrySetException(new EndOfStreamException("owned semantic process stopped"));
        try{if(_process is { HasExited:false } p)p.Kill(true);}catch{ }
    }
    public void Dispose()
    {
        if(_disposed)return;_disposed=true;Ready=false;Status="stopped";_lifetime.Cancel();
        var process=_process;
        try{process?.StandardInput.Close();}catch{ }
        Shutdown=Task.Run(async()=>{if(process==null)return;try{using var wait=new CancellationTokenSource(2000);await process.WaitForExitAsync(wait.Token).ConfigureAwait(false);}catch{StopOwnedProcess();}finally{process.Dispose();}});
    }
}
