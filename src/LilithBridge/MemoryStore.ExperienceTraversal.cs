using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace LilithBridge;

// Shared read-only traversal. The message tool exposes only exchange/before/after;
// a traversal is a reading plan, not a claim of one semantic episode.
internal sealed record ExperienceTraversalOptions(string Scope = "connected", string Cursor = "", int PageBytes = 16384);
internal sealed record ExperienceTraversalLink(string From, string To, string Kind, string Qualification);
internal sealed record ExperienceTraversalNode(string Reference, string Exchange, LogicalDialogueUnit? Unit, GeneratedReplySnapshot? Generated)
{
    internal string Body => Unit?.Text ?? Generated!.BodyText;
    internal string Version => Unit?.VersionFingerprint ?? Generated!.VersionFingerprint;
    internal string Session => Unit?.SessionId ?? Generated!.SessionId;
    internal object Slice(int offset, int chars)
    {
        int end=offset+chars;
        var m=Generated?.Metadata;
        return new {
            entry_kind="source", reference=Reference, exchange_anchor=Exchange,
            source_kind=Unit==null?"generated_public_answer":Unit.IsMessage?"logical_message":"independent_utterance",
            version_fingerprint=Version, body_sha256=MemoryStore.SemanticTextHash(Body),
            role=Unit?.Role??"assistant", origin=Unit?.Origin??Generated!.Origin,
            session_id=Session, time=(Unit?.Timestamp??m!.CapturedAtUtc).ToString("O"),
            coverage=Unit?.Coverage??Generated!.EpistemicStatus,
            head_known=Unit?.HeadKnown,tail_known=Unit?.TailKnown,
            offset,chars,total_chars=Body.Length,text=Body.Substring(offset,chars),
            supplied_view_complete=offset==0&&end==Body.Length,
            source_ranges=Unit?.SourceSpans.Where(s=>s.OffsetInUnit<end&&s.OffsetInUnit+s.Chars>offset).Select(s=>new {
                event_id=s.EventId,source_hash=s.TextHash,
                source_offset=s.SourceOffset+Math.Max(offset,s.OffsetInUnit)-s.OffsetInUnit,
                chars=Math.Min(end,s.OffsetInUnit+s.Chars)-Math.Max(offset,s.OffsetInUnit),
                qualification="exact_raw_range;read_locator_only;no_write_alias"
            }).ToArray(),
            program_gaps=Unit?.Gaps.Where(g=>g.OffsetInUnit<end&&g.OffsetInUnit+g.Chars>offset).Select(g=>new {
                offset=g.OffsetInUnit,chars=g.Chars,reason=g.Reason,qualification="program_marker_not_original_words"
            }).ToArray(),
            generation=Generated==null?null:new {
                original_message_ref=Generated.LogicalOwnerKey,request_id=Generated.RequestId,
                generation_attempt_id=Generated.AttemptId,received_extent=m!.PublicTextExtent,
                network_body_complete=m.NetworkBodyComplete,display_outcome=m.DeliveryOutcome,
                terminal=m.DeliveryTerminal,player_reading="unknown",
                confirmed_shown_ranges=m.ShownRanges.Where(r=>r.GeneratedStartChar.HasValue&&r.GeneratedStartChar.Value<end
                    &&r.GeneratedStartChar.Value+r.Chars>offset).Select(r=>new {
                    event_id=r.EventId,raw_fingerprint=r.RawFingerprint,start=r.GeneratedStartChar,chars=r.Chars,
                    qualification="confirmed_display_mapping;not_a_read_receipt"
                }).ToArray(),
                qualification="only_exact_confirmed_ranges_are_displayed;other_body_ranges_not_established_as_told"
            }
        };
    }
}
internal sealed record ExperienceTraversalGraph(string Root,string Session,string PermissionQuery,string Anchor,string Scope,
    string Fingerprint,IReadOnlyList<ExperienceTraversalNode> Nodes,IReadOnlyList<ExperienceTraversalLink> Links,
    LogicalMessageReadPacket LogicalMessages,int QualifiedCatalogNodes,int UnreadableRecords);
internal sealed record ExperienceTraversalPage(ExperienceTraversalGraph Graph,IReadOnlyList<object> Entries,string NextCursor,
    int EntryStart,int OffsetStart,int PageBytes)
{
    internal string Json => Render();
    internal string Render(bool omitted=false) => ReviewContext.Serialize(new {
        status=omitted?"body_omitted":NextCursor.Length>0?"traversal_page":"selected_traversal_exhausted",mode="message",
        reason=omitted?"existing_request_budget":"",
        snapshot_id=Graph.Fingerprint,message_ref=Graph.Anchor,message_scope=Graph.Scope,
        archive_coverage="qualified_available_sources_only;not_proof_of_absence",
        organization="exact_question_response_groups;available_order_and_explicit_correction_links;not_semantic_episode_certification",
        qualified_catalog_nodes=Graph.QualifiedCatalogNodes,selected_source_nodes=Graph.Nodes.Count,selected_links=Graph.Links.Count,
        unreadable_or_omitted_records=Graph.UnreadableRecords,entry_start=EntryStart,body_offset_start=OffsetStart,
        page_utf8_limit=PageBytes,entries=omitted?Array.Empty<object>():Entries,
        next_cursor=omitted?Graph.Fingerprint+":"+EntryStart+":"+OffsetStart:NextCursor,more=omitted||NextCursor.Length>0,
        next_read="Continue mode=message with the same message_ref/message_scope and traversal_cursor=next_cursor. Other returned references can be new anchors; unread neighbours remain unknown.",
        completeness_note="Exhaustion means this selected available traversal only, not complete experience or all archive. Available order is not cause, same topic is not same event, source correction is user report not independently certified truth. Unread text and non-displayed generated text are not speech or player knowledge. Read-only locators grant no write authority."
    });
}
internal sealed partial class MemoryStore
{
    // One immutable read view within an existing source-lock hold. Never cached
    // across operations: each final permission check captures a fresh view.
    private sealed record ExperienceTraversalInput(LogicalDialogueCatalog Catalog,
        IReadOnlyList<GeneratedReplySnapshot> Generated,IReadOnlyList<MemoryTurn> Corrections);
    private ExperienceTraversalInput CaptureExperienceTraversalInput(string query,BridgeConfig config)
    {
        using var mention=BeginMentionRequest(query,true);
        using var native=BeginNativeDialogueRequest(query,true,config);
        return new(CurrentLogicalMessageCatalog(),GetQualifiedGeneratedReplies(query,config),ReadExperiences(includeSuppressed:true));
    }
    private ExperienceTraversalGraph? BuildExperienceTraversal(string permissionQuery,string anchor,BridgeConfig config,string scope,
        CancellationToken token,out string reason,ExperienceTraversalInput? traversalInput=null)
    {
        reason="";
        if(scope is not ("exchange" or "connected" or "timeline" or "before" or "after")){reason="invalid_traversal_scope";return null;}
        if(!LogicalMessageReadAllowed(permissionQuery,config,crossSession:true)){reason="traversal_not_allowed";return null;}
        using var mention=BeginMentionRequest(permissionQuery,true);
        using var native=BeginNativeDialogueRequest(permissionQuery,true,config);
        traversalInput??=CaptureExperienceTraversalInput(permissionQuery,config);
        var catalog=traversalInput.Catalog;
        var generated=traversalInput.Generated;
        string Exchange(LogicalDialogueUnit u) => u.Role=="user"?u.Key:
            catalog.FindByEvent(u.SourceEventId) is {Role:"user"} q&&q.SessionId==u.SessionId?q.Key:u.Key;
        var all=catalog.Units.Select(u=>new ExperienceTraversalNode(u.Key,Exchange(u),u,null))
            .Concat(generated.Select(g=>new ExperienceTraversalNode("generated/"+g.Key,
                catalog.FindByEvent(g.SourceUserId) is {Role:"user"} q&&q.SessionId==g.SessionId?q.Key:"generated/"+g.Key,null,g)))
            .ToDictionary(n=>n.Reference,StringComparer.Ordinal);
        if(!all.TryGetValue(anchor,out var seed)){reason="anchor_unavailable_or_not_permitted";return null;}
        var links=new List<ExperienceTraversalLink>();
        foreach(var n in all.Values)
        {
            token.ThrowIfCancellationRequested();
            if(n.Exchange!=n.Reference)links.Add(new(n.Exchange,n.Reference,"exact_source_question_response",
                "stored_source_id_same_session;not_semantic_cause_or_agreement"));
            if(n.Generated is {} g)
                foreach(var shown in g.ShownRanges)
                    if(catalog.FindByEvent(shown.EventId) is {} view)
                        links.Add(new(n.Reference,view.Key,"exact_generated_display_view",
                            "stored_event_fingerprint_mapping;not_another_occurrence;not_full_view_coverage_or_player_reading"));
        }
        // Reuse the existing exact-complete-utterance correction binding. No
        // semantic similarity, LLM verdict or fixed lexical threshold invents a link.
        var rawCorrections=traversalInput.Corrections;
        foreach(var input in rawCorrections.Where(t=>t.Role=="user"&&t.Origin=="user"&&t.DeliveryState=="received"))
        {
            token.ThrowIfCancellationRequested();
            var intent=ParseRawSourceCorrection(input.Text);if(intent==null)continue;
            var targets=RawCorrectionTargets(input,intent,rawCorrections,true);
            if(targets.Count==1&&catalog.FindByEvent(targets[0].EventId) is {} old
                &&catalog.FindByEvent(input.EventId) is {} correction)
                links.Add(new(old.Key,correction.Key,"exact_user_source_correction",
                    "existing_unique_complete_old_utterance_binding;historical_user_report;not_external_truth"));
        }
        links=links.Distinct().OrderBy(l=>l.From,StringComparer.Ordinal).ThenBy(l=>l.To,StringComparer.Ordinal).ThenBy(l=>l.Kind,StringComparer.Ordinal).ToList();
        var wanted=new HashSet<string>(StringComparer.Ordinal);
        void AddExchange(string key){foreach(var n in all.Values.Where(n=>n.Exchange==key))wanted.Add(n.Reference);}
        AddExchange(seed.Exchange);
        if(scope=="timeline")
            foreach(var n in all.Values.Where(n=>n.Session==seed.Session))wanted.Add(n.Reference);
        if(scope is "before" or "after")
        {
            // Explicit source-relative question only: expand up to four nearby
            // available exchanges, never the whole historical session. The
            // half-hour boundary and capacity are host limits, not topic/cause
            // labels; restricted/missing intervening originals remain unknown.
            var order=all.Values.Where(n=>n.Unit is {Role:"user"}&&n.Session==seed.Session)
                .OrderBy(n=>n.Unit!.FirstSequence).ThenBy(n=>n.Reference,StringComparer.Ordinal).ToArray();
            int at=Array.FindIndex(order,n=>n.Reference==seed.Exchange);
            if(at>=0)
                for(int distance=1;distance<=4;distance++)
                {
                    int next=at+(scope=="before"?-distance:distance);
                    if(next<0||next>=order.Length)break;
                    if(Math.Abs((order[next].Unit!.Timestamp-order[at].Unit!.Timestamp).TotalMinutes)>30)break;
                    AddExchange(order[next].Exchange);
                }
        }
        if(scope is "connected" or "before" or "after")
        {
            bool changed;
            do {
                token.ThrowIfCancellationRequested();int count=wanted.Count;
                foreach(var l in links.Where(l=>l.Kind=="exact_user_source_correction"))
                    if(wanted.Contains(l.From)||wanted.Contains(l.To)) {AddExchange(all[l.From].Exchange);AddExchange(all[l.To].Exchange);}
                changed=wanted.Count!=count;
            }while(changed);
        }
        // Timeline is a navigable order, not an automatic episode boundary.
        // Preserve frontiers even when connected/exchange does not expand them.
        foreach(var group in all.Values.Where(n=>n.Unit is {Role:"user"}).GroupBy(n=>n.Session))
        {
            var order=group.OrderBy(n=>n.Unit!.Timestamp).ThenBy(n=>n.Unit!.FirstSequence).ThenBy(n=>n.Reference,StringComparer.Ordinal).ToArray();
            for(int i=1;i<order.Length;i++)
                if(wanted.Contains(order[i-1].Reference)||wanted.Contains(order[i].Reference))
                    links.Add(new(order[i-1].Reference,order[i].Reference,"available_session_order",
                        "qualified_available_order_only;intervening_missing_or_restricted_records_unknown;not_cause_or_same_episode"));
        }
        var nodes=all.Values.Where(n=>wanted.Contains(n.Reference)).GroupBy(n=>n.Exchange)
            .OrderBy(g=>all[g.Key].Unit?.Timestamp??all[g.Key].Generated!.Metadata.CapturedAtUtc)
            .ThenBy(g=>all[g.Key].Session,StringComparer.Ordinal)
            .ThenBy(g=>all[g.Key].Unit?.FirstSequence??long.MaxValue).ThenBy(g=>g.Key,StringComparer.Ordinal)
            .SelectMany(g=>g.OrderBy(n=>n.Reference==n.Exchange?0:n.Unit!=null?1:2)
                .ThenBy(n=>n.Unit?.FirstSequence??long.MaxValue)
                .ThenBy(n=>n.Unit?.Timestamp??n.Generated!.Metadata.CapturedAtUtc).ThenBy(n=>n.Reference,StringComparer.Ordinal)).ToArray();
        // Refs outside this selection are navigation only; their bodies were not read.
        var selectedLinks=links.Where(l=>wanted.Contains(l.From)||wanted.Contains(l.To)).Distinct()
            .OrderBy(l=>l.Kind,StringComparer.Ordinal).ThenBy(l=>l.From,StringComparer.Ordinal).ThenBy(l=>l.To,StringComparer.Ordinal).ToArray();
        var packet=LogicalMessagePacket(nodes.Where(n=>n.Unit!=null).Select(n=>n.Unit!).ToArray(),true,false);
        string fingerprint=SemanticTextHash(JsonSerializer.Serialize(new {
            schema="experience-traversal-prototype-v1",root=_root,session=_sessionId,permissionQuery,anchor,scope,
            nodes=nodes.Select(n=>new {n.Reference,n.Exchange,n.Version}).ToArray(),links=selectedLinks,
            source_contexts=packet.SourceContexts.OrderBy(p=>p.Key,StringComparer.Ordinal).ToArray()
        },DialogueMaterialEnvelope.DataJson));
        return new(_root,_sessionId,permissionQuery,anchor,scope,fingerprint,nodes,selectedLinks,packet,all.Count,
            _experienceReadFailures+_experienceMalformedRecords+catalog.Omissions.Count);
    }
    internal ExperienceTraversalPage? CaptureExperienceTraversal(string permissionQuery,string anchor,BridgeConfig config,
        ExperienceTraversalOptions options,CancellationToken token,out string reason)
    {
        lock(_experienceLock) lock(_generatedRepliesLock)
        {
            var graph=BuildExperienceTraversal(permissionQuery,anchor,config,options.Scope,token,out reason);
            if(graph==null)return null;
            int entry=0,offset=0;
            if(options.Cursor.Length>0)
            {
                var p=options.Cursor.Split(':');
                if(p.Length!=3||p[0]!=graph.Fingerprint||!int.TryParse(p[1],out entry)||!int.TryParse(p[2],out offset)
                    ||entry<0||entry>graph.Nodes.Count+graph.Links.Count||offset<0
                    ||entry<graph.Nodes.Count&&offset>graph.Nodes[entry].Body.Length
                    ||entry>=graph.Nodes.Count&&offset!=0
                    ||entry<graph.Nodes.Count&&offset<graph.Nodes[entry].Body.Length&&char.IsLowSurrogate(graph.Nodes[entry].Body[offset])){reason="traversal_cursor_stale_or_invalid";return null;}
            }
            int limit=Math.Clamp(options.PageBytes,4096,65536),start=entry,startOffset=offset;
            var entries=new List<object>();
            string Cursor(int e,int o)=>e<graph.Nodes.Count+graph.Links.Count?graph.Fingerprint+":"+e+":"+o:"";
            ExperienceTraversalPage Packet(int e,int o)=>new(graph,entries.ToArray(),Cursor(e,o),start,startOffset,limit);
            int Bytes(object value,int e,int o){entries.Add(value);try{return Encoding.UTF8.GetByteCount(Packet(e,o).Json);}finally{entries.RemoveAt(entries.Count-1);}}
            while(entry<graph.Nodes.Count+graph.Links.Count)
            {
                token.ThrowIfCancellationRequested();
                if(entry>=graph.Nodes.Count)
                {
                    var l=graph.Links[entry-graph.Nodes.Count];var wire=new {entry_kind="link",from=l.From,to=l.To,kind=l.Kind,qualification=l.Qualification,
                        from_body_in_selection=graph.Nodes.Any(n=>n.Reference==l.From),to_body_in_selection=graph.Nodes.Any(n=>n.Reference==l.To)};
                    if(Bytes(wire,entry+1,0)>limit){if(entries.Count==0){reason="traversal_metadata_exceeds_page";return null;}break;}
                    entries.Add(wire);entry++;continue;
                }
                var n=graph.Nodes[entry];int chars=Math.Min(12000,n.Body.Length-offset);
                if(chars>0&&offset+chars<n.Body.Length&&char.IsHighSurrogate(n.Body[offset+chars-1]))chars--;
                while(true)
                {
                    int next=offset+chars;int nextEntry=next==n.Body.Length?entry+1:entry,nextOffset=next==n.Body.Length?0:next;
                    var wire=n.Slice(offset,chars);
                    if(Bytes(wire,nextEntry,nextOffset)<=limit){entries.Add(wire);entry=nextEntry;offset=nextOffset;break;}
                    if(entries.Count>0)return Packet(entry,offset);
                    int minimum=offset<n.Body.Length&&char.IsHighSurrogate(n.Body[offset])&&offset+1<n.Body.Length&&char.IsLowSurrogate(n.Body[offset+1])?2:1;
                    if(chars<=minimum){reason="traversal_metadata_exceeds_page";return null;}
                    int smaller=Math.Max(minimum,chars/2);
                    if(offset+smaller<n.Body.Length&&char.IsHighSurrogate(n.Body[offset+smaller-1]))smaller--;
                    chars=Math.Max(minimum,smaller);
                }
            }
            return Packet(entry,offset);
        }
    }
    internal bool TryUseExperienceTraversal(ExperienceTraversalPage page,string permissionQuery,BridgeConfig config,Func<bool> use)
    {
        lock(_experienceLock) lock(_generatedRepliesLock)
        {
            var g=page.Graph;
            if(g.Root!=_root||g.Session!=_sessionId||g.PermissionQuery!=permissionQuery)return false;
            var fresh=BuildExperienceTraversal(permissionQuery,g.Anchor,config,g.Scope,CancellationToken.None,out _);
            if(fresh==null||fresh.Fingerprint!=g.Fingerprint)return false;
            string cursor=g.Fingerprint+":"+page.EntryStart+":"+page.OffsetStart;
            var exact=CaptureExperienceTraversal(permissionQuery,g.Anchor,config,new(g.Scope,cursor,page.PageBytes),CancellationToken.None,out _);
            if(exact==null)return false;
            // The whole qualified catalog count is a capture-time diagnostic,
            // not part of this selected graph. An unrelated append may change
            // it without changing any supplied entry, link, range or version.
            // Compare all other rendered fields exactly, including omissions.
            var comparable=exact with { Graph=exact.Graph with { QualifiedCatalogNodes=g.QualifiedCatalogNodes } };
            if(comparable.Json!=page.Json)return false;
            // Fail closed if public body/display metadata or correction context
            // changed after capture. This does not claim to erase prior exposure.
            return TryUseLogicalMessageRead(g.LogicalMessages,permissionQuery,config,
                ()=>g.Nodes.Where(n=>n.Generated!=null).All(n=>TryUseGeneratedReply(n.Generated!,permissionQuery,config,()=>true))&&use());
        }
    }
}
