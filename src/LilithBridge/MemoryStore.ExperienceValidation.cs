using System;
using System.Collections.Generic;
using System.Linq;

namespace LilithBridge;

internal sealed partial class MemoryStore
{
    private ExperienceValidationView? _experienceValidationView;
    private Dictionary<MemoryTurn, string>? _retainedRawFingerprints;
    private Dictionary<string, List<CachedLogicalUnit>>? _retainedLogicalUnits;

    private sealed record CachedLogicalUnit(MemoryTurn[] OrigMembers, bool IsMessage, string? Coverage, LogicalDialogueUnit Unit);

    // The archive cache owns these records. This private view borrows them only
    // for synchronous reads under _experienceLock. Public/general readers still
    // receive detached MemoryTurn copies from ReadExperiences.
    private sealed class ExperienceValidationView
    {
        internal IReadOnlyList<MemoryTurn> Turns { get; }
        internal IReadOnlyList<MemoryTurn> NativeHistoryTurns { get; }
        internal bool HasAmbiguousEventIds { get; private set; }
        private readonly Dictionary<string, MemoryTurn?> _byId = new(StringComparer.Ordinal);
        private readonly Dictionary<MemoryTurn, string> _rawFingerprints;
        private readonly Dictionary<string, List<CachedLogicalUnit>> _retainedUnits;
        private readonly LogicalArchiveView _logical = new(), _scopedLogical = new(), _messageLogical = new(), _reviewLogical = new(), _semanticLogical = new();
        private ReflectionArchiveIndex? _reflectionArchive;
        internal ReflectionArchiveIndex ReflectionArchive => _reflectionArchive ??= IndexReflectionArchive(Turns);
        private readonly Dictionary<(string Session, string Source, string Origin, string Request), IReadOnlyList<MemoryTurn>> _byOwner;

        internal ExperienceValidationView(IEnumerable<MemoryTurn> records, Dictionary<MemoryTurn, string>? priorFingerprints = null,
            Dictionary<string, List<CachedLogicalUnit>>? priorUnits = null)
        {
            var ordered = records.OrderBy(t => t.Timestamp).ThenBy(t => t.SessionId, StringComparer.Ordinal)
                .ThenBy(t => t.Sequence).ToArray();
            Turns = Array.AsReadOnly(ordered);
            _retainedUnits = priorUnits ?? new(StringComparer.Ordinal);
            _rawFingerprints = new Dictionary<MemoryTurn, string>(ordered.Length, ReferenceEqualityComparer.Instance);
            if (priorFingerprints != null)
            {
                foreach (var turn in ordered)
                    if (priorFingerprints.TryGetValue(turn, out var fp))
                        _rawFingerprints[turn] = fp;
            }
            var owners = new Dictionary<(string, string, string, string), List<MemoryTurn>>();
            var native = new List<MemoryTurn>();
            foreach (var turn in ordered)
            {
                // In-memory appends can repeat an id. Keep ambiguity visible,
                // rather than silently selecting one source as authoritative.
                if (_byId.TryAdd(turn.EventId, turn))
                {
                    if (IsNativeHistoryRecord(turn)) native.Add(turn);
                }
                else { _byId[turn.EventId] = null; HasAmbiguousEventIds = true; }
                if (!HistoryBoundary.TryReplyBubble(turn, out string request, out _)) continue;
                var key = (turn.SessionId, turn.SourceEventId, turn.Origin, request);
                if (!owners.TryGetValue(key, out var group)) owners[key] = group = new();
                group.Add(turn);
            }
            _byOwner = owners.ToDictionary(p => p.Key, p => (IReadOnlyList<MemoryTurn>)p.Value.AsReadOnly());
            NativeHistoryTurns = native.AsReadOnly();
        }

        internal Dictionary<MemoryTurn, string> RawFingerprints => _rawFingerprints;
        internal Dictionary<string, List<CachedLogicalUnit>> RetainedUnits => _retainedUnits;
        internal string GetRawFingerprint(MemoryTurn turn)
        {
            if (_rawFingerprints.TryGetValue(turn, out var fp)) return fp;
            fp = LogicalDialogueCatalog.RawFingerprint(turn);
            _rawFingerprints[turn] = fp;
            return fp;
        }
        private LogicalDialogueUnit GetOrCreateLogicalUnit(IReadOnlyList<MemoryTurn> members, bool isMessage,
            string? coverage, Func<MemoryTurn, string> fingerprint)
        {
            string anchorId = members[0].EventId;
            lock (_retainedUnits)
            {
                if (_retainedUnits.TryGetValue(anchorId, out var list))
                {
                    foreach (var entry in list)
                    {
                        if (entry.IsMessage == isMessage && entry.Coverage == coverage && entry.OrigMembers.Length == members.Count)
                        {
                            bool same = true;
                            for (int i = 0; i < members.Count; i++)
                                if (!ReferenceEquals(entry.OrigMembers[i], members[i])) { same = false; break; }
                            if (same) return entry.Unit;
                        }
                    }
                }
            }
            var created = LogicalDialogueUnit.Create(members, isMessage, coverage, fingerprint);
            var refs = members is MemoryTurn[] arr ? (MemoryTurn[])arr.Clone() : members.ToArray();
            lock (_retainedUnits)
            {
                if (!_retainedUnits.TryGetValue(anchorId, out var list))
                    _retainedUnits[anchorId] = list = new(1);
                list.Add(new(refs, isMessage, coverage, created));
            }
            return created;
        }

        internal bool TryGetUnique(string id, out MemoryTurn turn)
        {
            if (_byId.TryGetValue(id, out var found) && found != null) { turn = found; return true; }
            turn = null!;
            return false;
        }

        internal bool ContainsId(string id) => _byId.ContainsKey(id);
        internal bool HasVisibleAmbiguity(HashSet<string> denied) => HasAmbiguousEventIds
            && _byId.Any(p => p.Value == null && !denied.Contains(p.Key));

        // Catalogs own detached records and immutable logical metadata. Current
        // visibility is evaluated on every read; only an unchanged projection
        // is reused, never an earlier permission decision.
        internal LogicalDialogueCatalog LogicalCatalog(Func<MemoryTurn, bool> visible, bool scoped, bool dialogueOnly)
            => (dialogueOnly ? _messageLogical : scoped ? _scopedLogical : _logical).Read(Turns, visible, GetRawFingerprint, GetOrCreateLogicalUnit);
        internal LogicalDialogueCatalog SemanticCatalog(Func<MemoryTurn, bool> visible)
            => _semanticLogical.Read(Turns, visible, GetRawFingerprint, GetOrCreateLogicalUnit);
        internal LogicalDialogueCatalog ReviewCatalog(Func<MemoryTurn, bool> visible)
            => _reviewLogical.Read(Turns, visible, GetRawFingerprint, GetOrCreateLogicalUnit);

        private sealed class LogicalArchiveView
        {
            private bool[]? _visible;
            private LogicalDialogueCatalog? _catalog;
            internal LogicalDialogueCatalog Read(IReadOnlyList<MemoryTurn> turns, Func<MemoryTurn, bool> visible,
                Func<MemoryTurn, string>? knownFingerprint = null,
                Func<IReadOnlyList<MemoryTurn>, bool, string?, Func<MemoryTurn, string>, LogicalDialogueUnit>? getOrCreateUnit = null)
            {
                var mask = new bool[turns.Count];
                bool changed = _visible == null || _visible.Length != mask.Length;
                for (int i = 0; i < mask.Length; i++)
                {
                    mask[i] = visible(turns[i]);
                    if (!changed && mask[i] != _visible![i]) changed = true;
                }
                if (changed || _catalog == null)
                {
                    var catalog = LogicalDialogueCatalog.Build(turns.Where((_, i) => mask[i]), knownFingerprint, getOrCreateUnit);
                    _catalog = catalog; _visible = mask;
                }
                return _catalog;
            }
        }

        internal IReadOnlyList<MemoryTurn> GetOwnerReplyEvents(string sessionId, string sourceUserId, string origin, string requestId)
            => _byOwner.TryGetValue((sessionId, sourceUserId, origin, requestId), out var found)
                ? found : Array.Empty<MemoryTurn>();
    }

    private void InvalidateExperienceValidationView(bool preserveFingerprints = false)
    {
        _retainedRawFingerprints = preserveFingerprints
            ? (_experienceValidationView?.RawFingerprints ?? _retainedRawFingerprints)
            : null;
        _retainedLogicalUnits = preserveFingerprints
            ? (_experienceValidationView?.RetainedUnits ?? _retainedLogicalUnits)
            : null;
        _experienceValidationView = null;
        InvalidateSourceValidation();
    }

    private ExperienceValidationView GetExperienceValidationView()
    {
        lock (_experienceLock)
        {
            // Preserve disabled/missing-directory behavior, including disappearance
            // after a prior successful load. No permission survives this check.
            if (!_experienceArchiveEnabled || !System.IO.Directory.Exists(ExperienceDirectory))
                return new ExperienceValidationView(Array.Empty<MemoryTurn>());
            if (_experienceCache == null) LoadExperienceCache();
            if (_experienceValidationView == null)
            {
                _experienceValidationView = new ExperienceValidationView(_experienceCache!, _retainedRawFingerprints, _retainedLogicalUnits);
                _retainedRawFingerprints = _experienceValidationView.RawFingerprints;
                _retainedLogicalUnits = _experienceValidationView.RetainedUnits;
            }
            return _experienceValidationView;
        }
    }

    private IEnumerable<MemoryTurn> GetExperienceValidationTurns(bool includeSuppressed = false)
    {
        var view = GetExperienceValidationView();
        if (includeSuppressed) return view.Turns;
        var denied = SuppressedExperienceIds();
        return view.Turns.Where(t => !denied.Contains(t.EventId));
    }

    private LogicalDialogueCatalog GetExperienceLogicalCatalog(bool scoped = true, bool dialogueOnly = false)
    {
        lock (_experienceLock)
        {
            var archive = GetExperienceValidationView();
            var denied = SuppressedExperienceIds();
            return archive.LogicalCatalog(t => !denied.Contains(t.EventId)
                && (!dialogueOnly || t.Role is "user" or "assistant" && t.DeliveryState is "received" or "delivered")
                && (!scoped || IsMentionAllowed(t) && IsNativeSupplyAllowed(t)), scoped, dialogueOnly);
        }
    }

    private LogicalDialogueCatalog GetReviewLogicalCatalog()
    {
        lock (_experienceLock)
        {
            var archive = GetExperienceValidationView();
            var denied = SuppressedExperienceIds();
            // Review has its own exact projection: current-session dialogue,
            // without imposing the message reader's delivery-state filter.
            // Re-evaluate all live visibility on every read, including a session
            // change; only detached structure and pure text tokens are reused.
            return archive.ReviewCatalog(t => !denied.Contains(t.EventId)
                && t.SessionId == _sessionId && t.Role is "user" or "assistant"
                && t.EventId.Length > 0 && IsMentionAllowed(t) && IsNativeSupplyAllowed(t));
        }
    }
}
