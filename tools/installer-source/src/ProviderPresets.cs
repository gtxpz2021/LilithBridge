using System;using System.Linq;
namespace LilithSetup {
 public static class ProviderPresets {
  public const string DeepSeekUrl="https://api.deepseek.com",DefaultModel="deepseek-flash";
  public static readonly string[] Models={"deepseek-flash","deepseek-v4-pro"};
  static readonly string[] PreservedAliases={"deepseek-v4-flash","deepseek-v4-flash-vision-exp"};
  public static bool IsDeepSeekUrl(string value){Uri u;if(!Uri.TryCreate(value,UriKind.Absolute,out u)||u.Scheme!="https"||!u.IsDefaultPort||!String.Equals(u.Host,"api.deepseek.com",StringComparison.OrdinalIgnoreCase)||u.UserInfo!=""||u.Query!=""||u.Fragment!="")return false;return new[]{"","/v1","/chat/completions","/v1/chat/completions"}.Contains(u.AbsolutePath.TrimEnd('/'),StringComparer.Ordinal);}
  public static bool Matches(string url,string model){return IsDeepSeekUrl(url)&&(Models.Contains(model,StringComparer.Ordinal)||PreservedAliases.Contains(model,StringComparer.Ordinal));}
  public static bool SameEndpoint(string a,string b){if(IsDeepSeekUrl(a)&&IsDeepSeekUrl(b))return true;Uri x,y;if(!Uri.TryCreate(a,UriKind.Absolute,out x)||!Uri.TryCreate(b,UriKind.Absolute,out y))return String.Equals(a,b,StringComparison.Ordinal);return String.Equals(x.Scheme,y.Scheme,StringComparison.OrdinalIgnoreCase)&&String.Equals(x.Authority,y.Authority,StringComparison.OrdinalIgnoreCase)&&String.Equals(x.AbsolutePath.TrimEnd('/'),y.AbsolutePath.TrimEnd('/'),StringComparison.Ordinal)&&x.Query==y.Query&&x.Fragment==y.Fragment&&x.UserInfo==y.UserInfo;}
  public static string ModelNote(string model){if(model=="deepseek-v4-pro")return "Pro 不支持图像；需要读取图像时请选择 Flash。模型费用以服务商说明为准。";if(model==DefaultModel)return "Flash 支持文本和图像；地址已自动填写。模型费用以服务商说明为准。";if(PreservedAliases.Contains(model,StringComparer.Ordinal))return "当前模型 ID 可继续使用。模型费用以服务商说明为准。";return "模型 ID 可编辑；自定义模型能力需自行核对，本工具不联网测试。";}
 }
}
