using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace LilithBridge;

internal sealed partial class MemoryStore
{
    private sealed record RawCorrection(string OldFragment, string Source);
    private readonly Dictionary<string,List<string>> _readSourceCorrections=new(StringComparer.Ordinal);
    private readonly Dictionary<string,List<string>> _readPossibleSourceCorrections=new(StringComparer.Ordinal);
    private string _rawCorrectionReadSignature="";

    // Complete old words identify a source; a fragment only discovers candidates.
    // Never drop a logical operator or infer scope to change current eligibility.
    private static string CanonicalCorrectionUtterance(string s)=>Regex.Replace(s.Trim(),@"\s+"," ").TrimEnd('。','.');
    private static bool IsAuthoritativeStoredCorrection(MemoryTurn c)=>c.Kind!="source.correction";
    private static RawCorrection? ParseRawSourceCorrection(string source)
    {
        if(UtteranceAttribution.Reported("user",source)!=null || Regex.IsMatch(source,@"[？?]"))return null;
        var m=Regex.Match(source.Trim(),
            @"^(?:(?:先)?(?:纠正|更正|修正)(?:一下|一个信息|一点|一处|个信息)?[：:，,]\s*)?我(?:前面|之前|刚才|先前|原来)(?:说|提到|描述)(?:的)?(?:“(?<quoted>[^”]{4,512})”|(?<old>[^，,。！？?；;：:]{4,128}?))(?:不准确|说错了|不对|有误)[，,：:]\s*(?<new>.+)$",
            RegexOptions.Singleline|RegexOptions.CultureInvariant);
        if(!m.Success)return null;
        if(Regex.IsMatch(m.Groups["new"].Value,@"引用|转述|别人的|(?:不是|并非|并不是|没有|不要|不必|并不).{0,8}(?:更正|纠正|修正|修改|改动)|^(?:如果|假如|假设|是否|会不会)"))return null;
        string old=m.Groups["quoted"].Success?m.Groups["quoted"].Value:m.Groups["old"].Value.Trim('“','”','"','「','」',' ');
        return old.Length>=4?new(old,source):null;
    }
    private static bool ExactCorrectionTarget(MemoryTurn t,RawCorrection intent)
        =>CanonicalCorrectionUtterance(t.Text)==CanonicalCorrectionUtterance(intent.OldFragment);

    // Loose matching may collect a question, conditional or quotation beside
    // later words. Its result is unbound and cannot alter current or permissions.
    private static bool PossibleCorrectionTarget(MemoryTurn t,RawCorrection intent)
    {
        string text=Regex.Replace(t.Text,@"[^\p{L}\p{N}]","").ToLowerInvariant();
        string old=Regex.Replace(intent.OldFragment,@"[^\p{L}\p{N}]","").ToLowerInvariant();
        if(old.Length<4)return false;
        if(text.Contains(old,StringComparison.Ordinal))return true;
        for(int split=2;split<=old.Length-2;split++)
        {
            string left=old[..split],right=old[split..];
            for(int at=text.IndexOf(left,StringComparison.Ordinal);at>=0;at=text.IndexOf(left,at+1,StringComparison.Ordinal))
            {int end=at+left.Length;for(int gap=1;gap<=4&&end+gap+right.Length<=text.Length;gap++)
                if(text.AsSpan(end+gap,right.Length).SequenceEqual(right.AsSpan()))return true;}
        }
        return false;
    }
    private static List<MemoryTurn> RawCorrectionTargets(MemoryTurn input,RawCorrection intent,IEnumerable<MemoryTurn> turns,bool exact)
        =>turns.Where(t=>t.Role=="user"&&t.Origin=="user"&&t.DeliveryState=="received"&&t.EventId!=input.EventId&&t.Timestamp<input.Timestamp
            &&ParseRawSourceCorrection(t.Text)==null&&(exact?ExactCorrectionTarget(t,intent):PossibleCorrectionTarget(t,intent)))
            .GroupBy(t=>t.EventId,StringComparer.Ordinal).Select(g=>g.First()).ToList();

    private UserMemoryOperationResult? TryApplyRawSourceCorrection(MemoryTurn input)
    {
        var intent=ParseRawSourceCorrection(input.Text);if(intent==null)return null;
        lock(_experienceLock)
        {
            if(_autonomous.Any(t=>t.OperationId==input.EventId&&t.RequestKind=="correct"&&IsAuthoritativeStoredCorrection(t)))
                return new(true,"already_applied","这条来源更正已记录，仍按现存版本读取。",ContinueDialogue:true);
            var all=ReadExperiences();var targets=RawCorrectionTargets(input,intent,all,true);
            if(targets.Count!=1)
            {
                int possible=RawCorrectionTargets(input,intent,all,false).Count;
                return new(false,targets.Count>1||possible>1?"source_target_ambiguous":possible>0?"source_fragment_unbound":"source_target_not_found",
                    "完整旧原话尚不能唯一确认；保留后来原话作为可能相关材料，不改变旧来源或判断的当前资格。",Candidates:targets.Count>1?targets.Count:possible,ContinueDialogue:true);
            }
            if(!_longTermEnabled||!_experienceArchiveEnabled||!VerifyUserEvidence(input.EventId,input.Text))return new(false,"source_unavailable","更正尚未可靠记录。",ContinueDialogue:true);
            var target=targets[0];
            if(!IsMentionAllowed(target)||!IsNativeSupplyAllowed(target)||SuppressedExperienceIds().Contains(target.EventId))return new(false,"source_unavailable","旧来源当前不可用。",ContinueDialogue:true);
            var link=new MemoryTurn{EventId=Guid.NewGuid().ToString("N"),RecallId="source-"+target.EventId,Revision=1,OperationId=input.EventId,
                SourceEventId=input.EventId,SourceEventIds=new[]{target.EventId,input.EventId},RequestKind="correct",Kind="source.full_correction",
                Role="user",Origin="user",Source="explicit_user",Text=input.Text,Evidence=input.Text,Validity="user_reported",LifecycleState="active",Tier="archival",Timestamp=input.Timestamp};
            _autonomous.Add(link);
            if(!SaveList("v4-native-autonomous-memory.json",_autonomous)){_autonomous.Remove(link);return new(false,"persistence_failed","来源关系未保存；原话未改。",ContinueDialogue:true);}
            return new(true,"source_corrected","已连接完整旧原话与用户更正，保留原文；这不证明新说法为事实。",link.RecallId,link.Revision,Match:"exact_complete_utterance",Candidates:1,ContinueDialogue:true);
        }
    }
    private void BuildRawSourceCorrectionReadView()
    {
        var turns=_b1ReadTurns.Values.Where(t=>t.Role=="user"&&t.Origin=="user"&&t.DeliveryState=="received").ToList();
        string signature=B1Hash(string.Join("|",turns.OrderBy(t=>t.EventId,StringComparer.Ordinal).Select(t=>t.EventId+":"+t.Text)));
        if(signature==_rawCorrectionReadSignature)return;_rawCorrectionReadSignature=signature;
        _readSourceCorrections.Clear();_readPossibleSourceCorrections.Clear();
        void Add(Dictionary<string,List<string>> map,string old,string later){if(!map.TryGetValue(old,out var list))map[old]=list=new();list.Add(later);}
        foreach(var input in turns)
        {
            var intent=ParseRawSourceCorrection(input.Text);if(intent==null)continue;
            var exact=RawCorrectionTargets(input,intent,turns,true);
            if(exact.Count==1)Add(_readSourceCorrections,exact[0].EventId,input.EventId);
            foreach(var possible in RawCorrectionTargets(input,intent,turns,false))
                if(exact.Count!=1||possible.EventId!=exact[0].EventId)Add(_readPossibleSourceCorrections,possible.EventId,input.EventId);
        }
    }
    private bool HasRawSourceCorrection(string id)=>_readSourceCorrections.TryGetValue(id,out var later)&&later.Count>0;
    private bool IsExactReadCorrection(string old,string later)=>_readSourceCorrections.TryGetValue(old,out var ids)&&ids.Contains(later);
}
