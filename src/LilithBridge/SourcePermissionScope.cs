using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace LilithBridge;

/// <summary>
/// Monotonic host clock for native display waits; decoupled from Unity's per-frame frozen unscaledTime.
/// </summary>
internal static class NativeAppearanceClock
{
    internal static double MonotonicSeconds() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
}

internal readonly struct SelectionSnapshotKey : IEquatable<SelectionSnapshotKey>
{
    internal readonly MemoryStore Store;
    internal readonly long Generation;
    internal readonly string Stamp;
    internal readonly BridgeConfig Config;

    internal SelectionSnapshotKey(MemoryStore store, long generation, string stamp, BridgeConfig config)
    {
        Store = store;
        Generation = generation;
        Stamp = stamp;
        Config = config;
    }

    public bool Equals(SelectionSnapshotKey other) => ReferenceEquals(Store, other.Store)
        && Generation == other.Generation
        && string.Equals(Stamp, other.Stamp, StringComparison.Ordinal)
        && ReferenceEquals(Config, other.Config);

    public override bool Equals(object? obj) => obj is SelectionSnapshotKey other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(
        RuntimeHelpers.GetHashCode(Store),
        Generation,
        Stamp,
        RuntimeHelpers.GetHashCode(Config));
}

/// <summary>
/// Synchronous-only source-permission validation scope (never survives an await, tick, write, or callback).
/// Eliminates exponential DAG re-traversal and repeated B1 index construction within one synchronous gate.
/// </summary>
internal static class SourcePermissionScope
{
    private static long _mutationGeneration = 1;
    [ThreadStatic] private static ScopeState? _current;

    internal static ScopeState? Current
    {
        get
        {
            var scope = _current;
            if (scope == null) return null;
            long live = Volatile.Read(ref _mutationGeneration);
            if (scope.MutationGeneration != live) scope.Reset(live);
            return scope;
        }
    }

    internal static long MutationGeneration => Volatile.Read(ref _mutationGeneration);

    internal static void Invalidate()
    {
        long next = Interlocked.Increment(ref _mutationGeneration);
        _current?.Reset(next);
    }

    internal static Guard Enter()
    {
        long live = Volatile.Read(ref _mutationGeneration);
        if (_current is { } existing)
        {
            if (existing.MutationGeneration != live) existing.Reset(live);
            return default;
        }
        var created = new ScopeState(live);
        _current = created;
        return new Guard(created);
    }

    internal readonly struct Guard : IDisposable
    {
        private readonly ScopeState? _owned;
        internal Guard(ScopeState? owned) => _owned = owned;
        public void Dispose()
        {
            if (_owned != null && ReferenceEquals(_current, _owned))
            {
                _current = null;
                _owned.Reset(0);
            }
        }
    }

    private sealed class ApiResultKeyComparer : IEqualityComparer<(ApiResult Result, bool IncludeChatHistory)>
    {
        internal static readonly ApiResultKeyComparer Instance = new();
        public bool Equals((ApiResult Result, bool IncludeChatHistory) x, (ApiResult Result, bool IncludeChatHistory) y)
            => ReferenceEquals(x.Result, y.Result) && x.IncludeChatHistory == y.IncludeChatHistory;
        public int GetHashCode((ApiResult Result, bool IncludeChatHistory) obj)
            => HashCode.Combine(RuntimeHelpers.GetHashCode(obj.Result), obj.IncludeChatHistory);
    }

    private sealed class LeafIdentityComparer : IEqualityComparer<object>
    {
        internal static readonly LeafIdentityComparer Instance = new();
        public new bool Equals(object? x, object? y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x == null || y == null) return false;
            if (x is SelectionSnapshotKey sx && y is SelectionSnapshotKey sy) return sx.Equals(sy);
            if (x is TaggedLeafKey tx && y is TaggedLeafKey ty) return tx.Equals(ty);
            return false;
        }

        public int GetHashCode(object obj) => obj switch
        {
            SelectionSnapshotKey sk => sk.GetHashCode(),
            TaggedLeafKey tk => tk.GetHashCode(),
            _ => RuntimeHelpers.GetHashCode(obj)
        };
    }

    private sealed class StoreConfigKeyComparer : IEqualityComparer<(MemoryStore Store, BridgeConfig Config)>
    {
        internal static readonly StoreConfigKeyComparer Instance = new();
        public bool Equals((MemoryStore Store, BridgeConfig Config) x, (MemoryStore Store, BridgeConfig Config) y)
            => ReferenceEquals(x.Store, y.Store) && ReferenceEquals(x.Config, y.Config);
        public int GetHashCode((MemoryStore Store, BridgeConfig Config) obj)
            => HashCode.Combine(RuntimeHelpers.GetHashCode(obj.Store), RuntimeHelpers.GetHashCode(obj.Config));
    }

    internal static IEqualityComparer<object> LeafKeyComparer => LeafIdentityComparer.Instance;

    internal sealed class ScopeState
    {
        internal long MutationGeneration { get; private set; }
        internal readonly Dictionary<(ApiResult Result, bool IncludeChatHistory), bool> RequestPermissions
            = new(ApiResultKeyComparer.Instance);
        internal readonly Dictionary<(ApiResult Result, bool IncludeChatHistory), bool> SelfPermissions
            = new(ApiResultKeyComparer.Instance);
        internal readonly Dictionary<ApiResult, string> Drafts = new(ReferenceEqualityComparer.Instance);
        internal readonly Dictionary<ApiResult, string> SelfDrafts = new(ReferenceEqualityComparer.Instance);
        internal readonly Dictionary<SelectionSnapshotKey, bool> SelectionCurrent = new();
        internal readonly Dictionary<object, bool> LeafChecks = new(LeafIdentityComparer.Instance);
        internal readonly Dictionary<MemoryStore, object> BorrowedB1Bases = new(ReferenceEqualityComparer.Instance);
        internal readonly Dictionary<(MemoryStore Store, BridgeConfig Config), object> NativeDialogueUpperBounds
            = new(StoreConfigKeyComparer.Instance);

        internal ScopeState(long mutationGeneration) => MutationGeneration = mutationGeneration;

        internal void Reset(long nextGeneration)
        {
            RequestPermissions.Clear();
            SelfPermissions.Clear();
            Drafts.Clear();
            SelfDrafts.Clear();
            SelectionCurrent.Clear();
            LeafChecks.Clear();
            BorrowedB1Bases.Clear();
            NativeDialogueUpperBounds.Clear();
            MutationGeneration = nextGeneration;
        }
    }
}

internal readonly struct TaggedLeafKey : IEquatable<TaggedLeafKey>
{
    internal readonly object Owner;
    internal readonly string Tag;

    internal TaggedLeafKey(object owner, string tag)
    {
        Owner = owner;
        Tag = tag;
    }

    public bool Equals(TaggedLeafKey other)
        => ReferenceEquals(Owner, other.Owner) && string.Equals(Tag, other.Tag, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is TaggedLeafKey other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(RuntimeHelpers.GetHashCode(Owner), Tag);
}

internal sealed class CarryLeafCheck
{
    internal object IdentityKey { get; }
    private readonly Func<bool> _evaluate;

    internal CarryLeafCheck(object identityKey, Func<bool> evaluate)
    {
        IdentityKey = identityKey;
        _evaluate = evaluate;
    }

    internal bool Evaluate()
    {
        var scope = SourcePermissionScope.Current;
        if (scope != null && scope.LeafChecks.TryGetValue(IdentityKey, out bool cached))
            return cached;
        bool allowed;
        try { allowed = _evaluate(); }
        catch { allowed = false; }
        scope?.LeafChecks.TryAdd(IdentityKey, allowed);
        return allowed;
    }
}

internal sealed class CarryPermissionChain
{
    internal sealed class DirectEntry
    {
        internal DialogueWorkSession Work { get; }
        internal ApiResult? Final { get; }
        internal string ExpectedText { get; }
        internal string ExpectedDraft { get; }

        internal DirectEntry(DialogueWorkSession work, ApiResult? final, string expectedText, string expectedDraft)
        {
            Work = work;
            Final = final;
            ExpectedText = expectedText;
            ExpectedDraft = expectedDraft;
        }
    }

    private readonly DirectEntry[] _directEntries;
    private readonly IReadOnlyList<string>? _archivedSegments;
    private readonly CarryLeafCheck[] _ancestorLeaves;
    private readonly CarryLeafCheck? _recoveredLeaf;

    internal DirectEntry[] DirectEntries => _directEntries;
    internal CarryLeafCheck[] AncestorLeaves => _ancestorLeaves;
    internal CarryLeafCheck? RecoveredLeaf => _recoveredLeaf;
    internal Func<bool> PermissionDelegate { get; }

    internal CarryPermissionChain(
        DirectEntry[] directEntries,
        IReadOnlyList<string>? archivedSegments,
        CarryLeafCheck[] ancestorLeaves,
        CarryLeafCheck? recoveredLeaf)
    {
        _directEntries = directEntries;
        _archivedSegments = archivedSegments;
        _ancestorLeaves = ancestorLeaves;
        _recoveredLeaf = recoveredLeaf;
        PermissionDelegate = EvaluateAll;
    }

    internal bool EvaluateCarryOnly()
    {
        using var scope = SourcePermissionScope.Enter();
        foreach (var leaf in _ancestorLeaves)
        {
            if (!leaf.Evaluate()) return false;
        }
        foreach (var entry in _directEntries)
        {
            if (entry.Work.EvictedSourceLeaves is { } evictedLeaves)
            {
                foreach (var wl in evictedLeaves)
                    if (!wl.Evaluate()) return false;
            }
            else if (entry.ExpectedText.Length > 0
                && TemporaryExpressionSession.WorkText(entry.Work, entry.Final, _archivedSegments) != entry.ExpectedText)
            {
                return false;
            }
            if (TemporaryExpressionSession.DraftSelf(entry.Final) != entry.ExpectedDraft)
                return false;
        }
        return true;
    }

    private bool EvaluateAll()
    {
        using var scope = SourcePermissionScope.Enter();
        return EvaluateCarryOnly() && (_recoveredLeaf == null || _recoveredLeaf.Evaluate());
    }
}
