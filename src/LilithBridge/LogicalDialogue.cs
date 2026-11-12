using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LilithBridge;

internal sealed record LogicalDialogueSourceSpan(string EventId, string TextHash,
    int OffsetInUnit, int SourceOffset, int Chars);
internal sealed record LogicalDialogueGap(int OffsetInUnit, int Chars, string Reason,
    string BeforeEventId, string AfterEventId);

internal sealed class LogicalDialogueRun
{
    private readonly MemoryTurn[] _members;
    internal LogicalDialogueRun(IReadOnlyList<MemoryTurn> members, string text, int offsetInUnit,
        int? firstChunkIndex, int? lastChunkIndex, long firstSequence, long lastSequence)
        : this(members, text, offsetInUnit, firstChunkIndex, lastChunkIndex, firstSequence, lastSequence, false) { }
    private LogicalDialogueRun(IReadOnlyList<MemoryTurn> members, string text, int offsetInUnit,
        int? firstChunkIndex, int? lastChunkIndex, long firstSequence, long lastSequence, bool ownedSnapshots)
    {
        _members = ownedSnapshots ? members.ToArray() : members.Select(t => t.Copy()).ToArray();
        Text = text; OffsetInUnit = offsetInUnit;
        FirstChunkIndex = firstChunkIndex; LastChunkIndex = lastChunkIndex;
        FirstSequence = firstSequence; LastSequence = lastSequence;
    }
    // Only the builder/unit's private detached records may enter this path.
    // Neither the run nor its unit mutates them; Members still returns copies.
    internal static LogicalDialogueRun FromOwnedSnapshots(IReadOnlyList<MemoryTurn> members, string text, int offsetInUnit,
        int? firstChunkIndex, int? lastChunkIndex, long firstSequence, long lastSequence)
        => new(members, text, offsetInUnit, firstChunkIndex, lastChunkIndex, firstSequence, lastSequence, true);
    public IReadOnlyList<MemoryTurn> Members => _members.Select(t => t.Copy()).ToArray();
    public string Text { get; }
    public int OffsetInUnit { get; }
    public int? FirstChunkIndex { get; }
    public int? LastChunkIndex { get; }
    public long FirstSequence { get; }
    public long LastSequence { get; }
}

// An available request owner, not a certified whole generation or HTTP attempt.
// Gapped owners remain one retrieval choice but never one unbroken quotation.
internal sealed class LogicalDialogueUnit
{
    private readonly MemoryTurn[] _members;
    internal LogicalDialogueUnit(IReadOnlyList<MemoryTurn> members, bool isMessage, string? overrideCoverage = null)
        : this(members, isMessage, overrideCoverage, false, LogicalDialogueCatalog.RawFingerprint) { }
    private LogicalDialogueUnit(IReadOnlyList<MemoryTurn> members, bool isMessage, string? overrideCoverage,
        bool ownedSnapshots, Func<MemoryTurn, string> fingerprint)
    {
        _members = ownedSnapshots ? members.ToArray() : members.Select(t => t.Copy()).ToArray();
        if (_members.Length == 0) throw new ArgumentException("A logical unit requires source records");
        IsMessage = isMessage;
        var first = _members[0]; var last = _members[^1];
        AnchorEventId = first.EventId; Role = first.Role; Origin = first.Origin;
        SessionId = first.SessionId; SourceEventId = first.SourceEventId; Timestamp = first.Timestamp;
        if (isMessage && HistoryBoundary.TryReplyBubble(first, out string request, out int chunk))
        {
            RequestId = request; FirstChunkIndex = chunk;
            HistoryBoundary.TryReplyBubble(last, out _, out int lastChunk); LastChunkIndex = lastChunk;
        }
        else RequestId = "";
        Key = isMessage ? "message/" + MemoryStore.SemanticTextHash(JsonSerializer.Serialize(new[] {
            SessionId, RequestId, Origin, SourceEventId })) : "utterance/" + first.EventId;
        FirstSequence = first.Sequence; LastSequence = last.Sequence;
        var runs = new List<LogicalDialogueRun>(); var spans = new List<LogicalDialogueSourceSpan>();
        var gaps = new List<LogicalDialogueGap>(); var text = new StringBuilder();
        int start = 0;
        for (int i = 1; i <= _members.Length; i++)
        {
            if (i < _members.Length && isMessage && HistoryBoundary.SameReplyRun(_members[i - 1], _members[i])) continue;
            if (runs.Count > 0)
            {
                var before = _members[start - 1]; var after = _members[start];
                string reason = "non_contiguous_source_records";
                string marker = "\n[logical_source_gap: non-contiguous recorded fragments; no missing text supplied]\n";
                gaps.Add(new(text.Length, marker.Length, reason, before.EventId, after.EventId)); text.Append(marker);
            }
            int runStart = text.Length; var rows = _members.Skip(start).Take(i - start).ToArray();
            foreach (var row in rows)
            {
                spans.Add(new(row.EventId, MemoryStore.SemanticTextHash(row.Text), text.Length, 0, row.Text.Length));
                text.Append(row.Text);
            }
            int? firstIndex = HistoryBoundary.TryReplyBubble(rows[0], out _, out int a) ? a : null;
            int? lastIndex = HistoryBoundary.TryReplyBubble(rows[^1], out _, out int b) ? b : null;
            runs.Add(LogicalDialogueRun.FromOwnedSnapshots(rows, string.Concat(rows.Select(t => t.Text)), runStart,
                firstIndex, lastIndex, rows[0].Sequence, rows[^1].Sequence));
            start = i;
        }
        Text = text.ToString(); TextHash = MemoryStore.SemanticTextHash(Text);
        Runs = runs.AsReadOnly(); SourceSpans = spans.AsReadOnly(); Gaps = gaps.AsReadOnly();
        HasGaps = gaps.Count > 0; HeadKnown = isMessage ? FirstChunkIndex == 0 : first.Role == "user";
        TailKnown = !isMessage && first.Role == "user";
        Coverage = overrideCoverage ?? (isMessage
            ? (HeadKnown ? "head_known" : "head_missing") + (HasGaps ? ";gapped_available_runs" : ";continuous_available_run")
                + ";generation_tail_unknown;request_owner_not_http_attempt"
            : first.Role == "user" ? "single_user_record" : "single_record_extent_unknown");
        VersionFingerprint = LogicalDialogueCatalog.JsonFingerprint(new {
            key = Key, coverage = Coverage, members = _members.Select(fingerprint).ToArray()
        });
    }
    // Build has already detached these records once. Sharing private snapshots
    // inside this immutable catalog never grants access to store-owned turns.
    internal static LogicalDialogueUnit FromOwnedSnapshots(IReadOnlyList<MemoryTurn> members, bool isMessage,
        string? overrideCoverage, Func<MemoryTurn, string> fingerprint)
        => new(members, isMessage, overrideCoverage, true, fingerprint);
    public string Key { get; }
    public string VersionFingerprint { get; }
    public string Text { get; }
    public string TextHash { get; }
    public string Role { get; }
    public string Origin { get; }
    public string SessionId { get; }
    public string RequestId { get; }
    public string SourceEventId { get; }
    public bool IsMessage { get; }
    public bool HasGaps { get; }
    public bool HeadKnown { get; }
    public bool TailKnown { get; }
    public string Coverage { get; }
    public int? FirstChunkIndex { get; }
    public int? LastChunkIndex { get; }
    public long FirstSequence { get; }
    public long LastSequence { get; }
    public string AnchorEventId { get; }
    public DateTimeOffset Timestamp { get; }
    public IReadOnlyList<MemoryTurn> Members => _members.Select(t => t.Copy()).ToArray();
    public IReadOnlyList<LogicalDialogueRun> Runs { get; }
    public IReadOnlyList<LogicalDialogueSourceSpan> SourceSpans { get; }
    public IReadOnlyList<LogicalDialogueGap> Gaps { get; }
    public bool ContainsExact(string text) => Runs.Any(r => r.Text.Contains(text, StringComparison.Ordinal));
    private HashSet<string>? _matchingTokens;
    // Pure text computation; never a relevance or source permission decision.
    internal IReadOnlySet<string> MatchingTokens => _matchingTokens ??= MemorySemantics.MatchingTokens(Text);
}

internal sealed class LogicalDialogueCatalog
{
    private readonly Dictionary<string, LogicalDialogueUnit> _events;
    private readonly Dictionary<string, LogicalDialogueUnit> _keys;
    private LogicalDialogueCatalog(IReadOnlyList<LogicalDialogueUnit> units, IReadOnlyList<string> omissions)
    {
        Units = Array.AsReadOnly(units.ToArray()); Omissions = Array.AsReadOnly(omissions.ToArray());
        _keys = Units.ToDictionary(u => u.Key, StringComparer.Ordinal);
        _events = new(StringComparer.Ordinal);
        foreach (var unit in Units) foreach (var span in unit.SourceSpans) _events.Add(span.EventId, unit);
    }
    public IReadOnlyList<LogicalDialogueUnit> Units { get; }
    public IReadOnlyList<string> Omissions { get; }
    public LogicalDialogueUnit? FindByEvent(string eventId) => _events.GetValueOrDefault(eventId);
    public LogicalDialogueUnit? FindByKey(string key) => _keys.GetValueOrDefault(key);
    internal static string JsonFingerprint<T>(T value) => Convert.ToHexString(
        SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value))).ToLowerInvariant();
    internal static string RawFingerprint(MemoryTurn turn) => JsonFingerprint(turn);
    private static bool Copyable(MemoryTurn? t) => t != null && !string.IsNullOrEmpty(t.EventId)
        && t.Text != null && t.Role != null && t.Origin != null && t.Kind != null && t.SessionId != null
        && t.SourceEventId != null && t.DeliveryState != null && t.SourceEventIds != null
        && t.SourceNoteIds != null && t.SourceDayIds != null && t.SourceDependencies != null && t.DayCoveredEventIds != null
        && t.ContextDependencies != null;
    public static LogicalDialogueCatalog Build(IEnumerable<MemoryTurn> raw)
    {
        var omissions = new List<string>(); var source = new List<MemoryTurn>();
        foreach (var row in raw)
            if (Copyable(row)) source.Add(row.Copy()); else omissions.Add("invalid_source_record");
        var accepted = new List<MemoryTurn>();
        Dictionary<MemoryTurn, string>? duplicateFingerprints = null;
        string Fingerprint(MemoryTurn turn) => duplicateFingerprints != null && duplicateFingerprints.TryGetValue(turn, out var known)
            ? known : RawFingerprint(turn);
        foreach (var group in source.GroupBy(t => t.EventId, StringComparer.Ordinal))
        {
            var first = group.First();
            // A singleton cannot have conflicting copies. Its one fingerprint
            // is computed by the unit; duplicate groups keep the exact check.
            if (group.Skip(1).Any())
            {
                var versions = group.Select(RawFingerprint).Distinct(StringComparer.Ordinal).Take(2).ToArray();
                if (versions.Length > 1) { omissions.Add("conflicting_source_identity:" + group.Key); continue; }
                (duplicateFingerprints ??= new())[first] = versions[0];
            }
            accepted.Add(first);
        }
        var positions = accepted.Select((t, i) => (t.EventId, i)).ToDictionary(x => x.EventId, x => x.i, StringComparer.Ordinal);
        var ambiguousSequence = accepted.Where(t => t.SessionId.Length > 0 && t.Sequence > 0)
            .GroupBy(t => (t.SessionId, t.Sequence)).Where(g => g.Count() > 1)
            .SelectMany(g => g.Select(t => t.EventId)).ToHashSet(StringComparer.Ordinal);
        var units = new List<LogicalDialogueUnit>(); var strict = new List<(MemoryTurn Turn, string Request)>();
        foreach (var row in accepted)
        {
            if (!ambiguousSequence.Contains(row.EventId) && HistoryBoundary.TryReplyBubble(row, out string request, out _))
                strict.Add((row, request));
            else units.Add(LogicalDialogueUnit.FromOwnedSnapshots(new[] { row }, false,
                ambiguousSequence.Contains(row.EventId) ? "ambiguous_sequence;independent_record_only" : null, Fingerprint));
        }
        foreach (var group in strict.GroupBy(x => (x.Turn.SessionId, x.Request, x.Turn.Origin, x.Turn.SourceEventId)))
        {
            var members = group.Select(x => x.Turn).OrderBy(t => t.Sequence).ToArray();
            int previous = -1; bool ambiguous = false;
            foreach (var row in members)
            {
                HistoryBoundary.TryReplyBubble(row, out _, out int index);
                if (index <= previous) ambiguous = true; previous = index;
            }
            if (ambiguous)
                foreach (var row in members) units.Add(LogicalDialogueUnit.FromOwnedSnapshots(new[] { row }, false,
                    "ambiguous_chunk_order;independent_record_only", Fingerprint));
            else units.Add(LogicalDialogueUnit.FromOwnedSnapshots(members, true, null, Fingerprint));
        }
        return new(units.OrderBy(u => u.SourceSpans.Min(s => positions[s.EventId])).ToArray(), omissions.AsReadOnly());
    }
    public LogicalDialogueUnit? ResolveCandidate(SemanticRecallCandidate candidate)
    {
        if (candidate.Kind == "message")
        {
            var unit = FindByKey(candidate.EventId);
            return unit != null && MemoryStore.SemanticMessageCandidateMatches(candidate, unit) ? unit : null;
        }
        if (candidate.Kind != "utterance") return null;
        var rawUnit = FindByEvent(candidate.EventId);
        var row = rawUnit?.Members.FirstOrDefault(t => t.EventId == candidate.EventId);
        return row != null && MemoryStore.SemanticCandidateMatches(candidate, row) ? rawUnit : null;
    }
}

internal sealed class LogicalDialogueSelection : IReadOnlyList<MemoryTurn>
{
    private readonly MemoryTurn[] _records;
    public LogicalDialogueSelection(IEnumerable<LogicalDialogueUnit> units, IEnumerable<string>? directHitEventIds = null,
        Func<MemoryTurn, MemoryTurn>? materialize = null)
    {
        var chosen = units.ToArray();
        foreach (var group in chosen.GroupBy(u => u.Key, StringComparer.Ordinal))
            if (group.Select(u => u.VersionFingerprint).Distinct(StringComparer.Ordinal).Skip(1).Any())
                throw new InvalidOperationException("Conflicting logical dialogue snapshots");
        Units = Array.AsReadOnly(chosen.DistinctBy(u => u.Key).ToArray());
        _records = Units.SelectMany(u => u.Members).DistinctBy(t => t.EventId)
            .Select(t => materialize?.Invoke(t.Copy()) ?? t.Copy()).ToArray();
        DirectHitEventIds = Array.AsReadOnly((directHitEventIds ?? Array.Empty<string>()).Distinct(StringComparer.Ordinal).ToArray());
    }
    public static LogicalDialogueSelection FromRecords(IEnumerable<MemoryTurn> records, IEnumerable<string>? directHitEventIds = null)
        => new(LogicalDialogueCatalog.Build(records).Units, directHitEventIds);
    public IReadOnlyList<LogicalDialogueUnit> Units { get; }
    public IReadOnlyList<string> DirectHitEventIds { get; }
    public int Count => _records.Length;
    public MemoryTurn this[int index] => _records[index].Copy();
    public IEnumerator<MemoryTurn> GetEnumerator() => _records.Select(t => t.Copy()).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
