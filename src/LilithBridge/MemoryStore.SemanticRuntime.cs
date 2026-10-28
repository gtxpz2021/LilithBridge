using System.Text.Json;

namespace LilithBridge;
internal sealed partial class MemoryStore
{
    internal static string SemanticAuditKind(MemoryTurn t)=>B1Kind(t);
    internal IReadOnlyList<MemoryTurn> ResolveSemanticAudit(IEnumerable<SemanticSourceAudit> refs)
    {
        var result=new List<MemoryTurn>();
        foreach(var r in refs)
        {
            var current=B1Turn(r.Kind,r.EventId);
            if(current!=null&&SemanticTextHash(current.Text)==r.Hash&&IsMentionAllowed(current)&&IsNativeSupplyAllowed(current)
                &&!IsExperienceSuppressed(current.EventId))result.Add(current.Copy());
        }
        return result;
    }
    internal bool SemanticCurrentInputAllowed(DialogueRequest owner,string text)=>VerifyUserEvidence(owner.UserEventId,text)
        && !IsExperienceSuppressed(owner.UserEventId)
        && ReadExperiences().FirstOrDefault(t=>t.EventId==owner.UserEventId) is { } input
        && IsMentionAllowed(input)&&IsNativeSupplyAllowed(input);
    internal bool SemanticMaterialCurrent(DialogueRequest owner,string query,SemanticPreparedMaterial material)
    {
        using var timing=PreparationTiming.Measure("owner.material_check");
        if(!SemanticSupplyPermitted(material.Trace,query))return false;
        var history=RevalidateRequestHistory(material.History);
        if(history.Count!=material.History.Count||history.Where((t,i)=>t.EventId!=material.History[i].EventId||t.Text!=material.History[i].Text||t.Revision!=material.History[i].Revision).Any())return false;
        return B1InputsPermitted(owner,query,material.HostFootprint.Concat(owner.ReviewSourceRecords.Values));
    }
    // Actual review transcript sources stay registered across a host refresh.
    internal bool SemanticProtocolPermitted(DialogueRequest owner,string query)
    {
        using var timing=PreparationTiming.Measure("owner.protocol_check");
        if(!B1ProtocolInputsPermitted(owner,query))return false;
        var state=owner.SemanticMaterial;if(state==null||state.ProtocolMessages.Count==0)return true;
        var refs=state.Retired.SelectMany(a=>a.Sources).Concat(SemanticRequestMaterial.Audit(owner.B1HostFootprint.Concat(state.Current?.History??Array.Empty<MemoryTurn>())))
            .GroupBy(r=>r.Kind+":"+r.EventId+":"+r.Hash).Select(g=>g.First());
        var values=new List<string>();
        void Strings(JsonElement element)
        {
            if(element.ValueKind==JsonValueKind.String)
            {
                string value=element.GetString()??"";values.Add(value);
                if(value.StartsWith('{')||value.StartsWith('[')){try{using var nested=JsonDocument.Parse(value);Strings(nested.RootElement);}catch(JsonException){ }}
            }
            else if(element.ValueKind==JsonValueKind.Object)foreach(var p in element.EnumerateObject())Strings(p.Value);
            else if(element.ValueKind==JsonValueKind.Array)foreach(var v in element.EnumerateArray())Strings(v);
        }
        foreach(var message in state.ProtocolMessages)Strings(JsonSerializer.SerializeToElement(message));
        foreach(var r in refs)
        {
            var current=B1Turn(r.Kind,r.EventId);
            bool permitted=current!=null&&SemanticTextHash(current.Text)==r.Hash&&!IsExperienceSuppressed(r.EventId)
                &&IsMentionAllowed(current)&&IsNativeSupplyAllowed(current)&&!SuppressedTextMatches(current.Text);
            if(permitted)continue;
            // Inspect actual, locatable ID/full-body evidence, never infer that
            // every assistant message is contaminated because a model saw B1.
            if(values.Any(v=>v==r.EventId||v.Contains("utterance:"+r.EventId,StringComparison.Ordinal)
                ||v.Length==r.Length&&SemanticTextHash(v)==r.Hash))return false;
            if(current!=null&&SemanticTextHash(current.Text)==r.Hash&&current.Text.Length>0
                &&values.Any(v=>v.Contains(current.Text,StringComparison.Ordinal)))return false;
        }
        // CaptureB1 may supply additional material that never entered the host
        // footprint. Inspect only bodies actually shown in a sent packet, not
        // hidden dependencies or bare request-local short references. Reuse
        // the existing packet, including retired packets; do not copy withdrawn
        // plaintext into a new audit/store. B1 refs require their own resolver.
        if(owner.B1WasSupplied)
        {
            bool Carries(string body)=>body.Length>0&&values.Any(v=>v.Contains(body,StringComparison.Ordinal));
            var packets=owner.B1RetiredPackets.AsEnumerable();
            if(owner.B1Packet is { } active)packets=packets.Append(active);
            foreach(var material in packets.SelectMany(p=>p.Materials))
            {
                if(Carries(material.Text)&&!ResolveB1(material.Ref with{Purpose="permission"},query).Found)return false;
                foreach(var fragment in material.DisplayedReplyFragments??Array.Empty<B1Delivery>())
                    if(Carries(fragment.Text)&&!ResolveB1(new B1Ref("utterance",fragment.EventId,B1Hash(fragment.Text),"permission"),query).Found)return false;
            }
        }
        return true;
    }
}
