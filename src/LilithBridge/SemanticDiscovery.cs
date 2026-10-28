using System.Diagnostics;

namespace LilithBridge;

[Flags]
internal enum SemanticDomain { Events=1, B1=2, Thoughts=4, All=7 }

internal sealed class SemanticSnapshotUnavailableException : IOException
{
    internal string Reason {get;}
    internal SemanticSnapshotUnavailableException(string reason):base(reason)=>Reason=reason;
}

// Immutable copies only. Deadline governs cancellable worker waits. Synchronous
// snapshot locks/I/O cannot be preempted; the owner continuation queue is separate.
internal sealed record SemanticDiscoveryRequest(string OwnerId,long Generation,string Query,
    string DataRoot,string SessionId,SemanticDomain Domains,IReadOnlyList<SemanticIndexRecord> Snapshot,
    DateTimeOffset Deadline);

internal sealed partial class MemoryStore
{
    internal Func<SemanticDiscoveryRequest,CancellationToken,Task<SemanticRecallResult>>? SemanticDiscovery {get;set;}
    internal async Task<SemanticRecallResult> DiscoverSemanticAsync(string owner,long generation,string query,
        SemanticDomain domains,DateTimeOffset deadline,CancellationToken token)
    {
        using var callTiming=PreparationTiming.Measure("semantic.discovery_call_total");
        token.ThrowIfCancellationRequested();
        var discover=SemanticDiscovery;
        if(discover==null)return new(owner,SemanticTextHash(query),SemanticRecallWorker.ModelRevision,"none",
            Array.Empty<SemanticRecallCandidate>(),"semantic_unavailable",false,DataRoot:_root,SessionId:_sessionId,Domains:domains,Generation:generation);
        bool captured=false;
        try
        {
            if(DateTimeOffset.UtcNow>=deadline)
                return new(owner,SemanticTextHash(query),SemanticRecallWorker.ModelRevision,"none",Array.Empty<SemanticRecallCandidate>(),
                    "semantic_deadline_snapshot",false,_root,_sessionId,domains,Generation:generation);
            IReadOnlyList<SemanticIndexRecord> snapshot;
            using(PreparationTiming.Measure("semantic.snapshot"))snapshot=CaptureSemanticIndexSnapshot();
            captured=true;PreparationTiming.Current?.Value("snapshot_records",snapshot.Count);
            // The synchronous read is not preemptible. Recheck immediately on
            // return so expired/cancelled work never enters the worker queue.
            token.ThrowIfCancellationRequested();
            if(DateTimeOffset.UtcNow>=deadline)
                return new(owner,SemanticTextHash(query),SemanticRecallWorker.ModelRevision,"none",Array.Empty<SemanticRecallCandidate>(),
                    "semantic_deadline_snapshot",false,_root,_sessionId,domains,Generation:generation);
            var request=new SemanticDiscoveryRequest(owner,generation,query,_root,_sessionId,domains,snapshot,deadline);
            long queued=Stopwatch.GetTimestamp();
            return await Task.Run(async()=>
            {
                PreparationTiming.Current?.Record("semantic.background_queue",queued,Stopwatch.GetTimestamp());
                return await discover(request,token).ConfigureAwait(false);
            },token).ConfigureAwait(false);
        }
        catch(OperationCanceledException){throw;}
        catch(SemanticSnapshotUnavailableException error)
        {
            return new(owner,SemanticTextHash(query),SemanticRecallWorker.ModelRevision,"none",Array.Empty<SemanticRecallCandidate>(),
                error.Reason,false,_root,_sessionId,domains,Generation:generation);
        }
        catch
        {
            return new(owner,SemanticTextHash(query),SemanticRecallWorker.ModelRevision,"none",Array.Empty<SemanticRecallCandidate>(),
                captured?"semantic_discovery_failed":"semantic_snapshot_failed",false,_root,_sessionId,domains,Generation:generation);
        }
    }

    internal bool SemanticResultMatches(string query,SemanticRecallResult? result,SemanticDomain domain) =>
        result is {Unavailable.Length:0} && result.QueryHash==SemanticTextHash(query)
        && result.ModelRevision==SemanticRecallWorker.ModelRevision && result.IndexVersion.Length>0
        && (result.DataRoot.Length==0||result.DataRoot==_root)
        && (result.SessionId.Length==0||result.SessionId==_sessionId) && (result.Domains&domain)!=0;

    internal static string SemanticSourceVersion(MemoryTurn t)=>$"{t.Revision}:{t.LifecycleState}:{t.DeliveryState}";
    internal static bool SemanticCandidateMatches(SemanticRecallCandidate c,MemoryTurn t,string kind="utterance") =>
        c.Kind==kind && c.EventId==t.EventId && c.TextHash==SemanticTextHash(t.Text)
        && (c.Version.Length==0||c.Version==SemanticSourceVersion(t));
}
