namespace LilithBridge;

internal sealed partial class DialogueWorkSession
{
    private readonly HashSet<string> _archivePublishing = new(StringComparer.Ordinal);
    private readonly Dictionary<string,(MemoryStore Store,string Key)> _archiveBindings = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reportedArchiveFailures = new(StringComparer.Ordinal);
    internal void HoldArchivePublication(string attempt) { lock (_gate) _archivePublishing.Add(attempt); }
    internal void PublishAfterArchive(string attempt) { lock (_gate) _archivePublishing.Remove(attempt); }
    internal void WithholdUnpublished(string segment)
    {
        lock(_gate)
        {
            var s=_segments.Single(x=>x.Id==segment);
            if(s.Shown.Count>0||s.Accepted.Count>0||s.Uncertain.Count>0)throw new InvalidOperationException("Stream fragment already presented");
            s.Next=s.Chunks.Length;_stateRevision++;
        }
    }
    internal WorkExpression[] ArchiveSnapshot()
    { lock (_gate) return _segments.Select(s=>View(s,true)).ToArray(); }
    internal void BindArchive(string segment, MemoryStore store, string key)
    { lock (_gate) _archiveBindings.TryAdd(segment,(store,key)); }
    internal GeneratedReplySnapshot[] ArchiveSourcesForCapture()
    {
        (MemoryStore Store,string Key)[] bindings;
        lock(_gate)bindings=_archiveBindings.Values.ToArray();
        return bindings.Select(b=>b.Store.ReadPublicExpressionForCapture(b.Key)).Where(s=>s!=null).Select(s=>s!).ToArray();
    }
    internal void FlushArchive()
    {
        (WorkExpression Expression, MemoryStore Store,string Key)[] writes; string outcome;
        lock (_gate)
        {
            outcome = _outcome;
            writes = _segments.Where(s=>_archiveBindings.ContainsKey(s.Id))
                .Select(s=>(View(s,true),_archiveBindings[s.Id].Store,_archiveBindings[s.Id].Key)).ToArray();
        }
        // Never acquire a source/store lock while holding the work/UI lock.
        foreach(var write in writes)
        {
            var receipt=write.Store.RecordPublicExpressionState(write.Key,write.Expression,outcome);
            bool report=false;
            if(!receipt.Succeeded){lock(_gate)report=_reportedArchiveFailures.Add(write.Key+"|"+receipt.Reason);}
            if(report)try{NativeEventLog.Trace("work.expression.receipt_failed","mod","system","Public expression receipt was not saved",receipt.Reason);}catch{ }
        }
    }
}
