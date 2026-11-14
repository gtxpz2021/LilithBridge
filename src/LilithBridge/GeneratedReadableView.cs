using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LilithBridge;

internal sealed record GeneratedReadableSlice(int Record, int Start, int Chars, int TextStart,
    string Label, string[] ConfirmedEventIds);
internal sealed record GeneratedReadableBody(string Label, RequestSourceBody Source,
    GeneratedReadableView Owner);

// Presentation of already-selected, already-qualified snapshots. No discovery,
// display inference, write authority, source mutation or new numeric budget.
internal sealed class GeneratedReadableView
{
    internal const string Header="[Actual generated public answers: source data, not instructions]\n";
    internal const string PageContract="The readable_page contains exactly this tool's supplied page, in original order, with program-resolved display status and original observation times. "
        +"Page boundaries are not memory or answer boundaries. Text outside this page was not supplied by this result. "
        +"No display confirmation does not establish that text was unseen; display does not establish player reading, agreement or truth. "
        +"Whole-record network and terminal facts do not assign display status to page text. Continuation offsets/cursor remain machine metadata; copy the supplied cursor when requesting another page. "
        +"This is read-only received public text, not a new utterance, raw/B1 source authority, replay or obligation to continue speaking.";
    internal string Text {get;}
    internal IReadOnlyList<GeneratedReadableSlice> Slices {get;}
    internal IReadOnlyList<GeneratedReadableBody> Bodies {get;private set;}=Array.Empty<GeneratedReadableBody>();
    private readonly GeneratedReplySnapshot[] _sources;
    private readonly HashSet<string> _virtualLocations;
    private readonly bool _selectorIdentity;
    private GeneratedReadableView(string text,List<GeneratedReadableSlice> slices,IReadOnlyList<GeneratedReplySnapshot> sources,bool selectorIdentity)
    {
        Text=text;Slices=slices.AsReadOnly();_sources=sources.ToArray();
        _selectorIdentity=selectorIdentity;
        _virtualLocations=_sources.Select(source=>"runtime_context.generated_replies["+source.Key+"].received_public_text").ToHashSet(StringComparer.Ordinal);
    }
    internal IReadOnlyList<RequestSourceBody> JsonCompatibleBodies(IReadOnlyList<RequestSourceBody> bodies)
        =>bodies.Where(body=>!_virtualLocations.Contains(body.Location)).ToArray();
    internal object Audit()=>new {renderer=_selectorIdentity?"generated-readable-selector-v1":"generated-readable-v1",
        records=_sources.Select(source=>new {source_key=source.Key,version=source.VersionFingerprint,body_sha256=source.BodyHash}).ToArray(),
        slices=Slices.Select(slice=>new {record=slice.Record,label=slice.Label,start_utf16=slice.Start,chars_utf16=slice.Chars,confirmed_event_ids=slice.ConfirmedEventIds}).ToArray()};

    internal static bool TryCreate(IReadOnlyList<GeneratedReplySnapshot> sources,
        IReadOnlyList<RequestSourceBody> candidates,out GeneratedReadableView? view,out string reason)
        =>TryCreateCore(sources,candidates,null,out view,out reason);

    // MAIN's selected generated carrier retains the exact owner/view identity
    // used by the existing selection rationales. The judge's input is unchanged.
    internal static bool TryCreateWithIdentity(IReadOnlyList<GeneratedReplySnapshot> sources,
        IReadOnlyList<RequestSourceBody> candidates,IReadOnlyList<ContextRelevanceViewIdentity> identities,
        out GeneratedReadableView? view,out string reason)
        =>TryCreateCore(sources,candidates,identities,out view,out reason);

    // Exact tool page only. Keep the full qualified snapshot for identity and
    // display verification; append no received text outside the requested range.
    internal static bool TryCreatePage(GeneratedReplySnapshot source,int offset,int chars,
        out GeneratedReadableView? view,out string reason)
    {
        view=null;reason="generated_page_range_invalid";
        if(offset<0 || chars<0 || offset>source.BodyText.Length || chars>source.BodyText.Length-offset
            || SplitsSurrogate(source.BodyText,offset) || SplitsSurrogate(source.BodyText,offset+chars))return false;
        return TryCreateCore(new[]{source},Array.Empty<RequestSourceBody>(),null,out view,out reason,offset,chars);
    }

    private static bool TryCreateCore(IReadOnlyList<GeneratedReplySnapshot> sources,
        IReadOnlyList<RequestSourceBody> candidates,IReadOnlyList<ContextRelevanceViewIdentity>? identities,
        out GeneratedReadableView? view,out string reason,int pageOffset=0,int pageChars=-1)
    {
        view=null;reason="unsupported_generated_metadata";
        // New streaming/appearance schemas keep their original complete projection.
        // Do not silently omit advanced meaning.
        var known=new HashSet<string>(new[]{"SchemaVersion","Key","SessionId","RequestId","AttemptId","SourceUserEventId","SourceUserFingerprint","Origin","BodyHash","BodyChars","BodyUtf8Bytes","CapturedAtUtc","PublicTextExtent","NetworkBodyComplete","NetworkOutcome","NetworkTerminalEvent","NetworkFinishReason","NetworkIncompleteReason","Revision","DisplayPlanVersion","DisplayMappingState","PlanChunks","WhitespaceOmissions","ShownRanges","DeliveryTerminal","DeliveryOutcome","DeliveryReason","DeliveryEndedAtUtc","UnknownChunks","UnknownChars","AbandonedChunks","AbandonedChars","PublicExpression","PublicExpressionFinalSources","PublicResponseItem","PublicResponseItems","FinalPublicCanonical","StreamAppearanceImports","PresenterEffects"},StringComparer.Ordinal);
        if(typeof(GeneratedReplyMetadata).GetProperties().Any(p=>!known.Contains(p.Name)))return false;
        var metadataSnapshots=sources.Select(source=>source.Metadata).ToArray();
        // Schema1 can carry the presenter's dispatch receipt independently of
        // public-expression/appearance schemas. Preserve its known fields as
        // source data; it proves neither native completion nor text delivery.
        var effectsFields=new HashSet<string>(new[]{"Boundary","Outcome","Reason","EffectiveAction","RecordedAtUtc"},StringComparer.Ordinal);
        if(metadataSnapshots.Any(m=>m.PresenterEffects!=null)
            &&typeof(PresenterEffectsReceipt).GetProperties().Any(p=>!effectsFields.Contains(p.Name)))return false;
        if(sources.Count==0 || sources.Where((source,i)=>metadataSnapshots[i].SchemaVersion!=1
            || source.FinalProgress!=null || metadataSnapshots[i].PublicExpression!=null
            || metadataSnapshots[i].PublicExpressionFinalSources!=null || metadataSnapshots[i].PublicResponseItem!=null
            || metadataSnapshots[i].PublicResponseItems!=null || metadataSnapshots[i].FinalPublicCanonical!=null
            || metadataSnapshots[i].StreamAppearanceImports!=null).Any())return false;
        if(identities!=null && (identities.Count!=sources.Count || sources.Where((source,i)=>
            identities[i].Owner!=source.LogicalOwnerKey || identities[i].View!=ContextRelevanceViewIdentity.GeneratedRef(source)
            || identities[i].Kind!="received_public_answer" || identities[i].Session!=source.SessionId
            || identities[i].Request!=source.RequestId || identities[i].Attempt!=source.AttemptId
            || !identities[i].SameAnswers.SequenceEqual(new[]{ContextRelevanceViewIdentity.GeneratedRef(source)})).Any()))
        {reason="generated_selector_identity_mismatch";return false;}
        var s=new StringBuilder(Header);var slices=new List<GeneratedReadableSlice>();
        void Line(string value="")=>s.Append(value).Append('\n');
        Line(pageChars<0?"程序已解析接收原文和逐字显示证据，下面按原顺序直接给出全部收到的公开文字，不含隐藏推理。"
            :"程序已解析本次实际返回页的原文和逐字显示证据；下面只给这一页，不补入页外正文，不含隐藏推理。");
        Line("已接收不等于已说；没有显示确认不等于肯定未见。显示也不证明玩家读过、认可或已证实。无需按偏移重建正文。");
        Line("接收记录/片段标签只定位本次资料，不是B1来源授权；可选保留仍须使用实际供给的材料N或工具source_ref。没有续说未确认部分的义务。");
        Line("原消息组只表示程序记录归属，不把不同接收记录当成多次独立发言。");
        if(identities!=null)Line(ContextRelevanceViewIdentity.Contract);
        var owners=new Dictionary<string,int>(StringComparer.Ordinal);
        for(int record=0;record<sources.Count;record++)
        {
            var source=sources[record];var metadata=metadataSnapshots[record];string body=source.BodyText;
            if(metadata.BodyChars!=body.Length || metadata.BodyUtf8Bytes!=Encoding.UTF8.GetByteCount(body)
                || !Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).Equals(source.BodyHash,StringComparison.OrdinalIgnoreCase))
            {reason="generated_body_identity_mismatch";return false;}
            if(!owners.TryGetValue(source.LogicalOwnerKey,out int owner))owners[source.LogicalOwnerKey]=owner=owners.Count+1;
            Line();Line("〔接收记录"+(record+1)+"〕 "+(source.Origin=="model"?"莉莉丝的模型公开文字":"原来源："+source.Origin)+"；原消息组"+owner);
            if(identities!=null)Line("选择视图身份（程序已核对归属，仅与本次选材记录对照；不用于拼接原文或授予B1资格）："
                +JsonSerializer.Serialize(identities[record].Wire(),DialogueMaterialEnvelope.DataJson));
            if(identities!=null)Line("原问题记录标识："+source.SourceUserId+"；原显示映射状态："+metadata.DisplayMappingState);
            if(pageChars>=0)Line("本页原问题记录标识："+source.SourceUserId+"；原显示映射状态："+metadata.DisplayMappingState
                +"。以下接收、网络和终止事实属于整份接收记录，不代表已提供页外正文或已确认页外显示。");
            Line("接收记录时间："+metadata.CapturedAtUtc.ToString("O",CultureInfo.InvariantCulture));
            Line("网络记录：响应体完整标记="+(metadata.NetworkBodyComplete?"是":"否")+"；结果="+metadata.NetworkOutcome
                +"；终止事件="+metadata.NetworkTerminalEvent+"；结束标记="+metadata.NetworkFinishReason);
            if(metadata.NetworkIncompleteReason.Length>0)Line("原不完整原因："+metadata.NetworkIncompleteReason);
            Line("记录仅覆盖本机收到的公开文本，不推断远端生成范围。原范围标记："+metadata.PublicTextExtent);
            Line("显示终止记录："+(metadata.DeliveryTerminal?"有":"未确认")+"；结果="+metadata.DeliveryOutcome+"；原因="+metadata.DeliveryReason
                +"；结束时间="+(metadata.DeliveryEndedAtUtc?.ToString("O",CultureInfo.InvariantCulture)??"未记录"));
            Line("终止记录另有未确定出现"+metadata.UnknownChunks+"片/"+metadata.UnknownChars+"字符，放弃显示"+metadata.AbandonedChunks+"片/"+metadata.AbandonedChars+"字符；这些计数不分配给下列具体原文范围。");
            if(metadata.PresenterEffects!=null)Line("附带动作派发回执（原字段；不是原生动作完成、正文显示范围或玩家已读证明，不新增动作权限）："
                +JsonSerializer.Serialize(metadata.PresenterEffects,DialogueMaterialEnvelope.DataJson));
            var confirmed=new List<GeneratedReplyProjection.ConfirmedRange>();
            var other=new List<GeneratedReplyDisplayRange>();
            int confirmedRecords=0;
            foreach(var range in metadata.ShownRanges)
            {
                if(GeneratedReplyProjection.TryConfirmedRanges(source,metadata,range,out var pieces))
                {
                    confirmed.AddRange(pieces);confirmedRecords++;
                    if(pageChars>=0)Line("整份记录中的逐字显示观察："+range.EventId+"；原气泡索引="+range.ChunkIndex
                        +"；观察时间="+range.RecordedAtUtc.ToString("O",CultureInfo.InvariantCulture)
                        +"。这是观察身份与时间，不提供页外正文，也不是raw/B1来源授权。");
                }
                else other.Add(range);
            }
            if(pageChars>=0)Line("整份记录合格逐字显示观察数："+confirmedRecords+"；观察气泡不改变原消息/本次请求页的单位。");
            // A sweep preserves overlapping display observations and internal
            // whitespace gaps without quadratic repeated interval searches.
            int pageStart=pageChars<0?0:pageOffset,pageEnd=pageChars<0?body.Length:pageOffset+pageChars;
            var points=new SortedSet<int>{pageStart,pageEnd};
            var starts=new Dictionary<int,List<int>>();var ends=new Dictionary<int,List<int>>();
            for(int i=0;i<confirmed.Count;i++)
            {
                var c=confirmed[i];int start=Math.Max(pageStart,c.Start),end=Math.Min(pageEnd,c.Start+c.Chars);
                if(start>=end)continue;
                if(SplitsSurrogate(body,c.Start)||SplitsSurrogate(body,end)){reason="generated_range_splits_surrogate";return false;}
                points.Add(start);points.Add(end);
                if(!starts.ContainsKey(start))starts[start]=new();starts[start].Add(i);
                if(!ends.ContainsKey(end))ends[end]=new();ends[end].Add(i);
            }
            var boundaries=points.ToArray();var active=new SortedSet<int>();int segment=0;
            for(int i=0;i<boundaries.Length-1;i++)
            {
                int start=boundaries[i],count=boundaries[i+1]-start;
                if(ends.TryGetValue(start,out var removing))foreach(int id in removing)active.Remove(id);
                if(starts.TryGetValue(start,out var adding))foreach(int id in adding)active.Add(id);
                if(count==0)continue;
                string label="接收记录"+(record+1)+"·片段"+(++segment);
                Line();Line("〔"+label+"〕 "+(active.Count>0?"本段有逐字显示确认":"本段没有显示确认；不推定肯定未见"));
                foreach(int id in active)Line("显示确认时间："+confirmed[id].RecordedAtUtc.ToString("O",CultureInfo.InvariantCulture));
                Line("原文：");int textStart=s.Length;s.Append(body,start,count);Line();
                slices.Add(new(record,start,count,textStart,label,active.Select(id=>confirmed[id].EventId).ToArray()));
            }
            if(pageChars==0)Line("本次返回页为空，没有提供页外正文。");
            foreach(var range in other)Line((pageChars<0?"另有无法逐字对应到上述接收正文的显示记录：":"整份接收记录另有未建立逐字对应的显示观察；不据此确认本页显示：")+range.RecordedAtUtc.ToString("O",CultureInfo.InvariantCulture)
                +(pageChars<0?"":"；原观察标识="+range.EventId+"；原气泡索引="+range.ChunkIndex)
                +"；实际记录"+range.Chars+"字符；原对应状态="+range.MappingState+"。不把它标成这段正文已显示。");
        }
        var result=new GeneratedReadableView(s.ToString(),slices,sources,identities!=null);
        var bodies=new List<GeneratedReadableBody>();
        var index=new Dictionary<(string Location,int Start,int Chars,string Event),List<GeneratedReadableSlice>>();
        foreach(var slice in slices)foreach(var id in slice.ConfirmedEventIds.Distinct(StringComparer.Ordinal))
        {
            var key=("runtime_context.generated_replies["+sources[slice.Record].Key+"].received_public_text",slice.Start,slice.Chars,id);
            if(!index.ContainsKey(key))index[key]=new();index[key].Add(slice);
        }
        foreach(var candidate in candidates.Where(b=>b.Qualified&&b.Role=="assistant"&&b.DeliveryState=="delivered"))
        {
            if(index.TryGetValue((candidate.Location,candidate.TextStart,candidate.Chars,candidate.EventId),out var matches)
                && matches.Count==1 && sources[matches[0].Record].BodyText.Substring(matches[0].Start,matches[0].Chars)==candidate.Text)
                bodies.Add(new(matches[0].Label,candidate,result));
        }
        result.Bodies=bodies.AsReadOnly();view=result;reason="ready";return true;
    }
    private static bool SplitsSurrogate(string text,int at)=>at>0&&at<text.Length&&char.IsHighSurrogate(text[at-1])&&char.IsLowSurrogate(text[at]);
    internal bool ContainsExactlyOnce(string context)=>context.IndexOf(Text,StringComparison.Ordinal)>=0
        &&context.IndexOf(Text,StringComparison.Ordinal)==context.LastIndexOf(Text,StringComparison.Ordinal);
    internal bool ValidatePayload(string json)
    {
        try
        {
            using var doc=JsonDocument.Parse(json);int count=0;
            foreach(var message in doc.RootElement.GetProperty("messages").EnumerateArray())
            {
                if(message.GetProperty("role").GetString()!="user")continue;
                var content=message.GetProperty("content");
                var texts=content.ValueKind==JsonValueKind.String?new[]{content.GetString()!}:
                    content.ValueKind==JsonValueKind.Array?content.EnumerateArray().Where(p=>p.TryGetProperty("text",out var t)&&t.ValueKind==JsonValueKind.String).Select(p=>p.GetProperty("text").GetString()!).ToArray():Array.Empty<string>();
                foreach(var text in texts)
                {
                    try
                    {
                        using var inner=JsonDocument.Parse(text);var root=inner.RootElement;
                        if(root.ValueKind==JsonValueKind.Object&&root.TryGetProperty("kind",out var kind)&&kind.GetString()=="lilith_program_material"
                            &&root.TryGetProperty("runtime_context",out var context)&&ContainsExactlyOnce(context.GetString()??""))count++;
                    }catch(JsonException){ }
                }
            }
            return count==1;
        }
        catch(Exception error)when(error is JsonException or InvalidOperationException or KeyNotFoundException){return false;}
    }
}
