using System.Text;
using System.Text.Json;
using System.Security.Cryptography;

namespace LilithBridge;

// Transient read domain; never raw evidence, B1 write refs, or a new stored thought.
internal sealed record DayArtifactReadItem(MemoryTurn Turn,string SourceKind,string Version,
    string OwnerId,string Text,int Offset=0,int? NextOffset=null,string NextCursor="");
internal sealed record DayArtifactReadPacket(string Status,string Reason,string RootVersion,
    string Query,ChatHistoryReadOptions Options,IReadOnlyList<MemoryTurn> Roots,
    IReadOnlyList<DayArtifactReadItem> Items,IReadOnlyList<ChatHistoryOmission> Omissions,
    string NextCursor="",bool ScopeComplete=true,string DataVersion="");

internal sealed partial class MemoryStore
{
    private static string ArtifactDigest(string text)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private string ArtifactVersion(string kind,MemoryTurn t)=>SourceVersion(kind,t).Fingerprint;
    private static string ArtifactPacketDigest(DayArtifactReadPacket p)=>ArtifactDigest(JsonSerializer.Serialize(new {
        p.Status,p.Reason,p.RootVersion,p.Query,p.Options,p.NextCursor,p.ScopeComplete,
        roots=p.Roots.Select(t=>new{t.EventId,t.Text,t.Revision,t.Kind,t.SourceEventIds,t.SourceNoteIds,t.SourceDayIds,t.SourceDependencies,t.ContextDependencies}),
        items=p.Items.Select(i=>new{i.Turn.EventId,i.SourceKind,i.Version,i.OwnerId,i.Text,i.Offset,i.NextOffset,i.NextCursor}),p.Omissions
    }));
    private static DayArtifactReadPacket SealArtifacts(DayArtifactReadPacket p)=>p with{DataVersion=ArtifactPacketDigest(p)};
    private Dictionary<string,MemoryTurn> ArtifactSourceLookup()
    {
        var result=new Dictionary<string,MemoryTurn>(StringComparer.Ordinal);
        foreach(var t in ReadExperiences())result["utterance:"+t.EventId]=t;
        foreach(var t in GetReflectionNotes(int.MaxValue).Concat(GetSettledNotes(int.MaxValue)))result["note:"+t.EventId]=t;
        foreach(var t in GetDayArtifacts(int.MaxValue))result["day:"+t.EventId]=t;
        return result;
    }
    private static string[] ArtifactDeclaredRefs(MemoryTurn t)=>t.SourceEventIds.Select(id=>"utterance:"+id)
        .Concat(t.SourceNoteIds.Select(id=>"note:"+id)).Concat(t.SourceDayIds.Select(id=>"day:"+id)).Distinct().ToArray();
    private static bool ArtifactCursor(string cursor,string prefix,string digest,out int offset)
    {
        offset=0;if(cursor.Length==0)return true;
        var parts=cursor.Split(':');return parts.Length==3&&parts[0]==prefix&&parts[2]==digest
            &&int.TryParse(parts[1],out offset)&&offset>=0;
    }
    internal DayArtifactReadPacket ReadDayArtifacts(string rootVersion,string query,ChatHistoryReadOptions options,
        string requestText,BridgeConfig config)
    {
        lock(_experienceLock)
        {
            var priorMention=_mentionReadPolicy.Value;var priorNative=_nativeDialoguePolicy.Value;
            _mentionReadPolicy.Value=null;_nativeDialoguePolicy.Value=null;
            try
            {
            DayArtifactReadPacket Empty(string status,string reason)=>SealArtifacts(new(status,reason,rootVersion,query,options,
                Array.Empty<MemoryTurn>(),Array.Empty<DayArtifactReadItem>(),Array.Empty<ChatHistoryOmission>(),ScopeComplete:false));
            if(!_dreamsEnabled||!config.MemoryDreamsEnabled||!ChatHistoryReadAllowed(requestText,config,out _))return Empty("unavailable","disabled_or_out_of_scope");
            if(_listLoadStates.TryGetValue("v4-native-dreams.json",out var load)&&load==ListLoadState.ReadFailed)
                return Empty("unavailable","artifact_archive_unreadable");
            using var mention=BeginMentionRequest(requestText,true);
            using var native=BeginNativeDialogueRequest(requestText,true,config);
            var all=GetDayArtifacts(int.MaxValue).OrderByDescending(t=>t.Timestamp).ThenBy(t=>t.EventId,StringComparer.Ordinal).ToArray();
            var lookup=ArtifactSourceLookup();
            var roots=new List<MemoryTurn>();var items=new List<DayArtifactReadItem>();var omissions=new List<ChatHistoryOmission>();
            string next="";bool complete=true;
            void Add(MemoryTurn turn,string kind,string owner,int start=0,int chars=700)
            {
                start=Math.Clamp(start,0,turn.Text.Length);int end=Math.Min(turn.Text.Length,start+chars);
                if(start>0&&start<turn.Text.Length&&char.IsLowSurrogate(turn.Text[start]))start--;
                if(end>start&&end<turn.Text.Length&&char.IsHighSurrogate(turn.Text[end-1]))end--;
                string version=ArtifactVersion(kind,turn);int? after=end<turn.Text.Length?end:null;
                items.Add(new(turn.Copy(),kind,version,owner,turn.Text.Substring(start,end-start),start,after,
                    after.HasValue?"artifact-record:"+after.Value+":"+ArtifactDigest(owner+"|"+kind+"|"+turn.EventId+"|"+version):""));
            }
            if(rootVersion.Length==0)
            {
                if(options.Scope!="focus")return Empty("unavailable","artifact_root_required");
                // Optional literal clue; no scoring, inferred interest or semantic exclusion.
                var selected=all.Where(t=>query.Length==0||t.Text.Contains(query,StringComparison.OrdinalIgnoreCase)).ToArray();
                string digest=ArtifactDigest(query+"|"+string.Join("|",selected.Select(t=>t.EventId+":"+ArtifactVersion("day",t))));
                if(!ArtifactCursor(options.Cursor,"artifacts",digest,out int offset))return Empty("unavailable","artifact_cursor_changed");
                foreach(var t in selected.Skip(offset).Take(3)){roots.Add(t.Copy());Add(t,"day",t.EventId);}
                if(offset+roots.Count<selected.Length){next="artifacts:"+(offset+roots.Count)+":"+digest;complete=false;}
            }
            else
            {
                var root=all.SingleOrDefault(t=>t.EventId==rootVersion);
                if(root==null)return Empty("unavailable","artifact_unavailable");
                roots.Add(root.Copy());var refs=ArtifactDeclaredRefs(root);
                if(options.Scope=="record")
                {
                    string key=options.Reference;
                    if(key!="day:"+rootVersion&&!refs.Contains(key))return Empty("unavailable","not_a_declared_artifact_source");
                    if(!lookup.TryGetValue(key,out var t)){omissions.Add(new(key.Split(':')[0],key[(key.IndexOf(':')+1)..],"source_unavailable"));}
                    else
                    {
                        string kind=key[..key.IndexOf(':')];string version=ArtifactVersion(kind,t);
                        string digest=ArtifactDigest(rootVersion+"|"+kind+"|"+t.EventId+"|"+version);
                        if(!ArtifactCursor(options.Cursor,"artifact-record",digest,out int start)||start!=options.Offset)
                            return Empty("unavailable","artifact_record_cursor_changed");
                        if(start>0&&options.Cursor.Length==0)return Empty("unavailable","artifact_record_cursor_required");
                        if(options.Find.Length>0)
                        {
                            int found=t.Text.IndexOf(options.Find,StringComparison.OrdinalIgnoreCase);
                            if(found<0)return Empty("not_found","literal_not_found_in_record");
                            start=Math.Max(0,found-Math.Min(1200,options.BodyChars/2));
                        }
                        Add(t,kind,rootVersion,start,options.BodyChars);
                    }
                }
                else
                {
                    if(options.Scope=="focus")Add(root,"day",rootVersion);
                    var permitted=refs.Where(lookup.ContainsKey).ToArray();
                    foreach(var key in refs.Where(k=>!lookup.ContainsKey(k)))omissions.Add(new(key[..key.IndexOf(':')],key[(key.IndexOf(':')+1)..],"source_unavailable"));
                    string digest=ArtifactDigest(ArtifactVersion("day",root)+"|"+string.Join("|",permitted.Select(k=>k+":"+ArtifactVersion(k[..k.IndexOf(':')],lookup[k]))));
                    if(!ArtifactCursor(options.Cursor,"artifact-sources",digest,out int offset))return Empty("unavailable","artifact_source_cursor_changed");
                    int count=options.Scope=="focus"?11:12;
                    foreach(var key in permitted.Skip(offset).Take(count))Add(lookup[key],key[..key.IndexOf(':')],rootVersion);
                    if(offset+Math.Min(count,Math.Max(0,permitted.Length-offset))<permitted.Length)
                    {next="artifact-sources:"+(offset+count)+":"+digest;complete=false;}
                }
            }
            if(items.Count==0)return Empty("not_found","no_qualified_artifact_material");
            return SealArtifacts(new("ok","qualified_reading_material",rootVersion,query,options,roots,items,omissions,next,complete));
            }
            finally{_mentionReadPolicy.Value=priorMention;_nativeDialoguePolicy.Value=priorNative;}
        }
    }
    internal bool TryUseDayArtifacts(DayArtifactReadPacket packet,string query,BridgeConfig config,Func<bool> use)
    {
        lock(_experienceLock)
        {
            var priorMention=_mentionReadPolicy.Value;var priorNative=_nativeDialoguePolicy.Value;
            _mentionReadPolicy.Value=null;_nativeDialoguePolicy.Value=null;
            try
            {
            if(packet.Items.Count==0||packet.DataVersion!=ArtifactPacketDigest(packet)||!_dreamsEnabled||!config.MemoryDreamsEnabled
                ||!ChatHistoryReadAllowed(query,config,out _))return false;
            using var mention=BeginMentionRequest(query,true);using var native=BeginNativeDialogueRequest(query,true,config);
            var lookup=ArtifactSourceLookup();
            foreach(var root in packet.Roots)
                if(!lookup.TryGetValue("day:"+root.EventId,out var now)||ArtifactVersion("day",now)!=ArtifactVersion("day",root))return false;
            foreach(var i in packet.Items)
            {
                var root=packet.Roots.SingleOrDefault(r=>r.EventId==i.OwnerId);
                if(root==null||i.SourceKind+":"+i.Turn.EventId!="day:"+root.EventId
                    &&!ArtifactDeclaredRefs(root).Contains(i.SourceKind+":"+i.Turn.EventId))return false;
                if(!lookup.TryGetValue(i.SourceKind+":"+i.Turn.EventId,out var now)||ArtifactVersion(i.SourceKind,now)!=i.Version
                    ||i.Offset<0||i.Offset+i.Text.Length>now.Text.Length||now.Text.Substring(i.Offset,i.Text.Length)!=i.Text)return false;
            }
            return use();
            }
            finally{_mentionReadPolicy.Value=priorMention;_nativeDialoguePolicy.Value=priorNative;}
        }
    }
    internal static DayArtifactReadPacket ProjectDayArtifacts(DayArtifactReadPacket packet,int maxItems,int chars)
    {
        var selected=packet.Items.Take(maxItems).Select(i=>{
            int length=Math.Min(chars,i.Text.Length);if(length>0&&length<i.Text.Length&&char.IsHighSurrogate(i.Text[length-1]))length--;
            int? next=i.Offset+length<i.Turn.Text.Length?i.Offset+length:null;
            return i with{Text=i.Text[..length],NextOffset=next,NextCursor=next.HasValue?
                "artifact-record:"+next.Value+":"+ArtifactDigest(i.OwnerId+"|"+i.SourceKind+"|"+i.Turn.EventId+"|"+i.Version):""};
        }).ToArray();
        var owners=selected.Select(i=>i.OwnerId).ToHashSet();
        var omitted=packet.Omissions.Concat(packet.Items.Skip(maxItems).Select(i=>new ChatHistoryOmission(i.SourceKind,i.Turn.EventId,"request_budget_omitted"))).ToArray();
        return SealArtifacts(packet with{Roots=packet.Roots.Where(r=>owners.Contains(r.EventId)).ToArray(),Items=selected,Omissions=omitted,ScopeComplete=false});
    }
}
