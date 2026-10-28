namespace LilithBridge;

internal sealed partial class MemoryStore
{
    internal SessionReadMethodOwner ReadMethods { get; } = new();
    internal SessionReadMethodOwner SyncReadMethods(BridgeConfig config)
    {
        ReadMethods.Sync(string.Join("|",_sessionId,_root,DataPaths.Root(),config.DataDirectory),config.SessionReadMethodsEnabled);
        return ReadMethods;
    }
    // Resolver reuse does not open a B1 database for ordinary/history/review sources.
    // Only a genuinely supplied B1 identity needs its existing store resolver.
    private MethodSource? ResolveMethodSource(B1Ref reference, B1Document? doc)
    {
        if(reference.Kind=="recent_slot")
        {
            var matches=_events.Where(IsMethodRecentSlot).Where(t=>MethodRecentSlotId(t)==reference.Version).ToArray();
            if(matches.Length!=1)return null;
            var turn=matches[0];
            if(!IsDerivedMemoryVisible(turn)||!IsMentionAllowed(turn)||!IsNativeSupplyAllowed(turn)
                ||SuppressedTextMatches(turn.Text))return null;
            string hash=B1Hash(turn.Text);
            if(reference.Sha256.Length>0&&reference.Sha256!=hash)return null;
            return new("recent_slot",reference.Version,hash,SourceVersion("recent_slot",turn).Fingerprint,
                reference.Purpose,"current_recent_slot");
        }
        if(reference.Kind=="profile_slot")
        {
            var matches=_profile.Where(t=>t.Role==reference.Version).ToArray();
            if(matches.Length!=1)return null;
            var profile=matches[0];
            if(!IsDerivedMemoryVisible(profile)||!IsMentionAllowed(profile)||!IsNativeSupplyAllowed(profile)
                ||SuppressedTextMatches(profile.Text))return null;
            string hash=B1Hash(profile.Text);
            if(reference.Sha256.Length>0&&reference.Sha256!=hash)return null;
            return new("profile_slot",reference.Version,hash,SourceVersion("profile_slot",profile).Fingerprint,
                reference.Purpose,"current_profile_slot");
        }
        var result=ResolveB1(reference,doc??new B1Document(),new(StringComparer.Ordinal));
        if(!result.Found)return null;
        var m=result.Material!;
        if(reference.Kind=="b1" && doc!=null && B1ManagementState(doc,reference.Version) is "withdrawn" or "suppressed")return null;
        return new(reference.Kind,reference.Version,m.Ref.Sha256,m.VersionFingerprint,reference.Purpose,m.State);
    }
    private static bool IsMethodRecentSlot(MemoryTurn t)=>t.EventId.Length==0
        &&(t.Kind=="native.memory"||t.Kind.StartsWith("interaction.",StringComparison.OrdinalIgnoreCase));
    private static string MethodRecentSlotId(MemoryTurn t)=>B1Hash(System.Text.Json.JsonSerializer.Serialize(new{t.Kind,t.Role,t.Timestamp}));
    internal bool MethodSourcesPermitted(IEnumerable<MethodSource> sources,DialogueRequest request)
        =>MethodSourcesPermitted(sources,request.ReviewCurrentText,request.B1ReadConfig);
    // Catalog lifetime check only: this does not authorize or supply anything.
    // Reuse exact source resolution without the enclosing request's temporary
    // mention/native selection. Persistent suppression and version checks stay.
    internal bool MethodSourcesCurrent(IEnumerable<MethodSource> sources)
    {
        var dependencies=sources.Distinct().ToArray();
        if(dependencies.Length==0)return true;
        return WithNeutralMethodSourceView(()=>MethodSourceVersionsMatch(dependencies));
    }
    private T WithNeutralMethodSourceView<T>(Func<T> inspect)
    {
        lock(_experienceLock)
        {
            var mention=_mentionReadPolicy.Value;var native=_nativeDialoguePolicy.Value;
            var turns=_b1ReadTurns;var permission=_b1Permission;
            bool session=_b1ReadSession,built=_b1ReadSessionBuilt;
            var document=_b1MemoDocument;var resolved=_b1ResolutionMemo.ToArray();
            var entries=_b1IndexedEntries;var index=_b1Index;var search=_b1SearchIndex;
            var sourceSearch=_b1SourceSearchIndex;var discovery=_b1DiscoveryIndex;
            _mentionReadPolicy.Value=null;_nativeDialoguePolicy.Value=null;
            // The lifetime probe has its own resolver view even if called in
            // an existing B1 read session. Never reuse or publish its memo.
            _b1ReadSession=false;
            try{return inspect();}
            finally
            {
                _mentionReadPolicy.Value=mention;_nativeDialoguePolicy.Value=native;
                _b1ReadTurns=turns;_b1Permission=permission;
                _b1ReadSession=session;_b1ReadSessionBuilt=built;_b1MemoDocument=document;
                _b1ResolutionMemo.Clear();foreach(var item in resolved)_b1ResolutionMemo.Add(item.Key,item.Value);
                _b1IndexedEntries=entries;_b1Index=index;_b1SearchIndex=search;
                _b1SourceSearchIndex=sourceSearch;_b1DiscoveryIndex=discovery;
            }
        }
    }
    // Failure of exact resolution is not proof that a retained result died.
    // Only explicit tombstones or a unique object's actual version can prove it.
    internal SourceContinuity CheckMethodSourceContinuity(IEnumerable<MethodSource> sources)
        => WithNeutralMethodSourceView(()=>
        {
            var result=SourceContinuity.Same;
            foreach(var source in sources.Distinct())
            {
                SourceContinuity next;
                try
                {
                    if(SuppressedExperienceIds().Contains(source.Id))next=SourceContinuity.KnownInvalid;
                    else
                    {
                        BeginB1Read();
                        var doc=source.Kind=="b1"?LoadB1():null;
                        var now=source.Kind=="b1"&&doc==null?null
                            :ResolveMethodSource(new(source.Kind,source.Id,source.Hash,source.Purpose),doc);
                        next=now==source?SourceContinuity.Same:MethodSourceInvalidationFacts(source,doc);
                    }
                }
                catch { next=SourceContinuity.Unknown; }
                result=MergeContinuity(result,next);
            }
            return result;
        });
    private SourceContinuity MethodSourceInvalidationFacts(MethodSource source,B1Document? doc)
    {
        if(source.Kind=="b1")
        {
            if(doc==null)return SourceContinuity.Unknown;
            string management=B1ManagementState(doc,source.Id);
            if(management is "withdrawn" or "suppressed" or "deleting" or "deleted")return SourceContinuity.KnownInvalid;
            var entries=doc.Entries.Where(e=>e.Version==source.Id).ToArray();
            if(entries.Length!=1)return SourceContinuity.Unknown;
            var entry=entries[0];
            if(SuppressedTextMatches(entry.Text)||B1Hash(entry.Text)!=source.Hash)return SourceContinuity.KnownInvalid;
            string? head=doc.Current.GetValueOrDefault(entry.LogicalId);
            if(source.Purpose=="current")
            {
                if(string.IsNullOrEmpty(head))return SourceContinuity.Unknown;
                if(head!=source.Id)return SourceContinuity.KnownInvalid;
            }
            string fingerprint=B1Hash(System.Text.Json.JsonSerializer.Serialize(entry)+"|management="+management
                +(source.Purpose=="current"?"|"+head:""));
            return fingerprint!=source.Fingerprint?SourceContinuity.KnownInvalid:SourceContinuity.Unknown;
        }
        var candidates=source.Kind switch
        {
            "profile_slot"=>_profile.Where(t=>t.Role==source.Id),
            "recent_slot"=>_events.Where(IsMethodRecentSlot).Where(t=>MethodRecentSlotId(t)==source.Id),
            _=>ReadExperiences(true,false).Concat(_reflectionNotes).Concat(_reflectionLessons)
                .Concat(_relationshipReadings).Concat(_dreams).Concat(_autonomous).Concat(_profile).Concat(_summaries)
                .Concat(_native).Concat(_events).Where(t=>t.EventId==source.Id&&B1Kind(t)==source.Kind)
        };
        var matches=candidates.ToArray();
        if(matches.Length!=1||matches[0].SchemaVersion!=1)return SourceContinuity.Unknown;
        var turn=matches[0];
        if(HasReviewRetractionFact(turn))return SourceContinuity.KnownInvalid;
        return B1Hash(turn.Text)!=source.Hash||SourceVersion(source.Kind,turn).Fingerprint!=source.Fingerprint
            ?SourceContinuity.KnownInvalid:SourceContinuity.Unknown;
    }
    internal bool MethodSourcesPermitted(IEnumerable<MethodSource> sources,string query,BridgeConfig? config)
    {
        var dependencies=sources.Distinct().ToArray();
        if(dependencies.Length==0)return true;
        lock(_experienceLock)
        {
            using var mention=BeginMentionRequest(query,true);
            using var native=BeginNativeDialogueRequest(query,true,config);
            return MethodSourceVersionsMatch(dependencies);
        }
    }
    // Shared material gate. Consumers keep their own owner/operation lifecycle.
    // use starts/accepts synchronously; no network task is awaited under this lock.
    internal bool TryUseReviewSources(IEnumerable<MethodSource> derived,IReadOnlyList<MemoryTurn> records,
        string suppliedText,string query,BridgeConfig config,Func<bool> use)
    {
        lock(_experienceLock)
        {
            var mention=_mentionReadPolicy.Value;var native=_nativeDialoguePolicy.Value;
            _mentionReadPolicy.Value=null;_nativeDialoguePolicy.Value=null;
            try
            {
                using var mentionScope=BeginMentionRequest(query,true);
                using var nativeScope=BeginNativeDialogueRequest(query,true,config);
                if(!MethodSourceVersionsMatch(derived)||!ReviewRecordsStillPermitted(records)
                    ||!IsMentionTextAllowed(suppliedText)||SuppressedTextMatches(suppliedText))return false;
                return use();
            }
            finally{_mentionReadPolicy.Value=mention;_nativeDialoguePolicy.Value=native;}
        }
    }
    // Caller holds _experienceLock and selects the appropriate policy scope.
    private bool MethodSourceVersionsMatch(IEnumerable<MethodSource> dependencies)
    {
        BeginB1Read();B1Document? doc=null;
        foreach(var s in dependencies)
        {
            if(s.Kind=="b1" && (doc??=LoadB1())==null)return false;
            var now=ResolveMethodSource(new(s.Kind,s.Id,s.Hash,s.Purpose),doc);
            if(now!=s)return false;
        }
        return true;
    }
    internal MethodSource[]? CaptureMethodSources(DialogueRequest request,out string error)
    {
        error="source_tracking_unavailable";
        if(!request.MethodBaseTrackingAvailable || request.Frame is {DataUrl.Length:>0}
            ||request.ReviewPriorThoughtSupplied || request.ReviewDerivationUntracked || request.MethodProtocolUntracked
            ||request.MemoryOperation!=null)return null;
        lock(_experienceLock)
        {
            using var mention=BeginMentionRequest(request.ReviewCurrentText,true);
            using var native=BeginNativeDialogueRequest(request.ReviewCurrentText,true,request.B1ReadConfig);
            BeginB1Read();B1Document? doc=null;var sources=new List<MethodSource>();
            var input=B1Turn("utterance",request.UserEventId);
            if(input==null||input.Role!="user"||input.Origin!="user"||input.DeliveryState!="received"
                ||input.Text!=request.ReviewCurrentText)return null;
            bool Add(B1Ref reference,string fingerprint="",string state="")
            {
                if(reference.Kind=="b1" && (doc??=LoadB1())==null)return false;
                var s=ResolveMethodSource(reference,doc);
                if(s==null || fingerprint.Length>0 && s.Fingerprint!=fingerprint || state.Length>0 && s?.State!=state)return false;
                if(sources.Contains(s))return true;
                sources.Add(s);
                if(reference.Kind=="b1")
                {
                    var e=FindB1(doc!,reference.Version)!;
                    foreach(var r in e.Sources.Concat(e.ObjectAnchors).Concat(e.Packet.Footprint).Concat(B1DisplayFootprint(e.Packet)))
                        if(!Add(r with{Purpose="permission"}))return false;
                }
                return true;
            }
            foreach(var t in request.MethodBaseSources.Append(input).Concat(request.ReviewSourceRecords.Values))
            {
                if(t.Kind=="profile")
                {
                    if(string.IsNullOrWhiteSpace(t.Role)||!Add(new("profile_slot",t.Role,B1Hash(t.Text),"permission"),
                        SourceVersion("profile_slot",t).Fingerprint))return null;
                    continue;
                }
                if(IsMethodRecentSlot(t))
                {
                    if(!Add(new("recent_slot",MethodRecentSlotId(t),B1Hash(t.Text),"permission"),
                        SourceVersion("recent_slot",t).Fingerprint))return null;
                    continue;
                }
                string kind=B1Kind(t);
                if(!Add(new(kind,t.EventId,B1Hash(t.Text),"permission"),SourceVersion(kind,t).Fingerprint))return null;
            }
            // Permission footprint may be conservative. It is not a declaration
            // that the full bodies of those records were shown to the model.
            if(request.PendingPayloadIncludesB1 && request.B1Packet is { } packet)
            {
                foreach(var m in packet.Materials)
                    if(!Add(m.Ref,m.VersionFingerprint,m.State))return null;
                foreach(var r in packet.Footprint.Concat(B1DisplayFootprint(packet)))
                    if(!Add(r with{Purpose="permission"}))return null;
            }
            foreach(var s in request.MethodProtocolSources.Concat(request.ReviewDerivedMethodSources))
                if(!Add(new(s.Kind,s.Id,s.Hash,s.Purpose),s.Fingerprint,s.State))return null;
            error="";return sources.Distinct().ToArray();
        }
    }
}
