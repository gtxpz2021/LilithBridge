using System;using System.IO;
namespace LilithSetup {
 // Optional bundled work text. Not user conversation or MIT source. This only
 // fills an absent fixed file; it never selects a source or grants sending.
 public static class BundledStory {
  public const string CorpusHash="6f43c95bfc9d4713474b7bc182aff9934a262ea7595419198fd0b0ca7ede622f";
  public const long CorpusBytes=1794674;
  internal static string CopyAbsent(string source,string data){
   source=SetupCore.SafePath(source,null);data=SetupCore.SafePath(data,null);SetupCore.NoReparse(source);SetupCore.NoReparse(data);
   SetupCore.Require(File.Exists(source)&&new FileInfo(source).Length==CorpusBytes&&SetupCore.FileHash(source)==CorpusHash,"LBINSTALL_STORY_PACKAGE_HASH_MISMATCH");
   string directory=Path.Combine(data,"private-story"),target=Path.Combine(directory,"first-game.collection.jsonl");SetupCore.NoReparse(directory);SetupCore.NoReparse(target);
   SetupCore.Require(!Directory.Exists(target),"LBINSTALL_STORY_TARGET_IS_DIRECTORY_PRESERVED");
   if(File.Exists(target))return SetupCore.FileHash(target)==CorpusHash?"already-present":"existing-different-preserved";
   Directory.CreateDirectory(directory);SetupCore.NoReparse(directory);string temp=Path.Combine(directory,".first-game-"+Guid.NewGuid().ToString("N")+".tmp");
   try{
    using(var input=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.Read))
    using(var output=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)){SetupCore.Require(input.Length==CorpusBytes,"LBINSTALL_STORY_PACKAGE_HASH_MISMATCH");input.CopyTo(output);output.Flush(true);}
    SetupCore.Require(SetupCore.FileHash(temp)==CorpusHash,"LBINSTALL_STORY_PACKAGE_HASH_MISMATCH");SetupCore.NoReparse(target);
    try{File.Move(temp,target);}catch(IOException){if(File.Exists(target))return "concurrent-target-preserved";throw;}
    SetupCore.Require(SetupCore.FileHash(target)==CorpusHash,"LBINSTALL_STORY_COPY_VERIFY_FAILED");return "installed-absent-only";
   }finally{if(File.Exists(temp))File.Delete(temp);}
  }
  public static string Prepare(string package,CopyContext context){
   string result="";InstallerCore.PrepareOwnedDataWhileStopped(context.GameRoot,()=>{
    string root=ExistingDataRoot.Require(context.GameRoot);SetupCore.Require(SetupCore.Same(root,context.DataRoot),"LBINSTALL_DATA_ROOT_CHANGED_RECHECK");
    ExistingDataRoot.EnsureApprovedDirectory(context.GameRoot);result=CopyAbsent(Path.Combine(package,"story/first-game.collection.jsonl"),root);
   });return result;
  }
  public static string Guidance(string result){if(result=="not-selected")return "本轮未选择插件安装，未准备语料。也可在游戏内作品资料页自行选择安装包setup/story里的JSONL。";return result=="existing-different-preserved"||result=="concurrent-target-preserved"?"同名语料已存在，原文件保持不变。随包新版可从安装包setup/story路径自行选择；当前story.file与许可未改。":"原作语料文件已就绪；所选路径/本地启用将在最后受保护提交中保存，已有选源与许可按计划保留。资料发送可在游戏内“推荐使用设置”知情选择，不需要手填语料路径。";}
 }
}
