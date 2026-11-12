namespace LilithBridge;

internal sealed partial class DialogueWorkSession
{
    internal string ReserveFinalProgress(string text,string attempt,Func<bool> permission,PublicStreamItemIdentity item)
    {
        if(!Offer(text,attempt,false,permission,item))return "";
        lock(_gate)
        {
            var s=_segments.Last();s.Progressive=true;s.ReadyCount=0;return s.Id;
        }
    }
    internal bool SetFinalProgress(string segment,string body,IReadOnlyList<string> ready,int readyThrough,bool captureStopped=false)
    {
        lock(_gate)
        {
            SyncCancellation();
            if(_disposed||!captureStopped&&(_ownerToken.IsCancellationRequested||_generationDone||_outcome!="running"))return false;
            var s=_segments.Single(x=>x.Id==segment);
            if(readyThrough<0||readyThrough>body.Length)return false;
            var chunks=ready.Concat(BubbleChunks.Split(body[readyThrough..])).ToArray();
            var plan=BubbleChunks.BuildStreamingPlan(body,chunks);
            if(!plan.IsValid)return false;
            int committed=captureStopped?FinalProgressCommittedUnsafe(s):s.Next;
            if(committed>ready.Count||Enumerable.Range(0,committed).Any(i=>i>=chunks.Length||s.Chunks[i]!=chunks[i]))return false;
            // Prevent a concurrent flush from applying this body/plan to the
            // preceding immutable checkpoint. Publication remains held by owner.
            _archiveBindings.Remove(segment);
            s.ReceivedPublicText=body;s.Chunks=chunks;s.Plan=plan;s.ReadyCount=ready.Count;s.ProgressThrough=readyThrough;
            if(captureStopped)s.Next=chunks.Length;
            _stateRevision++;return true;
        }
    }
    internal StreamAppearanceEvidence[] FinalProgressAppearance(string segment)
    {
        lock(_gate)
        {
            var s=_segments.Single(x=>x.Id==segment);
            return s.Shown.OrderBy(i=>i).Select(i=>s.Appearances[i]).ToArray();
        }
    }
    internal WorkExpression FinalProgressSnapshot(string segment)
    {lock(_gate)return View(_segments.Single(x=>x.Id==segment),true);}
    internal void ReplaceProgressArchive(string segment,MemoryStore store,string key)
    {lock(_gate)_archiveBindings[segment]=(store,key);}
    internal string[] FinalProgressChunks(string segment)
    {lock(_gate)return _segments.Single(x=>x.Id==segment).Chunks.ToArray();}
    private static int FinalProgressCommittedUnsafe(Segment s)=>s.Shown.Concat(s.Accepted).Concat(s.Uncertain).DefaultIfEmpty(-1).Max()+1;
    internal (string[] Chunks,int Through) FinalProgressCommitted(string segment)
    {
        lock(_gate)
        {
            var s=_segments.Single(x=>x.Id==segment);int count=FinalProgressCommittedUnsafe(s);
            int through=count==0?0:s.Plan.Chunks[count-1].StartChar+s.Plan.Chunks[count-1].GeneratedChars;
            return(s.Chunks.Take(count).ToArray(),through);
        }
    }
    internal void FreezeFinalProgress(string segment)
    {lock(_gate){var s=_segments.Single(x=>x.Id==segment);s.ReadyCount=FinalProgressCommittedUnsafe(s);}}
}
