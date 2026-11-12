using System.Text.Json;

namespace LilithBridge;

// Only renderer-owned, exact same-request carriers. No arbitrary substring
// search in conversation text grants a reusable original or write authority.
internal sealed record InitialExperienceReuseRange(int ViewStart,int GeneratedStart,int Chars,string EventId,string Sha256);
internal sealed record InitialExperienceReuse(string Reference,string Version,string Text,string Location,string Block,
    IReadOnlyList<InitialExperienceReuseRange>? Ranges=null,string ProgramBlockId="",string SelectionRole="auxiliary");
internal sealed record InitialExperienceAssembly(IReadOnlyList<InitialExperienceReuse> Reuses,object Audit);
internal sealed record InitialExperienceAnchor(string Reference,string Basis,string Role,string Scope="connected");

internal sealed partial class DialogueRequest
{
    internal string InitialExperiencePreparationStatus {get;private set;}="disabled";
    internal string[] InitialExperienceDeferred {get;private set;}=Array.Empty<string>();
    internal double InitialExperiencePreparationMs {get;private set;}
    internal string InitialExperiencePreparationNote=>!RequestConfig.InitialExperienceEnabled||InitialExperience!=null
        ||InitialExperiencePreparationStatus is "disabled" or "no_selected_historical_anchor"?"":
        "\n[首次材料准备状态] "+InitialExperiencePreparationStatus+
        "；本地问答/更正补齐未安装。既有已选原文与读取权限未改，未补齐不等于没有相关前因；可按实际需要继续查证，不要求填补缺失或提及此程序状态。";

    internal void BeginInitialExperiencePreparation()
    {
        if(RequestConfig.InitialExperienceEnabled)SourceEnvelope=true;
    }
    private InitialExperienceReuse[] InitialExperienceCarriers(string actualContext)
    {
        var result=new List<InitialExperienceReuse>();
        lock(_b1Ownership)
        {
            foreach(var view in _contextBodyViews.Where(v=>actualContext.Contains(v.Block,StringComparison.Ordinal)))
            {
                try
                {
                    using var doc=JsonDocument.Parse(view.DataJson);
                    var root=doc.RootElement;string id=root.GetProperty("program_block_id").GetString()!;
                    // The same canonical location must identify exactly one block.
                    if(actualContext.IndexOf(view.Block,StringComparison.Ordinal)!=actualContext.LastIndexOf(view.Block,StringComparison.Ordinal)
                        ||_contextBodyViews.Any(other=>other.Block!=view.Block&&actualContext.Contains(other.Block,StringComparison.Ordinal)
                            &&ContextBlockIdentity(other.DataJson)==id))continue;
                    int index=0;
                    foreach(var message in root.GetProperty("messages").EnumerateArray())
                    {
                        if(message.GetProperty("body_included").GetBoolean())
                            result.Add(new(message.GetProperty("message_ref").GetString()!,message.GetProperty("version_fingerprint").GetString()!,
                                message.GetProperty("text").GetString()!,"runtime_context.experience_blocks["+id+"].messages["+index+"].text",view.Block,null,id,
                                    message.GetProperty("member_index").EnumerateArray().Any(m=>view.AnchorEventIds.Contains(m.GetProperty("event_id").GetString()!,StringComparer.Ordinal))?"anchor":
                                    message.GetProperty("member_index").EnumerateArray().Any(m=>view.DirectEventIds.Contains(m.GetProperty("event_id").GetString()!,StringComparer.Ordinal))?"direct":"auxiliary"));
                        index++;
                    }
                }
                catch(Exception e) when(e is JsonException or InvalidOperationException or KeyNotFoundException) { }
            }
        }
        lock(_generatedSupplyLock)
            if(_generatedMaterial is {} generated&&generated.Block.Length>0&&actualContext.Contains(generated.Block,StringComparison.Ordinal)
                &&actualContext.IndexOf(generated.Block,StringComparison.Ordinal)==actualContext.LastIndexOf(generated.Block,StringComparison.Ordinal))
                result.AddRange(generated.Sources.Select(g=>new InitialExperienceReuse("generated/"+g.Key,g.VersionFingerprint,g.BodyText,
                    "runtime_context.generated_replies["+g.Key+"].body_parts",generated.Block,SelectionRole:
                    SemanticMaterial?.Result?.Candidates.Any(c=>c.Kind=="generated_reply"&&c.EventId=="generated/"+g.Key
                        &&c.Version==g.VersionFingerprint&&c.TextHash==MemoryStore.SemanticTextHash(g.BodyText))==true?"direct":"auxiliary")));
        return result.GroupBy(r=>r.Reference,StringComparer.Ordinal).Where(g=>g.Distinct().Count()==1).Select(g=>g.First()).ToArray();
    }

    // One post-compose stage consumes exact renderer receipts, not discovery
    // candidates. The controller calls Begin before Build to fix attribution.
    // No re-run of lexical/semantic retrieval and no new selector round.
    internal bool PrepareRecalledInitialExperience(MemoryStore store,string currentText,string recallQuery,string context,
        IReadOnlyList<MemoryTurn> history,IReadOnlyList<MemoryTurn> recalled,SemanticRecallTrace? trace)
    {
        if(!RequestConfig.InitialExperienceEnabled)return false;
        var watch=System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var timing=PreparationTiming.Use(PreparationTrace);
            using var stage=PreparationTiming.Measure("prepare.experience.auto");
            InitialExperiencePreparationStatus="preparing";
            if(currentText!=recallQuery){InitialExperiencePreparationStatus="query_owner_mismatch";return false;}
            if(IsCancelled||HttpAttempts!=0||InitialExperience!=null||_initialExperienceSealed)
            {InitialExperiencePreparationStatus="owner_not_preparing";return false;}
            var carriers=store.InitialExperienceViewCarriers(currentText,RequestConfig,InitialExperienceCarriers(context));
            bool relevanceActive=RequestConfig.ContextRelevanceEnabled&&SemanticMaterial?.Result?.RelevanceBypassed!=true;
            var plan=trace?.RelevancePlan;
            if(relevanceActive&&(plan?.Status!="accepted"||!store.ContextRelevanceSourcesCurrent(plan.Snapshot,RequestConfig)))
            {InitialExperiencePreparationStatus="selection_unavailable_or_changed;legacy_body_and_independent_read_retained";return false;}
            var anchors=store.InitialExperienceAnchors(currentText,RequestConfig,history,recalled,carriers,
                relevanceActive?plan:null,Token);
            if(anchors.Length==0){InitialExperiencePreparationStatus="no_selected_historical_anchor";return false;}
            // A fixed host capacity is not a semantic verdict. All original
            // choices keep their existing context; extra closures remain locatable.
            var selected=anchors.Take(8).ToArray();
            InitialExperienceDeferred=anchors.Skip(8).Select(a=>a.Reference).ToArray();
            var assembly=new InitialExperienceAssembly(carriers,new {
                basis=relevanceActive?"existing_accepted_context_relevance_choices":"existing_actually_rendered_recall;not_raw_discovery_candidates",
                model_requests_added=0,anchors=selected.Select(a=>new {reference=a.Reference,basis=a.Basis,role=a.Role,scope=a.Scope}),
                local_order_capacity=new {prior_or_next_exchanges=4,absolute_minutes=30,
                    qualification="host_capacity_for_explicit_speech_relative_lookup_only;not_an_episode_boundary_or_cause",
                    beyond="available_session_order_frontier_and_independent_source_reads_remain;not_irrelevant_or_absent"},
                deferred=InitialExperienceDeferred,deferral="host_capacity_only;not_irrelevant;existing_context_and_independent_read_retained",
                scope="exact_exchange_and_explicit_correction_closure;explicit_speech_relative_queries_add_bounded_local_available_order;not_cause",
                choice_qualification="existing_relevance_hypothesis_or_recall_selection;not_historical_cause_truth_or_required_mention"});
            lock(_b1Ownership)
            {
                if(IsCancelled||_disposed||HttpAttempts!=0||_initialExperienceSealed||_initialExperience!=null)
                {InitialExperiencePreparationStatus="owner_not_preparing";return false;}
                var packet=store.PrepareInitialExperiencePacket(Id,UserEventId,currentText,RequestConfig,
                    selected.Select(a=>new InitialExperienceSeed(a.Reference,a.Scope)).ToArray(),RequestConfig.InitialExperienceMaxUtf8Bytes,
                    Token,out string reason,assembly);
                if(packet==null){InitialExperiencePreparationStatus=reason;return false;}
                bool installed=store.TryUseInitialExperience(packet,()=>
                {
                    if(IsCancelled||_disposed||HttpAttempts!=0||_initialExperienceSealed)return false;
                    Volatile.Write(ref _initialExperience,packet);SourceEnvelope=true;return true;
                });
                InitialExperiencePreparationStatus=installed?"prepared":"source_changed_before_install";
                return installed;
            }
        }
        catch(OperationCanceledException){throw;}
        catch(Exception error)
        {
            // Preparation is optional, before any HTTP. Preserve the existing
            // legal context/tool route rather than fabricating a partial packet.
            InitialExperiencePreparationStatus="preparation_unavailable:"+error.GetType().Name;return false;
        }
        finally
        {
            InitialExperiencePreparationMs=watch.Elapsed.TotalMilliseconds;
            PreparationTrace?.Value("initial_experience_prepared",InitialExperience!=null?1:0);
            NativeEventLog.Trace("context.initial_experience","mod","system","local first-packet preparation",InitialExperiencePreparationStatus);
        }
    }
}

internal sealed partial class MemoryStore
{
    internal InitialExperienceReuse[] InitialExperienceViewCarriers(string query,BridgeConfig config,InitialExperienceReuse[] carriers)
    {
        using var mention=BeginMentionRequest(query,true);
        using var native=BeginNativeDialogueRequest(query,true,config);
        var catalog=CurrentLogicalMessageCatalog();var result=carriers.ToList();
        foreach(var carrier in carriers.Where(c=>c.Reference.StartsWith("generated/",StringComparison.Ordinal)))
        {
            var generated=GetExactGeneratedReply(carrier.Reference[10..],query,config);
            if(generated==null||generated.VersionFingerprint!=carrier.Version||generated.BodyText!=carrier.Text)continue;
            var answer=new ContextRelevanceAnswerOwner(generated,GeneratedMappedOriginals(generated));
            foreach(var unit in catalog.Units.Where(u=>!u.HasGaps&&answer.Covers(u)))
            {
                if(result.Any(r=>r.Reference==unit.Key))continue;
                var ranges=new List<InitialExperienceReuseRange>();int at=0;
                foreach(var span in unit.SourceSpans.OrderBy(s=>s.OffsetInUnit))
                {
                    var raw=unit.Members.FirstOrDefault(t=>t.EventId==span.EventId);
                    var maps=generated.DisplayRanges.Where(d=>d.EventId==span.EventId&&d.GeneratedStartChar.HasValue
                        &&d.RawFingerprint==(raw==null?"":LogicalDialogueCatalog.RawFingerprint(raw))).ToArray();
                    if(raw==null||span.OffsetInUnit!=at||span.SourceOffset!=0||span.Chars!=raw.Text.Length||maps.Length!=1)break;
                    int start=maps[0].GeneratedStartChar!.Value;
                    if(start<0||start+raw.Text.Length>generated.BodyText.Length
                        ||generated.BodyText.Substring(start,raw.Text.Length)!=raw.Text)break;
                    ranges.Add(new(span.OffsetInUnit,start,span.Chars,span.EventId,RequestSourceBody.Hash(raw.Text)));at+=span.Chars;
                }
                if(at==unit.Text.Length&&ranges.Count>0)
                    result.Add(new(unit.Key,unit.VersionFingerprint,unit.Text,carrier.Location,carrier.Block,ranges.ToArray(),SelectionRole:carrier.SelectionRole));
            }
        }
        return result.ToArray();
    }
    internal InitialExperienceAnchor[] InitialExperienceAnchors(string query,BridgeConfig config,IReadOnlyList<MemoryTurn> history,
        IReadOnlyList<MemoryTurn> recalled,IReadOnlyList<InitialExperienceReuse> carriers,ContextRelevancePlan? plan,
        CancellationToken token)
    {
        using var mention=BeginMentionRequest(query,true);
        using var native=BeginNativeDialogueRequest(query,true,config);
        var catalog=CurrentLogicalMessageCatalog();
        var actual=carriers.ToDictionary(c=>c.Reference,StringComparer.Ordinal);
        var recalledIds=recalled.Select(t=>t.EventId).ToHashSet(StringComparer.Ordinal);
        var historyIds=history.Select(t=>t.EventId).ToHashSet(StringComparer.Ordinal);
        var output=new List<InitialExperienceAnchor>();
        string Exchange(string reference)
        {
            var unit=catalog.FindByKey(reference);
            if(unit is {Role:"assistant"}&&catalog.FindByEvent(unit.SourceEventId) is {Role:"user"} question&&question.SessionId==unit.SessionId)
                return question.Key;
            if(reference.StartsWith("generated/",StringComparison.Ordinal)
                &&GetExactGeneratedReply(reference[10..],query,config) is {} generated
                &&catalog.FindByEvent(generated.SourceUserId) is {Role:"user"} q&&q.SessionId==generated.SessionId)return q.Key;
            return reference;
        }
        void Add(string reference,string basis,string role,string scope="connected")
        {
            token.ThrowIfCancellationRequested();
            if(!actual.ContainsKey(reference))return; // candidates/metadata are not body supply
            string anchor=Exchange(reference);
            int at=output.FindIndex(a=>a.Reference==anchor);
            if(at<0)output.Add(new(anchor,basis,role,scope));
            else if(output[at].Scope=="connected"&&scope!="connected")output[at]=new(anchor,basis,role,scope);
        }
        if(plan!=null)
        {
            // Source dependencies do not become new semantic seeds.
            foreach(var choice in plan.Choices)
            {
                var item=plan.Snapshot.Candidates.First(i=>i.Key==choice.Key);
                if(item.Unit is {} unit)
                {
                    Add(unit.Key,"selected_original",choice.Role);
                    // ContextBuilder can carry this selected display view by its
                    // exact received original. Equality of owner alone is insufficient.
                    foreach(var carrier in carriers.Where(c=>c.Reference.StartsWith("generated/",StringComparison.Ordinal)))
                        if(GetExactGeneratedReply(carrier.Reference[10..],query,config) is {} original
                            &&new ContextRelevanceAnswerOwner(original,GeneratedMappedOriginals(original)).Covers(unit))
                            Add(carrier.Reference,"selected_display_view_carried_by_exact_received_original",choice.Role);
                }
                else if(item.Generated is {} generated)Add("generated/"+generated.Key,"selected_received_public_answer",choice.Role);
                else if(item.Thought!=null)
                    foreach(var source in item.Provenance.Where(t=>recalledIds.Contains(t.EventId)))
                        if(catalog.FindByEvent(source.EventId) is {} provenance)
                            Add(provenance.Key,"provenance_of_selected_revisable_thought;not_new_relevance_seed",choice.Role);
            }
        }
        else
        {
            // The renderer receipt keeps direct hits separate from neighbours,
            // thought provenance and the other actually supplied originals.
            // Missing direct metadata fails closed for extra preparation only.
            // A speech-relative query expands the located anchor, not every
            // lower-ranked lexical alternative. Those originals remain in their
            // already rendered carriers and independent source-read route.
            foreach(var carrier in carriers.Where(c=>c.SelectionRole is "anchor" or "direct")
                .Where(c=>MemorySemantics.ParseSpeechRelativeRecall(query)==null||c.SelectionRole=="anchor"))
                if(catalog.FindByKey(carrier.Reference) is {} unit&&!unit.Members.All(t=>historyIds.Contains(t.EventId)))
                    Add(unit.Key,"renderer_receipt_direct_original_recall",carrier.SelectionRole,
                        carrier.SelectionRole=="anchor"?MemorySemantics.ParseSpeechRelativeRecall(query)?.Scope??"connected":"connected");
                else if(carrier.Reference.StartsWith("generated/",StringComparison.Ordinal)
                    &&GetExactGeneratedReply(carrier.Reference[10..],query,config) is {} g
                    &&!historyIds.Contains(g.SourceUserId))
                    Add(carrier.Reference,"actually_carried_direct_semantic_public_generation","direct");
            // A received generation carried by the recent history window is not
            // automatically a new historical anchor. Only exact direct display
            // receipts above, or explicit accepted C4 choices, may seed it.
        }
        return output.ToArray();
    }
}
