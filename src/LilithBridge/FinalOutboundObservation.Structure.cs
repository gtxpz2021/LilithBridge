using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LilithBridge;

internal static class FinalOutboundStructure
{
    private static readonly HashSet<string> Keys = new(StringComparer.Ordinal)
    {
        "model","messages","input","instructions","tools","tool_choice","type","role","content","text","output","name",
        "function","parameters","properties","required","additionalProperties","description","enum","items","arguments",
        "tool_calls","tool_call_id","call_id","id","function_call","function_call_output","status","reasoning","summary",
        "encrypted_content","_openai_output","image_url","url","detail","input_image","image","format","schema","json_schema",
        "kind","schema_version","history","runtime_context","current_input_reported_speech","reported_speech","speaker","sources",
        "source_ref","source_refs","source_event_id","source_event_ids","event_id","origin","delivery_state","session_id","created_at",
        "v","availability","coverage","focus","materials","omitted","runs","shared","fragments","scope","at","source","state",
        "nature","conditions","qualification","discovery_source","discovery_qualification","first","last","root","role_hint",
        "text_ref","body_ref","source_bodies","bodies","offset","length","chars","total_chars","start","end","ranges","sha256",
        "start_char","char_count","location",
        "revision","version","source_version","fingerprint","revises","revised_by","relation","target","about","method","result",
        "records","source_versions","source_kind","source_id","operation_id","mode","question","query","record","data","ref",
        "temperature","top_p","max_tokens","max_output_tokens","stream","store","thinking","reasoning_effort","effort",
        "parallel_tool_calls","include","service_tier","truncation","metadata","background","instructions_hash"
    };
    private static string Segment(string key) => Keys.Contains(key) ? key : "key_sha256_" + FinalOutboundObservation.Hash(key);
    private sealed class Scan
    {
        internal readonly List<object> Nodes = new();
        internal readonly List<object> Locations = new();
        internal readonly List<object> Layers = new();
        internal readonly Dictionary<string,List<int>> HostIds;
        internal bool Truncated;
        internal Scan(Dictionary<string,List<int>> ids) => HostIds = ids;
        internal void Walk(JsonElement item, string path, string carrier, string? bodyPath = null, int decodedDepth = 0, bool opaque = false)
        {
            if (Nodes.Count >= 100000) { Truncated = true; return; }
            if (item.ValueKind == JsonValueKind.Object)
            {
                bool referenceObject=path.EndsWith(".text_ref",StringComparison.Ordinal)||path.EndsWith(".body_ref",StringComparison.Ordinal);
                if (item.TryGetProperty("text",out _)) bodyPath = path + ".text";
                else if(item.TryGetProperty("text_ref",out _)) bodyPath = path + ".text_ref";
                if(item.TryGetProperty("type",out var typ) && typ.ValueKind==JsonValueKind.String
                    && typ.GetString()=="reasoning")opaque=true;
                foreach (var prop in item.EnumerateObject())
                {
                    string next = path + "." + Segment(prop.Name);
                    if(prop.Name is "event_id" or "source_event_id" or "source_ref" or "text_ref" or "body_ref"
                        && prop.Value.ValueKind==JsonValueKind.String && !string.IsNullOrEmpty(prop.Value.GetString()))
                    {
                        string identity=prop.Value.GetString()??"";
                        Locations.Add(new { source_identity=HostIds.ContainsKey(identity)?FinalOutboundObservation.Identity(identity):"sha256:"+FinalOutboundObservation.Hash(identity), identity_field=prop.Name,
                            identity_role=prop.Name=="source_event_id"?(referenceObject?"declared_body_reference_event":"declared_dependency_identity"):prop.Name=="event_id"?"declared_event_identity"
                                :prop.Name is "text_ref" or "body_ref"?"declared_body_reference":"declared_material_alias",
                            identity_path=next, body_path=bodyPath, carrier, host_source_indices=HostIds.GetValueOrDefault(identity)??new List<int>(),
                            body_path_kind=referenceObject||item.TryGetProperty("text_ref",out _)||item.TryGetProperty("body_ref",out _)?"reference"
                                :item.TryGetProperty("text",out _)?"inline":"inherited; identity_may_be_dependency_not_body_owner",
                            binding=HostIds.ContainsKey(identity)?"declared_identity_with_host_metadata":"declared_identity_unmapped",
                            start_char=Number(item,"start_char")??Number(item,"offset"), char_count=Number(item,"char_count")??Number(item,"length")??Number(item,"chars"),
                            total_chars=Number(item,"total_chars"),range_coordinates="decoded_text_utf16; not_wire_bytes",
                            declared_location_sha256=item.TryGetProperty("location",out var loc)&&loc.ValueKind==JsonValueKind.String
                                ?FinalOutboundObservation.Hash(loc.GetString()??""):null });
                    }
                    Walk(prop.Value,next,carrier,bodyPath,decodedDepth,opaque||prop.Name is "encrypted_content" or "_openai_output");
                }
            }
            else if(item.ValueKind==JsonValueKind.Array)
            {
                int i=0;foreach(var value in item.EnumerateArray())Walk(value,path+"["+i+++ "]",carrier,bodyPath,decodedDepth,opaque);
            }
            else if(item.ValueKind==JsonValueKind.String)
            {
                string value=item.GetString()??"";
                Nodes.Add(new { path,carrier,kind="string",utf16_chars=value.Length,utf8_bytes=Encoding.UTF8.GetByteCount(value),
                    sha256=FinalOutboundObservation.Hash(value),opaque });
                if(opaque||decodedDepth>=8)return;
                string trimmed=value.TrimStart();
                if(trimmed.StartsWith("{",StringComparison.Ordinal)||trimmed.StartsWith("[",StringComparison.Ordinal))
                {
                    try
                    {
                        using var nested=JsonDocument.Parse(value);
                        Layers.Add(new { path,carrier="decoded_json_string",coordinates="decoded_json_path_not_wire_byte_offset" });
                        Walk(nested.RootElement,path+"::$decoded","decoded_json_string",bodyPath,decodedDepth+1);
                        return;
                    }
                    catch(JsonException){ }
                }
                int marker=value.IndexOf("[B1 有限材料包",StringComparison.Ordinal);
                int start=marker<0?-1:value.IndexOf('{',marker);
                if(start>=0)
                {
                    try
                    {
                        var reader=new Utf8JsonReader(Encoding.UTF8.GetBytes(value[start..]));
                        using var nested=JsonDocument.ParseValue(ref reader);
                        Layers.Add(new { path,carrier="embedded_b1_json",decoded_segment_start_utf16=start,
                            coordinates="decoded_text_utf16_and_nested_json_path_not_wire_byte_offset" });
                        Walk(nested.RootElement,path+"::$b1","embedded_b1_json",bodyPath,decodedDepth+1);
                    }
                    catch(JsonException){ }
                }
            }
            else Nodes.Add(new { path,carrier,kind=item.ValueKind.ToString().ToLowerInvariant(),sha256=FinalOutboundObservation.Hash(item.GetRawText()) });
        }
    }
    private static long? Number(JsonElement obj,string key) => obj.TryGetProperty(key,out var v)&&v.ValueKind==JsonValueKind.Number
        &&v.TryGetInt64(out long n)?n:null;
    internal static object Describe(string json,ApiResult envelope,IReadOnlyList<MemoryTurn>? history)
    {
        var sources=new List<object>();var ids=new Dictionary<string,List<int>>(StringComparer.Ordinal);
        void Add(string identity,object record)
        {
            if(string.IsNullOrEmpty(identity))return;
            if(!ids.TryGetValue(identity,out var list))ids[identity]=list=new();
            list.Add(sources.Count);sources.Add(record);
        }
        void Turn(MemoryTurn t,string identity,string lane,string fingerprint="")
        {
            Add(identity,new { identity=FinalOutboundObservation.Identity(identity),event_id=FinalOutboundObservation.Identity(t.EventId),
                lane,kind=FinalOutboundObservation.Identity(t.Kind),revision=t.Revision,body_sha256=FinalOutboundObservation.Hash(t.Text),
                version_fingerprint=FinalOutboundObservation.Identity(fingerprint),
                role=FinalOutboundObservation.Identity(t.Role),origin=FinalOutboundObservation.Identity(t.Origin),delivery=FinalOutboundObservation.Identity(t.DeliveryState),
                dependencies=t.SourceDependencies.Concat(t.ContextDependencies??Array.Empty<DaySourceDependency>()).Select(x=>new
                {kind=FinalOutboundObservation.Identity(x.Kind),event_id=FinalOutboundObservation.Identity(x.EventId),fingerprint=FinalOutboundObservation.Identity(x.Fingerprint)}).ToArray() });
        }
        foreach(var t in history??Array.Empty<MemoryTurn>())Turn(t,t.EventId,"history_argument; not_by_itself_proof_of_wire_presence");
        if(envelope.DialogueRequest is { } owner)
        {
            foreach(var pair in owner.ReviewSourceRecords)Turn(pair.Value,pair.Key,"registered_review_source",owner.ReviewSourceVersions.GetValueOrDefault(pair.Key)??"");
            if(owner.B1Packet is { } packet)
                foreach(var pair in packet.ShortRefs)
                {
                    var r=pair.Value;var material=packet.Materials.FirstOrDefault(m=>m.Ref==r);
                    Add(pair.Key,new { identity=FinalOutboundObservation.Identity(pair.Key),lane="b1_short_ref",kind=FinalOutboundObservation.Identity(r.Kind),
                        version=FinalOutboundObservation.Identity(r.Version),sha256=FinalOutboundObservation.Identity(r.Sha256),
                        version_fingerprint=FinalOutboundObservation.Identity(material?.VersionFingerprint),
                        supplied_ranges=material?.SuppliedRanges,displayed_fragments=material?.DisplayedReplyFragments?.Select(x=>new
                        {step_id=FinalOutboundObservation.Identity(x.StepId),request_id=FinalOutboundObservation.Identity(x.RequestId),
                            event_id=FinalOutboundObservation.Identity(x.EventId),start_char=x.StartChar,
                            chars=x.Text.Length,body_sha256=FinalOutboundObservation.Hash(x.Text),outcome=FinalOutboundObservation.Identity(x.Outcome)}).ToArray() });
                }
        }
        foreach(var p in envelope.PublicViewSources)Add(p.Id,new {identity=FinalOutboundObservation.Identity(p.Id),lane="public_view_source",
            kind=FinalOutboundObservation.Identity(p.Kind),body_sha256=FinalOutboundObservation.Hash(p.Text)});
        using var doc=JsonDocument.Parse(json);
        var scan=new Scan(ids);scan.Layers.Add(new {path="$",carrier="wire_json",coordinates="json_path; no_wire_byte_spans_claimed"});
        scan.Walk(doc.RootElement,"$","wire_json");
        int functions=0,namespaces=0,toolResults=0;
        void CountTools(JsonElement list)
        {
            if(list.ValueKind!=JsonValueKind.Array)return;
            foreach(var tool in list.EnumerateArray())
                if(tool.ValueKind==JsonValueKind.Object && tool.TryGetProperty("type",out var t) && t.ValueKind==JsonValueKind.String)
                {
                    if(t.GetString()=="function")functions++;
                    else if(t.GetString()=="namespace")
                    {namespaces++;if(tool.TryGetProperty("tools",out var nested))CountTools(nested);}
                }
        }
        if(doc.RootElement.TryGetProperty("tools",out var tools))CountTools(tools);
        foreach(string messageKey in new[]{"messages","input"})
            if(doc.RootElement.TryGetProperty(messageKey,out var messages)&&messages.ValueKind==JsonValueKind.Array)
                foreach(var message in messages.EnumerateArray())
                    if(message.ValueKind==JsonValueKind.Object &&
                        (message.TryGetProperty("role",out var r)&&r.ValueKind==JsonValueKind.String&&r.GetString()=="tool"
                         ||message.TryGetProperty("type",out var t)&&t.ValueKind==JsonValueKind.String&&t.GetString()=="function_call_output"))toolResults++;
        return new { path_encoding="known_schema_keys; unknown_property_names_replaced_by_sha256; numeric_array_indices",
            source_binding_scope="declared_wire_identities_only; host_metadata_is_not_proof_of_supply; no_text_similarity_binding",
            opaque_policy="hash_and_length_only; full_body_mode_preserves_exact_body_including_opaque_continuation",
            structure_truncated=scan.Truncated,layers=scan.Layers,string_and_scalar_fields=scan.Nodes,
            source_locations=scan.Locations,host_sources=sources,tool_function_definition_count=functions,
            tool_namespace_container_count=namespaces,tool_result_count=toolResults };
    }
}
