namespace LilithBridge;

internal sealed partial class MemoryStore
{
    // Synchronous read operation only. Never survives a Tick, await, or use
    // callback. Each top-level operation re-reads generated bodies/metadata;
    // exact unchanged file bytes may reuse their immutable parsed projection.
    [ThreadStatic] private static SourceValidationState? _sourceValidation;

    private sealed class SourceValidationState
    {
        internal readonly MemoryStore Owner;
        internal readonly Dictionary<string, GeneratedReplySnapshot?> Loaded = new(StringComparer.Ordinal);
        internal readonly Dictionary<GeneratedQualificationKey, GeneratedQualificationMemo> Qualified = new();
        internal readonly HashSet<MethodPermissionKey> Methods = new();
        internal readonly List<GeneratedQualificationFrame> Frames = new();
        internal long Revision;
        internal SourceValidationState(MemoryStore owner) => Owner = owner;
        internal void Clear() { Loaded.Clear(); Qualified.Clear(); Methods.Clear(); Revision++; }
    }
    private sealed record GeneratedQualificationKey(string Key, string Version, string Body,
        object? MentionPolicy, object? NativePolicy, BridgeConfig? Config, string DataRoot);
    private sealed record GeneratedQualificationMemo(GeneratedReplyQualification Result,
        DateTimeOffset? Deadline, int PublicDepth);
    private sealed record MethodPermissionKey(MethodSource Source, string Query, BridgeConfig? Config,
        object? MentionPolicy, object? NativePolicy);
    private sealed class GeneratedQualificationFrame
    {
        internal readonly int BasePublicDepth;
        internal DateTimeOffset? Deadline;
        internal int PublicDepth;
        internal GeneratedQualificationFrame(int depth) => BasePublicDepth = depth;
    }

    private IDisposable BeginSourceValidation()
    {
        var previous = _sourceValidation;
        if (previous?.Owner == this) return new MentionScope(() => { });
        _sourceValidation = new(this);
        return new MentionScope(() => _sourceValidation = previous);
    }

    // Also called by history invalidation. A reentrant write must not leave a
    // result from before that write eligible for another use in this operation.
    private void InvalidateSourceValidation()
    {
        if (_sourceValidation?.Owner == this) _sourceValidation.Clear();
    }
    private SourceValidationState? CurrentSourceValidation => _sourceValidation?.Owner == this ? _sourceValidation : null;

    private bool UseAfterSourceValidation(Func<bool> use)
    {
        var validation=_sourceValidation;
        if(validation==null)return use();
        validation.Clear();
        _sourceValidation=null;
        try{return use();}
        finally
        {
            // A nested consumer's reads never populate its caller's memo.
            validation.Clear();
            _sourceValidation=validation;
        }
    }

    private void ObserveSourceDeadline(DateTimeOffset? deadline)
    {
        if (!deadline.HasValue || CurrentSourceValidation is not { } state) return;
        foreach (var frame in state.Frames)
            if (!frame.Deadline.HasValue || deadline.Value < frame.Deadline.Value) frame.Deadline = deadline;
    }
    private void ObserveSourceDepth(int absoluteDepth)
    {
        if (CurrentSourceValidation is not { } state) return;
        foreach (var frame in state.Frames)
            frame.PublicDepth = Math.Max(frame.PublicDepth, absoluteDepth - frame.BasePublicDepth);
    }

    private GeneratedReplySnapshot? LoadGeneratedForValidation(string key)
    {
        var state = CurrentSourceValidation;
        if (state != null && state.Loaded.TryGetValue(key, out var existing)) return existing;
        var loaded = LoadGeneratedCore(key);
        if (state != null) state.Loaded[key] = loaded;
        return loaded;
    }

    private GeneratedReplyQualification QualifyGeneratedReplyInScope(GeneratedReplySnapshot snapshot)
    {
        var state = CurrentSourceValidation!;
        var key = new GeneratedQualificationKey(snapshot.Key, snapshot.VersionFingerprint, snapshot.Body,
            _mentionReadPolicy.Value, _nativeDialoguePolicy.Value, ConfigStore.PublishedForInitialExperience, DataPaths.Root());
        // A live native authorization can have its own dynamic lifetime. Source
        // reads can be shared, but that permission is still evaluated per use.
        bool canMemo = _nativeDialoguePolicy.Value?.Authorization == null;
        if (canMemo && state.Qualified.TryGetValue(key, out var memo)
            && (!memo.Deadline.HasValue || DateTimeOffset.UtcNow < memo.Deadline.Value)
            && _publicExpressionReadStack.Count + memo.PublicDepth <= 16
            && !_publicExpressionReadStack.Contains(snapshot.Key))
        {
            ObserveSourceDeadline(memo.Deadline);
            ObserveSourceDepth(_publicExpressionReadStack.Count + memo.PublicDepth);
            return memo.Result;
        }
        long revision = state.Revision;
        var frame = new GeneratedQualificationFrame(_publicExpressionReadStack.Count);
        state.Frames.Add(frame);
        try
        {
            var result = QualifyGeneratedReplyCore(snapshot);
            // Denials can depend on the current recursion path/depth. Only a
            // completed successful closure is reusable elsewhere in this DAG.
            if (canMemo && result.Permitted && revision == state.Revision)
                state.Qualified[key] = new(result, frame.Deadline, frame.PublicDepth);
            return result;
        }
        finally { state.Frames.RemoveAt(state.Frames.Count - 1); }
    }

    private bool GeneratedRepliesPermitted(IEnumerable<GeneratedReplySnapshot> sources, string query, BridgeConfig config)
    {
        if (!GeneratedConfigAllows(config)) return false;
        lock (_experienceLock)
        lock (_generatedRepliesLock)
        {
            using var mention = BeginMentionRequest(query);
            using var validation = BeginSourceValidation();
            return sources.All(source => QualifyGeneratedReply(source).Permitted);
        }
    }
}
