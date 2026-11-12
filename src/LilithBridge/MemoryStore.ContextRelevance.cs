using System.Text;
using System.Text.Json;

namespace LilithBridge;

internal sealed partial class MemoryStore
{
    internal static int SemanticCodePointOffset(string text,int offset)
    {int at=0;for(int n=0;n<offset&&at<text.Length;n++){if(char.IsHighSurrogate(text[at])&&at+1<text.Length&&char.IsLowSurrogate(text[at+1]))at+=2;else at++;}return at;}
    internal static string RelevanceContextHash(IEnumerable<MemoryTurn> history)=>SemanticTextHash(JsonSerializer.Serialize(history));
    internal ContextRelevanceSnapshot CaptureContextRelevance(string owner,long generation,string query,BridgeConfig config,SemanticRecallResult? semantic)
    {lock(_experienceLock)return CaptureContextRelevanceCore(owner,generation,query,config,semantic);}
    private ContextRelevanceSnapshot CaptureContextRelevanceCore(string owner,long generation,string query,BridgeConfig config,SemanticRecallResult? semantic)
    {
        using var mention=BeginMentionRequest(query,true);
        using var native=BeginNativeDialogueRequest(query,true,config);
        var history=GetRequestHistorySelection(config,RevalidateRequestHistory(GetRequestHistory(config))).Select(t=>t.Copy()).ToArray();
        var snapshot=new ContextRelevanceSnapshot {Owner=owner,Generation=generation,Root=_root,Session=_sessionId,Query=query,
            ContextHash=RelevanceContextHash(history),History=history,Model=semantic?.ModelRevision??"none",Index=semantic?.IndexVersion??"none"};
        var catalog=LogicalDialogueCatalog.Build(ReadExperiences());
        var ids=history.Select(t=>t.EventId).ToHashSet(StringComparer.Ordinal);
        var historyOwners=LogicalDialogueCatalog.Build(history).Units.Select(u=>u.Key).ToHashSet(StringComparer.Ordinal);
        var generatedReplies=GetQualifiedGeneratedReplies(query,config);
        var answerOwners=generatedReplies.Select(s=>new ContextRelevanceAnswerOwner(s,GeneratedMappedOriginals(s))).ToArray();
        snapshot.GeneratedIdentities=answerOwners.ToDictionary(a=>a.Source.Key,a=>a.Identity,StringComparer.Ordinal);
        foreach(var unit in LogicalDialogueCatalog.Build(history).Units)
        {
            var identity=ContextRelevanceViewIdentity.ForUnit(unit,answerOwners);
            foreach(var member in unit.Members)snapshot.HistoryIdentities[member.EventId]=identity;
        }
        snapshot.RecentGenerated=generatedReplies.Where(s=>historyOwners.Contains(s.LogicalOwnerKey)
            ||s.SourceUserId.Length>0&&ids.Contains(s.SourceUserId)).ToArray();
        var hits=semantic!=null&&SemanticResultMatches(query,semantic,SemanticDomain.All)
            ?semantic.Candidates.GroupBy(c=>c.EventId,StringComparer.Ordinal).ToDictionary(g=>g.Key,g=>g.OrderByDescending(c=>c.Score).First(),StringComparer.Ordinal)
            :new Dictionary<string,SemanticRecallCandidate>(StringComparer.Ordinal);
        string speaker=MemorySemantics.RequestedHistorySpeaker(query);
        var scope=MemorySemantics.ParseExperienceRecallQuery(query);
        string[] identifiers=MemorySemantics.RecallIdentifiers(query),quotes=UtteranceAttribution.LiteralQuotes(query);
        // These are ordinary association candidates, not an exact-quote lookup.
        // Quotes, names and requested speakers inform the judge; they cannot
        // erase differently worded past exchanges before it sees them.
        bool Allowed(LogicalDialogueUnit u)=>u.Members.All(t=>t.DeliveryState is "received" or "delivered"&&t.Role is "user" or "assistant"
            &&IsMentionAllowed(t)&&IsNativeSupplyAllowed(t));
        MemoryTurn[] Provenance(IEnumerable<string> sources)=>sources.Where(id=>id.Length>0).Distinct(StringComparer.Ordinal)
            .Select(id=>catalog.FindByEvent(id)).Where(u=>u!=null&&u.Members.All(t=>IsMentionAllowed(t)&&IsNativeSupplyAllowed(t)))
            .SelectMany(u=>u!.Members).DistinctBy(t=>t.EventId).Select(t=>t.Copy()).ToArray();
        var items=new List<ContextRelevanceItem>();
        if(config.LongTermMemoryEnabled&&config.ExperienceArchiveEnabled&&_longTermEnabled&&_experienceArchiveEnabled
            &&!MemorySemantics.DeclinesArchiveRecall(query))
        {
            foreach(var unit in catalog.Units.Where(Allowed).Where(u=>!u.Members.All(t=>ids.Contains(t.EventId))))
            {
                string wire=unit.IsMessage?unit.Key:unit.AnchorEventId;
                hits.TryGetValue(wire,out var hit);
                var provenance=Provenance(new[]{unit.SourceEventId});
                var sourceUser=provenance.FirstOrDefault(t=>t.EventId==unit.SourceEventId&&t.Role=="user");
                items.Add(new() {Id=wire,Kind=unit.IsMessage?"message":"utterance",Version=unit.VersionFingerprint,
                    Text=unit.Text,Role=unit.Role,Origin=unit.Origin,Coverage=unit.Coverage,Timestamp=unit.Timestamp,Unit=unit,
                    Identity=ContextRelevanceViewIdentity.ForUnit(unit,answerOwners),
                    SourceUserId=sourceUser?.EventId??"",SourceUserText=sourceUser?.Text??"",SourceUserFingerprint=sourceUser==null?"":LogicalDialogueCatalog.RawFingerprint(sourceUser),
                    Provenance=provenance,HitStart=hit?.CharStart??DiscoveryHitOffset(unit.Text,query,null),HitEnd=hit?.CharEnd??0,DiscoveryScore=hit?.Score??double.NegativeInfinity});
            }
            foreach(var source in generatedReplies
                .Where(s=>!historyOwners.Contains(s.LogicalOwnerKey)&&!(s.SourceUserId.Length>0&&ids.Contains(s.SourceUserId))))
            {
                string wire="generated/"+source.Key;hits.TryGetValue(wire,out var hit);
                var provenance=Provenance(new[]{source.SourceUserId});
                var sourceUser=provenance.FirstOrDefault(t=>t.EventId==source.SourceUserId&&t.Role=="user");
                items.Add(new() {Id=wire,Kind="generated_reply",Version=source.VersionFingerprint,Text=source.BodyText,Role="assistant",Origin="generated_public_answer",
                    Coverage="received_public_text;not_all_confirmed_said;player_reading_unknown",Timestamp=source.Metadata.CapturedAtUtc,Generated=source,
                    Identity=snapshot.GeneratedIdentities[source.Key],
                    SourceUserId=source.SourceUserId,SourceUserText=sourceUser?.Text??"",SourceUserFingerprint=sourceUser==null?"":LogicalDialogueCatalog.RawFingerprint(sourceUser),
                    Provenance=provenance,HitStart=hit?.CharStart??DiscoveryHitOffset(source.BodyText,query,null),HitEnd=hit?.CharEnd??0,DiscoveryScore=hit?.Score??double.NegativeInfinity});
            }
            if(config.ReflectionEnabled&&config.ReflectionRecallInChat)
            foreach(var thought in GetReflectionNotes(int.MaxValue).Concat(GetSettledNotes(int.MaxValue)).Concat(GetReflectionLessons(int.MaxValue))
                .Where(t=>IsMentionAllowed(t)&&IsNativeSupplyAllowed(t)).DistinctBy(t=>t.EventId))
            {
                hits.TryGetValue(thought.EventId,out var hit);
                items.Add(new() {Id=thought.EventId,Kind=thought.Kind=="reflection.lesson"?"lesson":"note",Version=SemanticSourceVersion(thought),Text=thought.Text,
                    Role="assistant",Origin="retained_thought",Coverage="revisable_interpretation;not_player_speech",Timestamp=thought.Timestamp,Thought=thought.Copy(),
                    Identity=new("thought/"+thought.EventId,"thought/"+thought.EventId,"retained_thought",Array.Empty<string>(),Array.Empty<string>(),thought.SessionId),
                    Provenance=Provenance(thought.SourceEventIds),HitStart=hit?.CharStart??DiscoveryHitOffset(thought.Text,query,null),HitEnd=hit?.CharEnd??0,DiscoveryScore=hit?.Score??double.NegativeInfinity});
            }
        }
        // Reciprocal rank is discovery ordering only. Every rank remains a candidate;
        // the byte envelope is an explicit inspection capacity, never a relevance floor.
        var semanticOrder=items.OrderByDescending(c=>c.DiscoveryScore).ThenBy(c=>c.Id,StringComparer.Ordinal).ToArray();
        var lexicalOrder=items.OrderByDescending(c=>MemorySemantics.Similarity(query,c.Text+"\n"+c.SourceUserText)).ThenBy(c=>c.Id,StringComparer.Ordinal).ToArray();
        var ranks=semanticOrder.Select((c,i)=>(c.Id,Rank:i+1)).ToDictionary(x=>x.Id,x=>x.Rank,StringComparer.Ordinal);
        var lexRanks=lexicalOrder.Select((c,i)=>(c.Id,Rank:i+1)).ToDictionary(x=>x.Id,x=>x.Rank,StringComparer.Ordinal);
        var ordered=items.OrderByDescending(c=>(double.IsNegativeInfinity(c.DiscoveryScore)?0:1d/(60+ranks[c.Id]))+1d/(60+lexRanks[c.Id]))
            .ThenBy(c=>c.Id,StringComparer.Ordinal).ToArray();
        var presented=new List<ContextRelevanceItem>();snapshot.Discovered=ordered.Length;
        int limit=Math.Clamp(config.ContextRelevanceMaxUtf8Bytes,16384,1048576);
        snapshot.Id=new string('0',64);
        // Per-item JSON sizes are additive. Reserve scalar-count growth and then
        // verify the final bytes. Never serialize the whole growing pool N times.
        int used=Encoding.UTF8.GetByteCount(snapshot.Payload())+256;
        foreach(var item in ordered)
        {
            item.Key="m"+presented.Count;
            // A long record is inspectable around its actual discovery hit. Selection
            // always expands the full original, never this excerpt as speech.
            if(item.Text.Length<=12000)item.Excerpts=new[]{new ContextRelevanceExcerpt(0,item.Text)};
            else
            {
                int start=Math.Clamp(item.HitStart-600,0,Math.Max(0,item.Text.Length-3600));
                // UTF-16 boundaries must not split a surrogate pair.
                if(start>0&&char.IsLowSurrogate(item.Text[start]))start--;
                int count=Math.Min(3600,item.Text.Length-start);if(start+count<item.Text.Length&&char.IsHighSurrogate(item.Text[start+count-1]))count--;
                item.Excerpts=start==0?new[]{new ContextRelevanceExcerpt(0,item.Text[..count])}
                    :new[]{new ContextRelevanceExcerpt(0,item.Text[..Math.Min(600,item.Text.Length)]),new ContextRelevanceExcerpt(start,item.Text.Substring(start,count))};
            }
            int bytes=Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(item.Wire(),DialogueMaterialEnvelope.DataJson))+1;
            if(used+bytes>limit){snapshot.CapacityDeferred++;continue;}
            presented.Add(item);used+=bytes;
        }
        snapshot.Candidates=presented.ToArray();
        if(Encoding.UTF8.GetByteCount(snapshot.Payload())>limit){snapshot.Status="current_context_exceeds_inspection_budget";snapshot.Candidates=Array.Empty<ContextRelevanceItem>();snapshot.CapacityDeferred=items.Count;}
        snapshot.Id=SemanticTextHash(JsonSerializer.Serialize(new {owner,generation,query,root=_root,session=_sessionId,snapshot.ContextHash,snapshot.Model,snapshot.Index,
            owner_view_schema=ContextRelevanceViewIdentity.Schema,
            history_views=snapshot.HistoryIdentities.OrderBy(p=>p.Key,StringComparer.Ordinal).Select(p=>new{event_id=p.Key,identity=p.Value.Wire()}).ToArray(),
            recent_generated=snapshot.RecentGenerated.Select(s=>new{s.Key,s.VersionFingerprint}).ToArray(),
            candidates=snapshot.Candidates.Select(c=>new{c.Key,c.Id,c.Version,identity=c.Identity?.Wire(),hash=SemanticTextHash(c.Text),c.SourceUserFingerprint,provenance=c.Provenance.Select(LogicalDialogueCatalog.RawFingerprint)}).ToArray()}));
        return snapshot;
    }
    internal bool ContextRelevanceSourcesCurrent(ContextRelevanceSnapshot snapshot,BridgeConfig config,bool selectedOnly=false,ContextRelevancePlan? plan=null)
    {lock(_experienceLock)return ContextRelevanceSourcesCurrentCore(snapshot,config,selectedOnly,plan);}
    private bool ContextRelevanceSourcesCurrentCore(ContextRelevanceSnapshot snapshot,BridgeConfig config,bool selectedOnly,ContextRelevancePlan? plan)
    {
        if(snapshot.Root!=_root||snapshot.Session!=_sessionId)return false;
        using var mention=BeginMentionRequest(snapshot.Query,true);using var native=BeginNativeDialogueRequest(snapshot.Query,true,config);
        var history=GetRequestHistorySelection(config,RevalidateRequestHistory(GetRequestHistory(config)));
        if(RelevanceContextHash(history)!=snapshot.ContextHash)return false;
        foreach(var source in snapshot.RecentGenerated)
            if(GetExactGeneratedReply(source.Key,snapshot.Query,config) is not {} live||live.VersionFingerprint!=source.VersionFingerprint)return false;
        var catalog=LogicalDialogueCatalog.Build(ReadExperiences());
        bool RawCurrent(MemoryTurn old)=>catalog.FindByEvent(old.EventId)?.Members.FirstOrDefault(t=>t.EventId==old.EventId) is {} t
            &&LogicalDialogueCatalog.RawFingerprint(t)==LogicalDialogueCatalog.RawFingerprint(old)&&IsMentionAllowed(t)&&IsNativeSupplyAllowed(t);
        var thoughts=GetReflectionNotes(int.MaxValue).Concat(GetSettledNotes(int.MaxValue)).Concat(GetReflectionLessons(int.MaxValue))
            .GroupBy(t=>t.EventId,StringComparer.Ordinal).ToDictionary(g=>g.Key,g=>g.First(),StringComparer.Ordinal);
        foreach(var item in selectedOnly&&plan!=null?plan.Selected:snapshot.Candidates)
        {
            if(item.Unit is {} unit&&(catalog.FindByKey(unit.Key) is not {} current||current.VersionFingerprint!=unit.VersionFingerprint||!unit.Members.All(RawCurrent)))return false;
            if(item.Generated is {} generated&&(GetExactGeneratedReply(generated.Key,snapshot.Query,config) is not {} live||live.VersionFingerprint!=generated.VersionFingerprint))return false;
            if(item.Thought is {} thought&&(!thoughts.TryGetValue(thought.EventId,out var liveThought)
                ||LogicalDialogueCatalog.RawFingerprint(liveThought)!=LogicalDialogueCatalog.RawFingerprint(thought)||!IsMentionAllowed(liveThought)||!IsNativeSupplyAllowed(liveThought)))return false;
            if(!item.Provenance.All(RawCurrent))return false;
        }
        return true;
    }
    internal SemanticRecallResult ApplyContextRelevance(SemanticRecallResult result,ContextRelevancePlan plan)
    {
        if(result.QueryId!=plan.Snapshot.Owner||result.Generation!=plan.Snapshot.Generation
            ||result.QueryHash!=SemanticTextHash(plan.Snapshot.Query)
            ||result.DataRoot.Length>0&&result.DataRoot!=plan.Snapshot.Root
            ||result.SessionId.Length>0&&result.SessionId!=plan.Snapshot.Session)
            throw new SemanticMaterialException("context_relevance_owner_mismatch");
        var hits=result.Candidates.GroupBy(c=>c.EventId,StringComparer.Ordinal).ToDictionary(g=>g.Key,g=>g.First(),StringComparer.Ordinal);
        var selected=plan.Selected.ToArray();
        // Exact original questions and thought sources are provenance, not fresh
        // relevance anchors. No sequence-neighbour or timestamp family inference.
        var catalog=LogicalDialogueCatalog.Build(ReadExperiences());
        var provenance=selected.SelectMany(i=>i.Provenance).Select(t=>catalog.FindByEvent(t.EventId)).Where(u=>u!=null).DistinctBy(u=>u!.Key).ToArray();
        // Keep original typed discovery for separate B1 and explicit-thought consumers.
        var allowed=result.Candidates.Where(c=>c.Kind is not ("utterance" or "message" or "generated_reply")).ToList();
        foreach(var item in selected.Where(i=>i.Unit!=null))
            allowed.Add(hits.TryGetValue(item.Id,out var hit)?hit:new(item.Id,SemanticTextHash(item.Text),0,0,item.Text.Length,item.Kind,item.Unit!.IsMessage?item.Unit.VersionFingerprint:SemanticSourceVersion(item.Unit.Members[0])));
        foreach(var unit in provenance)
        {
            string id=unit!.IsMessage?unit.Key:unit.AnchorEventId;
            if(allowed.All(c=>c.EventId!=id))allowed.Add(new(id,unit.TextHash,0,0,unit.Text.Length,unit.IsMessage?"message":"utterance",unit.IsMessage?unit.VersionFingerprint:SemanticSourceVersion(unit.Members[0])));
        }
        return result with {Candidates=allowed.ToArray(),RelevancePlan=plan,Unavailable="",Domains=SemanticDomain.All,
            IndexVersion=result.IndexVersion.Length>0?result.IndexVersion:"relevance-local",ModelRevision=SemanticRecallWorker.ModelRevision};
    }
}
