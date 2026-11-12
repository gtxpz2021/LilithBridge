using System.Text.Json;

namespace LilithBridge;

internal sealed record SemanticSourceAudit(string Kind,string EventId,string Hash,int Length);
internal sealed record SemanticMaterialAudit(int Revision,string Reason,IReadOnlyList<SemanticSourceAudit> Sources,int HttpAttempts);
internal sealed record SemanticPreparedMaterial(int Revision,string RequestId,long Generation,string QueryHash,string Context,
    IReadOnlyList<MemoryTurn> History,SemanticRecallTrace Trace,IReadOnlyList<MemoryTurn> HostFootprint,IReadOnlyList<MemoryTurn> RecallSources,
    ThoughtRecallBoundary Thought,ClaimRecallBoundary Claim,
    IReadOnlyList<MemoryTurn>? MethodRuntimeSources=null,bool MethodRuntimeTracked=true)
{
    internal PaperSourceSnapshot? PaperSources { get; init; }
    internal int RecallSupplementCount {get;init;}
}
internal sealed record SemanticMessageRefresh(List<object> Messages,IReadOnlyList<string> RuntimeBlocks,
    IReadOnlyList<MemoryTurn> History,HistoryProjectionSummary Projection);

// Request-local supply revisions. Immutable audit is identities/hashes only;
// it does not introduce a second store retaining withdrawn source bodies.
internal sealed class SemanticRequestMaterial
{
    internal readonly string RequestId,QueryHash,DataRoot;
    internal readonly long Generation;
    internal SemanticRecallResult? Result;
    internal SemanticPreparedMaterial? Current { get; private set; }
    internal Func<CancellationToken,Task<SemanticPreparedMaterial>>? RefreshOnOwner;
    internal List<object> ProtocolMessages { get; } = new();
    internal List<SemanticMaterialAudit> Retired { get; } = new();
    internal List<(int Revision,string Attempt,string Outcome,IReadOnlyList<SemanticSourceAudit> Sources)> HttpAudit { get; } = new();
    internal bool ReplacementAccepted { get; private set; }
    internal int RefreshCount { get; private set; }
    private int _lastRebasedBoundary=-1;
    internal bool RebasedAtBoundary(int attempts)=>_lastRebasedBoundary==attempts;
    private readonly object _auditLock=new();
    private readonly List<PaperSourceSnapshot> _sentPaperSources = new();
    private readonly List<RecallSupplement> _recallSupplements=new();
    internal bool RecallSupplementsPermitted()=>_recallSupplements.All(s=>s.IsPermitted());
    // Registration and composition run on the existing main-thread material owner.
    internal void AddRecallSupplement(RecallSupplement supplement,DialogueRequest owner)
    {
        if(owner.Id!=RequestId||owner.IsCancelled||!supplement.IsPermitted()
            ||_recallSupplements.Any(s=>s.Kind==supplement.Kind))
            throw new SemanticMaterialException("recall_source_changed");
        _recallSupplements.Add(supplement);
        ApplyRecallSupplements(Current??throw new InvalidOperationException("No initial material"),owner);
    }
    internal SemanticPreparedMaterial ApplyRecallSupplements(SemanticPreparedMaterial material,DialogueRequest owner)
    {
        if(!RecallSupplementsPermitted())throw new SemanticMaterialException("recall_source_changed");
        var additions=_recallSupplements.Skip(material.RecallSupplementCount).ToArray();
        if(additions.Length==0)return material;
        var sources=additions.SelectMany(s=>s.Sources).Select(t=>t.Copy()).ToArray();
        var paper=material.PaperSources;
        foreach(var item in additions)paper=PaperSourceSnapshot.Combine(paper,item.PaperSources);
        var revised=material with {Revision=material.Revision+1,
            Context=material.Context+Environment.NewLine+string.Join(Environment.NewLine,additions.Select(s=>s.Context)),
            HostFootprint=material.HostFootprint.Concat(sources).ToArray(),
            RecallSources=material.RecallSources.Concat(sources).ToArray(),PaperSources=paper,
            RecallSupplementCount=_recallSupplements.Count};
        Accept(revised,owner,"recall_supplement");
        return revised;
    }
    internal SemanticRequestMaterial(string id,long generation,string query,string dataRoot)
    {RequestId=id;Generation=generation;QueryHash=MemoryStore.SemanticTextHash(query);DataRoot=dataRoot;}
    internal static IReadOnlyList<SemanticSourceAudit> Audit(IEnumerable<MemoryTurn> turns)=>turns
        .GroupBy(t=>t.EventId).Select(g=>g.First()).Select(t=>new SemanticSourceAudit(MemoryStore.SemanticAuditKind(t),t.EventId,MemoryStore.SemanticTextHash(t.Text),t.Text.Length)).ToArray();
    internal void Accept(SemanticPreparedMaterial material,DialogueRequest owner,string reason)
    {
        if(owner.Id!=RequestId||owner.IsCancelled||material.RequestId!=RequestId||material.Generation!=Generation||material.QueryHash!=QueryHash)
            throw new OperationCanceledException("semantic material owner changed");
        if(Current!=null)
        {
            Retired.Add(new(Current.Revision,reason,Audit(owner.B1HostFootprint.Concat(Current.History)),owner.HttpAttempts));
            RefreshCount++;
            if(reason=="materials_rebased")_lastRebasedBoundary=owner.HttpAttempts;
        }
        Current=material;
        owner.MethodMaterialRevision++;
        owner.MethodRuntimeSources=(material.MethodRuntimeSources??Array.Empty<MemoryTurn>()).Select(t=>t.Copy()).ToArray();
        owner.MethodRuntimeTracked=material.MethodRuntimeTracked;
        // Old footprint has already been retained as immutable identities.
        owner.B1HostFootprint.Clear();owner.B1HostFootprint.AddRange(material.HostFootprint.Select(t=>t.Copy()));
        if(reason=="materials_rebased")ReplacementAccepted=true;
    }
    internal async Task<SemanticPreparedMaterial> PrepareAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if(!RecallSupplementsPermitted())throw new SemanticMaterialException("recall_source_changed");
        if(RefreshOnOwner==null)throw new InvalidOperationException("semantic owner refresh is unavailable");
        var material=await RefreshOnOwner(token).ConfigureAwait(false);token.ThrowIfCancellationRequested();
        if(!RecallSupplementsPermitted())throw new SemanticMaterialException("recall_source_changed");
        return material;
    }
    internal void ObserveHttp(string attempt,string outcome)
    {
        lock(_auditLock)
        {
            int at=HttpAudit.FindIndex(a=>a.Attempt==attempt);
            if(at<0&&Current!=null)
            {
                HttpAudit.Add((Current.Revision,attempt,outcome,Audit(Current.RecallSources)));
                if (outcome == "started" && Current.PaperSources is { } paper) _sentPaperSources.Add(paper);
            }
            else if(at>=0){var old=HttpAudit[at];HttpAudit[at]=(old.Revision,old.Attempt,outcome,old.Sources);}
        }
    }
    internal PaperSourceSnapshot PaperSourcesForReply(PaperSourceSnapshot fallback)
    {
        lock (_auditLock)
        {
            // Keep each actually dispatched version; a later refresh does not
            // erase sources already read in this logical reply's protocol.
            if (_sentPaperSources.Count == 0) return Current?.PaperSources ?? fallback;
            return new(_sentPaperSources.SelectMany(p => p.Records).ToArray(),
                _sentPaperSources.SelectMany(p => p.SceneIds).Distinct().ToArray(), fallback.Query, fallback.PlayerDirected);
        }
    }
}
