using System;
using System.Collections.Generic;
using System.Linq;

namespace LilithBridge;

// A page is actual bounded historical supply, not an endorsement or hidden reasoning.
internal sealed record B1ExactReviewPage(string Version, int Offset, string Json, string Fingerprint);

internal sealed partial class MemoryStore
{
    internal B1ExactReviewPage ReadExactB1(string version, int offset, string query, BridgeConfig config)
    {
        lock (_experienceLock)
        {
            B1ExactReviewPage Result(object value)
            {
                string json = ReviewContext.Serialize(value);
                return new(version, offset, json, B1Hash(json));
            }
            if (!Guid.TryParseExact(version, "N", out _) || offset < 0 || offset > ReviewContext.MaxRecordOffset)
                return Result(new { status="invalid_arguments", reason="exact_b1_guid_and_bounded_offset_required" });
            using var mention = BeginMentionRequest(query);
            using var native = BeginNativeDialogueRequest(query, true, config);
            BeginB1Read();
            var doc = LoadB1(true);
            if (doc == null) return Result(new { status="unavailable", reason="store_"+B1StorageStatus });
            var resolved = ResolveB1(new("b1", version, "", "history"), doc, new());
            if (!resolved.Found) return Result(new { status="unavailable", source_ref="b1:"+version, reason=resolved.Reason,
                records=Array.Empty<object>(), formation_status="not_supplied" });
            var entry = FindB1(doc, version)!;
            var original = resolved.Material!;
            if (!B1ReadOnlyMaterialAllowed(original))
                return Result(new { status="unavailable", source_ref="b1:"+version, reason="permission_denied" });
            // Re-resolve every saved source. Never rebuild missing material from a
            // current source, nor expose a saved excerpt after source deletion.
            var rows = new List<(string Kind, B1Material Material, string Status, string Text)>();
            rows.Add(("retained_opinion", original, B1ManagementState(doc,version)=="withdrawn"?"withdrawn_historical_only":resolved.Reason, original.Text));
            foreach (var saved in entry.Packet.Materials)
            {
                var now = ResolveB1(saved.Ref with { Purpose="history" }, doc, new());
                bool allowed = now.Found && B1ReadOnlyMaterialAllowed(saved)
                    && (saved.DiscoverySource == null || ResolveB1(saved.DiscoverySource with { Purpose="permission" },doc,new()).Found);
                rows.Add(("saved_formation_supply", saved, allowed?"available_historical_supply":now.Found?"permission_denied":now.Reason,
                    allowed?saved.Text:""));
            }
            foreach (var omission in entry.Packet.Omissions)
            {
                var now=ResolveB1(omission.Ref with {Purpose="history"},doc,new());
                // An omission records non-supply, never an invitation to fill in
                // an unseen historical body from today's source.
                var locator=new B1Material(omission.Ref,"","",default,"program_recorded_omission","not_supplied","");
                rows.Add(("saved_formation_omission",locator,"not_supplied:"+omission.Reason+";current_availability:"+now.Reason,""));
            }
            foreach (var dependency in entry.Packet.Footprint.Where(r=>!entry.Packet.Materials.Any(m=>m.Ref.Key==r.Key)
                &&!entry.Packet.Omissions.Any(o=>o.Ref.Key==r.Key)))
            {
                var now=ResolveB1(dependency with {Purpose="history"},doc,new());
                var locator=new B1Material(dependency,"","",default,"saved_permission_footprint","body_not_recorded","");
                rows.Add(("formation_dependency_without_saved_body",locator,"body_not_recorded;current_availability:"+now.Reason,""));
            }
            // Offset counts UTF-16 text units plus one separator per row, even
            // unavailable/empty rows. This gives those rows a bounded page slot.
            int position=0, remaining=2500, next=offset;
            var records=new List<object>();
            foreach (var row in rows)
            {
                int end=checked(position+row.Text.Length+1);
                if (end<=offset) {position=end;continue;}
                if (remaining<=0 || records.Count>=4) break;
                int start=Math.Clamp(offset-position,0,row.Text.Length);
                if(start>0 && start<row.Text.Length && char.IsLowSurrogate(row.Text[start]) && char.IsHighSurrogate(row.Text[start-1]))start--;
                int count=Math.Min(remaining,row.Text.Length-start);
                if(count>0 && start+count<row.Text.Length && char.IsHighSurrogate(row.Text[start+count-1]))count--;
                var m=row.Material;
                records.Add(new { type=row.Kind, source_ref=m.Ref.Key, source_sha256=m.Ref.Sha256,
                    status=row.Status, original_time=m.At==default?null:m.At.ToString("O"), origin=m.Origin,
                    scope=row.Text.Length>0?m.Scope:null, relation=row.Text.Length>0?m.Relation:null,
                    target=row.Text.Length>0?m.Target:null, offset=start, total_chars=row.Text.Length,
                    text=row.Text.Substring(start,count), truncated=start+count<row.Text.Length,
                    supplied_ranges=row.Text.Length>0?m.SuppliedRanges:null });
                remaining-=Math.Max(1,count);
                next=start+count<row.Text.Length?position+start+count:end;
                if(start+count<row.Text.Length)break;
                position=end;
            }
            int total=rows.Sum(r=>r.Text.Length+1);
            int? nextOffset=next<total?next:null;
            return Result(new { status=offset>total?"invalid_offset":nextOffset.HasValue?"truncated":"ok", mode="read",
                source_ref="b1:"+version, logical_id=entry.LogicalId, original_time=entry.At.ToString("O"),
                relation=entry.Relation, target=entry.Target, revision=entry.Revision, generated_by=entry.GeneratedBy,
                selected_head=doc.Current.GetValueOrDefault(entry.LogicalId), management=B1ManagementState(doc,version),
                current_qualification=ResolveB1(new("b1",version,"","current"),doc,new()).Reason,
                meaning="Stored opinion and actual finite supplied formation material; not current endorsement, external truth, or complete private reasoning. Selection is not authorship.",
                formation_status=entry.Packet.Materials.Count==0?"no_material_recorded":"recorded_finite_supply",
                packet_id=entry.Packet.Id, packet_protocol=entry.Packet.ProtocolVersion,
                omitted_material_count=entry.Packet.Omissions.Count,
                records, offset, total_units=total, next_offset=nextOffset,
                next_locator=nextOffset.HasValue?new {mode="read",source_refs=new[]{"b1:"+version},offset=nextOffset}:null,
                pagination="Each page rechecks live permission; availability changes can change offsets. Restart at zero after a source change.",
                write_references_added=false });
        }
    }

    internal bool TryUseExactB1(B1ExactReviewPage page, string query, BridgeConfig config, Func<bool> use)
    {
        lock (_experienceLock)
        {
            var current=ReadExactB1(page.Version,page.Offset,query,config);
            return current.Fingerprint==page.Fingerprint && use();
        }
    }
}
