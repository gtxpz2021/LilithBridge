using System;using System.IO;using System.Text;using System.Collections.Generic;
namespace LilithSetup {
 // Resolves the already effective root. Never copies data or credentials, adds
 // an isolation marker to a legacy install, or merges two installations.
 public sealed class ExistingRootReview {
  public string Game,Root,Kind,Stamp,PathStamp;public bool NeedsConfirmation;
  public string Summary {get{return "配置、记忆与已有登录保留原位："+Root+"\r\n"+(NeedsConfirmation?"这是无标记或外置旧资料根，可能由其他副本共用。请确认它正是旧插件使用的目录；不复制、不合并、不重置。安装时需关闭所有使用该资料的游戏。":"保持已有隔离标记和资料目录，不迁移数据。");}}
 }
 public static class ExistingDataRoot {
  static readonly Dictionary<string,string> Approved=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
  static FileAttributes? Attributes(string path){try{return File.GetAttributes(path);}catch(FileNotFoundException){return null;}catch(DirectoryNotFoundException){return null;}}
  static FileAttributes? SafeAttributes(string path){for(string p=path;!String.IsNullOrEmpty(p);p=Path.GetDirectoryName(p)){var a=Attributes(p);SetupCore.Require(!a.HasValue||(a.Value&FileAttributes.ReparsePoint)==0,"LBINSTALL_DATA_ROOT_REPARSE_REJECTED");}return Attributes(path);}
  public static string ProductionRoot {get{return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"LilithAI");}}
  public static ExistingRootReview Review(string game){return Review(game,Environment.GetEnvironmentVariable("LILITH_BRIDGE_DATA_DIR"),ProductionRoot);}
  // Explicit inputs are used only by bounded offline fixtures.
  public static ExistingRootReview Review(string game,string environment,string production){
   game=SetupCore.SafePath(game,null);SetupCore.NoReparse(game);production=SetupCore.SafePath(production,null);
   string marker=Path.Combine(game,InstallManifest.Marker);var markerAttributes=SafeAttributes(marker);string marked=null,markerHash="MISSING";
   if(markerAttributes.HasValue){
    SetupCore.Require((markerAttributes.Value&FileAttributes.Directory)==0,"LBINSTALL_MARKER_INVALID");
    SetupCore.Require(new FileInfo(marker).Length<=8192,"LBINSTALL_MARKER_INVALID");byte[] bytes=File.ReadAllBytes(marker);markerHash=SetupCore.Hash(bytes);
    foreach(string line in new UTF8Encoding(false,true).GetString(bytes).TrimStart('\uFEFF').Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries)){
     if(String.IsNullOrWhiteSpace(line)||line.TrimStart().StartsWith("#"))continue;
     SetupCore.Require(marked==null,"LBINSTALL_MARKER_INVALID");string value=line.Trim();if(value.Length>=2&&value.StartsWith("\"")&&value.EndsWith("\""))value=value.Substring(1,value.Length-2);marked=SetupCore.SafePath(value,game);
    }
    SetupCore.Require(marked!=null,"LBINSTALL_MARKER_INVALID");
   }
   bool existing=File.Exists(Path.Combine(game,InstallManifest.Plugin));
   string root,kind;
   if(environment!=null){root=SetupCore.SafePath(environment,game);SetupCore.Require(marked==null||SetupCore.Same(root,marked),"LBINSTALL_ENVIRONMENT_ROOT_CONFLICT");kind="environment";}
   else if(marked!=null){root=marked;kind="marker";}
   else if(existing){root=production;kind="legacy-default";}
   else {root=Path.Combine(game,"LilithBridgeData");kind="fresh-isolated";}
   var rootAttributes=SafeAttributes(root);SetupCore.Require(!rootAttributes.HasValue||(rootAttributes.Value&FileAttributes.Directory)!=0,"LBINSTALL_DATA_ROOT_NOT_DIRECTORY");
   SetupCore.Require(root.Length>Path.GetPathRoot(root).Length&&!SetupCore.Inside(root,game,true)&&!SetupCore.Inside(Path.Combine(game,"BepInEx"),root,true),"LBINSTALL_DATA_ROOT_UNSAFE");
   // Explicit roots must satisfy the product's isolation contract. Only its
   // unmarked legacy fallback is allowed to remain at the production default.
   if(kind!="legacy-default")SetupCore.Require(!SetupCore.Inside(production,root,true)&&!SetupCore.Inside(root,production,true),"LBINSTALL_EXPLICIT_PRODUCTION_ROOT_INVALID");
   // A new installation must not silently change where the first marker points.
   if(!existing&&marked==null)SetupCore.Require(environment==null||SetupCore.Same(root,Path.Combine(game,"LilithBridgeData")),"LBINSTALL_NEW_ENVIRONMENT_ROOT_REQUIRES_REVIEW");
   string directoryIdentity=rootAttributes.HasValue?FileIdentity.Directory(root):"MISSING";
   string pathStamp=SetupCore.Hash(Encoding.UTF8.GetBytes(game+"\n"+root+"\n"+kind+"\n"+markerHash+"\n"+(environment??"<unset>")));
   string stamp=SetupCore.Hash(Encoding.UTF8.GetBytes(pathStamp+"\n"+directoryIdentity));
   return new ExistingRootReview{Game=game,Root=root,Kind=kind,Stamp=stamp,PathStamp=pathStamp,NeedsConfirmation=(existing&&marked==null)||((existing||marked!=null)&&!SetupCore.Inside(game,root,false))};
  }
  public static void Approve(ExistingRootReview shown,bool confirm){SetupCore.Require(confirm&&shown!=null,"LBINSTALL_DATA_ROOT_REVIEW_REQUIRED");var fresh=Review(shown.Game);SetupCore.Require(fresh.Stamp==shown.Stamp,"LBINSTALL_DATA_ROOT_CHANGED_RECHECK");lock(Approved)Approved[fresh.Game]=fresh.Stamp;}
  public static void RequireApproved(ExistingRootReview fresh){if(!fresh.NeedsConfirmation)return;string stamp;lock(Approved)SetupCore.Require(Approved.TryGetValue(fresh.Game,out stamp)&&stamp==fresh.Stamp,"LBINSTALL_DATA_ROOT_REVIEW_REQUIRED");}
  public static string Require(string game){var review=Review(game);RequireApproved(review);return review.Root;}
  internal static void EnsureApprovedDirectory(string game){
   var before=Review(game);RequireApproved(before);if(Directory.Exists(before.Root))return;
   // The user approved this effective path. Bind our own first creation to its
   // new directory identity without treating it as an external root switch.
   Directory.CreateDirectory(before.Root);var after=Review(game);
   SetupCore.Require(after.PathStamp==before.PathStamp,"LBINSTALL_DATA_ROOT_CHANGED_RECHECK");
   if(after.NeedsConfirmation)Approve(after,true);
  }
 }
 public static class LoginGuidance {
   public const string InGame="游戏内：⋯ → 模型与资料 → 对话模型 → 提供方 OpenAI；认证账户 → OpenAI 认证 → ChatGPT 计划额度（独立登录） → Continue with ChatGPT，由本人完成浏览器授权。随后点顶部推荐使用设置，知情并采用推荐：所选对话/历史/记忆/反思及按需作品片段会发到当前提供方，后台消耗所选认证额度或API费用；不整库上传，不默认打开图片。旧明确关闭项保留，可在同一入口明确开启或关闭普通记忆提取与反思工作；若提示需正常重进游戏则照提示操作。Terra仅在账户目录含该模型且未选模型时补low，不转API；Plus等以实际权限为准。断开账户用断开 ChatGPT 连接，返回/×只关闭面板。已有登录原位保留，安装器不读取/导入认证凭据。";
  public static string Completion(bool keepService,bool hasLegacyKey){return "所选组件已完成。"+(keepService?"现有服务与认证选择保持不变。":hasLegacyKey?"API设置已保存。":"API Key未填写，可之后自行设置。")+"\r\n"+InGame;}
 }
}
