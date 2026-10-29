using System;using System.IO;using System.IO.Compression;using System.Text;using System.Linq;using System.Collections.Generic;
namespace LilithSetup {
 public sealed class LoaderPreparationPlan {
  public readonly string Source,GameRoot,Catalog,Fingerprint;
  internal readonly Dictionary<string,string> Before;
  internal LoaderPreparationPlan(string source,string game,string catalog,string fingerprint,Dictionary<string,string> before){Source=source;GameRoot=game;Catalog=catalog;Fingerprint=fingerprint;Before=new Dictionary<string,string>(before,StringComparer.OrdinalIgnoreCase);}
 }
 public sealed class LoaderPreparationResult {public string State,Journal;public LoaderPreparationPlan Plan;public readonly List<string> Added=new List<string>(),Existing=new List<string>(),Conflicts=new List<string>();}
 public static class LoaderPreparation {
  sealed class Item {public string Rel,Hash;public byte[] Bytes;}
  static string P(string root,string rel){string p=SetupCore.SafePath(rel,root);SetupCore.Require(SetupCore.Inside(root,p,false),"LBINSTALL_DEPENDENCY_PATH_ESCAPES");SetupCore.NoReparse(p);return p;}
  static byte[] Read(Stream s){using(var m=new MemoryStream()){var b=new byte[81920];int n;while((n=s.Read(b,0,b.Length))>0){if(m.Length+n>134217728)throw new SetupException("LBINSTALL_DEPENDENCY_FILE_TOO_LARGE");m.Write(b,0,n);}return m.ToArray();}}
  static void Record(string path,Dictionary<string,object> doc){byte[] b=new UTF8Encoding(false).GetBytes(DataJson.Write(doc));string temp=path+".tmp";using(var f=new FileStream(temp,FileMode.Create,FileAccess.Write,FileShare.None)){f.Write(b,0,b.Length);f.Flush(true);}if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);}
  public static LoaderPreparationResult FromFolder(string source,string game,string manifest){SetupCore.NoReparse(source);return Prepare(game,manifest,delegate(string rel){string p=P(source,rel);return File.Exists(p)?File.OpenRead(p):null;},null,false);}
  public static LoaderPreparationResult FromZip(string source,string game,string manifest){return Archive(source,game,manifest,false,null);}
  public static LoaderPreparationResult PreviewZip(string source,string game,string manifest){return Archive(source,game,manifest,true,null);}
  public static LoaderPreparationResult ExecuteZip(LoaderPreparationPlan approved){SetupCore.Require(approved!=null,"LBINSTALL_DEPENDENCY_CHECK_FIRST");return Archive(approved.Source,approved.GameRoot,approved.Catalog,false,approved);}
  static LoaderPreparationResult Archive(string source,string game,string manifest,bool preview,LoaderPreparationPlan approved){
   source=SetupCore.SafePath(source,null);SetupCore.NoReparse(source);string sourceHash=SetupCore.FileHash(source);var sourceCatalog=DataJson.Read(File.ReadAllText(manifest));if(sourceCatalog.ContainsKey("approved_archive_sha256"))SetupCore.Require(sourceHash==(string)sourceCatalog["approved_archive_sha256"],"LBINSTALL_DEPENDENCY_ARCHIVE_CHECKSUM_MISMATCH");
   using(var zip=ZipFile.OpenRead(source)){var entries=new Dictionary<string,ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);foreach(var e in zip.Entries){string rel=e.FullName.Replace('/','\\');if(rel.EndsWith("\\"))continue;if(Path.IsPathRooted(rel)||rel.Split('\\').Any(x=>x==".."||x=="."))throw new SetupException("LBINSTALL_DEPENDENCY_ARCHIVE_PATH_REJECTED");if(entries.ContainsKey(rel))throw new SetupException("LBINSTALL_DEPENDENCY_ARCHIVE_DUPLICATE");entries[rel]=e;}
    return PrepareBound(game,manifest,delegate(string rel){ZipArchiveEntry entry;return entries.TryGetValue(rel.Replace('/','\\'),out entry)?entry.Open():null;},null,preview,approved,source,sourceHash);
   }
  }
  internal static LoaderPreparationResult Prepare(string game,string manifest,Func<string,Stream> open,Action<int> checkpoint,bool preview){return PrepareBound(game,manifest,open,checkpoint,preview,null,"folder","");}
  static LoaderPreparationResult PrepareBound(string rawGame,string manifestPath,Func<string,Stream> open,Action<int> checkpoint,bool preview,LoaderPreparationPlan approved,string source,string sourceHash){
   InstallerCore.RequireStopped();string game=SetupCore.SafePath(rawGame,null);SetupCore.NoReparse(game);manifestPath=SetupCore.SafePath(manifestPath,null);SetupCore.NoReparse(manifestPath);
   SetupCore.Require(HostCapabilities.BasicHostMatches(game),"LBINSTALL_DEPENDENCY_HOST_CHANGED_RECHECK");
   byte[] catalogBytes=File.ReadAllBytes(manifestPath);var doc=DataJson.Read(Encoding.UTF8.GetString(catalogBytes));var rows=doc["files"] as object[];SetupCore.Require(rows!=null&&rows.Length>0&&rows.Length<500,"LBINSTALL_DEPENDENCY_CATALOG_INVALID");
   var items=new List<Item>();long total=0;var unique=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
   foreach(Dictionary<string,object> row in rows){string rel=(string)row["path"];string normal=rel.Replace('\\','/');bool allowed=normal.StartsWith("BepInEx/core/",StringComparison.OrdinalIgnoreCase)||normal.StartsWith("dotnet/",StringComparison.OrdinalIgnoreCase)||normal=="BepInEx/unity-libs/2021.3.45.zip"||new[]{"winhttp.dll","doorstop_config.ini",".doorstop_version","BepInEx-LICENSE.txt","DotNet-LICENSE.txt","DotNet-ThirdPartyNotices.txt"}.Contains(normal);SetupCore.Require(unique.Add(rel)&&allowed,"LBINSTALL_DEPENDENCY_FORBIDDEN_PATH");
    byte[] bytes;using(var s=open(rel)){if(s==null&&row.ContainsKey("optional")&&(bool)row["optional"])continue;SetupCore.Require(s!=null,"LBINSTALL_DEPENDENCY_SOURCE_MISSING:"+rel);bytes=Read(s);}SetupCore.Require(SetupCore.Hash(bytes)==(string)row["sha256"],"LBINSTALL_DEPENDENCY_SOURCE_CHECKSUM_MISMATCH:"+rel);total+=bytes.Length;SetupCore.Require(total<314572800,"LBINSTALL_DEPENDENCY_PACKAGE_TOO_LARGE");
    if(rel.Equals("doorstop_config.ini",StringComparison.OrdinalIgnoreCase)){string text=Encoding.UTF8.GetString(bytes);text=System.Text.RegularExpressions.Regex.Replace(text,@"(?im)^(\s*ignore_disable_switch\s*=\s*)true\s*$","${1}false");bytes=new UTF8Encoding(false).GetBytes(text);}items.Add(new Item{Rel=rel,Bytes=bytes,Hash=SetupCore.Hash(bytes)});
   }
   items=items.OrderBy(x=>x.Rel.Equals("winhttp.dll",StringComparison.OrdinalIgnoreCase)?1:0).ToList();var result=new LoaderPreparationResult();var states=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
   var stamp=new StringBuilder(game.ToUpperInvariant()+"|"+source+"|"+sourceHash+"|"+SetupCore.Hash(catalogBytes)+"\n");
   foreach(var rel in new[]{"Lilith.exe","GameAssembly.dll","UnityPlayer.dll","Lilith_Data/il2cpp_data/Metadata/global-metadata.dat"})stamp.Append(rel).Append('|').Append(InstallerCore.FileState(P(game,rel))).Append('\n');
   foreach(var item in items){string before=InstallerCore.FileState(P(game,item.Rel));states[item.Rel]=before;stamp.Append(item.Rel).Append('|').Append(before).Append('|').Append(item.Hash).Append('\n');if(before=="MISSING")result.Added.Add(item.Rel);else if(before==item.Hash)result.Existing.Add(item.Rel);else result.Conflicts.Add(item.Rel);}
   var current=new LoaderPreparationPlan(source,game,manifestPath,SetupCore.Hash(Encoding.UTF8.GetBytes(stamp.ToString())),states);
   if(approved!=null)SetupCore.Require(SetupCore.Same(approved.GameRoot,game)&&approved.Fingerprint==current.Fingerprint,"LBINSTALL_DEPENDENCY_PREVIEW_CHANGED_RECHECK");
   if(result.Conflicts.Count>0){result.State="ExistingFilesPreserved";return result;}
   result.Plan=current;if(preview){result.State="ReadyToPrepare";return result;}
   foreach(var item in items)SetupCore.Require(InstallerCore.FileState(P(game,item.Rel))==current.Before[item.Rel],"LBINSTALL_DEPENDENCY_PREVIEW_CHANGED_RECHECK");
   string[] planned=result.Added.ToArray();result.Added.Clear();string state=P(game,".LilithBridge-dependencies");Directory.CreateDirectory(state);
   using(var gate=new FileStream(P(state,"lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None)){
    string folder=P(state,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);result.Journal=P(folder,"journal.json");var added=new List<object>();var journal=new Dictionary<string,object>{{"schema",1},{"kind","shared-loader-preparation"},{"phase","Applying"},{"approved_snapshot",current.Fingerprint},{"planned_additions",planned},{"files",added},{"plugin_rollback_removes_these_files",false}};Record(result.Journal,journal);
    try{foreach(var item in items){string target=P(game,item.Rel);string before=current.Before[item.Rel];SetupCore.Require(InstallerCore.FileState(target)==before,"LBINSTALL_DEPENDENCY_PREVIEW_CHANGED_RECHECK");if(before!="MISSING")continue;InstallerCore.RequireStopped();Directory.CreateDirectory(Path.GetDirectoryName(target));
      using(var f=new FileStream(target,FileMode.CreateNew,FileAccess.Write,FileShare.None)){result.Added.Add(item.Rel);added.Add(new Dictionary<string,object>{{"path",item.Rel},{"sha256",item.Hash}});journal["files"]=added.ToArray();Record(result.Journal,journal);f.Write(item.Bytes,0,item.Bytes.Length);f.Flush(true);}
      if(checkpoint!=null)checkpoint(result.Added.Count);
     }journal["phase"]="Prepared";Record(result.Journal,journal);result.State="PendingFirstRun";return result;
    }catch{
     var retained=new List<object>();bool stopped=true;try{InstallerCore.RequireStopped();}catch{stopped=false;}
     foreach(var item in items.AsEnumerable().Reverse()){if(!result.Added.Contains(item.Rel))continue;try{string target=P(game,item.Rel);if(!File.Exists(target))continue;if(stopped&&SetupCore.FileHash(target)==item.Hash)File.Delete(target);else retained.Add(new Dictionary<string,object>{{"path",item.Rel},{"reason",stopped?"changed_or_incomplete_file_preserved":"game_running_no_cleanup"}});}catch(Exception error){retained.Add(new Dictionary<string,object>{{"path",item.Rel},{"reason","cleanup_failed"},{"error_type",error.GetType().Name}});}}
     journal["retained_files"]=retained.ToArray();journal["phase"]=retained.Count==0?"FailedPreparationCleaned":"RecoveryNeedsReview";Record(result.Journal,journal);throw;
    }
   }
  }
 }
}
