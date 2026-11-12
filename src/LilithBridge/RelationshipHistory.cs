using System.Text;
using System.Text.Json;
namespace LilithBridge;

// Finite read-only projection. Limits apply to records, not a whole family.
internal sealed partial class MemoryStore
{
 private const int RelationshipHistoryBytes = 16384;
 private sealed record RelationshipHistoryCapture(JsonElement Projection,
  IReadOnlyList<MemoryTurn> Records,IReadOnlyList<DaySourceDependency> Versions);
 private sealed class ReflectionHistoryFamily
 {
  internal MemoryTurn Root = null!;
  internal string Lineage = "complete";
  internal bool NoteCountLimited, RawCountLimited;
  internal readonly List<MemoryTurn> Readings = new(), Notes = new(), Raw = new();
  internal readonly List<MemoryTurn> SuppliedReadings = new(), SuppliedNotes = new(), SuppliedRaw = new();
 }
 private RelationshipHistoryCapture CaptureRelationshipHistory(IReadOnlyList<MemoryTurn> roots,
  IReadOnlyList<MemoryTurn> suppliedEvidence,IReadOnlyList<MemoryTurn> suppliedNotes,
  IReadOnlyList<MemoryTurn> suppliedNoteHistory,IReadOnlyList<MemoryTurn> suppliedSettled)
 {
  var readable=CreateNoteHistoryPermissionEvaluator(out _);
  var index=_relationshipReadings.GroupBy(t=>t.EventId).ToDictionary(g=>g.Key,g=>g.ToArray());
  var noteIndex=_reflectionNotes.GroupBy(t=>t.EventId).ToDictionary(g=>g.Key,g=>g.ToArray());
   var rawIndex=GetExperienceValidationTurns().GroupBy(t=>t.EventId).ToDictionary(g=>g.Key,g=>g.ToArray());
   Func<MemoryTurn,bool>? isCurrent=null;
  var families=new List<ReflectionHistoryFamily>();
  foreach(var root in roots.OrderByDescending(t=>t.Timestamp).ThenBy(t=>t.EventId,StringComparer.Ordinal).Take(2))
  {
   if(root.SourceEventId.Length==0 || !readable("reading",root.EventId))continue;
   var f=new ReflectionHistoryFamily{Root=root};f.Readings.Add(root);
   var seen=new HashSet<string>{root.EventId};var child=root;
   while(child.SourceEventId.Length>0 && f.Readings.Count<3)
   {
    if(!index.TryGetValue(child.SourceEventId,out var parents)||parents.Length!=1||!seen.Add(child.SourceEventId))
    {f.Lineage="chain_broken";break;}
    var parent=parents[0];
    if(parent.Kind!=RelationshipReadingKind||parent.Origin!="reflection"||!IsSupersededLifecycle(parent)
      ||parent.RecallId!=root.RecallId||parent.Revision>=child.Revision)
    {f.Lineage="chain_broken";break;}
    // Retain the qualified prefix, without traversing denied body/link metadata.
    if(!readable("reading",parent.EventId)){f.Lineage="history_unavailable";break;}
    f.Readings.Add(parent);child=parent;
   }
   if(f.Lineage=="complete" && child.SourceEventId.Length>0)f.Lineage="ancestor_count_limit";
   foreach(string id in f.Readings.SelectMany(t=>t.SourceNoteIds).Distinct())
   {
    if(!noteIndex.TryGetValue(id,out var found)||found.Length!=1||!readable("note",id))continue;
    if(f.Notes.Count==6){f.NoteCountLimited=true;continue;}
    f.Notes.Add(found[0]);
   }
   foreach(string id in f.Readings.Concat(f.Notes).SelectMany(t=>t.SourceEventIds).Distinct())
   {
    if(!rawIndex.TryGetValue(id,out var found)||found.Length!=1||!readable("utterance",id))continue;
    if(f.Raw.Count==12){f.RawCountLimited=true;continue;}
    f.Raw.Add(found[0]);
   }
   families.Add(f);
  }
  object TextRef(string section,string field,MemoryTurn t)=>new{source_section=section,id_field=field,event_id=t.EventId};
  string NoteSection(MemoryTurn t)=>suppliedNotes.Any(x=>x.EventId==t.EventId)?"previous_notes"
    :suppliedNoteHistory.Any(x=>x.EventId==t.EventId)?"note_history.entries"
    :suppliedSettled.Any(x=>x.EventId==t.EventId)?"settled_notes":"";
  object Record(MemoryTurn t,IEnumerable<MemoryTurn> pool,string section,string field)
  {
   if(section.Length>0)return new{event_id=t.EventId,kind=t.Kind,revision=t.Revision,at=t.Timestamp,
     lifecycle=t.LifecycleState,revises=t.SourceEventId,current_qualification=AssessMaterialSupply(t,isCurrent??=CreateCurrentBasisEvaluator()).Reason,
    text_ref=TextRef(section,field,t)};
   return new{event_id=t.EventId,recall_id=t.RecallId,kind=t.Kind,revision=t.Revision,at=t.Timestamp,
    lifecycle=t.LifecycleState,validity=t.Validity,revises=t.SourceEventId,
    revised_by=pool.Where(x=>x.SourceEventId==t.EventId && readable(t.Kind==RelationshipReadingKind?"reading":"note",x.EventId)).Select(x=>x.EventId).ToArray(),
     current_qualification=AssessMaterialSupply(t,isCurrent??=CreateCurrentBasisEvaluator()).Reason,source_event_ids=t.SourceEventIds,
    source_note_ids=t.SourceNoteIds,source_day_ids=t.SourceDayIds,text=t.Text,
    source_context=DescribeSourceAttribution(t)};
  }
  object Utterance(MemoryTurn t)=>suppliedEvidence.Any(x=>x.EventId==t.EventId)
    ?new {event_id=t.EventId,speaker=t.Role,origin=t.Origin,delivery=t.DeliveryState,at=t.Timestamp,
     text_ref=TextRef("utterances","event_id",t)}
    :(object)new{event_id=t.EventId,speaker=t.Role,origin=t.Origin,delivery=t.DeliveryState,at=t.Timestamp,
     text=t.Text,reported_speech=UtteranceAttribution.Reported(t.Role,t.Text)};
  JsonElement Project()=>JsonSerializer.SerializeToElement(new {
   use="只读且可能不完整的历史，不增加引用或revises权限。解释属于各自写下的时点；原话只证明说过什么。current_qualification不是现在赞同。text_ref指向同一Source已供正文。coverage标明未展开范围，不补写缺失经过。可以维持、修订、不提或无新产物。",
   families=families.Select(f=>new{head_id=f.Root.EventId,
    readings=f.SuppliedReadings.OrderBy(t=>t.Revision).Select(t=>Record(t,_relationshipReadings,
     roots.Any(x=>x.EventId==t.EventId)?"previous_readings":"","reading_id")),
    notes=f.SuppliedNotes.OrderBy(t=>t.Timestamp).Select(t=>Record(t,_reflectionNotes,NoteSection(t),"note_id")),
    utterances=f.SuppliedRaw.OrderBy(t=>t.Timestamp).Select(Utterance),
    coverage=new{lineage=f.Lineage,reading_records_not_expanded=f.Readings.Count-f.SuppliedReadings.Count,
     note_records_not_expanded=f.Notes.Count-f.SuppliedNotes.Count,raw_records_not_expanded=f.Raw.Count-f.SuppliedRaw.Count,
     not_expanded_reason="byte_budget",note_count_limit=f.NoteCountLimited,raw_count_limit=f.RawCountLimited,
     note_ancestors_not_expanded=f.Notes.Any(t=>t.SourceEventId.Length>0&&!f.Notes.Any(n=>n.EventId==t.SourceEventId)),
     day_bodies_not_expanded=f.Readings.Concat(f.Notes).Any(t=>t.SourceDayIds.Length>0)}})},DialogueMaterialEnvelope.DataJson);
  // Reserve finite coverage metadata before allocating whole records.
  if(Encoding.UTF8.GetByteCount(Project().GetRawText())>RelationshipHistoryBytes)
   return new(JsonSerializer.SerializeToElement(new{families=Array.Empty<object>(),omissions=new[]{new{reason="metadata_byte_budget"}}}),Array.Empty<MemoryTurn>(),Array.Empty<DaySourceDependency>());
  // Round-robin layers across both families; skip large bodies, keep trying.
  for(int i=0;i<12;i++)
  foreach(int kind in new[]{0,1,2})
  foreach(var f in families)
  {
   var source=kind==0?f.Readings:kind==1?f.Notes:f.Raw;
   var target=kind==0?f.SuppliedReadings:kind==1?f.SuppliedNotes:f.SuppliedRaw;
   if(i>=source.Count)continue;
   target.Add(source[i]);
   if(Encoding.UTF8.GetByteCount(Project().GetRawText())>RelationshipHistoryBytes)target.RemoveAt(target.Count-1);
  }
  var captured=new Dictionary<string,(string Kind,MemoryTurn Turn)>();
  foreach(var f in families)
  {
   // Root owns visible coverage; unsupplied ancestors get no body footprint.
   captured["reading:"+f.Root.EventId]=("reading",f.Root.Copy());
   foreach(var t in f.SuppliedReadings)captured["reading:"+t.EventId]=("reading",t.Copy());
   foreach(var t in f.SuppliedNotes)captured["note:"+t.EventId]=("note",t.Copy());
   foreach(var t in f.SuppliedRaw)captured["utterance:"+t.EventId]=("utterance",t.Copy());
  }
  return new(Project(),captured.Values.Select(x=>x.Turn).ToArray(),captured.Values.Select(x=>DayDependency(x.Kind,x.Turn)).ToArray());
 }
 private bool RelationshipHistoryStillReadable(ReflectionRequestSnapshot request,out string reason)
 {
  reason="relationship_history_unavailable";
  if(request.RelationshipHistoryRecords.Count==0)return true;
  var readable=CreateNoteHistoryPermissionEvaluator(out _);
  var raw=GetExperienceValidationTurns().ToArray();
  foreach(var prior in request.RelationshipHistoryRecords)
  {
   string kind=prior.Kind==RelationshipReadingKind?"reading":prior.Kind=="reflection.note"?"note":"utterance";
   if(!readable(kind,prior.EventId))return false;
   var pool=kind=="reading"?_relationshipReadings:kind=="note"?_reflectionNotes:raw.AsEnumerable();
   var found=pool.Where(t=>t.EventId==prior.EventId).ToArray();
   if(found.Length!=1)return false;
   var version=request.RelationshipHistoryVersions.FirstOrDefault(d=>d.Kind==kind&&d.EventId==prior.EventId);
   if(version==null||found[0].RecallId!=prior.RecallId||DayDependency(kind,found[0])!=version)
   {reason="relationship_history_changed";return false;}
  }
  return true;
 }
}
