using System;
using System.Linq;

namespace LilithBridge;

// One request-local addition, not another memory store or cached retrieval owner.
internal sealed record RecallSupplement(string Kind,string Context,MemoryTurn[] Sources,
    PaperSourceSnapshot PaperSources,Func<bool> Permitted)
{
    internal bool IsPermitted(){try{return Permitted();}catch{return false;}}
}

internal sealed partial class MemoryStore
{
    internal bool RecallSupplementPermitted(PaperSourceSnapshot snapshot)
    {
        if(!PaperSourcesPermitted(snapshot))return false;
        lock(_experienceLock)
        {
            foreach(var old in snapshot.Records)
            {
                // Keep the existing handling of unaddressed legacy/profile slots.
                if(old.EventId.Length==0)continue;
                string kind=B1Kind(old);var current=B1Turn(kind,old.EventId);
                if(current==null||SourceVersion(kind,current)!=SourceVersion(kind,old))return false;
            }
            return true;
        }
    }
}
