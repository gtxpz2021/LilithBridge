using System.Linq;

namespace LilithBridge;

internal sealed partial class MemoryStore
{
    // Frozen sources of the actual sent view, independent of mutable request
    // diagnostics, short aliases and the current presentation instance. Reuse
    // normal read-purpose and permission resolution; never grant new sources.
    internal bool ReadableB1SourcesPermitted(B1Packet supplied,string query,BridgeConfig config)
    {
        lock(_experienceLock)
        {
            using var mention=BeginMentionRequest(query);
            using var native=BeginNativeDialogueRequest(query,true,config);
            // This live boolean gate also runs during display ticks. Borrow the
            // existing archive index under its lock instead of copying history;
            // restore resolver state before any general reader can observe it.
            using var borrowed=BeginBorrowedB1BooleanRead();
            BeginB1Read(borrowArchive:true);
            var doc=ReadB1Storage(initializeMissing:false);
            if(doc==null)return false;
            foreach(var old in supplied.Materials)
            {
                var resolved=ResolveB1(old.Ref,doc,new());
                if(!resolved.Found||resolved.Material is not {} current)return false;
                if(old.Ref.Kind!="b1")
                {
                    // Existing source fingerprint includes revision/lifecycle/
                    // validity: a rewritten or requalified raw source is distinct.
                    if(current.VersionFingerprint!=old.VersionFingerprint)return false;
                }
                else if(current.Text!=old.Text||current.Scope!=old.Scope||current.At!=old.At
                    ||current.Origin!=old.Origin||current.Nature!=old.Nature||current.Conditions!=old.Conditions
                    ||current.Relation!=old.Relation||current.Target!=old.Target
                    ||current.LogicalId!=old.LogicalId||current.Revision!=old.Revision||current.AnchorStatus!=old.AnchorStatus)
                    return false;
                // For B1 history-purpose material, lawful historical discussion
                // is not promoted to current endorsement. Current purpose still
                // requires current qualification in ResolveB1; deletion/withdrawal
                // follows that existing policy, not a new blanket read veto.
            }
            return supplied.Footprint.Concat(B1DisplayFootprint(supplied))
                .All(reference=>ResolveB1(reference with {Purpose="permission"},doc,new()).Found);
        }
    }
}
