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
        var metadataSnapshots=sources.Select(source=>source.ReadMetadata).ToArray();
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
        var s=new StringBuilder("[未完整展示的草稿回复（参考草稿，未展示部分玩家未看到）]\n");var slices=new List<GeneratedReadableSlice>();
        void Line(string value="")=>s.Append(value).Append('\n');
        for(int record=0;record<sources.Count;record++)
        {
            var source=sources[record];var metadata=metadataSnapshots[record];string body=source.BodyText;
            if(metadata.BodyChars!=body.Length || metadata.BodyUtf8Bytes!=Encoding.UTF8.GetByteCount(body)
                || !Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).Equals(source.BodyHash,StringComparison.OrdinalIgnoreCase))
            {reason="generated_body_identity_mismatch";return false;}
            string localTime = metadata.CapturedAtUtc == default ? "" : metadata.CapturedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            Line($"〔草稿{record + 1}{(localTime.Length > 0 ? " | " + localTime : "")}〕{(pageChars >= 0 ? "（分页原文）" : "")}");
            var confirmed=new List<GeneratedReplyProjection.ConfirmedRange>();
            var other=new List<GeneratedReplyDisplayRange>();
            foreach(var range in metadata.ShownRanges)
            {
                if(GeneratedReplyProjection.TryConfirmedRanges(source,metadata,range,out var pieces))
                    confirmed.AddRange(pieces);
                else other.Add(range);
            }
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
            var rawSegments=new List<(bool Shown, int Start, int Count, int SliceIndex)>();
            for(int i=0;i<boundaries.Length-1;i++)
            {
                int start=boundaries[i],count=boundaries[i+1]-start;
                if(ends.TryGetValue(start,out var removing))foreach(int id in removing)active.Remove(id);
                if(starts.TryGetValue(start,out var adding))foreach(int id in adding)active.Add(id);
                if(count==0)continue;
                string piece = body.Substring(start, count);
                if (string.IsNullOrWhiteSpace(piece) && active.Count > 0) continue;
                bool isShown = active.Count > 0;
                string label="接收记录"+(record+1)+"·片段"+(++segment);
                int idx = slices.Count;
                slices.Add(new(record,start,count,0,label,active.Select(id=>confirmed[id].EventId).ToArray()));
                rawSegments.Add((isShown, start, count, idx));
            }
            int pos = 0;
            while (pos < rawSegments.Count)
            {
                bool shown = rawSegments[pos].Shown;
                int runStart = rawSegments[pos].Start;
                int runEnd = runStart + rawSegments[pos].Count;
                var runSliceIndices = new List<int> { rawSegments[pos].SliceIndex };
                pos++;
                while (pos < rawSegments.Count && rawSegments[pos].Shown == shown
                    && string.IsNullOrWhiteSpace(body.Substring(runEnd, rawSegments[pos].Start - runEnd)))
                {
                    runEnd = rawSegments[pos].Start + rawSegments[pos].Count;
                    runSliceIndices.Add(rawSegments[pos].SliceIndex);
                    pos++;
                }
                string mergedText = body.Substring(runStart, runEnd - runStart);
                if (string.IsNullOrWhiteSpace(mergedText)) continue;
                string prefix = shown ? "- 已展示：" : "- 未展示/未确认：";
                s.Append(prefix);
                int textStart = s.Length;
                s.Append(mergedText);
                Line();
                foreach (int si in runSliceIndices)
                {
                    var old = slices[si];
                    slices[si] = old with { TextStart = textStart + (old.Start - runStart) };
                }
            }
            if(pageChars==0)Line("（本次分页为空）");
            if(other.Count>0)Line($"（另有 {other.Count} 条未确定对应关系的显示片段）");
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
                    if (text.StartsWith("[程序提供的近期对话与背景", StringComparison.Ordinal))
                    {
                        if (ContainsExactlyOnce(text)) count++;
                        continue;
                    }
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
