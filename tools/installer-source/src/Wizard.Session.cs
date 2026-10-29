using System;using System.IO;using System.Text;using System.Text.RegularExpressions;using System.Collections.Generic;
namespace LilithSetup {
 public static class WizardSession {
  public static void Save(WizardOptions options,string state){
   string cache=SetupCore.SafePath(options.CacheRoot,null);SetupCore.NoReparse(cache);Directory.CreateDirectory(cache);SaveCopy(options,WizardProvisioning.PathIn(cache,"last-session.json"),state);
  }
  public static void SaveCopy(WizardOptions options,string path,string state){
   string cache=SetupCore.SafePath(options.CacheRoot,null);path=SetupCore.SafePath(path,null);SetupCore.Require(SetupCore.Inside(cache,path,false),"LBWIZ_SESSION_PATH_REJECTED");SetupCore.NoReparse(path);Directory.CreateDirectory(Path.GetDirectoryName(path));string temp=path+".tmp";SetupCore.NoReparse(temp);
   var doc=new Dictionary<string,object>{{"schema",1},{"game_root",options.GameRoot},{"state",state},{"plugin",options.Plugin},{"loader",options.Loader},{"runtime",options.Runtime},{"model",options.Model},{"configure",options.Configure},{"enable_recall",options.EnableRecall},{"network",options.Network},{"download_plugin",options.DownloadPlugin},{"offline_root",options.OfflineRoot}};
   if(options.Update!=null)doc["release"]=new Dictionary<string,object>{{"tag",options.Update.Tag},{"release_id",options.Update.ReleaseId},{"asset_id",options.Update.AssetId},{"name",options.Update.Asset.Name},{"sha256",options.Update.Asset.Hash},{"bytes",options.Update.Asset.Bytes}};
   // Intentionally exclude Key, provider draft, configuration contents, corpus and command lines.
   File.WriteAllText(temp,DataJson.Write(doc),new UTF8Encoding(false));if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);
  }
  public static WizardOptions Read(string cache){
   return ReadFile(cache,WizardProvisioning.PathIn(cache,"last-session.json"));
  }
  public static WizardOptions ReadFile(string cache,string path){
   try{
    path=SetupCore.SafePath(path,null);SetupCore.Require(SetupCore.Inside(cache,path,false),"LBWIZ_SESSION_PATH_REJECTED");SetupCore.NoReparse(path);if(!File.Exists(path))return null;SetupCore.Require(new FileInfo(path).Length<32768,"LBWIZ_SESSION_TOO_LARGE");var doc=DataJson.Read(File.ReadAllText(path));SetupCore.Require(Convert.ToInt32(doc["schema"])==1,"LBWIZ_SESSION_UNSUPPORTED");
    var result=new WizardOptions{CacheRoot=cache,GameRoot=SetupCore.SafePath((string)doc["game_root"],null),Plugin=(bool)doc["plugin"],Loader=(bool)doc["loader"],Runtime=(bool)doc["runtime"],Model=(bool)doc["model"],Configure=(bool)doc["configure"],EnableRecall=(bool)doc["enable_recall"],Network=(bool)doc["network"],DownloadPlugin=(bool)doc["download_plugin"],OfflineRoot=(string)doc["offline_root"]};
    var release=DataJson.Map(doc,"release",false);if(release!=null&&release.Count>0){string tag=(string)release["tag"],name=(string)release["name"];long bytes=Convert.ToInt64(release["bytes"]);SetupCore.Require(Regex.IsMatch(tag,"^[A-Za-z0-9._-]{1,100}$")&&TrustedDownloads.IsInstallerArchive(name)&&bytes>0&&bytes<=52428800,"LBWIZ_SESSION_RELEASE_INVALID");result.Update=new ReleaseUpdate{Tag=tag,ReleaseId=Convert.ToInt64(release["release_id"]),AssetId=Convert.ToInt64(release["asset_id"]),Asset=new DownloadSpec(name,"https://github.com/gtxpz2021/LilithBridge/releases/download/"+tag+"/"+name,bytes,(string)release["sha256"],"github.com","release-assets.githubusercontent.com")};}
    return result;
   }catch{return null;}
  }
 }
}
