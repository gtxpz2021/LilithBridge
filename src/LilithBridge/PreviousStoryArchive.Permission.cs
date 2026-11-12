using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace LilithBridge;

internal static partial class PreviousStoryArchive
{
    internal static bool CurrentSelectionPermitted(BridgeConfig config)
    {
        try
        {
            var current = ConfigStore.Load();
            return CanSupply(config) && CanSupply(current)
                && string.Equals(Path.GetFullPath(config.DataDirectory), Path.GetFullPath(current.DataDirectory), StringComparison.OrdinalIgnoreCase)
                && string.Equals(StoryPath(config), StoryPath(current), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    internal static Func<bool>? CaptureSupplyPermission(BridgeConfig config,string context)
    {
        if(!context.Contains("[作品资料·局部节选",StringComparison.Ordinal))return null;
        try
        {
            var hashes=Regex.Matches(context,"\\\"archive_sha256\\\":\\\"([A-Fa-f0-9]{64})\\\"")
                .Cast<Match>().Select(m=>m.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if(hashes.Length!=1||!TryRead(config,out _,out string digest,out _)
                ||!string.Equals(digest,hashes[0],StringComparison.OrdinalIgnoreCase))return ()=>false;
            string root=Path.GetFullPath(config.DataDirectory),path=StoryPath(config),stamp=Stamp(path);
            return ()=>
            {
                try
                {
                    var current=ConfigStore.Load();
                    return CanSupply(current)&&string.Equals(root,Path.GetFullPath(current.DataDirectory),StringComparison.OrdinalIgnoreCase)
                        &&string.Equals(path,StoryPath(current),StringComparison.OrdinalIgnoreCase)&&stamp.Length>0&&Stamp(path)==stamp
                        &&TryRead(current,out _,out string liveDigest,out _)&&string.Equals(digest,liveDigest,StringComparison.OrdinalIgnoreCase);
                }
                catch{return false;}
            };
        }
        catch{return ()=>false;}
    }
    private static string StoryPath(BridgeConfig config)=>Path.GetFullPath(Path.IsPathRooted(config.PreviousStoryFile)
        ?config.PreviousStoryFile:Path.Combine(config.DataDirectory,config.PreviousStoryFile));
    private static string Stamp(string path)
    {var f=new FileInfo(path);return f.Exists?f.Length+":"+f.CreationTimeUtc.Ticks+":"+f.LastWriteTimeUtc.Ticks:"";}
}
