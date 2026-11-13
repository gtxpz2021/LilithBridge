using System;using System.IO;using System.Collections.Generic;
namespace LilithSetup {
 public static class StorySetup {
  static Dictionary<string,object> Story(Dictionary<string,object> doc){return DataJson.Map(doc,"story",false);}
  public static bool HasExistingChoice(Dictionary<string,object> doc){var s=Story(doc);if(s==null)return false;object file=DataJson.Get(s,"file");SetupCore.Require(file==null||file is string,"LBSETUP_STORY_BAD_TYPE");foreach(string k in new[]{"enabled","on_demand","allow_model_data"})SetupCore.Require(!s.ContainsKey(k)||s[k] is bool,"LBSETUP_STORY_BAD_TYPE");return !String.IsNullOrWhiteSpace(file as string)||(s.ContainsKey("enabled")&&!(bool)s["enabled"])||(s.ContainsKey("on_demand")&&!(bool)s["on_demand"]);}
  static bool Applies(Snapshot snapshot,EditRequest edit){return edit.ConfigureStory&&(!HasExistingChoice(snapshot.Document)||edit.SwitchExistingStory);}
  public static void Apply(Dictionary<string,object> doc,Snapshot snapshot,EditRequest edit){
   if(!Applies(snapshot,edit))return;
   string path=SetupCore.SafePath(edit.StoryFile,null);var s=DataJson.Map(doc,"story",true);
   s["file"]=path;s["enabled"]=true;s["on_demand"]=true;
   // A local-only choice retains the existing story-domain permission, including
   // absence. Only this explicit choice may grant it; no other domain is touched.
   if(edit.AllowStoryModelData)s["allow_model_data"]=true;
  }
  public static bool WouldChange(Snapshot snapshot,EditRequest edit){if(!Applies(snapshot,edit))return false;var doc=DataJson.Read(DataJson.Write(snapshot.Document));Apply(doc,snapshot,edit);return DataJson.Write(doc)!=DataJson.Write(snapshot.Document);}
  public static void ValidateTarget(Snapshot snapshot,EditRequest edit){if(!Applies(snapshot,edit))return;string path=SetupCore.SafePath(edit.StoryFile,null);SetupCore.NoReparse(path);SetupCore.Require(File.Exists(path)&&new FileInfo(path).Length==BundledStory.CorpusBytes&&SetupCore.FileHash(path)==BundledStory.CorpusHash,"LBSETUP_BUNDLED_STORY_TARGET_DIFFERENT_PRESERVED");}
  public static string Plan(Snapshot snapshot,WizardOptions options,string data){
   if(!options.ConfigureBundledStory)return "未选择随包原作语料配置；现有作品资料及许可保持。";
   bool existing=HasExistingChoice(snapshot.Document);string prior=DataJson.Get(Story(snapshot.Document),"allow_model_data") is bool?(bool)DataJson.Get(Story(snapshot.Document),"allow_model_data")?"允许":"不允许":"未设置";
   return "随包第一作语料：仅文件缺失时新增 "+Path.Combine(data,"private-story/first-game.collection.jsonl")+"。已有同名文件不覆盖。\r\n"+(existing&&!options.SwitchExistingStory?"已有选源或显式禁用，默认保持，不切换；如要本轮改用随包库，请明确勾选切换已有作品资料设置。":"将自动定位随包语料，并开启本地作品资料与按需读取。")+"\r\n"+(options.AllowStoryModelData?"明确允许这份原作资料用于当前模型，仅限作品资料域。":"不新增作品资料外发许可；原作品资料许可状态保持（"+prior+"）。")+"其他资料许可与提供方不因此改变。ChatGPT登录后在“推荐使用设置”一次核对所选对话/记忆/反思及作品片段发送范围和额度；图片不默认开启，旧关闭项保留。";
  }
  public static string Result(Snapshot snapshot,EditRequest edit){if(!edit.ConfigureStory)return "未选择向导配置随包语料，已有作品资料设置保持。";if(!Applies(snapshot,edit))return "随包文件已核对；已有作品资料选源/开关/许可保持，未按默认选项切换。";return "作品资料路径已配置，本地库已启用并按需读取；无需再手填路径或选库。"+(edit.AllowStoryModelData?"本次仅明确启用作品资料域的模型使用许可；其他总许可与登录不改。":"本次不新增模型使用许可；原作品资料许可保持。")+"ChatGPT由本人游戏内登录，随后点顶部“推荐使用设置”一次知情选择；仅登录不会授权资料发送。旧后台关闭项可用其中“明确开启普通记忆提取与反思工作”；提示需正常重进时照提示操作。";}
 }
 public static class BuildInfo {
  public const string Build = "0.1.4.19-cn-integrated.candidate.1";
  public const string ProductHash = "bfb40b65f2bfeb2963cb753967e6d6d8cb5fac864896103927c1994c321858e6";
  public const string Caption = "LilithBridge · 包内产品 .104/bfb40b65 · 安装器 .19 · cn-integrated.candidate.1";
  public static string Feedback(CopyContext context){return "\r\n包构建："+Build+"；包内DLL："+ProductHash+"\r\n本次目标DLL："+context.PluginHash+"\r\n已有日志："+Path.Combine(context.GameRoot,"BepInEx/LogOutput.log")+"；"+Path.Combine(context.DataRoot,"telemetry/v4-native-events.jsonl")+"。反馈请附构建身份、发生时间和最短复现步骤；日志由本人选择，不自动上传，勿公开密钥/认证或整个资料目录。";}
 }
}
