using System;using System.IO;using System.Linq;using System.Text;using System.Collections.Generic;using System.Security.Cryptography;using Mono.Cecil;
namespace LilithSetup {
 public sealed class HostCapabilityReport {
  public string State="PendingPreparation", GameVersion, Detail; public bool BasicHost, LoaderPresent, InteropFresh, ContractPassed, CanInstall;
  public readonly List<string> Missing=new List<string>(), ContractFailures=new List<string>(), Warnings=new List<string>();
 }
 public static class HostCapabilities {
  public static bool BasicHostMatches(string raw){try{string game=SetupCore.SafePath(raw,null);SetupCore.NoReparse(game);return Pe64(P(game,"Lilith.exe"))&&Pe64(P(game,"GameAssembly.dll"))&&File.Exists(P(game,"UnityPlayer.dll"))&&NativeLilithIdentity(P(game,"Lilith_Data/il2cpp_data/Metadata/global-metadata.dat"));}catch{return false;}}
  public static bool IsNamedBridge(string path){try{using(var a=AssemblyDefinition.ReadAssembly(path,new ReaderParameters{ReadSymbols=false,ReadingMode=ReadingMode.Deferred,InMemory=true}))return a.Name.Name=="LilithBridge";}catch{return false;}}
  static bool NativeLilithIdentity(string path){var data=File.ReadAllBytes(path);if(data.Length<200||BitConverter.ToUInt32(data,0)!=0xFAB11BAFu||BitConverter.ToInt32(data,4)!=31)return false;int strings=BitConverter.ToInt32(data,24),size=BitConverter.ToInt32(data,28),types=BitConverter.ToInt32(data,160),bytes=BitConverter.ToInt32(data,164);if(strings<0||size<1||strings>data.Length-size||types<0||bytes<0||bytes%88!=0||types>data.Length-bytes)return false;var wanted=new HashSet<string>{"LilithStateManager","DialogueManager","DialogueNode"};for(int p=types;p<types+bytes;p+=88){int ix=BitConverter.ToInt32(data,p);if(ix<0||ix>=size)continue;int end=Array.IndexOf<byte>(data,0,strings+ix,size-ix);if(end<0||end-strings-ix>512)continue;wanted.Remove(Encoding.UTF8.GetString(data,strings+ix,end-strings-ix));}return wanted.Count==0;}
  static string P(string a,string b){string p=SetupCore.SafePath(b,a);SetupCore.Require(SetupCore.Inside(a,p,false),"LBINSTALL_HOST_PATH_ESCAPES");SetupCore.NoReparse(p);return p;}
  static bool Pe64(string p){if(!File.Exists(p))return false;SetupCore.NoReparse(p);using(var s=File.OpenRead(p))using(var b=new BinaryReader(s)){if(s.Length<256||b.ReadUInt16()!=0x5a4d)return false;s.Position=60;int at=b.ReadInt32();if(at<64||at>s.Length-26)return false;s.Position=at;return b.ReadUInt32()==0x4550&&b.ReadUInt16()==0x8664;}}
  static string T(TypeReference t){var g=t as GenericParameter;if(g!=null)return (g.Type==GenericParameterType.Method?"!!":"!")+g.Position;var a=t as ArrayType;if(a!=null)return T(a.ElementType)+"["+new string(',',a.Rank-1)+"]";var by=t as ByReferenceType;if(by!=null)return T(by.ElementType)+"&";var pt=t as PointerType;if(pt!=null)return T(pt.ElementType)+"*";var gi=t as GenericInstanceType;if(gi!=null)return T(gi.ElementType)+"<"+String.Join(",",gi.GenericArguments.Select(T))+">";return t.FullName;}
  static string Sig(MethodReference m){return (m.HasThis?"instance ":"static ")+m.Name+"`"+m.GenericParameters.Count+"("+String.Join(",",m.Parameters.Select(x=>T(x.ParameterType)))+")->"+T(m.ReturnType);}
  static string Scope(TypeReference t){var r=t.GetElementType().Scope as AssemblyNameReference;return r==null?"":r.Name;}
  static bool External(string scope){return scope!=""&&scope!="LilithBridge"&&scope!="mscorlib"&&!scope.StartsWith("System",StringComparison.Ordinal)&&scope!="netstandard"&&scope!="Microsoft.CSharp";}
  static IEnumerable<TypeDefinition> Types(IEnumerable<TypeDefinition> source){foreach(var t in source){yield return t;foreach(var n in Types(t.NestedTypes))yield return n;}}
  sealed class Images:IDisposable {
   readonly string game;readonly Dictionary<string,AssemblyDefinition> loaded=new Dictionary<string,AssemblyDefinition>();
   public Images(string root){game=root;}
   public AssemblyDefinition Get(string name){if(!System.Text.RegularExpressions.Regex.IsMatch(name,@"^[A-Za-z0-9_.-]+$"))return null;AssemblyDefinition a;if(loaded.TryGetValue(name,out a))return a;foreach(var dir in new[]{"BepInEx/interop","BepInEx/core","dotnet"}){string p=P(P(game,dir),name+".dll");if(File.Exists(p)){SetupCore.NoReparse(p);a=AssemblyDefinition.ReadAssembly(p,new ReaderParameters{ReadSymbols=false,ReadingMode=ReadingMode.Deferred,InMemory=true});loaded[name]=a;return a;}}return null;}
   public TypeDefinition Type(string assembly,string name){var a=Get(assembly);return a==null?null:Types(a.MainModule.Types).FirstOrDefault(x=>x.FullName==name);}
   public IEnumerable<TypeDefinition> Hierarchy(TypeDefinition t){var seen=new HashSet<string>();for(int i=0;t!=null&&i<32;i++){if(!seen.Add(t.FullName))yield break;yield return t;var b=t.BaseType;t=b==null?null:Type(Scope(b),b.GetElementType().FullName);}}
   public void Dispose(){foreach(var a in loaded.Values)a.Dispose();}
  }
  public static List<string> CheckContract(string plugin,string game){
   var errors=new List<string>();var done=new HashSet<string>();
   using(var host=new Images(game))using(var own=AssemblyDefinition.ReadAssembly(plugin,new ReaderParameters{ReadSymbols=false,ReadingMode=ReadingMode.Deferred,InMemory=true})){
    foreach(var member in own.MainModule.GetMemberReferences()){
     string assembly=Scope(member.DeclaringType);if(!External(assembly))continue;string type=member.DeclaringType.GetElementType().FullName;
     var wanted=member as MethodReference;var field=member as FieldReference;string key=assembly+":"+type+":"+(wanted!=null?Sig(wanted):member.Name);if(!done.Add(key))continue;
     var actual=host.Type(assembly,type);if(actual==null){errors.Add("type missing: "+assembly+"/"+type);continue;}
     bool found=wanted!=null?host.Hierarchy(actual).SelectMany(x=>x.Methods).Any(x=>Sig(x)==Sig(wanted)):field!=null&&host.Hierarchy(actual).SelectMany(x=>x.Fields).Any(x=>x.Name==field.Name&&T(x.FieldType)==T(field.FieldType));
     if(!found)errors.Add("member signature missing: "+key);
    }
    var dialogue=host.Type("Assembly-CSharp","DialogueManager");
    foreach(var spec in new[]{new[]{"AdvanceDialogue"},new[]{"SelectOption","System.Int32"},new[]{"CompleteCurrentNode"},new[]{"CompleteCurrentNodeAfterState"},new[]{"RunPostStateThenComplete","DialogueNode"},new[]{"EndDialogue","System.Boolean"}}){
     if(dialogue==null||!dialogue.Methods.Any(m=>!m.IsStatic&&m.Name==spec[0]&&m.Parameters.Select(x=>T(x.ParameterType)).SequenceEqual(spec.Skip(1))))errors.Add("native continuation signature missing: DialogueManager."+spec[0]+"("+String.Join(",",spec.Skip(1))+")");
    }
    foreach(var type in Types(own.MainModule.Types))foreach(var attribute in type.CustomAttributes.Where(x=>x.AttributeType.FullName=="HarmonyLib.HarmonyPatch")){
     var ht=attribute.ConstructorArguments.Select(x=>x.Value).OfType<TypeReference>().FirstOrDefault();var name=attribute.ConstructorArguments.Select(x=>x.Value).OfType<string>().FirstOrDefault();
     if(ht==null||name==null||!External(Scope(ht)))continue;var target=host.Type(Scope(ht),ht.FullName);var typed=attribute.ConstructorArguments.FirstOrDefault(x=>x.Type.FullName=="System.Type[]").Value as CustomAttributeArgument[];string[] args=typed==null?null:typed.Select(x=>T((TypeReference)x.Value)).ToArray();if(target==null||!host.Hierarchy(target).SelectMany(x=>x.Methods).Any(x=>x.Name==name&&(args==null||x.Parameters.Select(p=>T(p.ParameterType)).SequenceEqual(args))))errors.Add("Harmony target missing: "+ht.FullName+"."+name);
    }
   }
   return errors.Distinct().ToList();
  }
  static void HashFile(ICryptoTransform h,string p){SetupCore.NoReparse(p);using(var s=File.OpenRead(p)){var b=new byte[81920];int n;while((n=s.Read(b,0,b.Length))>0)h.TransformBlock(b,0,n,b,0);}}
  static void HashText(ICryptoTransform h,string text){var b=Encoding.UTF8.GetBytes(text);h.TransformBlock(b,0,b.Length,b,0);}
  public static bool InteropMatchesCurrentGame(string game){
   string marker=P(game,"BepInEx/interop/assembly-hash.txt");if(!File.Exists(marker))return false;
   using(var md5=MD5.Create()){
    HashFile(md5,P(game,"GameAssembly.dll"));string libs=P(game,"BepInEx/unity-libs");if(Directory.Exists(libs))foreach(var f in Directory.EnumerateFiles(libs,"*.dll",SearchOption.TopDirectoryOnly)){HashText(md5,Path.GetFileName(f));HashFile(md5,f);}
    string map=P(game,"BepInEx/DeobfuscationMap.csv.gz");if(File.Exists(map))HashFile(md5,map);
    foreach(var name in new[]{"Il2CppInterop.Generator","Cpp2IL.Core"})using(var a=AssemblyDefinition.ReadAssembly(P(game,"BepInEx/core/"+name+".dll"),new ReaderParameters{ReadSymbols=false,ReadingMode=ReadingMode.Deferred}))HashText(md5,a.Name.Version.ToString());
    md5.TransformFinalBlock(new byte[0],0,0);return String.Equals(BitConverter.ToString(md5.Hash).Replace("-","").ToLowerInvariant(),File.ReadAllText(marker).Trim(),StringComparison.OrdinalIgnoreCase);
   }
  }
  public static HostCapabilityReport Inspect(string rawGame,string plugin){
   string game=SetupCore.SafePath(rawGame,null);SetupCore.NoReparse(game);var r=new HostCapabilityReport();
   foreach(var f in new[]{"Lilith.exe","GameAssembly.dll","UnityPlayer.dll","Lilith_Data/il2cpp_data/Metadata/global-metadata.dat"})if(!File.Exists(P(game,f)))r.Missing.Add(f);
   if(r.Missing.Count>0){r.Detail="Select the game root containing Lilith.exe and IL2CPP files.";return r;}
   if(!Pe64(P(game,"Lilith.exe"))||!Pe64(P(game,"GameAssembly.dll"))){r.State="ConfirmedIncompatible";r.Detail="The current loader/installer requires x64 PE executables.";return r;}
   using(var s=File.OpenRead(P(game,"Lilith_Data/il2cpp_data/Metadata/global-metadata.dat")))using(var b=new BinaryReader(s)){if(s.Length<8||b.ReadUInt32()!=0xFAB11BAF){r.State="ConfirmedIncompatible";r.Detail="Invalid IL2CPP metadata header.";return r;}}
   if(!NativeLilithIdentity(P(game,"Lilith_Data/il2cpp_data/Metadata/global-metadata.dat"))){r.State="NeedsHostReview";r.Detail="Required Lilith game types or the understood metadata schema could not be identified; no version hash blacklist was used.";return r;}
   r.BasicHost=true;r.GameVersion=System.Diagnostics.FileVersionInfo.GetVersionInfo(P(game,"Lilith.exe")).FileVersion;
   foreach(var f in new[]{"winhttp.dll","doorstop_config.ini","dotnet/coreclr.dll","dotnet/hostpolicy.dll","BepInEx/core/BepInEx.Core.dll","BepInEx/core/BepInEx.Unity.IL2CPP.dll","BepInEx/core/Il2CppInterop.Runtime.dll","BepInEx/core/Il2CppInterop.Generator.dll","BepInEx/core/Cpp2IL.Core.dll"})if(!File.Exists(P(game,f)))r.Missing.Add(f);
   if(r.Missing.Count>0){r.State="NeedsLoader";r.Detail="Prepare the approved loader package. Existing differing files must be preserved.";return r;}r.LoaderPresent=true;
   if(!Pe64(P(game,"winhttp.dll"))||!Pe64(P(game,"dotnet/coreclr.dll"))){r.State="ConfirmedIncompatible";r.Detail="The installed native loader or CoreCLR architecture is not x64.";return r;}
   string config=File.ReadAllText(P(game,"doorstop_config.ini"));if(!System.Text.RegularExpressions.Regex.IsMatch(config,@"(?im)^\s*enabled\s*=\s*true\s*$")||!System.Text.RegularExpressions.Regex.IsMatch(config,@"(?im)^\s*target_assembly\s*=\s*BepInEx[\\/]core[\\/]BepInEx\.Unity\.IL2CPP\.dll\s*$")){r.State="NeedsLoaderConfiguration";r.Detail="Doorstop must be enabled and target the IL2CPP loader. Existing configuration is not overwritten.";return r;}
   if(!File.Exists(P(game,"BepInEx/interop/Assembly-CSharp.dll"))){r.State="PendingFirstRun";r.Detail="Start this prepared game once without the plugin, let the loader generate interop, close it, then recheck.";return r;}
   try{r.InteropFresh=InteropMatchesCurrentGame(game);}catch(Exception e){r.Warnings.Add("Interop freshness could not be read: "+e.GetType().Name);}
   if(!r.InteropFresh){r.State="PendingRegeneration";r.Detail="Interop is missing its current-game generation association or is stale. Regenerate with the loader; do not copy another game's interop.";return r;}
   r.ContractFailures.AddRange(CheckContract(plugin,game));r.ContractPassed=r.ContractFailures.Count==0;
   if(!r.ContractPassed){r.State="ConfirmedIncompatible";r.Detail="Current generated interop is missing required plugin interfaces. See exact member failures.";return r;}
   r.State="StaticContractPassed";r.CanInstall=true;r.Detail="Required static signatures passed; runtime/game behaviour is not yet verified.";r.Warnings.Add("Trigger value 35 semantics and complete Hook/Plugin.Load behaviour need manual runtime validation; the initialization candidate has owner-scoped rollback, while actual Unity lifecycle and same-frame reload remain unverified.");return r;
  }
 }
}
