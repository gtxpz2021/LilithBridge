using System.Text;
using System.Text.Json;

namespace LilithBridge;

internal sealed partial class MemoryStore
{
 // Search selection is unchanged. These are source-order reading neighbours,
 // not new search hits, confirmed events or verified native delivery.
 private LegacyArchivePacket CompleteLegacyArchiveContext(LegacyArchivePacket packet,LegacyArchiveCatalog catalog,
  Dictionary<string,byte[]> bytes)
 {
  var args=packet.Query;
  if(args.Query.Length==0||args.Id.Length>0||args.Offset>0||args.Cursor.Length>0||packet.Records.Count==0)return packet;
  var contexts=new List<LegacyArchiveView>();var gaps=new List<LegacyArchiveGap>();bool truncated=false;
  var omitted=catalog.Omissions.Select(g=>g.Id).ToHashSet(StringComparer.Ordinal);
  var valid=catalog.Entries.Where(e=>LegacyIdentityError(e).Length==0&&!omitted.Contains(e.Id)).ToArray();
  var ids=valid.GroupBy(e=>e.Id).Where(g=>g.Count()==1).ToDictionary(g=>g.Key,g=>g.Single(),StringComparer.Ordinal);
  var positions=valid.GroupBy(e=>(e.Family,e.Source.Path,e.Ordinal)).ToDictionary(g=>g.Key,g=>g.ToArray());
  var selected=packet.Records.Select(v=>v.Id).ToHashSet(StringComparer.Ordinal);
  var roots=packet.Records.Where(v=>ids.ContainsKey(v.Id)).Select(v=>(Entry:ids[v.Id],Anchor:v.Id)).ToList();
  var from=LegacyTime(args.After.Length==0?null:args.After);var until=LegacyTime(args.Before.Length==0?null:args.Before);
  var checkedRows=new Dictionary<string,(bool Allowed,string Observed)>(StringComparer.Ordinal);
  LegacyArchivePacket Draft()=>packet with {RelatedContext=contexts,ContextOmissions=gaps,ContextTruncated=truncated};
  bool Fits()=>Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(Draft(),LegacyJson))<=packet.MaterialBytes;
  void Note(string anchor,string reason)
  {
   truncated=true;if(gaps.Count>=8||gaps.Any(g=>g.Id==anchor&&g.Reason==reason))return;
   gaps.Add(new(anchor,reason));if(!Fits())gaps.RemoveAt(gaps.Count-1);
  }
  bool Allowed(LegacyArchiveEntry e,string anchor,out string observed)
  {
   observed="";var time=LegacyTime(e.Timestamp);
   if(from!=null&&(time==null||time<from)||until!=null&&(time==null||time>=until))return false;
   if(!checkedRows.TryGetValue(e.Id,out var row))
   {
    string reason=LegacyPermissionError(e);string seen="";
    if(reason.Length==0)reason=LegacySourceError(e,bytes,out seen);
    row=(reason.Length==0,seen);checkedRows[e.Id]=row;
   }
   observed=row.Observed;if(!row.Allowed)Note(anchor,"context_withheld_or_source_changed");return row.Allowed;
  }
  LegacyArchiveEntry? Neighbour(LegacyArchiveEntry e,int delta,string anchor)
  {
   if(!positions.TryGetValue((e.Family,e.Source.Path,e.Ordinal+delta),out var rows))return null;
   if(rows.Length!=1||!ids.ContainsKey(rows[0].Id)){Note(anchor,"context_position_ambiguous");return null;}
   return Allowed(rows[0],anchor,out _)?rows[0]:null;
  }
  var plan=new List<(LegacyArchiveEntry Entry,string Anchor,string Relation)>();
  var planned=new HashSet<string>(selected,StringComparer.Ordinal);
  void Plan(LegacyArchiveEntry e,string anchor,string relation)
  {if(planned.Add(e.Id))plan.Add((e,anchor,relation));}
  // The production archive had no ±1 context stage. Reuse the same bounded
  // source-order neighbourhood as the offline packet, inside this read only.
  var basic=new List<(LegacyArchiveEntry Entry,string Anchor)>(roots);
  foreach(var root in roots)
   foreach(int delta in new[]{-1,1})
    if(Neighbour(root.Entry,delta,root.Anchor) is {} neighbour)
    {Plan(neighbour,root.Anchor,"source_order_neighbour");basic.Add((neighbour,root.Anchor));}
  var visited=new HashSet<string>(StringComparer.Ordinal);
  // Finish included user requests before optional missing prefixes. No
  // question words, activity names or expected target IDs enter this rule.
  foreach(var item in basic.OrderBy(x=>x.Entry.Role=="user"?0:1))
  {
   var start=item.Entry;int walked=0;
   while(start.Role=="assistant"&&walked++<24&&Neighbour(start,-1,item.Anchor) is {} previous)start=previous;
   if(!visited.Add(start.Id))continue;
   Plan(start,item.Anchor,"exchange_completion");var current=start;walked=0;
   while(walked++<24&&Neighbour(current,1,item.Anchor) is {} next&&next.Role=="assistant")
   {Plan(next,item.Anchor,"exchange_completion");current=next;}
  }
  foreach(var item in plan)
  {
   if(contexts.Count>=12||packet.Records.Count+contexts.Count>=24){Note(item.Anchor,"context_record_limit");break;}
   var e=item.Entry;if(!Allowed(e,item.Anchor,out string observed))continue;
   int chars=Math.Min(1600,e.Text.Length);
   if(chars>0&&chars<e.Text.Length&&char.IsHighSurrogate(e.Text[chars-1]))chars--;
   LegacyArchiveView View(int length)=>new(e.Id,e.Version,e.Family,e.Ordinal,e.Role,e.Timestamp,e.TimestampBasis,e.Evidence,
    LegacyHash(e.Text),0,e.Text.Length,e.Text[..length],length<e.Text.Length?length:null,e.Source.Path,e.Source.Sha256,
    observed,observed!=e.Source.Sha256,e.Proofs.Select(p=>p.Kind).ToArray(),
    e.RawRecord.TryGetProperty("Kind",out var kind)?kind.GetString()??"unknown":"companion_chat",e.Source.Format,item.Anchor,item.Relation);
   contexts.Add(View(chars));
   while(!Fits()&&chars>0)
   {chars/=2;if(chars>0&&char.IsHighSurrogate(e.Text[chars-1]))chars--;contexts[^1]=View(chars);}
   if(!Fits()||chars==0&&e.Text.Length>0){contexts.RemoveAt(contexts.Count-1);Note(item.Anchor,"context_budget_omitted");}
   else if(chars<e.Text.Length)Note(item.Anchor,"context_body_partial");
  }
  // Optional context never displaces an original qualified search result.
  while(!Fits()&&gaps.Count>0)gaps.RemoveAt(gaps.Count-1);
  while(!Fits()&&contexts.Count>0){contexts.RemoveAt(contexts.Count-1);truncated=true;}
  return Draft();
 }
}
