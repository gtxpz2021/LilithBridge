using System.Text.Json;
using System.Threading;

namespace LilithBridge;

// Short-lived request ownership only. No new persistent memory or model phase.
internal sealed class ReflectionPreparation : IDisposable
{
    private readonly CancellationTokenSource _cancellation = new();
    internal string Id { get; } = Guid.NewGuid().ToString("N");
    internal long Generation { get; }
    internal string DataRoot { get; }
    internal string SessionId { get; }
    internal string Query { get; }
    internal string WindowFingerprint { get; }
    internal IReadOnlyList<MemoryTurn> Window { get; }
    internal CancellationToken Token { get; }
    internal SemanticRecallResult? SemanticResult { get; private set; }
    internal string SemanticStatus { get; private set; } = "not_requested";
    internal string WorkerEpoch { get; private set; } = "";
    internal bool Staged { get; set; }
    internal ReflectionPreparation(long generation, string root, string session, string query, IReadOnlyList<MemoryTurn> window)
    {
        Generation = generation; DataRoot = root; SessionId = session; Query = query;
        Window = window.Select(t => t.Copy()).ToArray(); WindowFingerprint = Fingerprint(Window); Token = _cancellation.Token;
    }
    internal static string Fingerprint(IReadOnlyList<MemoryTurn> window) => MemoryStore.SemanticTextHash(JsonSerializer.Serialize(window));
    internal bool Matches(string root, string session, IReadOnlyList<MemoryTurn> window) => !Token.IsCancellationRequested
        && DataRoot == root && SessionId == session && WindowFingerprint == Fingerprint(window);
    internal void Unavailable(string reason) { SemanticResult = null; SemanticStatus = reason; }
    internal bool Accept(SemanticRecallResult result, string epoch)
    {
        bool valid = !Token.IsCancellationRequested && result.QueryId == Id && result.Generation == Generation
            && result.QueryHash == MemoryStore.SemanticTextHash(Query) && result.DataRoot == DataRoot && result.SessionId == SessionId
            && result.ModelRevision == SemanticRecallWorker.ModelRevision && (result.Domains & SemanticDomain.Thoughts) != 0
            && epoch.Length > 0 && result.IndexVersion.StartsWith(epoch + ":", StringComparison.Ordinal)
            && result.Unavailable.Length == 0;
        if (!valid) { Unavailable(result.Unavailable.Length > 0 ? result.Unavailable : "semantic_scope_changed"); return false; }
        SemanticResult = result; WorkerEpoch = epoch; SemanticStatus = result.IndexComplete ? "available" : "partial"; return true;
    }
    internal bool Owns(SemanticRecallResult? result) => result != null && ReferenceEquals(result, SemanticResult)
        && !Token.IsCancellationRequested && result.QueryId == Id && result.Generation == Generation
        && result.IndexVersion.StartsWith(WorkerEpoch + ":", StringComparison.Ordinal);
    internal void Cancel() { if (!Token.IsCancellationRequested) _cancellation.Cancel(); }
    public void Dispose() { Cancel(); _cancellation.Dispose(); }
}

internal sealed partial class MemoryStore
{
    internal ReflectionPreparation? PrepareReflection(IReadOnlyList<MemoryTurn> window, long generation)
    {
        var current = ResolveReflectionPreparationWindow(window);
        return current.Count == window.Count && current.Count > 0
            ? new(generation, _root, _sessionId, ReflectionTopic(current), current) : null;
    }
    internal IReadOnlyList<MemoryTurn> ResolveReflectionPreparationWindow(IReadOnlyList<MemoryTurn> window)
    {
        if (!_longTermEnabled || !_experienceArchiveEnabled) return Array.Empty<MemoryTurn>();
        var ids = window.Select(t => t.EventId).ToHashSet(StringComparer.Ordinal);
        return ReadExperiences().Where(t => ids.Contains(t.EventId) && t.DeliveryState is "received" or "delivered"
            && IsMentionAllowed(t) && IsNativeSupplyAllowed(t)).Select(t => t.Copy()).ToArray();
    }
    internal bool ReflectionPreparationCurrent(ReflectionPreparation owner) =>
        owner.Matches(_root, _sessionId, ResolveReflectionPreparationWindow(owner.Window));

    // Both dispatch and result application recheck the complete read footprint
    // under the existing source lock. No await/model work runs under this lock.
    internal bool TryUseReflectionRequest(ReflectionPreparation owner, ReflectionRequestSnapshot request, Func<bool> use)
    {
        lock (_experienceLock)
        {
            if (!ReflectionPreparationCurrent(owner) || !NoteHistoryStillReadable(request, out _)) return false;
            var raw = ReadExperiences().ToDictionary(t => t.EventId, StringComparer.Ordinal);
            if (request.Evidence.Any(t => !raw.ContainsKey(t.EventId) || !IsMentionAllowed(raw[t.EventId]) || !IsNativeSupplyAllowed(raw[t.EventId]))) return false;
            var active = GetReflectionNotes(int.MaxValue).Select(t => t.EventId).ToHashSet(StringComparer.Ordinal);
            var settled = GetSettledNotes(int.MaxValue).Select(t => t.EventId).ToHashSet(StringComparer.Ordinal);
            var lessons = GetReflectionLessons(int.MaxValue).Select(t => t.EventId).ToHashSet(StringComparer.Ordinal);
            var readings = GetRelationshipReadings(int.MaxValue).Select(t => t.EventId).ToHashSet(StringComparer.Ordinal);
            if (request.Notes.Any(t => !active.Contains(t.EventId) || !IsNativeSupplyAllowed(t))
                || request.Settled.Any(t => !settled.Contains(t.EventId) || !IsNativeSupplyAllowed(t))
                || request.Lessons.Any(t => !lessons.Contains(t.EventId)) || request.Readings.Any(t => !readings.Contains(t.EventId))
                || request.RevisionTargets.Any(t => !RecordPermitted(t) || !IsMentionAllowed(t) || !IsNativeSupplyAllowed(t))) return false;
            foreach (var prior in request.Dependencies)
            {
                IEnumerable<MemoryTurn> pool = prior.Kind switch
                { "note" => _reflectionNotes, "lesson" => _reflectionLessons, "reading" => _relationshipReadings, "day" => _dreams, _ => raw.Values };
                var current = pool.FirstOrDefault(t => t.EventId == prior.EventId);
                if (current == null || SourceVersion(prior.Kind, current) != prior || !IsMentionAllowed(current) || !IsNativeSupplyAllowed(current)) return false;
            }
            return use();
        }
    }
}
