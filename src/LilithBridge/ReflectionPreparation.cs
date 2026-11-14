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
    internal ReflectionResultSave ResultSave { get; } = new();
    internal bool RetryBudgetEnabled { get; set; }
    internal ReflectionRetryReturn? RetryReturn { get; set; }
    internal ReflectionServiceTrace? IntentTrace { get; set; }
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
    internal void Cancel() { ResultSave.Cancel(); if (!Token.IsCancellationRequested) _cancellation.Cancel(); }
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
        return GetExperienceValidationTurns().Where(t => ids.Contains(t.EventId) && t.DeliveryState is "received" or "delivered"
            && IsMentionAllowed(t) && IsNativeSupplyAllowed(t)).Select(t => t.Copy()).ToArray();
    }
    internal bool ReflectionPreparationCurrent(ReflectionPreparation owner) =>
        owner.Matches(_root, _sessionId, ResolveReflectionPreparationWindow(owner.Window));

    // Both dispatch and result application recheck the complete read footprint
    // under the existing source lock. No await/model work runs under this lock.
    internal bool TryUseReflectionRequest(ReflectionPreparation owner, ReflectionRequestSnapshot request, Func<bool> use,
        bool recovering = false)
    {
        lock (_experienceLock)
        {
            if (!ReflectionPreparationCurrent(owner) || !ReflectionMaterialsCurrent(request, recovering)) return false;
            return TryUseB1SelectionGeneration(request.SelectionGeneration, use);
        }
    }

    private bool ReflectionMaterialsCurrent(ReflectionRequestSnapshot request, bool recovering = false)
    {
        if (!ReflectionRevisitContextBound(request)) return false;
        if (!MainRevisitOriginPermitted(request.RevisitContext?.Main, request)) return false;
        if (request.SelectionGeneration != CurrentB1SelectionGeneration()) return false;
        if (!ReflectionRequestOriginCurrent(request) || !ReflectionPurposesCurrent(request)) return false;
        if (!ReflectionGeneratedPublicCurrent(request)) return false;
        if (!NoteHistoryStillReadable(request, out _) || !RelationshipHistoryStillReadable(request, out _)) return false;
        var raw = GetExperienceValidationTurns().ToDictionary(t => t.EventId, StringComparer.Ordinal);
        if (request.Evidence.Any(t => !raw.ContainsKey(t.EventId) || !IsMentionAllowed(raw[t.EventId]) || !IsNativeSupplyAllowed(raw[t.EventId]))) return false;
        // A completed chain may itself have retired one of the versions it
        // read. Only that work's recorded targets may differ in lifecycle;
        // their body, basis, permissions and all other versions still match.
        var ownTargets = recovering ? _reflectionNotes.Concat(_reflectionLessons).Concat(_relationshipReadings)
            .Where(t => t.OperationId == request.RequestId && t.SourceEventId.Length > 0).Select(t => t.SourceEventId)
            .Concat(_reflectionNotes.Where(t => t.ReflectionSettlementOperationId == request.RequestId).Select(t => t.EventId))
            .ToHashSet(StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal);
        var restoredNotes = new Dictionary<string, MemoryTurn>(StringComparer.Ordinal);
        foreach (var prior in request.Dependencies)
        {
            IEnumerable<MemoryTurn> pool = prior.Kind switch
            { "note" => _reflectionNotes, "lesson" => _reflectionLessons, "reading" => _relationshipReadings, "day" => _dreams, _ => raw.Values };
            var current = pool.FirstOrDefault(t => t.EventId == prior.EventId);
            if (current == null || !IsMentionAllowed(current) || !IsNativeSupplyAllowed(current)) return false;
            if (ownTargets.Contains(prior.EventId))
            {
                if (current.LifecycleState is not ("superseded" or "settled")) return false;
                var supplied = request.Notes.Concat(request.Settled).Concat(request.Lessons)
                    .Concat(request.Readings).Concat(request.RevisionTargets).First(t => t.EventId == prior.EventId);
                current = current.Copy(); current.LifecycleState = supplied.LifecycleState;
            }
            if (SourceVersion(prior.Kind, current) != prior) return false;
            if (prior.Kind == "note" && ownTargets.Contains(prior.EventId)) restoredNotes[prior.EventId] = current;
        }
        // Only exact own-target lifecycle changes passed the fingerprint check
        // above. Re-evaluate the read graph through those original versions so
        // their unchanged dependent lessons/readings are not mistaken for an
        // external invalidation. This view grants no new proposal citations.
        // Recheck every day actually supplied, even if no proposal cites it.
        // The existing supplier distinguishes retired declared support from
        // ordinary revision of reading-only background; preserve that distinction.
        if (request.DayArtifacts.Count > 0)
        {
            var days = AvailableDayArtifactIds(restoredNotes);
            if (request.DayArtifacts.Any(t => !days.Contains(t.EventId))) return false;
        }
        var active = SelectAvailableReflectionNotes(int.MaxValue, null, restoredNotes).Select(t => t.EventId).ToHashSet(StringComparer.Ordinal);
        var settled = SelectAvailableSettledNotes(int.MaxValue, restoredNotes).Select(t => t.EventId).ToHashSet(StringComparer.Ordinal);
        var lessons = SelectAvailableReflectionLessons(int.MaxValue, restoredNotes).Select(t => t.EventId).ToHashSet(StringComparer.Ordinal);
        var readings = SelectAvailableRelationshipReadings(int.MaxValue, restoredNotes).Select(t => t.EventId).ToHashSet(StringComparer.Ordinal);
        if (request.Notes.Any(t => !(active.Contains(t.EventId) || ownTargets.Contains(t.EventId)) || !RecordPermitted(t) || !IsNativeSupplyAllowed(t))
            || request.Settled.Any(t => !(settled.Contains(t.EventId) || ownTargets.Contains(t.EventId)) || !RecordPermitted(t) || !IsNativeSupplyAllowed(t))
            || request.Lessons.Any(t => !(lessons.Contains(t.EventId) || ownTargets.Contains(t.EventId)) || !RecordPermitted(t))
            || request.Readings.Any(t => !(readings.Contains(t.EventId) || ownTargets.Contains(t.EventId)) || !RecordPermitted(t))
            || request.RevisionTargets.Any(t => !RecordPermitted(t) || !IsMentionAllowed(t) || !IsNativeSupplyAllowed(t))) return false;
        return true;
    }
}
