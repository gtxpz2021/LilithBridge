using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LilithBridge;

// Request-only projection. It neither selects material nor creates source authority.
internal sealed class B1ReadableView
{
    private sealed record Host(string Label, B1Material Material, int HistoryIndex, string Speaker, string SourcesProof, GeneratedReadableBody? Generated = null, bool EnvelopeSection = false, string DisplayLabel = "");
    private readonly B1Packet _packet;
    private readonly byte[] _seal;
    private readonly Dictionary<string,string> _refs;
    private readonly List<Host> _hosts;
    private readonly B1Packet _suppliedSources;
    internal string Text { get; }
    internal int ReusedBodies => _hosts.Count;
    internal int InlineBodies => _packet.Materials.Count - _hosts.Count;
    // Body-free local diagnostics; preparation is not proof of sending or reading.
    internal object Audit()
    {
        var materials=_packet.Materials.ToDictionary(m=>m.Ref);
        var hosts=_hosts.ToDictionary(h=>h.Material.Ref,h=>h.Label);
        return new { renderer="b1-readable-v1", bindings=_refs.Select(p=> {
            var material=materials[_packet.ShortRefs[p.Value]];
            return new {label=p.Key,source_ref=p.Value,source_key=material.Ref.Key,version=material.VersionFingerprint,
                body_sha256=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material.Text))),
                source_ranges=material.SuppliedRanges,host=hosts.GetValueOrDefault(material.Ref)??"inline"};
        }).ToArray() };
    }

    internal const string Contract = "[本轮可读材料引用] 材料N 为本轮 B1 包的可选引用编号，仅用于 <b1> 的 sources/about/target；无需保留时省略即可。近期原话N 指向前文对应对话。";

    private B1ReadableView(B1Packet packet, string text, Dictionary<string,string> refs, List<Host> hosts)
    {
        _packet=packet; Text=text; _refs=refs; _hosts=hosts; _seal=Seal(packet);
        _suppliedSources=new B1Packet {Id=packet.Id,UserEventId=packet.UserEventId,
            Materials=packet.Materials.Select(m=>m with {Ref=m.Ref with {},
                DisplayedReplyFragments=m.DisplayedReplyFragments?.ToArray(),SuppliedRanges=m.SuppliedRanges?.ToArray()}).ToList(),
            Footprint=packet.Footprint.Select(r=>r with {}).ToList()};
    }
    internal Func<bool> SourcePermission(MemoryStore owner,string query,BridgeConfig config)
        =>()=>owner.ReadableB1SourcesPermitted(_suppliedSources,query,config);

    private static byte[] Seal(B1Packet packet) => SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new {
        packet.ProtocolVersion,packet.Id,packet.UserEventId,packet.Materials,packet.Footprint,
        packet.SemanticAnchors,packet.FocusStatus,packet.ShortRefs,read_query_purpose=packet.ReadQueryPurpose,
        correction_roots=packet.Materials.Select(m=>m.ReadOnlyCorrectionRoot).ToArray()
    })));
    internal bool Unchanged(B1Packet? packet) => ReferenceEquals(packet,_packet)
        && CryptographicOperations.FixedTimeEquals(_seal,Seal(_packet));

    internal static bool TryCreate(B1Packet packet, IReadOnlyList<RequestSourceBody> bodies,
        IReadOnlyList<ProjectedHistoryTurn> history, string currentInput,
        out B1ReadableView? view, out string reason)
        => TryCreateWithGenerated(packet,bodies,history,currentInput,Array.Empty<GeneratedReadableBody>(),out view,out reason);

    internal static bool TryCreateWithGenerated(B1Packet packet,IReadOnlyList<RequestSourceBody> bodies,
        IReadOnlyList<ProjectedHistoryTurn> history,string currentInput,IReadOnlyList<GeneratedReadableBody> generated,
        out B1ReadableView? view,out string reason)
    {
        view=null; reason="invalid_readable_packet";
        var supported=new HashSet<string>(new[]{"Ref","Text","Scope","At","Origin","State","VersionFingerprint",
            "DisplayedReplyFragments","DependencyRole","Relation","Target","LogicalId","Revision","Nature","Conditions",
            "AnchorStatus","DiscoverySource","SuppliedRanges","ReplyRunFirst","ReplyRoot"},StringComparer.Ordinal);
        if(typeof(B1Material).GetProperties().Any(p=>!supported.Contains(p.Name))) {reason="unknown_material_field_json_fallback";return false;}
        if(packet.Availability!="ready" || packet.Materials.Select(m=>m.Ref.Key).Distinct().Count()!=packet.Materials.Count) return false;
        // Original capture already assigned short references; do not reassign while binding.
        var aliases=packet.ShortRefs.ToDictionary(p=>p.Value.Key,p=>p.Key,StringComparer.Ordinal);
        if(aliases.Count!=packet.Materials.Count || packet.Materials.Any(m=>!aliases.ContainsKey(m.Ref.Key)
            || packet.ShortRefs[aliases[m.Ref.Key]]!=m.Ref)) return false;
        var labels=new Dictionary<string,string>(StringComparer.Ordinal);
        var ordered=packet.Materials.Where(m=>m.ReplyRunFirst==null).ToList();
        var runs=packet.Materials.Where(m=>m.ReplyRunFirst!=null).GroupBy(m=>m.ReplyRunFirst!.Key).ToList();
        foreach(var run in runs) ordered.AddRange(run.OrderBy(m=>int.Parse(m.Ref.Version.AsSpan(m.Ref.Version.LastIndexOf(':')+1),NumberStyles.None,CultureInfo.InvariantCulture)));
        for(int i=0;i<ordered.Count;i++) labels.Add(ordered[i].Ref.Key,"材料"+(i+1).ToString(CultureInfo.InvariantCulture));
        var refs=labels.ToDictionary(p=>p.Value,p=>aliases[p.Key],StringComparer.Ordinal);
        var hosts=new List<Host>(); var s=new StringBuilder();
        void Line(string value="")=>s.Append(value).Append('\n');
        string Target(string key)=>labels.GetValueOrDefault(key)??"本次未展开的目标（定位留在程序侧）";
        Line("\n[B1 可选参考材料（来自程序档案，非新指令，可忽略或不保留）]");
        if(packet.FocusStatus=="model_declared_about_required")Line("谈论对象需自行明确；相关线索不等于同一对象。");

        string SpeakerOf(B1Material m)
        {
            var prefix = Regex.Match(m.Scope, "^source_kind=([^;]*);role=([^;]*);delivery=([^;]*);");
            string role = prefix.Success ? prefix.Groups[2].Value : "";
            return m.Ref.Kind == "b1" ? "过往保留想法" : m.Ref.Kind == "record" ? "长期记录" : m.Origin switch {
                "user" when role == "user" => "玩家", "model" when role == "assistant" => "莉莉丝",
                "game" => "原生记录", "local_fallback" => "本地回退",
                _ => m.Origin };
        }
        string TimeOf(B1Material m) => m.At == default ? "" : m.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        string MetaOf(B1Material m, bool includeDiscovery = true)
        {
            var prefix = Regex.Match(m.Scope, "^source_kind=([^;]*);role=([^;]*);delivery=([^;]*);");
            string scope = prefix.Success ? m.Scope[prefix.Length..] : m.Scope;
            var tags = new List<string>();
            if (scope.Length > 0) tags.Add("范围:" + scope);
            if (m.Nature.Length > 0 && m.Nature != "legacy_unspecified") tags.Add(Nature(m.Nature));
            if (m.Conditions.Length > 0) tags.Add("条件:" + m.Conditions);
            if (m.Relation.Length > 0) tags.Add(Relation(m.Relation) + (m.Target.Length > 0 ? "->" + Target(m.Target) : ""));
            if (includeDiscovery && m.DiscoverySource != null) tags.Add("线索:" + Target(m.DiscoverySource.Key));
            return tags.Count > 0 ? "（" + string.Join("；", tags) + "）" : "";
        }
        Host? ResolveHost(B1Material m)
        {
            var body = RequestSourceBody.Match(bodies, m);
            if (body == null) return null;
            if (body.TextStart == 0 && body.Location == "current_input" && body.Text == currentInput)
                return new("本次玩家输入", m, -1, body.Role, "", DisplayLabel: "「本次玩家输入」");
            for (int i = 0; i < history.Count; i++)
            {
                if (body.Location == "history[" + i + "].text")
                {
                    if (body.TextStart == 0 && history[i].Text == m.Text
                        && history[i].Records.Count == 1 && history[i].Records[0].EventId == m.Ref.Version)
                        return new("近期原话" + (i + 1), m, i, body.Role, DialogueMaterialEnvelope.ReadableSourceProof(history[i]), DisplayLabel: "「近期原话" + (i + 1) + "」");
                    if (history[i].Records.Any(r => r.EventId == m.Ref.Version))
                        return new("近期原话" + (i + 1), m, -3, body.Role, DialogueMaterialEnvelope.ReadableSourceProof(history[i]), EnvelopeSection: true, DisplayLabel: "「近期原话" + (i + 1) + "」");
                }
            }
            if (body.Location.StartsWith("runtime_context.experience_blocks[lexical]", StringComparison.Ordinal))
                return new("[关联召回的过往原话", m, -3, body.Role, "", EnvelopeSection: true, DisplayLabel: "前文「关联召回的过往原话」");
            if (body.Location.StartsWith("runtime_context.experience_blocks[semantic]", StringComparison.Ordinal))
                return new("[语义与联想参考", m, -3, body.Role, "", EnvelopeSection: true, DisplayLabel: "前文「语义与联想参考」");
            var matches = generated.Where(g => g.Source == body && g.Source.Text == m.Text).ToArray();
            if (matches.Length == 1)
                return new(matches[0].Label, m, -2, body.Role, "", matches[0], DisplayLabel: "「" + matches[0].Label + "」");
            return null;
        }
        string HostName(Host host) => host.DisplayLabel.Length > 0 ? host.DisplayLabel : "「" + host.Label + "」";

        void Item(B1Material m)
        {
            string speaker = SpeakerOf(m);
            string time = TimeOf(m);
            string meta = MetaOf(m);
            Host? host = ResolveHost(m);
            string head = $"〔{labels[m.Ref.Key]} | {speaker}{(time.Length > 0 ? " | " + time : "")}〕{meta}";
            if (host != null) { hosts.Add(host); Line($"{head} （内容同{HostName(host)}）"); }
            else Line($"{head} {m.Text}");
            if (m.DisplayedReplyFragments != null) foreach (var d in m.DisplayedReplyFragments)
                if (d.Text.Length > 0) Line($"  [已显示片段] {d.Text}");
        }
        foreach (var m in packet.Materials.Where(m => m.ReplyRunFirst == null)) Item(m);
        foreach (var run in runs)
        {
            var members = run.OrderBy(m => int.Parse(m.Ref.Version.AsSpan(m.Ref.Version.LastIndexOf(':') + 1), NumberStyles.None, CultureInfo.InvariantCulture)).ToArray();
            var memberHosts = members.Select(ResolveHost).ToArray();
            string rootLabel = Target(members[0].ReplyRoot!.Key);
            string firstLabel = labels[members[0].Ref.Key];
            string lastLabel = labels[members[^1].Ref.Key];
            string groupSpan = members.Length == 1 ? firstLabel : $"{firstLabel}–{lastLabel}";
            string speaker = SpeakerOf(members[0]);
            string time = TimeOf(members[0]);
            bool uniformOrigin = members.All(m => m.DisplayedReplyFragments is not { Count: > 0 }
                && string.Equals(m.Origin, members[0].Origin, StringComparison.Ordinal)
                && MetaOf(m, includeDiscovery: false) == MetaOf(members[0], includeDiscovery: false)
                && string.Equals(m.DiscoverySource?.Key, members[0].DiscoverySource?.Key, StringComparison.Ordinal));
            if (memberHosts.All(h => h != null) && uniformOrigin)
            {
                foreach (var h in memberHosts) hosts.Add(h!);
                string hostSummary = string.Join("、", memberHosts.Select(h => HostName(h!)).Distinct(StringComparer.Ordinal));
                string extra = MetaOf(members[0], includeDiscovery: false);
                Line(); Line($"[同次已显示回复组（原话根:{rootLabel}；共{members.Length}片）]");
                Line($"〔{groupSpan} | {speaker}{(time.Length > 0 ? " | " + time : "")}〕{extra} （完整内容同{hostSummary}；<b1>引用可用 {firstLabel}）");
            }
            else if (!memberHosts.Any(h => h != null) && uniformOrigin)
            {
                string meta = MetaOf(members[0], includeDiscovery: members[0].DiscoverySource?.Key != members[0].ReplyRoot!.Key);
                string merged = string.Concat(members.Select(m => m.Text));
                Line(); Line($"[同次已显示回复组（原话根:{rootLabel}；共{members.Length}片，已拼合）]");
                Line($"〔{groupSpan} | {speaker}{(time.Length > 0 ? " | " + time : "")}〕{meta} {merged}");
            }
            else
            {
                Line(); Line($"[同次已显示回复组（原话根:{rootLabel}）]");
                int idx = 0;
                while (idx < members.Length)
                {
                    if (memberHosts[idx] != null || members[idx].DisplayedReplyFragments is { Count: > 0 })
                    { Item(members[idx]); idx++; continue; }
                    int end = idx + 1;
                    string baseMeta = MetaOf(members[idx], includeDiscovery: members[idx].DiscoverySource?.Key != members[idx].ReplyRoot!.Key);
                    while (end < members.Length && memberHosts[end] == null
                        && members[end].DisplayedReplyFragments is not { Count: > 0 }
                        && string.Equals(members[end].Origin, members[idx].Origin, StringComparison.Ordinal)
                        && MetaOf(members[end], includeDiscovery: members[end].DiscoverySource?.Key != members[idx].ReplyRoot!.Key) == baseMeta
                        && string.Equals(members[end].DiscoverySource?.Key, members[idx].DiscoverySource?.Key, StringComparison.Ordinal))
                        end++;
                    if (end - idx == 1) { Item(members[idx]); idx++; continue; }
                    string segSpan = $"{labels[members[idx].Ref.Key]}–{labels[members[end - 1].Ref.Key]}";
                    string segText = string.Concat(members.Skip(idx).Take(end - idx).Select(m => m.Text));
                    Line($"〔{segSpan} | {SpeakerOf(members[idx])}{(TimeOf(members[idx]).Length > 0 ? " | " + TimeOf(members[idx]) : "")}〕{baseMeta} {segText}");
                    idx = end;
                }
            }
        }
        if(packet.Omissions.Count>0)
            Line($"（注：另有 {packet.Omissions.Count} 项受当前范围限制未展开）");
        string text=s.ToString();
        if(Encoding.UTF8.GetByteCount(text)>B1Continuity.MaxPacketUtf8Bytes){reason="readable_utf8_budget_json_fallback";return false;}
        view=new(packet,text,refs,hosts);reason="ready";return true;
    }

    // Validate the actual assembled main/tool-continuation payload BEFORE any
    // provider conversion. Original strings are carried unchanged by adapters.
    internal bool ValidatePayload(string json, B1Packet? packet)
    {
        if(!Unchanged(packet))return false;
        try
        {
            using var doc=JsonDocument.Parse(json);
            var messages=doc.RootElement.GetProperty("messages").EnumerateArray().ToArray();
            JsonElement carrier=default;string? cleanEnvelope=null;int count=0;
            foreach(var message in messages.Where(m=>m.GetProperty("role").GetString()=="user"))
            foreach(var text in ContentTexts(message.GetProperty("content")))
            {
                if (text.StartsWith("[程序提供的近期对话与背景", StringComparison.Ordinal))
                {
                    if (text.IndexOf(Text, StringComparison.Ordinal) >= 0 && text.IndexOf(Text, StringComparison.Ordinal) == text.LastIndexOf(Text, StringComparison.Ordinal))
                    { cleanEnvelope = text; count++; }
                    continue;
                }
                try
                {
                    using var data=JsonDocument.Parse(text);var root=data.RootElement;
                    if(root.ValueKind!=JsonValueKind.Object || !root.TryGetProperty("kind",out var kind)
                        || kind.GetString()!="lilith_program_material" || !root.TryGetProperty("runtime_context",out var context))continue;
                    string actual=context.GetString()??"";
                    if(actual.IndexOf(Text,StringComparison.Ordinal)<0 || actual.IndexOf(Text,StringComparison.Ordinal)!=actual.LastIndexOf(Text,StringComparison.Ordinal))continue;
                    carrier=root.Clone();count++;
                }
                catch(JsonException) { }
            }
            if(count!=1)return false;
            foreach(var host in _hosts)
            {
                if(host.Generated is {} generated)
                {
                    if(!generated.Owner.ContainsExactlyOnce(cleanEnvelope ?? (carrier.GetProperty("runtime_context").GetString()??"")))return false;
                }
                else if(host.EnvelopeSection)
                {
                    string? envelopeText = cleanEnvelope ?? (carrier.ValueKind == JsonValueKind.Object ? carrier.GetProperty("runtime_context").GetString() : null);
                    if(string.IsNullOrEmpty(envelopeText) || !envelopeText.Contains(host.Label,StringComparison.Ordinal) || !envelopeText.Contains(host.Material.Text,StringComparison.Ordinal))
                        return false;
                }
                else if(host.HistoryIndex<0)
                {
                    if(messages.Where(m=>m.GetProperty("role").GetString()=="user")
                        .SelectMany(m=>ContentTexts(m.GetProperty("content"))).Count(t=>t==host.Material.Text)!=1)return false;
                }
                else if (cleanEnvelope != null)
                {
                    if (!cleanEnvelope.Contains(host.Label, StringComparison.Ordinal) || !cleanEnvelope.Contains(host.Material.Text, StringComparison.Ordinal))
                        return false;
                }
                else
                {
                    var history=carrier.GetProperty("history");
                    if(host.HistoryIndex>=history.GetArrayLength())return false;
                    var row=history[host.HistoryIndex];
                    if(row.GetProperty("reading_label").GetString()!=host.Label || row.GetProperty("text").GetString()!=host.Material.Text
                        || row.GetProperty("speaker").GetString()!=host.Speaker
                        || row.GetProperty("sources").GetRawText()!=host.SourcesProof)return false;
                }
            }
            return true;
        }
        catch(Exception e) when(e is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException){return false;}
    }
    private static IEnumerable<string> ContentTexts(JsonElement content)
    {
        if(content.ValueKind==JsonValueKind.String){yield return content.GetString()!;yield break;}
        if(content.ValueKind==JsonValueKind.Array)foreach(var part in content.EnumerateArray())
            if(part.TryGetProperty("text",out var text) && text.ValueKind==JsonValueKind.String)yield return text.GetString()!;
    }
    internal bool TryTranslate(B1Packet packet,B1Choice choice,out B1Choice? translated)
    {
        translated=null;
        if(!Unchanged(packet) || choice.ProtocolVersion is not (2 or 3) || choice.Sources==null || choice.Target==null)return false;
        string? Map(string? key)=>key==null?null:_refs.TryGetValue(key,out var original)?original:key.StartsWith("材料",StringComparison.Ordinal)?null:key;
        var sources=choice.Sources.Select(Map).ToArray();var about=choice.About?.Select(Map).ToArray();
        string? target=choice.Target.Length==0?"":Map(choice.Target);
        if(sources.Any(x=>x==null)||about?.Any(x=>x==null)==true||target==null)return false;
        translated=new(){ProtocolVersion=choice.ProtocolVersion,Text=choice.Text,Scope=choice.Scope,Kind=choice.Kind,
            Sources=sources.Cast<string>().ToArray(),About=about?.Cast<string>().ToArray(),Target=target,Relation=choice.Relation,Nature=choice.Nature,Conditions=choice.Conditions};
        return true;
    }
    private static string Role(string role)=>role switch {
        "input"=>"本轮输入", "original_utterance_candidate"=>"找到的历史原话", "judgment_source"=>"理解的原始依据",
        "later_user_correction"=>"后来的用户更正", "later_user_wording_candidate"=>"可能的后续措辞，尚未建立更正关系",
        "late_related_user_wording_unbound"=>"可能相关的后续原话，未绑定同一对象",
        "related_exchange_user_wording_unbound"=>"相邻交流线索，未绑定同一对象",
        "late_public_reply"=>"后续原话的已显示回复", "historical_public_reply"=>"历史已显示回复",
        "recent_public_reply"=>"近期已显示回复", "context"=>"回看语境", _=>"原供给用途："+role };
    private static string Nature(string nature)=>nature switch {"reported"=>"当时的陈述，不表示程序核实了内容","inference"=>"当时形成的推断","hypothesis"=>"假设","question"=>"问题",_=>nature};
    private static string Relation(string relation)=>relation switch {"possible_later_wording_unbound"=>"可能的后续措辞，未绑定对象或更正关系","user_reported_source_correction"=>"用户报告的原话更正","literal_full_source_correction"=>"完整原话的字面更正","compare"=>"比较并存","qualify"=>"补充限定并存","revises"=>"明确修订旧理解",_=>relation};
    private static string Diagnostic(string value)=>value switch {"view_selected"=>"已选出的有限材料","source_view_selected"=>"已选出的原话材料","view_partial_budget"=>"部分材料因预算未供","not_found"=>"本次未取得匹配，不证明没有档案","discovery_incomplete"=>"发现过程未完整完成","qualification_restricted"=>"受来源资格限制",_=>value};
    private static string Omission(string value)=>value switch {"body_budget"=>"旧预算排除，不能据此区分数量与正文","body_char_budget"=>"正文字符预算排除","historical_public_reply_group_body_char_budget"=>"完整回复组因正文预算未供","rendered_utf8_byte_budget"=>"表示字节预算排除","public_reply_source_not_supplied"=>"回复所需原话根未供","candidate_not_expanded"=>"候选未展开","history_not_expanded"=>"历史版本未展开","permission_denied"=>"来源权限限制",_=>value};
}

internal sealed partial class DialogueRequest
{
    internal B1ReadableView? ReadableB1 {get;set;}
    internal string ReadableB1Fallback {get;set;}="";
    internal Func<bool>? ReadableB1SourcePermission {get;set;}
}

internal sealed partial class ApiResult
{
    internal Func<bool>? ReadableB1SourcePermission {get;set;}
    internal B1ReadableView? ReadableB1SentView {get;set;}
}
