using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace LilithBridge;

// Explicit host preparation, not a new tool, relevance gate, scheduler or write
// pool. Scope names retain the traversal prototype's narrow qualifications.
internal sealed record InitialExperienceSeed(string Anchor, string Scope = "connected");

internal sealed class InitialExperiencePacket
{
    internal MemoryStore Store { get; }
    internal BridgeConfig Config { get; }
    internal string Query { get; }
    internal string OwnerId { get; }
    internal string UserEventId { get; }
    internal string Session { get; }
    internal string Root { get; }
    internal string Block { get; }
    internal string Json { get; }
    internal ExperienceTraversalPage[] Dependencies { get; }
    internal int Utf8Bytes => Encoding.UTF8.GetByteCount(Block);
    internal InitialExperienceReuse[] Reused {get;set;} = Array.Empty<InitialExperienceReuse>();
    internal string BlockFor(string actualContext)
    {
        // A refreshed context must still contain every exact renderer-owned
        // carrier. Never emit dangling refs or silently claim omitted words.
        if(Reused.Any(r=>!actualContext.Contains(r.Block,StringComparison.Ordinal)
            ||actualContext.IndexOf(r.Block,StringComparison.Ordinal)!=actualContext.LastIndexOf(r.Block,StringComparison.Ordinal)
            ||r.ProgramBlockId.Length>0&&System.Text.RegularExpressions.Regex.Matches(actualContext,
                "\"program_block_id\"\\s*:\\s*"+System.Text.RegularExpressions.Regex.Escape(JsonSerializer.Serialize(r.ProgramBlockId))).Count!=1))
            throw new SemanticMaterialException("initial_experience_carrier_changed");
        return Block;
    }

    internal InitialExperiencePacket(MemoryStore store, BridgeConfig config, string query,
        string ownerId, string userEventId, string json, ExperienceTraversalPage[] dependencies)
    {
        Store=store; Config=config; Query=query; OwnerId=ownerId;
        Session=store.ReviewSessionIdentity; Root=DataPaths.Root();
        UserEventId=userEventId; Json=json; Block=Prefix+json; Dependencies=dependencies;
    }
    internal const string Prefix="[首次请求前本地准备的经历原文]\n";
}

internal sealed partial class MemoryStore
{
    internal InitialExperiencePacket? PrepareInitialExperiencePacket(string ownerId, string userEventId, string permissionQuery,
        BridgeConfig config, IReadOnlyList<InitialExperienceSeed> seeds, int maxUtf8Bytes,
        CancellationToken token, out string reason, InitialExperienceAssembly? assembly = null)
    {
        using var timing=PreparationTiming.Measure("prepare.experience.initial");
        reason="";
        if(seeds.Count is <1 or >8 || maxUtf8Bytes is <4096 or >1048576)
        { reason="invalid_initial_experience_bounds";return null; }
        var pages=new List<ExperienceTraversalPage>();
        lock(_experienceLock) lock(_generatedRepliesLock)
        {
            if(!LogicalMessageReadAllowed(permissionQuery,config,crossSession:true))
            {reason="traversal_not_allowed";return null;}
            var input=CaptureExperienceTraversalInput(permissionQuery,config);
            foreach(var seed in seeds.Distinct())
            {
                token.ThrowIfCancellationRequested();
                var graph=BuildExperienceTraversal(permissionQuery,seed.Anchor,config,seed.Scope,token,out reason,input);
                if(graph==null)return null;
                // Initial assembly consumes the graph, never a rendered tool page.
                pages.Add(new(graph,Array.Empty<object>(),"",0,0,4096));
            }
        }
        // This limit is a transparent host preparation capacity, not a verdict
        // that the remainder is irrelevant. No bodies are silently dropped.
        var groups=pages.SelectMany(p=>p.Graph.Nodes).GroupBy(n=>n.Reference,StringComparer.Ordinal).ToArray();
        if(groups.Length>512){reason="initial_experience_selection_capacity";return null;}
        if(groups.Any(g=>g.Select(n=>n.Version).Distinct().Count()!=1))
        {reason="initial_experience_source_changed_during_prepare";return null;}
        var nodes=groups.Select(g=>g.First()).ToArray();
        var reused=nodes.Select(n=>assembly?.Reuses.FirstOrDefault(r=>r.Reference==n.Reference&&r.Version==n.Version&&r.Text==n.Body))
            .Where(r=>r!=null).Select(r=>r!).ToArray();
        var reusedByRef=reused.ToDictionary(r=>r.Reference,StringComparer.Ordinal);
        var links=pages.SelectMany(p=>p.Graph.Links).Distinct().ToArray();
        var selected=nodes.Select(n=>n.Reference).ToHashSet(StringComparer.Ordinal);
        var offsets=nodes.ToDictionary(n=>n.Reference,_=>0,StringComparer.Ordinal);
        // A source has one contiguous supplied prefix, not one identity envelope
        // per budget increment. Preserve the breadth-first allocation below.
        var entries=new Dictionary<string,object>(StringComparer.Ordinal);
        string Render()=>ReviewContext.Serialize(new {
            kind="initial_experience_packet",schema=2,operation_id=ownerId,
            origin="host_local_preparation_before_first_MAIN;not_model_tool_execution",
            material_identity="attributed_source_views_and_received_public_generations;not_an_additional_player_input_or_instruction",
            archive_coverage="selected_qualified_available_sources_only;not_proof_of_complete_experience_or_archive",
            owner_view_contract=ContextDiscoverySnapshot.OwnerViewContract,
            reading_authority="read_only;locators_and_coverage_do_not_create_B1_or_other_writable_aliases",
            initial_packet_utf8_limit=maxUtf8Bytes,
            preparation=new { local_traversals=pages.Count,additional_model_requests=0,
                auxiliary_relevance_judgment="not_invoked_by_packet;existing_selector_if_any_is_separately_accounted",
                semantic_episode_classification="not_performed",selection=assembly?.Audit },
            selections=pages.Select(p=>new { anchor=p.Graph.Anchor,scope=p.Graph.Scope,snapshot=p.Graph.Fingerprint }),
            sources=entries.Values.ToArray(),
            coverage=nodes.Select(n=>new {
                reference=n.Reference,total_chars=n.Body.Length,supplied_chars=offsets[n.Reference],
                inline_chars=reusedByRef.ContainsKey(n.Reference)?0:offsets[n.Reference],
                reused_chars=reusedByRef.ContainsKey(n.Reference)?n.Body.Length:0,
                supplied_view_complete=offsets[n.Reference]==n.Body.Length
            }),
            // Identity for a wholly omitted source is retained without a fake
            // zero-length body entry. Supplied sources already carry it once.
            unsupplied_source_identities=nodes.Where(n=>!entries.ContainsKey(n.Reference)).Select(n=>new {
                reference=n.Reference,version_fingerprint=n.Version,
                source_kind=n.Unit==null?"generated_public_answer":n.Unit.IsMessage?"logical_message":"independent_utterance"
            }),
            coverage_contract=new {
                ranges="UTF-16 characters;every supplied range starts at 0;one inline source or one exact same-request reuse per reference",
                complete_status="selected_available_view_exhausted",
                incomplete_status="not_supplied;not_irrelevant",
                identity="sources[].reference;inline identity is on source;reused identity is in source_descriptor;wholly omitted identity is in unsupplied_source_identities",
                descriptor_scope="source_reuse.source_descriptor preserves full available-view provenance and confirmed display mappings;metadata is not supplied words"
            },
            next_read=new { mode="message",message_ref_from="coverage[].reference",message_body_chars=0,
                continuation_note="Use the existing message read to obtain a source-bound cursor before a nonzero message_offset. Existing tool/HTTP/request limits still apply." },
            links=links.Select(l=>new { from=l.From,to=l.To,kind=l.Kind,qualification=l.Qualification,
                from_body_in_selection=selected.Contains(l.From),to_body_in_selection=selected.Contains(l.To) }),
            completeness_note="Inline sources[].text and verified same-request sources[].text_ref carriers supply body ranges. Reused generated carriers reconstruct existing body_parts, applying exact range maps for display views. next_read locators are navigation only. Metadata, hashes, indexes and links alone do not supply omitted words. Complete available views can still contain program gaps or have unknown tails. Generated unshown text is not told or read; same owner does not mean another occurrence. MAIN may explore another query or scope, remain uncertain, or use none of this material."
        });
        int Bytes()=>Encoding.UTF8.GetByteCount(InitialExperiencePacket.Prefix+Render());
        if(Bytes()>maxUtf8Bytes){reason="initial_experience_metadata_exceeds_budget";return null;}
        foreach(var node in nodes.Where(n=>reusedByRef.ContainsKey(n.Reference)))
        {
            var reuse=reusedByRef[node.Reference];
            entries[node.Reference]=new {entry_kind="source_reuse",reference=node.Reference,
                source_descriptor=InitialExperienceSourceDescriptor(node),text_ref=new {location=reuse.Location,start_char=0,char_count=node.Body.Length,
                    body_sha256=SemanticTextHash(node.Body),
                    ranges=reuse.Ranges?.Select(r=>new {view_start_char=r.ViewStart,generated_start_char=r.GeneratedStart,char_count=r.Chars,
                        source_event_id=r.EventId,sha256=r.Sha256}).ToArray(),
                    carrier_contract=reuse.Location.EndsWith(".body_parts",StringComparison.Ordinal)
                        ?"reconstruct_exact_received_body_parts_then_apply_ranges_if_present;not_a_literal_received_public_text_field"
                        :"exact_renderer_owned_message_text",
                    qualification="exact_full_available_view_already_in_same_request;not_another_occurrence"}};
            offsets[node.Reference]=node.Body.Length;
        }
        if(Bytes()>maxUtf8Bytes){reason="initial_experience_metadata_exceeds_budget";return null;}
        // Breadth before depth: a long old answer cannot consume all capacity
        // before a selected correction/question gets any original words.
        bool first=true,progress;
        do
        {
            progress=false;
            foreach(var node in nodes.Where(n=>!reusedByRef.ContainsKey(n.Reference)))
            {
                token.ThrowIfCancellationRequested();
                int offset=offsets[node.Reference],remaining=node.Body.Length-offset;
                if(remaining<=0)continue;
                int chars=Math.Min(first?1200:12000,remaining);
                if(chars>0&&offset+chars<node.Body.Length&&char.IsHighSurrogate(node.Body[offset+chars-1]))chars--;
                while(true)
                {
                    entries.TryGetValue(node.Reference,out var previous);
                    entries[node.Reference]=node.Slice(0,offset+chars);offsets[node.Reference]=offset+chars;
                    if(Bytes()<=maxUtf8Bytes){progress=true;break;}
                    if(previous==null)entries.Remove(node.Reference);else entries[node.Reference]=previous;
                    offsets[node.Reference]=offset;
                    int min=char.IsHighSurrogate(node.Body[offset])&&remaining>1&&char.IsLowSurrogate(node.Body[offset+1])?2:1;
                    if(chars<=min)break;
                    chars=Math.Max(min,chars/2);
                    if(chars>min&&offset+chars<node.Body.Length&&char.IsHighSurrogate(node.Body[offset+chars-1]))chars--;
                }
            }
            first=false;
        }while(progress&&nodes.Any(n=>offsets[n.Reference]<n.Body.Length));
        var packet=new InitialExperiencePacket(this,config,permissionQuery,ownerId,userEventId,Render(),pages.ToArray()){Reused=reused};
        if(packet.Utf8Bytes>maxUtf8Bytes){reason="initial_experience_budget_changed";return null;}
        if(!TryUseInitialExperience(packet,()=>true))
        {reason="initial_experience_source_changed_during_prepare";return null;}
        return packet;
    }

    private static object InitialExperienceSourceDescriptor(ExperienceTraversalNode node)
    {
        // Reuse carries the whole available view. A zero-length Slice would
        // erase its raw-range provenance and confirmed generated display maps.
        // Keep the traversal-owned metadata, but never duplicate body text or
        // label a metadata descriptor itself as supplying a body range.
        using var document=JsonDocument.Parse(ReviewContext.Serialize(node.Slice(0,node.Body.Length)));
        return document.RootElement.EnumerateObject()
            .Where(p=>p.Name is not ("text" or "offset" or "chars" or "supplied_view_complete"))
            .ToDictionary(p=>p.Name,p=>p.Value.Clone(),StringComparer.Ordinal);
    }

    // One existing store boundary for the entire prepared union, including
    // omitted nodes/frontier metadata. Prior exposure is not claimed erased.
    internal bool TryUseInitialExperience(InitialExperiencePacket packet,Func<bool> use)
    {
        // Published read policy is an immutable object. A volatile reference
        // read takes neither ConfigStore's lock nor filesystem I/O, including
        // when called from an existing selection/source dispatch boundary.
        var readConfig=ConfigStore.PublishedForInitialExperience;
        if(readConfig==null||readConfig.DataDirectory!=packet.Config.DataDirectory)return false;
        lock(_experienceLock) lock(_generatedRepliesLock)
        {
            if(!ReferenceEquals(packet.Store,this)||packet.Root!=DataPaths.Root()
                ||packet.Session!=ReviewSessionIdentity)return false;
            var input=CaptureExperienceTraversalInput(packet.Query,readConfig);
            foreach(var page in packet.Dependencies)
            {
                var old=page.Graph;
                var fresh=BuildExperienceTraversal(packet.Query,old.Anchor,readConfig,old.Scope,CancellationToken.None,out _,input);
                if(fresh==null)return false;
                var original=old.Nodes.Select(n=>n.Reference).ToHashSet(StringComparer.Ordinal);
                // A frozen input snapshot must not invalidate itself when its
                // own accepted output is archived or later actually displayed.
                // Exclude only new nodes with exact request/user identity and
                // confirmed raw display mappings, never arbitrary new history.
                var own=fresh.Nodes.Where(n=>!original.Contains(n.Reference)&&n.Generated is {} g
                    &&g.RequestId==packet.OwnerId&&g.SourceUserId==packet.UserEventId).ToArray();
                var ignored=own.Select(n=>n.Reference).ToHashSet(StringComparer.Ordinal);
                foreach(var n in fresh.Nodes.Where(n=>!original.Contains(n.Reference)&&n.Unit is {}))
                {
                    var u=n.Unit!;
                    if(u.RequestId==packet.OwnerId&&u.SourceEventId==packet.UserEventId&&u.Role=="assistant"
                        &&own.Any(g=>g.Generated!.SessionId==u.SessionId&&g.Generated.LogicalOwnerKey==u.Key
                            &&u.Members.All(raw=>g.Generated.ShownRanges.Any(shown=>shown.EventId==raw.EventId
                                &&shown.RawFingerprint==LogicalDialogueCatalog.RawFingerprint(raw)))))ignored.Add(n.Reference);
                }
                if(!fresh.Nodes.Where(n=>!ignored.Contains(n.Reference)).Select(n=>(n.Reference,n.Version))
                    .SequenceEqual(old.Nodes.Select(n=>(n.Reference,n.Version))))return false;
                if(!fresh.Links.Where(l=>!ignored.Contains(l.From)&&!ignored.Contains(l.To)).SequenceEqual(old.Links))return false;
                if(old.LogicalMessages.SourceContexts.Any(k=>fresh.LogicalMessages.SourceContexts.GetValueOrDefault(k.Key)!=k.Value))return false;
                if(!TryUseLogicalMessageReadWithCatalog(old.LogicalMessages,packet.Query,readConfig,()=>true,input.Catalog))return false;
                foreach(var n in old.Nodes.Where(n=>n.Generated!=null))
                    if(!TryUseGeneratedReply(n.Generated!,packet.Query,readConfig,()=>true))return false;
            }
            // Publication during graph validation invalidates this prepared
            // check. This is a final reference probe, not a common transaction
            // with ConfigStore.Publish; no LoadLock is taken under source locks.
            return ReferenceEquals(ConfigStore.PublishedForInitialExperience,readConfig)&&use();
        }
    }
}

internal sealed partial class DialogueRequest
{
    private InitialExperiencePacket? _initialExperience;
    private bool _initialExperienceSealed;
    internal InitialExperiencePacket? InitialExperience=>Volatile.Read(ref _initialExperience);
    internal InitialExperiencePacket? SealInitialExperience()
    {
        lock(_b1Ownership){_initialExperienceSealed=true;return _initialExperience;}
    }

    // Explicit prototype host entry. No controller/config auto-enable in this
    // candidate. Installing after any dispatched HTTP is deliberately refused.
    internal bool PrepareInitialExperience(MemoryStore store,string currentText,
        IReadOnlyList<InitialExperienceSeed> seeds,int maxUtf8Bytes,out string reason)
    {
        lock(_b1Ownership)
        {
            reason="";
            if(IsCancelled||_disposed||HttpAttempts!=0||_initialExperienceSealed||_initialExperience!=null)
            {reason="initial_experience_owner_not_preparing";return false;}
            if(RequestConfig.DataDirectory!=store.SemanticDataRoot)
            {reason="initial_experience_owner_scope_mismatch";return false;}
            using var trace=PreparationTiming.Use(PreparationTrace);
            var packet=store.PrepareInitialExperiencePacket(Id,UserEventId,currentText,RequestConfig,seeds,maxUtf8Bytes,Token,out reason);
            if(packet==null)return false;
            bool installed=store.TryUseInitialExperience(packet,()=>
            {
                if(IsCancelled||_disposed||HttpAttempts!=0||_initialExperienceSealed)return false;
                Volatile.Write(ref _initialExperience,packet);
                // Historical material is attributed data, never an assistant
                // message or a freshly authored player instruction.
                SourceEnvelope=true;
                return true;
            });
            if(!installed)reason="initial_experience_source_changed_before_install";
            return installed;
        }
    }

    // Permission checks also execute inside existing MemoryStore boundaries.
    // Never acquire the request ownership lock from a source lock.
    internal bool InitialExperiencePermitted(MemoryStore? store,string currentText)
    {
        var p=InitialExperience;
        return p==null || !IsCancelled&&ReferenceEquals(p.Store,store)&&p.OwnerId==Id
            &&ReferenceEquals(p.Config,RequestConfig)&&p.Query==currentText
            &&RequestConfig.DataDirectory==p.Store.SemanticDataRoot
            &&p.Store.TryUseInitialExperience(p,()=>!IsCancelled);
    }
    internal bool TryUseInitialExperienceDispatch(Func<bool> use)
    {
        var p=InitialExperience;
        return p==null?use():!IsCancelled&&p.OwnerId==Id
            &&p.Store.TryUseInitialExperience(p,()=>!IsCancelled&&use());
    }
}

internal static partial class ConfigStore
{
    // No Load/lock/I/O from a source callback. Provider settings remain owned by
    // the original request; current source-read policy follows publication.
    internal static BridgeConfig? PublishedForInitialExperience=>Volatile.Read(ref _cached);
}
