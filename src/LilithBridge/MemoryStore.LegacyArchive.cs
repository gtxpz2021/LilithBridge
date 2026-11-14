using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LilithBridge;

internal sealed record LegacyArchiveQuery(string Query="",string After="",string Before="",string Cursor="",int Limit=12,string Id="",int Offset=0);
internal sealed class LegacyArchiveSource { public string Path{get;set;}="";public string Sha256{get;set;}="";public string Format{get;set;}=""; }
internal sealed class LegacyArchiveProof { public string Path{get;set;}="";public string Sha256{get;set;}="";public int Line{get;set;}public JsonElement Record{get;set;}public string Kind{get;set;}=""; }
internal sealed class LegacyArchiveEntry
{
 public string Id{get;set;}="";public string Version{get;set;}="";public string Family{get;set;}="";public int Ordinal{get;set;}
 public string Role{get;set;}="";public string Text{get;set;}="";public string? Timestamp{get;set;}
 public string TimestampBasis{get;set;}="";public string Evidence{get;set;}="";public JsonElement RawRecord{get;set;}
 public LegacyArchiveSource Source{get;set;}=new();public LegacyArchiveProof[] Proofs{get;set;}=Array.Empty<LegacyArchiveProof>();
 public string[] Aliases{get;set;}=Array.Empty<string>();
}
internal sealed class LegacyArchiveCatalog { public int Schema{get;set;}public LegacyArchiveEntry[] Entries{get;set;}=Array.Empty<LegacyArchiveEntry>();public LegacyArchiveGap[] Omissions{get;set;}=Array.Empty<LegacyArchiveGap>(); }
internal sealed record LegacyArchiveView(string Id,string Version,string Family,int Ordinal,string Role,string? Timestamp,string TimestampBasis,
 string Evidence,string TextHash,int Offset,int TotalChars,string Text,int? NextOffset,string SourcePath,string SourceOriginalHash,string SourceObservedHash,
 bool SourceContainerChanged,string[] ProofKinds,string RecordKind,string SourceFormat,string ContextAnchor="",string ContextRelation="");
internal sealed record LegacyArchiveGap(string Id,string Reason);
internal sealed record LegacyArchivePacket(string Status,LegacyArchiveQuery Query,int MaterialBytes,string CatalogHash,
 IReadOnlyList<LegacyArchiveView> Records,IReadOnlyList<LegacyArchiveGap> Omissions,int Matched,int Eligible,string NextCursor,string DataVersion,int OmissionsNotListed=0,
 IReadOnlyList<LegacyArchiveView>? RelatedContext=null,IReadOnlyList<LegacyArchiveGap>? ContextOmissions=null,bool ContextTruncated=false);

internal sealed partial class MemoryStore
{
 private string LegacyArchiveRoot=>Path.Combine(_root,"legacy-archive-v1");
 internal bool HasLegacyArchive=>File.Exists(Path.Combine(LegacyArchiveRoot,"catalog.json"));
 internal const int LegacyDefaultBytes=48000;
 private static string LegacyHash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
 private static string LegacyHash(string text)=>LegacyHash(Encoding.UTF8.GetBytes(text));
 private static readonly JsonSerializerOptions LegacyJson=new(){Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping};
 private static string Canonical(JsonElement e)=>e.ValueKind switch {
  JsonValueKind.Object=>"{"+string.Join(",",e.EnumerateObject().OrderBy(p=>p.Name,StringComparer.Ordinal).Select(p=>JsonSerializer.Serialize(p.Name,LegacyJson)+":"+Canonical(p.Value)))+"}",
  JsonValueKind.Array=>"["+string.Join(",",e.EnumerateArray().Select(Canonical))+"]",
  JsonValueKind.String=>LegacyJsonString(e.GetString()??""),_=>e.GetRawText()};
 private static string LegacyJsonString(string value)
 {
  var b=new StringBuilder("\"");foreach(char c in value)b.Append(c switch{'"'=>"\\\"",'\\'=>"\\\\",'\b'=>"\\b",'\f'=>"\\f",'\n'=>"\\n",'\r'=>"\\r",'\t'=>"\\t",_=>c<32?"\\u"+((int)c).ToString("x4"):c.ToString()});return b.Append('"').ToString();
 }
 private string LegacyPath(string relative)
 {
  if(Path.IsPathRooted(relative))throw new InvalidDataException("absolute_source_path");
  string root=Path.GetFullPath(LegacyArchiveRoot)+Path.DirectorySeparatorChar;
  string full=Path.GetFullPath(Path.Combine(root,relative));
  if(!full.StartsWith(root,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("source_path_escape");
  for(string? p=full;p!=null&&p.TrimEnd(Path.DirectorySeparatorChar)!=root.TrimEnd(Path.DirectorySeparatorChar);p=Path.GetDirectoryName(p))
   if(File.Exists(p)||Directory.Exists(p))if((File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("source_reparse_point");
  return full;
 }
 private (LegacyArchiveCatalog Catalog,string Hash) LoadLegacyArchive()
 {
  string path=Path.Combine(LegacyArchiveRoot,"catalog.json");var info=new FileInfo(path);
  if(!info.Exists||info.Length>4*1024*1024)throw new InvalidDataException("catalog_missing_or_large");
  byte[] b=File.ReadAllBytes(path);using var document=JsonDocument.Parse(b);var root=document.RootElement;
  if(root.ValueKind!=JsonValueKind.Object||!root.TryGetProperty("Schema",out var schema)||!schema.TryGetInt32(out int schemaValue)||schemaValue!=1
    ||!root.TryGetProperty("Entries",out var entries)||entries.ValueKind!=JsonValueKind.Array||entries.GetArrayLength()>10000)throw new InvalidDataException("catalog_schema_invalid");
  var values=new List<LegacyArchiveEntry>();int slot=0;
  foreach(var item in entries.EnumerateArray())
  {
   slot++;try{values.Add(item.Deserialize<LegacyArchiveEntry>()??new LegacyArchiveEntry{Id="invalid-slot:"+slot});}
   catch(JsonException){string id=item.ValueKind==JsonValueKind.Object&&item.TryGetProperty("Id",out var idValue)&&idValue.ValueKind==JsonValueKind.String?idValue.GetString()!:"invalid-slot:"+slot;values.Add(new LegacyArchiveEntry{Id=id});}
  }
  var omissions=new List<LegacyArchiveGap>();
  if(root.TryGetProperty("Omissions",out var gaps)&&gaps.ValueKind==JsonValueKind.Array)
   foreach(var gap in gaps.EnumerateArray())
    if(gap.ValueKind==JsonValueKind.Object&&gap.TryGetProperty("Id",out var id)&&id.ValueKind==JsonValueKind.String)
     omissions.Add(new((id.GetString()??"").Length<=200?id.GetString()??"":"invalid-import-slot","archive_visibility_withheld"));
  var c=new LegacyArchiveCatalog{Schema=1,Entries=values.ToArray(),Omissions=omissions.ToArray()};
  return(c,LegacyHash(b));
 }
 private static string LegacyIdentityError(LegacyArchiveEntry? e)
 {
  if(e==null||string.IsNullOrEmpty(e.Id)||e.Id.Length>200||e.Version==null||e.Version.Length!=64||!e.Version.All(Uri.IsHexDigit)||e.Ordinal<1
    ||e.Role is not("user" or "assistant")||e.Text==null||e.Family==null||e.Family.Length>128||e.Source==null||e.Source.Path==null||e.Source.Path.Length>240||e.Source.Sha256==null
    ||e.Source.Format is not("companion-role-content" or "native-memoryturn-v0")||e.Proofs==null||e.Proofs.Any(p=>p==null||p.Path==null||p.Kind==null)
    ||e.Aliases==null||e.Aliases.Any(s=>s==null)||!e.Aliases.Contains(e.Id)||e.RawRecord.ValueKind!=JsonValueKind.Object)return "invalid_entry_identity";
  return "";
 }
 private string LegacySourceError(LegacyArchiveEntry e,Dictionary<string,byte[]> bytes,out string observed)
 {
  observed="";
  try
  {
   byte[] Read(string path){string full=LegacyPath(path);if(!bytes.TryGetValue(full,out var b)){var f=new FileInfo(full);if(!f.Exists||f.Length>16*1024*1024)throw new InvalidDataException("source_missing_or_large");bytes[full]=b=File.ReadAllBytes(full);}return b;}
   byte[] b=Read(e.Source.Path);observed=LegacyHash(b);using var doc=JsonDocument.Parse(b);
   var root=doc.RootElement;if(root.ValueKind!=JsonValueKind.Array||e.Ordinal>root.GetArrayLength())return "source_record_missing";
   var raw=root[e.Ordinal-1];if(Canonical(raw)!=Canonical(e.RawRecord))return "source_record_changed";
   if(raw.TryGetProperty("Kind",out var kind)&&kind.ValueKind!=JsonValueKind.String)return "source_kind_invalid";
   string role=e.Source.Format=="companion-role-content"?raw.GetProperty("role").GetString()!:raw.GetProperty("Role").GetString()!;
   string text=e.Source.Format=="companion-role-content"?raw.GetProperty("content").GetString()!:raw.GetProperty("Text").GetString()!;
   if(role!=e.Role||text!=e.Text)return "source_role_or_text_changed";
   if(e.Source.Format=="native-memoryturn-v0"&&raw.GetProperty("Timestamp").GetString()!=e.Timestamp)return "source_timestamp_changed";
   foreach(var proof in e.Proofs)
   {
    var pb=Read(proof.Path);var lines=Encoding.UTF8.GetString(pb).TrimStart('\uFEFF').Split('\n');
    if(proof.Line<1||proof.Line>lines.Length)return "proof_missing";
    using var pd=JsonDocument.Parse(lines[proof.Line-1]);var p=pd.RootElement;
    if(Canonical(p)!=Canonical(proof.Record)||p.GetProperty("Text").GetString()!=e.Text
      ||p.GetProperty("Timestamp").GetString()!=e.Timestamp||p.GetProperty("Kind").GetString()!=proof.Kind)return "proof_changed";
    if(e.Role=="user"&&proof.Kind!="chat.send"||e.Role=="assistant"&&proof.Kind is not("chat.seed" or "chat.receive"))return "proof_role_mismatch";
    string actor=p.GetProperty("Actor").GetString()??"",source=p.GetProperty("Source").GetString()??"";
    if(actor!=(e.Role=="user"?"player":"lilith")||source!=(proof.Kind=="chat.receive"?"companion":"mod-ui"))return "proof_origin_mismatch";
   }
   if(e.Source.Format=="companion-role-content"&&e.Timestamp!=null&&e.Proofs.Length==0)return "timestamp_without_proof";
   string expectedAlias="legacy-occurrence:"+LegacyHash(Canonical(JsonSerializer.SerializeToElement(new{
    raw=e.RawRecord,timestamp=e.Timestamp,proofs=e.Proofs.Select(p=>LegacyHash(Canonical(p.Record))).ToArray()},LegacyJson)));
   if(!e.Aliases.ToHashSet(StringComparer.Ordinal).SetEquals(new[]{e.Id,expectedAlias}))return "occurrence_alias_mismatch";
   if(e.Proofs.Length==0&&e.Evidence!="history_saved_display_unknown")return "evidence_without_proof";
   if(e.Source.Format=="native-memoryturn-v0"&&e.TimestampBasis!="source_record")return "timestamp_basis_mismatch";
   if(e.Proofs.Length>0)
   {
    string expected=e.Proofs[0].Kind switch{"chat.seed"=>"native_bubble_seed_logged","chat.send"=>"player_submission_logged",_=>"companion_reply_chat_append_logged"};
    if(e.Evidence!=expected||e.TimestampBasis!="exact_ordered_telemetry")return "evidence_identity_mismatch";
   }
   return "";
  }
  catch(Exception error) when(error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException or ArgumentException)
  {return "source_unavailable_"+error.GetType().Name;}
 }
 private string LegacyPermissionError(LegacyArchiveEntry e)
 {
  var ids=SuppressedExperienceIds();if(e.Aliases.Any(ids.Contains))return "withdrawn";
  var turn=new MemoryTurn{EventId=e.Id,SourceEventIds=e.Aliases,Role=e.Role,Text=e.Text,Origin="legacy",Kind="legacy.archive",DeliveryState="legacy_unknown"};
  if(!IsMentionAllowed(turn)||!IsMentionTextAllowed(e.Text))return "mention_withheld";
  if(!IsNativeSupplyAllowed(turn))return "native_scope_withheld";
  return SuppressedTextMatches(e.Text)?"withdrawn_text":"";
 }
 private static HashSet<string> LegacyTokens(string text)
 {
  var result=new HashSet<string>(StringComparer.Ordinal);
  foreach(System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text.ToLowerInvariant(),"[\\u3400-\\u9fff]+|[a-z0-9_]+"))
  {string s=m.Value;if(s[0]>='\u3400'&&s[0]<='\u9fff'){if(s.Length==1)result.Add(s);for(int i=0;i<s.Length-1;i++)result.Add(s.Substring(i,2));}else result.Add(s);}
  return result;
 }
 private static DateTimeOffset? LegacyTime(string? value)=>value==null?null:DateTimeOffset.TryParse(value,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out var t)?t:null;
 internal LegacyArchivePacket ReadLegacyArchive(LegacyArchiveQuery args,string query,BridgeConfig config,int materialBytes=LegacyDefaultBytes)
 {
  lock(_experienceLock)
  {
   var mention=_mentionReadPolicy.Value;var native=_nativeDialoguePolicy.Value;_mentionReadPolicy.Value=null;_nativeDialoguePolicy.Value=null;
   try {using var m=BeginMentionRequest(query,true);using var n=BeginNativeDialogueRequest(query,true,config);return ReadLegacyArchiveCore(args,query,config,Math.Clamp(materialBytes,4096,LegacyDefaultBytes));}
   finally{_mentionReadPolicy.Value=mention;_nativeDialoguePolicy.Value=native;}
  }
 }
 private LegacyArchivePacket ReadLegacyArchiveCore(LegacyArchiveQuery args,string query,BridgeConfig config,int budget)
 {
  LegacyArchivePacket Fail(string reason)=>new(reason,args,budget,"",Array.Empty<LegacyArchiveView>(),new[]{new LegacyArchiveGap("",reason)},0,0,"",LegacyHash(reason));
  if(!_longTermEnabled||!config.LongTermMemoryEnabled||!config.ExperienceArchiveEnabled||MemorySemantics.DeclinesArchiveRecall(query))return Fail("archive_disabled_or_declined");
  if(!string.Equals(Path.GetFullPath(config.DataDirectory),_root,StringComparison.OrdinalIgnoreCase))return Fail("owner_root_mismatch");
  try
  {
   var(catalog,catalogHash)=LoadLegacyArchive();var bytes=new Dictionary<string,byte[]>(StringComparer.OrdinalIgnoreCase);
   var tokens=LegacyTokens(args.Query);var from=LegacyTime(args.After.Length==0?null:args.After);var until=LegacyTime(args.Before.Length==0?null:args.Before);
   var candidates=new List<(LegacyArchiveEntry Entry,string Reason,string Observed,int Score)>();
   var importedWithheld=catalog.Omissions.Select(g=>g.Id).ToHashSet(StringComparer.Ordinal);
   foreach(var gap in catalog.Omissions)if(args.Id.Length==0||args.Id==gap.Id)candidates.Add((new LegacyArchiveEntry{Id=gap.Id},gap.Reason,"",0));
   var duplicateIds=catalog.Entries.Where(e=>e!=null&&e.Id!=null).GroupBy(e=>e.Id).Where(g=>g.Count()>1).Select(g=>g.Key).ToHashSet(StringComparer.Ordinal);
   int slot=0;
   foreach(var original in catalog.Entries)
   {
    slot++;var e=original;
    string identity=LegacyIdentityError(e);
    if(identity.Length>0){string locator=e?.Id??"invalid-slot:"+slot;if(locator.Length>200)locator="invalid-slot:"+slot;candidates.Add((new LegacyArchiveEntry{Id=locator},identity,"",0));continue;}
    if(importedWithheld.Contains(e.Id))continue;
    if(duplicateIds.Contains(e.Id)){candidates.Add((e,"ambiguous_entry_identity","",0));continue;}
    if(args.Id.Length>0&&e.Id!=args.Id)continue;
    var time=LegacyTime(e.Timestamp);
    if(from!=null&&(time==null||time<from)||until!=null&&(time==null||time>=until))continue;
    // Denied text is never sent. Its ID-only omission is independent of query
    // lexical ranking; existing policies, not index membership, own eligibility.
    string permission=LegacyPermissionError(e);string observed="";
    int score=permission.Length>0||tokens.Count==0?0:LegacyTokens(e.Text).Count(tokens.Contains);
    if(args.Id.Length==0&&tokens.Count>0&&score==0&&permission.Length==0)continue;
    string reason=permission.Length>0?permission:LegacySourceError(e,bytes,out observed);
    candidates.Add((e,reason,observed,score));
   }
   var ordered=candidates.OrderByDescending(x=>x.Score).ThenBy(x=>LegacyTime(x.Entry.Timestamp)).ThenBy(x=>x.Entry.Id,StringComparer.Ordinal).ToArray();
   string selection=LegacyHash(catalogHash+JsonSerializer.Serialize(args with{Cursor=""},LegacyJson)+string.Join("|",ordered.Select(x=>x.Entry.Id+":"+x.Reason)));
   int start=0;
   if(args.Cursor.Length>0)
   {var p=args.Cursor.Split(':');if(p.Length!=2||p[0]!=selection||!int.TryParse(p[1],out start)||start<0||start>ordered.Length)return Fail("cursor_stale_or_invalid");}
   var gaps=new List<LegacyArchiveGap>();var views=new List<LegacyArchiveView>();int index=start,gapTotal=0;
   int eligible=ordered.Count(x=>x.Reason.Length==0);int limit=Math.Min(Math.Clamp(args.Limit,1,24),Math.Max(1,(budget-700)/1800));
   for(;index<ordered.Length&&views.Count<limit;index++)
   {
    var row=ordered[index];var e=row.Entry;
    if(row.Reason.Length>0){gapTotal++;if(gaps.Count<32)gaps.Add(new(e.Id,row.Reason));continue;}
    int offset=args.Id.Length>0?args.Offset:0;if(offset>e.Text.Length){gapTotal++;gaps.Add(new(e.Id,"offset_out_of_range"));continue;}
    if(offset>0&&offset<e.Text.Length&&char.IsLowSurrogate(e.Text[offset])){gapTotal++;gaps.Add(new(e.Id,"offset_splits_surrogate"));continue;}
    // Fair per-item reservation: one long record cannot consume the page.
    int slots=Math.Max(1,Math.Min(limit-views.Count,eligible-views.Count));
    int room=Math.Max(0,(budget-700-views.Sum(v=>Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(v,LegacyJson)))-gaps.Count*120)/slots-850);
    int chars=Math.Min(1600,Math.Min(e.Text.Length-offset,room/3));
    if(chars>0&&offset+chars<e.Text.Length&&char.IsHighSurrogate(e.Text[offset+chars-1]))chars--;
    if(chars==0&&offset<e.Text.Length){gapTotal++;gaps.Add(new(e.Id,"page_budget_omitted"));continue;}
    int end=offset+chars;
    views.Add(new(e.Id,e.Version,e.Family,e.Ordinal,e.Role,e.Timestamp,e.TimestampBasis,e.Evidence,LegacyHash(e.Text),offset,e.Text.Length,
      e.Text.Substring(offset,chars),end<e.Text.Length?end:null,e.Source.Path,e.Source.Sha256,row.Observed,row.Observed!=e.Source.Sha256,e.Proofs.Select(p=>p.Kind).ToArray(),
      e.RawRecord.TryGetProperty("Kind",out var rawKind)?rawKind.GetString()??"unknown":"companion_chat",e.Source.Format));
   }
   string cursor=index<ordered.Length?selection+":"+index:"";
   string Status()=>views.Count==0&&gapTotal==0?"not_found":gapTotal>0||cursor.Length>0||views.Any(v=>v.NextOffset!=null)?"partial":"ok";
   LegacyArchivePacket Draft()=>new(Status(),args,budget,catalogHash,views,gaps,ordered.Length,eligible,cursor,new string('0',64),Math.Max(0,gapTotal-gaps.Count));
   // Exact UTF-8 serialization, including metadata, owns the material budget.
   // Trim the largest excerpt first, preserving small independent neighbours.
   while(Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(Draft(),LegacyJson))>budget&&views.Any(v=>v.Text.Length>0))
   {
    var biggest=views.OrderByDescending(v=>Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(v.Text,LegacyJson))).First();int at=views.IndexOf(biggest);
    int length=biggest.Text.Length/2;if(length>0&&char.IsHighSurrogate(biggest.Text[length-1]))length--;
    if(length==0){views.RemoveAt(at);gapTotal++;gaps.Add(new(biggest.Id,"page_budget_omitted"));}
    else views[at]=biggest with{Text=biggest.Text[..length],NextOffset=biggest.Offset+length};
   }
   // The small floor normally holds all gaps. For unusually long locators,
   // aggregate the tail rather than making omission metadata unbounded.
   while(Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(Draft(),LegacyJson))>budget&&gaps.Count>1)gaps.RemoveAt(gaps.Count-1);
   while(Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(Draft(),LegacyJson))>budget&&views.Count>0)
   {var last=views[^1];views.RemoveAt(views.Count-1);gapTotal++;gaps.Add(new(last.Id,"metadata_budget_omitted"));}
   if(Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(Draft(),LegacyJson))>budget&&gaps.Count>0)
   {gaps.Clear();}
   var draft=CompleteLegacyArchiveContext(Draft(),catalog,bytes);return draft with{DataVersion=LegacyHash(JsonSerializer.Serialize(draft with{DataVersion=""},LegacyJson))};
  }
  catch(Exception error) when(error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or InvalidOperationException or ArgumentException)
  {return Fail("archive_unavailable_"+error.GetType().Name);}
 }
 internal bool TryUseLegacyArchive(LegacyArchivePacket packet,string query,BridgeConfig config,Func<bool> use)
 {
  lock(_experienceLock)
  {var now=ReadLegacyArchive(packet.Query,query,config,packet.MaterialBytes);return now.DataVersion==packet.DataVersion&&JsonSerializer.Serialize(now,LegacyJson)==JsonSerializer.Serialize(packet,LegacyJson)&&use();}
 }
 // Explicit old-archive commands use stable identity and exact version CAS.
 // They stop read access in the existing durable receipt ledger. Source backups
 // remain present; the actual receipt never describes a physical purge.
 private UserMemoryOperationResult? TryApplyLegacyArchiveCommand(MemoryTurn input)
 {
  var m=System.Text.RegularExpressions.Regex.Match(Clean(input.Text),@"^(收起旧档|删除旧档)[:：]\s*(legacy:[a-zA-Z0-9_-]+:\d+)@([0-9a-f]{64})\s*$");
  if(!m.Success)return null;
  lock(_experienceLock)
  {
   var applied=_autonomous.FirstOrDefault(t=>t.OperationId==input.EventId&&t.RecallId=="legacy-access-"+m.Groups[2].Value&&t.Source=="explicit_user");
   if(applied!=null&&ReadExperiences(true).Any(t=>t.EventId==input.EventId&&t.Role=="user"&&t.Origin=="user"&&t.DeliveryState=="received"&&t.Text==input.Text))
    return new(true,"already_applied","这条旧档的读取撤回已经保存；原始备份仍保留。",applied.RecallId,applied.Revision);
   if(!VerifyUserEvidence(input.EventId,input.Text))return new(false,"legacy_command_source_unverified","这次没有更改旧档读取权限。");
   try
   {
    var(catalog,_)=LoadLegacyArchive();
    if(catalog.Entries.Count(e=>e!=null&&e.Id==m.Groups[2].Value)!=1)return new(false,"legacy_target_changed","旧档身份不唯一，这次没有更改。");
    var e=catalog.Entries.SingleOrDefault(e=>e!=null&&e.Id==m.Groups[2].Value&&e.Version==m.Groups[3].Value);
    if(LegacyIdentityError(e).Length>0||LegacySourceError(e!,new(StringComparer.OrdinalIgnoreCase),out _).Length>0)return new(false,"legacy_target_changed","旧档目标或版本已变化，这次没有更改。");
    var receipt=new MemoryTurn{EventId=Guid.NewGuid().ToString("N"),RecallId="legacy-access-"+e.Id,OperationId=input.EventId,
      Role="user",Origin="user",Kind="autonomous",Source="explicit_user",SourceEventId=input.EventId,
      SourceEventIds=e.Aliases.Append(input.EventId).Distinct().ToArray(),RequestKind=m.Groups[1].Value=="删除旧档"?"delete":"suppress",
      Text="",Evidence="",LifecycleState=m.Groups[1].Value=="删除旧档"?"deleted":"forgotten",DeliveryState="received",Validity="user_reported"};
    _autonomous.Add(receipt);
    if(!SaveList("v4-native-autonomous-memory.json",_autonomous)){_autonomous.Remove(receipt);return new(false,"persistence_failed","读取权限未保存，这次没有完成撤回。");}
    return new(true,"legacy_read_withdrawn_source_retained","已停止这条旧档的后续读取；原始备份仍保留。",receipt.RecallId,receipt.Revision);
   }
   catch(Exception error) when(error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or InvalidOperationException or ArgumentException)
   {return new(false,"legacy_target_unavailable","旧档暂时无法核对，这次没有更改。");}
  }
 }
}
