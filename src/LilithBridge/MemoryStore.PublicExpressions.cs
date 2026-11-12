using System.Text.Json;

namespace LilithBridge;

internal sealed partial class MemoryStore
{
    private readonly HashSet<string> _publicExpressionReadStack = new(StringComparer.Ordinal);
    private string PublicExpressionRecoveryIndexPath => Path.Combine(GeneratedRepliesDirectory,".public-expression-recovery.json");
    private string[] PublicExpressionRecoveryKeys()
    {
        if(!File.Exists(PublicExpressionRecoveryIndexPath))return Array.Empty<string>();
        var keys=JsonSerializer.Deserialize<string[]>(File.ReadAllText(PublicExpressionRecoveryIndexPath,GeneratedUtf8));
        if(keys==null||keys.Length>64||keys.Any(k=>!ValidGeneratedKey(k))||keys.Distinct(StringComparer.Ordinal).Count()!=keys.Length)
            throw new InvalidDataException("Invalid public expression recovery index");
        return keys;
    }
    private void IndexPublicExpression(string key,PublicFinalCheckpoint? progress=null)
    {
        var prior=progress==null?new HashSet<string>():Enumerable.Range(1,progress.Revision-1)
            .Select(r=>PublicFinalCheckpointKey(progress with {Revision=r})).ToHashSet(StringComparer.Ordinal);
        var keys=PublicExpressionRecoveryKeys().Where(k=>k!=key&&!prior.Contains(k)).Append(key).TakeLast(64).ToArray();
        AtomicGeneratedWrite(PublicExpressionRecoveryIndexPath,GeneratedUtf8.GetBytes(JsonSerializer.Serialize(keys)),true);
    }
    internal static string PublicExpressionImmutableIdentity(GeneratedReplySnapshot source)
    {
        var m=source.Metadata;
        if(m.PublicExpression?.FinalProgress is {} progress)
            return SemanticTextHash(JsonSerializer.Serialize(new {m.SchemaVersion,m.Key,m.SessionId,m.RequestId,m.AttemptId,
                m.SourceUserEventId,m.SourceUserFingerprint,m.BodyHash,m.Origin,segment=m.PublicExpression.SegmentId,
                root=m.PublicExpression.SourceScopeRoot,sources=m.PublicExpression.Sources,stream_item=m.PublicExpression.StreamItem,progress}));
        if(m.PublicExpression?.StreamItem is {} stream)
            return SemanticTextHash(JsonSerializer.Serialize(new {m.SchemaVersion,m.Key,m.SessionId,m.RequestId,m.AttemptId,
                m.SourceUserEventId,m.SourceUserFingerprint,m.BodyHash,m.Origin,segment=m.PublicExpression.SegmentId,
                root=m.PublicExpression.SourceScopeRoot,sources=m.PublicExpression.Sources,stream_item=stream}));
        return SemanticTextHash(JsonSerializer.Serialize(new { m.SchemaVersion,m.Key,m.SessionId,m.RequestId,m.AttemptId,
            m.SourceUserEventId,m.SourceUserFingerprint,m.BodyHash,m.Origin,
            segment=m.PublicExpression?.SegmentId,root=m.PublicExpression?.SourceScopeRoot??m.PublicExpressionFinalSources?.SourceScopeRoot,
            sources=m.PublicExpression?.Sources??m.PublicExpressionFinalSources?.Sources }));
    }
    internal GeneratedReplySnapshot? ReadPublicExpressionForCapture(string key)
    {lock(_generatedRepliesLock){try{return LoadGenerated(key);}catch{return null;}}}
    private bool PublicExpressionCaptureSourcesCurrent(PublicExpressionSourceFootprint source)
    {
        using var mention=BeginMentionRequest(null);
        using var validation=BeginSourceValidation();
        var config=ConfigStore.PublishedForInitialExperience;
        if(config==null||DataPaths.Root()!=_root||!GeneratedConfigAllows(config)||!MethodSourcesPermitted(source.Sources,"",config))return false;
        foreach(var dependency in source.GeneratedSources)
        {
            var current=LoadGenerated(dependency.Key);
            if(current==null||current.BodyHash!=dependency.BodyHash||PublicExpressionImmutableIdentity(current)!=dependency.ImmutableIdentity
                ||!QualifyGeneratedReply(current).Permitted)return false;
        }
        return ReferenceEquals(config,ConfigStore.PublishedForInitialExperience);
    }
    private bool PublicExpressionScopeCurrent(BridgeConfig? config) => config != null && GeneratedConfigAllows(config)
        && config.ExperimentalTemporaryExpressions && DataPaths.Root()==_root
        && string.Equals(DataPaths.CanonicalPath(config.DataDirectory),DataPaths.CanonicalPath(_root),StringComparison.OrdinalIgnoreCase);

    internal GeneratedReplyWriteResult CapturePublicExpression(ApiResult generation,WorkExpression expression,string outcome,
        PublicFinalCheckpoint? progress=null,PublicExpressionSourceFootprint? originalSources=null)
    {
        if(!GeneratedCaptureScopeMatches(generation)||!GeneratedConfigAllows(generation.GenerationConfig))return new(false,"policy_scope_or_storage_disabled");
        if(expression.Final||expression.ReceivedPublicText.Length==0||generation.IsLocalFallback
            ||generation.DialogueRequest is not { } owner||owner.Id!=generation.RequestId
            ||expression.AttemptId!=generation.Response.AttemptId||expression.SourceUserEventId!=owner.UserEventId
            ||!expression.Id.StartsWith(owner.Id+":segment:",StringComparison.Ordinal))return new(false,"expression_owner_unverified");
        string key=progress!=null?PublicFinalCheckpointKey(progress):expression.StreamItem==null?GeneratedKey(generation):PublicStreamKey(_sessionId,generation.RequestId,
            generation.Response.AttemptId,generation.SourceUserEventId,expression.StreamItem);
        lock(_experienceLock)
        lock(_generatedRepliesLock)
        {
            try
            {
                var body=GeneratedUtf8.GetBytes(expression.ReceivedPublicText);string hash=GeneratedHash(body);
                if(hash!=expression.BodyHash)return new(false,"expression_body_identity_mismatch",key);
                if(File.Exists(GeneratedPath(key,".json")))
                {
                    var old=LoadGenerated(key);
                    if(old?.Metadata.PublicExpression?.SegmentId!=expression.Id||old.BodyText!=expression.ReceivedPublicText)
                        return new(false,"immutable_expression_conflict",key);
                    var receipt=RecordPublicExpressionState(key,expression,outcome);
                    if(receipt.Succeeded)IndexPublicExpression(key,progress);
                    return receipt;
                }
                var plan=progress==null?BubbleChunks.BuildPlan(expression.ReceivedPublicText):BubbleChunks.BuildStreamingPlan(expression.ReceivedPublicText,progress.ChunkTexts);
                var metadata=new GeneratedReplyMetadata {
                    SchemaVersion=2,Key=key,SessionId=_sessionId,RequestId=generation.RequestId,AttemptId=expression.AttemptId,
                    SourceUserEventId=expression.SourceUserEventId,SourceUserFingerprint=GeneratedSourceUserFingerprint(expression.SourceUserEventId),
                    BodyHash=hash,BodyChars=expression.ReceivedPublicText.Length,BodyUtf8Bytes=body.Length,CapturedAtUtc=DateTimeOffset.UtcNow,
                    NetworkBodyComplete=generation.Response.BodyComplete,NetworkOutcome=generation.Response.Outcome,
                    NetworkTerminalEvent=generation.Response.TerminalEvent,NetworkFinishReason=generation.Response.FinishReason,
                    DisplayPlanVersion=expression.DisplayPlanVersion,DisplayMappingState="temporary_expression_plan;not_raw_history",
                    PlanChunks=plan.Chunks.Select(c=>new GeneratedReplyPlanChunk(c.Index,c.StartChar,c.GeneratedChars,c.DisplayStartChar,c.Chars,SemanticTextHash(c.Text))).ToArray(),
                    WhitespaceOmissions=plan.WhitespaceOmissions.ToArray(),
                    PublicExpression=new PublicExpressionArchiveState {
                        SegmentId=expression.Id,WriterSessionId=_sessionId,SourceScopeRoot=_root,ReceiptRevision=expression.StateRevision,
                        WorkOutcome=outcome,SupersededBy=expression.SupersededBy,Ranges=expression.Ranges?.ToArray()??Array.Empty<WorkExpressionRange>(),
                        Sources=originalSources??generation.PublicExpressionFootprint??new(false,"missing_outbound_source_capture",Array.Empty<MethodSource>(),Array.Empty<PublicExpressionGeneratedSource>(),false,null),
                        StreamItem=expression.StreamItem,StreamSegmentCapacity=expression.StreamSegmentCapacity,FinalProgress=progress,StreamAppearances=expression.StreamAppearances
                    }
                };
                metadata.PublicExpression.ImmutableSourceHash=SemanticTextHash(JsonSerializer.Serialize(metadata.PublicExpression.Sources));
                if(!ValidatePublicExpression(metadata,expression.ReceivedPublicText))return new(false,"expression_plan_or_source_shape_invalid",key);
                if(metadata.PublicExpression.Sources.Complete&&!PublicExpressionCaptureSourcesCurrent(metadata.PublicExpression.Sources))
                    return new(false,"expression_sources_changed_before_archive",key);
                Directory.CreateDirectory(GeneratedRepliesDirectory);
                // Publish the schema/owner/closure before the body. A crash cannot
                // leave an unlabelled sensitive body that deletion cannot locate.
                SaveGeneratedMetadata(metadata);
                string path=GeneratedPath(key,".body.txt");
                if(File.Exists(path))
                {
                    if(!File.ReadAllBytes(path).SequenceEqual(body))return new(false,"expression_body_conflict",key);
                }
                else AtomicGeneratedWrite(path,body,overwrite:false);
                IndexPublicExpression(key,progress);
                return new(true,"public_expression_saved",key,hash);
            }
            catch(Exception error){return GeneratedFailure("public_expression_capture",key,error);}
        }
    }

    internal GeneratedReplyWriteResult RecordPublicExpressionState(string key,WorkExpression expression,string outcome)
    {
        if(DataPaths.Root()!=_root)return new(false,"expression_scope_changed",key);
        lock(_experienceLock)
        lock(_generatedRepliesLock)
        {
            try
            {
                var saved=LoadGenerated(key);var metadata=saved?.Metadata;var state=metadata?.PublicExpression;
                // A late callback cannot recreate a physically purged record.
                if(saved==null||state==null)return new(false,"expression_not_saved",key);
                if(state.WriterSessionId!=_sessionId||saved.BodyHash!=expression.BodyHash||state.SegmentId!=expression.Id
                    ||saved.AttemptId!=expression.AttemptId||saved.SourceUserId!=expression.SourceUserEventId
                    ||metadata!.DisplayPlanVersion!=expression.DisplayPlanVersion)return new(false,"expression_receipt_owner_mismatch",key);
                if(expression.StateRevision<state.ReceiptRevision)return new(true,"older_expression_receipt_ignored",key,saved.BodyHash);
                var ranges=expression.Ranges?.ToArray()??Array.Empty<WorkExpressionRange>();
                if(expression.StateRevision==state.ReceiptRevision)
                    return new(state.WorkOutcome==outcome&&state.SupersededBy==expression.SupersededBy&&state.Ranges.SequenceEqual(ranges),"expression_receipt_already_saved_or_conflicted",key,saved.BodyHash);
                // Confirmed appearance is monotonic. Stale/conflicting callbacks
                // cannot downgrade it to accepted, unknown or not-presented.
                if(state.Ranges.Any(old=>old.State=="shown"&&!ranges.Any(n=>n.Index==old.Index&&n.State=="shown")))
                    return new(false,"expression_shown_receipt_would_regress",key);
                state.StreamAppearances=expression.StreamAppearances;
                state.ReceiptRevision=expression.StateRevision;state.WorkOutcome=outcome;state.SupersededBy=expression.SupersededBy;state.Ranges=ranges;
                if(!ValidatePublicExpression(metadata!,saved.BodyText))return new(false,"expression_receipt_invalid",key);
                metadata!.Revision++;SaveGeneratedMetadata(metadata);
                return new(true,"expression_receipt_saved",key,saved.BodyHash);
            }
            catch(Exception error){return GeneratedFailure("public_expression_receipt",key,error);}
        }
    }

    private static bool ValidatePublicExpression(GeneratedReplyMetadata metadata,string body)
    {
        var state=metadata.PublicExpression;var sources=state?.Sources;
        int capacity=state?.StreamSegmentCapacity??DialogueRequest.MaxHttpAttempts;
        if(metadata.SchemaVersion!=2||state==null||sources==null||state.StreamItem!=null&&!PublicStreamShape(state.StreamItem)||state.Kind!="temporary_public_expression"
            ||capacity is not (6 or 64)
            ||metadata.SourceUserEventId.Length==0||metadata.SourceUserFingerprint.Length!=64||!Path.IsPathFullyQualified(state.SourceScopeRoot)
            ||state.WriterSessionId!=metadata.SessionId||state.ReceiptRevision<1||state.Ranges==null||sources.Sources==null||sources.GeneratedSources==null
            ||sources.Sources.Length>2048||sources.GeneratedSources.Length>32||sources.Sources.Any(s=>s==null)
            ||sources.GeneratedSources.Any(s=>s==null||!ValidGeneratedKey(s.Key)||s.BodyHash.Length!=64||s.ImmutableIdentity.Length!=64)
            ||state.ImmutableSourceHash!=SemanticTextHash(JsonSerializer.Serialize(sources))
            ||sources.Complete&&!sources.Sources.Any(s=>s.Kind=="utterance"&&s.Id==metadata.SourceUserEventId)
            ||!state.SegmentId.StartsWith(metadata.RequestId+":segment:",StringComparison.Ordinal)
            ||!int.TryParse(state.SegmentId[(metadata.RequestId+":segment:").Length..],out int index)||index<0||index>=capacity
            ||state.SupersededBy.Length>0&&(!state.SupersededBy.StartsWith(metadata.RequestId+":segment:",StringComparison.Ordinal)
                ||!int.TryParse(state.SupersededBy[(metadata.RequestId+":segment:").Length..],out int later)||later<=index||later>=capacity))return false;
        var progress=state.FinalProgress;
        if(progress!=null&&(progress.GenerationId.Length!=64||progress.Revision is <1 or >28||progress.ChunkTexts==null||progress.ChunkTexts.Length>256
            ||progress.ReadyChunks<0||progress.ReadyChunks>progress.ChunkTexts.Length||progress.Reason.Length>128
            ||progress.State is not ("receiving" or "selected_final_closed" or "deferred_item_closed" or "response_not_completed" or "interrupted_before_unit")
            ||state.StreamItem==null||metadata.Key!=PublicFinalCheckpointKey(progress)
            ||progress.GenerationId!=PublicFinalGenerationId(metadata.SessionId,metadata.RequestId,metadata.AttemptId,metadata.SourceUserEventId,state.StreamItem)))return false;
        var plan=progress==null?BubbleChunks.BuildPlan(body):BubbleChunks.BuildStreamingPlan(body,progress.ChunkTexts);
        if(!plan.IsValid||metadata.DisplayPlanVersion.Length==0||state.Ranges.Length!=plan.Chunks.Count||metadata.PlanChunks.Length!=plan.Chunks.Count
            ||!metadata.WhitespaceOmissions.SequenceEqual(plan.WhitespaceOmissions))return false;
        for(int i=0;i<plan.Chunks.Count;i++)
        {
            var r=state.Ranges[i];var p=plan.Chunks[i];var stored=metadata.PlanChunks[i];
            if(r.Index!=i||r.GeneratedStartChar!=p.StartChar||r.GeneratedChars!=p.GeneratedChars||r.DisplayStartChar!=p.DisplayStartChar||r.DisplayChars!=p.Chars
                ||r.State is not ("shown" or "uncertain" or "accepted_pending" or "not_presented")
                ||stored.Index!=i||stored.StartChar!=p.StartChar||stored.GeneratedChars!=p.GeneratedChars||stored.DisplayStartChar!=p.DisplayStartChar
                ||stored.Chars!=p.Chars||stored.TextHash!=SemanticTextHash(p.Text))return false;
        }
        if(progress!=null&&(state.StreamAppearances==null||state.StreamAppearances.Length!=state.Ranges.Count(r=>r.State=="shown")
            ||state.StreamAppearances.Select(e=>e.Index).Distinct().Count()!=state.StreamAppearances.Length
            ||state.StreamAppearances.Any(e=>e.SegmentId!=state.SegmentId||e.Index<0||e.Index>=plan.Chunks.Count
                ||state.Ranges[e.Index].State!="shown"||plan.Chunks[e.Index].Text!=e.Text||e.PriorPlanVersion.Length==0)))return false;
        return metadata.ShownRanges.Length==0; // separate temporary receipt, no invented raw utterance
    }

    private bool PublicExpressionSourcesPermitted(GeneratedReplySnapshot saved)
    {
        var metadata=saved.Metadata;
        var source=metadata.PublicExpression?.Sources??metadata.PublicExpressionFinalSources!.Sources;
        string root=metadata.PublicExpression?.SourceScopeRoot??metadata.PublicExpressionFinalSources!.SourceScopeRoot;
        var config=ConfigStore.PublishedForInitialExperience;
        if(!PublicExpressionScopeCurrent(config)||root!=_root||!source.Complete||SuppressedExperienceIds().Contains(saved.Key)||_publicExpressionReadStack.Count>=16
            ||!_publicExpressionReadStack.Add(saved.Key))return false;
        ObserveSourceDepth(_publicExpressionReadStack.Count);
        ObserveSourceDeadline(source.OriginalDeadline);
        try
        {
            if(source.OriginalDeadline.HasValue&&DateTimeOffset.UtcNow>=source.OriginalDeadline.Value)return false;
            // Ephemeral review/history owners are not transferable by storing a
            // locator. Unsupported grants are captured Complete=false upstream.
            if(source.RequiresReview&&ReviewContextModes.Normalize(config!.ReviewContextMode)!=ReviewContextModes.OnDemand)return false;
            if(!MethodSourcesPermitted(source.Sources,"",config))return false;
            foreach(var dependency in source.GeneratedSources)
            {
                var current=LoadGenerated(dependency.Key);
                if(current==null||current.BodyHash!=dependency.BodyHash||PublicExpressionImmutableIdentity(current)!=dependency.ImmutableIdentity
                    ||!QualifyGeneratedReply(current).Permitted)return false;
            }
            return ReferenceEquals(config,ConfigStore.PublishedForInitialExperience);
        }
        finally{_publicExpressionReadStack.Remove(saved.Key);}
    }
    private static bool ValidatePublicExpressionFinalClosure(GeneratedReplyMetadata metadata)
    {
        var closure=metadata.PublicExpressionFinalSources;var source=closure?.Sources;
        return closure!=null&&source!=null&&Path.IsPathFullyQualified(closure.SourceScopeRoot)
            &&source.Sources!=null&&source.GeneratedSources!=null&&source.Sources.Length<=2048&&source.GeneratedSources.Length is >0 and <=32
            &&source.Sources.All(s=>s!=null)&&source.GeneratedSources.All(s=>s!=null&&ValidGeneratedKey(s.Key)&&s.BodyHash.Length==64&&s.ImmutableIdentity.Length==64)
            &&closure.ImmutableSourceHash==SemanticTextHash(JsonSerializer.Serialize(source))
            &&(!source.Complete||source.Sources.Any(s=>s.Kind=="utterance"&&s.Id==metadata.SourceUserEventId));
    }

    internal TemporaryWorkCarry RecoverPublicExpressionCarry(BridgeConfig config,string query,IReadOnlyList<string>? excludedOperations=null)
    {
        if(!PublicExpressionScopeCurrent(config)||MemorySemantics.DeclinesArchiveRecall(query))return new("",()=>true);
        GeneratedReplySnapshot[] sources;
        try
        {
            // Bounded locators only; no new per-turn full archive/body scan.
            sources=PublicExpressionRecoveryKeys().Select(k=>GetExactGeneratedReply(k,query,config)).Where(s=>s!=null&&s.Metadata.PublicExpression!=null
                &&excludedOperations?.Contains(s.RequestId)!=true).Select(s=>s!)
                .GroupBy(s=>s.Metadata.PublicExpression?.FinalProgress?.GenerationId??s.Key)
                .Select(g=>g.OrderByDescending(s=>s.Metadata.PublicExpression?.FinalProgress?.Revision??0).First())
                .Where(s=>!HasPublicFinalCanonical(s)).OrderByDescending(s=>s.Metadata.CapturedAtUtc).ToArray();
        }
        catch(Exception error)
        { NativeEventLog.Trace("work.expression.recovery_unavailable","mod","system","Expression recovery index unavailable",error.GetType().Name);return new("",()=>true); }
        var operations=sources.Select(s=>s.RequestId).Distinct(StringComparer.Ordinal).Take(4).ToHashSet(StringComparer.Ordinal);
        var selected=new List<GeneratedReplySnapshot>();var records=new List<object>();var omitted=new List<object>();
        foreach(var source in sources.Where(s=>operations.Contains(s.RequestId)).Reverse())
        {
            var record=GeneratedReplyProjection.Record(source);records.Add(record);
            if(System.Text.Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(records,DialogueMaterialEnvelope.DataJson))>49152)
            { records.RemoveAt(records.Count-1);omitted.Add(new{key=source.Key,reason="carry_capacity_not_irrelevant",source.Metadata.BodyChars});continue; }
            selected.Add(source);
        }
        if(records.Count==0)return new("",()=>true);
        string context="[持久公开表达接续·生成与显示分别记录] 这些是先前模型实际生成的公开表达，不是隐藏推理、玩家发言或新事实；未说出的部分不表示玩家知道，不自动重放或继续旧后台工作。\n"
            +JsonSerializer.Serialize(new {kind="recovered_public_expressions",records,omitted,player_read="unknown"},DialogueMaterialEnvelope.DataJson);
        var frozen=selected.ToArray();
        return new(context,()=>GeneratedRepliesPermitted(frozen,query,config),frozen);
    }

    private bool PurgePublicExpressions(HashSet<string> ids,bool delete=true)
    {
        if(!Directory.Exists(GeneratedRepliesDirectory))return true;
        lock(_generatedRepliesLock)
        {
            var records=new List<(string Path,GeneratedReplyMetadata Metadata)>();
            foreach(var path in Directory.EnumerateFiles(GeneratedRepliesDirectory,"*.json"))
            {
                if(path==PublicExpressionRecoveryIndexPath)continue;
                var m=JsonSerializer.Deserialize<GeneratedReplyMetadata>(File.ReadAllText(path));
                if(m==null)return false;
                if(m.SchemaVersion!=2)continue;
                var sources=m.PublicExpression?.Sources??m.PublicExpressionFinalSources?.Sources;
                if(!ValidGeneratedKey(m.Key)||path!=GeneratedPath(m.Key,".json")||sources?.Sources==null||sources.GeneratedSources==null)return false;
                records.Add((path,m));
            }
            bool changed;
            do
            {
                changed=false;
                foreach(var (_,m) in records)
                {
                    var source=m.PublicExpression?.Sources??m.PublicExpressionFinalSources!.Sources;
                    if(!source.Complete||ids.Contains(m.SourceUserEventId)||source.Sources.Any(s=>ids.Contains(s.Id))
                        ||source.GeneratedSources.Any(s=>ids.Contains(s.Key)))changed|=ids.Add(m.Key);
                }
            }while(changed);
            if(!delete)return true;
            InvalidateSourceValidation();
            foreach(var (path,m) in records.Where(r=>ids.Contains(r.Metadata.Key)))
            {
                // Keep the metadata locator until its body has been removed.
                string body=GeneratedPath(m.Key,".body.txt");if(File.Exists(body))File.Delete(body);File.Delete(path);
            }
            if(File.Exists(PublicExpressionRecoveryIndexPath))
                AtomicGeneratedWrite(PublicExpressionRecoveryIndexPath,GeneratedUtf8.GetBytes(JsonSerializer.Serialize(PublicExpressionRecoveryKeys().Where(k=>!ids.Contains(k)))),true);
            return true;
        }
    }
}
