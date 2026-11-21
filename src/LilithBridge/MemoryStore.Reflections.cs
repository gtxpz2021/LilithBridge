using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LilithBridge;

internal sealed record ReflectionRequestSnapshot(string RequestId,
    [property: System.Text.Json.Serialization.JsonConverter(typeof(ReflectionSourceJsonConverter))] string Source,
    IReadOnlyList<MemoryTurn> Evidence, IReadOnlyList<MemoryTurn> Notes,
    IReadOnlyList<MemoryTurn> Lessons, IReadOnlyList<MemoryTurn> Readings,
    IReadOnlyList<MemoryTurn> DayArtifacts, IReadOnlyList<MemoryTurn> Settled)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ReflectionRevisitInvocation? RevisitContext { get; init; }
    // Optional request provenance, not a semantic target or a new citation pool.
    public ReflectionRequestOriginContext? RequestOriginContext { get; init; }
    public ReflectionPurposeContext[] PurposeContexts { get; init; } = Array.Empty<ReflectionPurposeContext>();
    // Missing on legacy journals means generation zero, never current-by-default.
    public long SelectionGeneration { get; init; }
    // Read-only received public answers, never additional note citation IDs.
    public IReadOnlyList<ReflectionGeneratedDependency> GeneratedPublicContext { get; init; } = Array.Empty<ReflectionGeneratedDependency>();
    // Programme-owned stamps of the exact versions supplied. Not model labels.
    public IReadOnlyList<DaySourceDependency> Dependencies { get; init; } = Array.Empty<DaySourceDependency>();
    public IReadOnlyList<MemoryTurn> RevisionTargets { get; init; } = Array.Empty<MemoryTurn>();
    // Read-only historical context, never an additional citation/revision pool.
    public IReadOnlyList<MemoryTurn> NoteHistory { get; init; } = Array.Empty<MemoryTurn>();
    public IReadOnlyList<DaySourceDependency> NoteHistoryPermissions { get; init; } = Array.Empty<DaySourceDependency>();
    public IReadOnlyList<MemoryTurn> RelationshipHistoryRecords { get; init; } = Array.Empty<MemoryTurn>();
    public IReadOnlyList<DaySourceDependency> RelationshipHistoryVersions { get; init; } = Array.Empty<DaySourceDependency>();
    public Dictionary<string, string>? ShortAliases { get; init; }
}

// Immutable request metadata, never a persistent belief or model-provided tag.
internal sealed record ThoughtRecallBoundary(bool RequiresUncertainty, string Query);

internal sealed class ReflectionNoteProposal
{
    public string Text { get; set; } = "";
    public string[] SourceEventIds { get; set; } = Array.Empty<string>();
    /// <summary>
    /// Optional: her own day-level artifacts (organisation / dream) this thought
    /// was re-read through. They never replace a raw utterance as the basis.
    /// </summary>
    public string[] SourceDayIds { get; set; } = Array.Empty<string>();
    public string Revises { get; set; } = "";
    /// <summary>
    /// Optional: a thought she is finished with. It stays in the archive (with
    /// her reason and the time) and can be reopened by a later revision.
    /// </summary>
    public string Settles { get; set; } = "";
    /// <summary>Her own short reason for closing that thought; required with Settles.</summary>
    public string SettleReason { get; set; } = "";
}

/// <summary>
/// A transferable lesson she draws from her own thoughts. It is not a rule about
/// the player, not user evidence, and it can be revised or dropped later.
/// </summary>
internal sealed class ReflectionLessonProposal
{
    public string Text { get; set; } = "";
    public string[] SourceNoteIds { get; set; } = Array.Empty<string>();
    public string Revises { get; set; } = "";
    public string Settles { get; set; } = "";
    public string SettleReason { get; set; } = "";
}

/// <summary>
/// Her own current understanding of the relationship, drawn from her thoughts.
/// It is an interpretation with provenance, not a score and not a fact.
/// </summary>
internal sealed class RelationshipReadingProposal
{
    public string Text { get; set; } = "";
    public string[] SourceNoteIds { get; set; } = Array.Empty<string>();
    public string Revises { get; set; } = "";
}

/// <summary>
/// Per-chain result of one reflection pass. The aggregate reason stays exactly
/// what the runtime always reported; this keeps the three chains apart so
/// telemetry can answer "she never proposed a lesson" instead of only
/// "this pass saved nothing".
/// </summary>
internal sealed record ReflectionChainOutcome(
    bool NoteProposed, string NoteReason,
    bool SettleProposed, string SettleReason,
    bool LessonProposed, string LessonReason,
    bool ReadingProposed, string ReadingReason,
    int NotesShown, int LessonsShown, int ReadingsShown, int DayArtifactsShown,
    int SettledShown)
{
    public string Describe() =>
        "note=" + Chain(NoteProposed, NoteReason)
        + ";settle=" + Chain(SettleProposed, SettleReason)
        + ";lesson=" + Chain(LessonProposed, LessonReason)
        + ";reading=" + Chain(ReadingProposed, ReadingReason)
        + ";shown=notes" + NotesShown + ",lessons" + LessonsShown + ",readings" + ReadingsShown
        + ",days" + DayArtifactsShown + ",settled" + SettledShown;

    private static string Chain(bool proposed, string reason)
    {
        if (!proposed)
            return "none";
        return reason is "saved" or "lesson_saved" or "reading_saved" or "settled"
            ? "saved"
            : reason == "persistence_failed" ? "failed:" + reason
            : reason is "already_applied" or "already_settled" ? "unchanged:" + reason
            : "rejected:" + reason;
    }
}

internal sealed partial class MemoryStore
{
    private readonly List<MemoryTurn> _reflectionNotes = new();
    private readonly List<MemoryTurn> _reflectionLessons = new();
    private readonly List<MemoryTurn> _relationshipReadings = new();

    public const string RelationshipReadingKind = "reflection.relationship";

    /// <summary>
    /// Section headers the chat projection writes. They are named here so the
    /// supply trace can answer "did the reflection section reach the payload" by
    /// header, instead of searching the context for note text.
    /// </summary>
    internal const string ThoughtBlockHeader = "[你的想法记录";
    internal const string ChatNoteHeader = "[你以前留下的想法]";
    internal const string SettledNoteHeader = "[你以前收起的想法记录]";
    internal const string LessonHeader = "[你自己的经验法则]";
    internal const string ThoughtLifecycleContract = "state=active 表示记录尚未被收起或后继版本取代；state=settled 表示记录曾被收起，时间和理由保留在 settled_at / settle_reason；state=superseded 表示该版本已被后继版本取代，关联见 revises / revised_by。这些是记录生命周期，仅凭状态不能判断此刻是否赞同、正在想或已经不再在意。可以采纳、提出异议、重新考虑，也可以不提起。";

    /// <summary>
    /// Handing an observation to a sink is pure telemetry: a sink that throws
    /// must never change which note is chosen or what the chat receives.
    /// </summary>
    private static void Observe(Action<ReflectionProjectionObservation>? sink,
        ReflectionProjectionObservation observation)
    {
        if (sink == null)
            return;
        try
        {
            sink(observation);
        }
        catch
        {
            // Telemetry is a bypass; it never decides.
        }
    }

    /// <summary>
    /// One fact at the stage where it was really decided. A sink is only passed
    /// from the chat projection path, so these observations describe an
    /// attempted projection by construction.
    /// </summary>
    private static ReflectionProjectionObservation Fact(MemoryTurn note,
        ReflectionProjectionStage stage, bool allAvailable,
        IReadOnlyList<string> availableSourceIds, IReadOnlyList<string> relevantSourceIds,
        bool anyRelevant, bool added) =>
        new(note.EventId, note.SourceEventIds, availableSourceIds, relevantSourceIds,
            allAvailable, anyRelevant, true, added, stage);

    /// <summary>
    /// Ids of her own day artifacts that the day consumer still supplies. Notes,
    /// lessons and readings that were re-read through a day resolve this same set,
    /// so "this day is current" cannot mean two different things in two read paths.
    /// </summary>
    private HashSet<string> AvailableDayArtifactIds(IReadOnlyDictionary<string, MemoryTurn>? restoredNotes = null)
    {
        if (restoredNotes == null && TryGetRequestReadMemo<HashSet<string>>("day_artifact_ids", out var cached))
            return cached;
        var ids = SelectAvailableDayArtifacts(int.MaxValue, restoredNotes).Select(t => t.EventId).ToHashSet(StringComparer.Ordinal);
        if (restoredNotes == null) SetRequestReadMemo("day_artifact_ids", ids);
        return ids;
    }

    /// <summary>
    /// A thought stays available while everything it rests on is: the raw
    /// utterances it came from, and any of her own day-level artifacts it was
    /// re-read through. Older thoughts that cite no day artifact are unaffected.
    /// </summary>
    private bool IsAvailableNote(MemoryTurn note, HashSet<string> availableEvents, HashSet<string> availableDays, Func<MemoryTurn, bool> isCurrent) =>
        IsActiveLifecycle(note) && note.Origin == "reflection" && isCurrent(note)
        && note.SourceEventIds.Length > 0 && note.SourceEventIds.All(availableEvents.Contains)
        && note.SourceDayIds.All(availableDays.Contains);

    /// <summary>
    /// The candidates are observed where the existing filter evaluates them, so
    /// a note that never survives availability is still visible in the trace —
    /// without a second, diagnostic enumeration of the same chain.
    /// </summary>
    public IReadOnlyList<MemoryTurn> GetReflectionNotes(int max,
        Action<ReflectionProjectionObservation>? onObservation = null)
        => SelectAvailableReflectionNotes(max, onObservation, null);

    private IReadOnlyList<MemoryTurn> SelectAvailableReflectionNotes(int max,
        Action<ReflectionProjectionObservation>? onObservation,
        IReadOnlyDictionary<string, MemoryTurn>? restoredNotes)
    {
        if (onObservation == null && restoredNotes == null && TryGetRequestReadMemo<List<MemoryTurn>>("reflection_notes", out var cached))
            return cached.TakeLast(Math.Max(0, max)).Select(t => t.Copy()).ToList();
        HashSet<string> available = GetExperienceValidationTurns().Select(t => t.EventId).ToHashSet(StringComparer.Ordinal);
        HashSet<string> availableDays = AvailableDayArtifactIds(restoredNotes);
        var isCurrent = CreateCurrentBasisEvaluator(available, restoredNotes);
        var kept = new List<MemoryTurn>();
        foreach (MemoryTurn candidate in ReflectionNoteView(restoredNotes))
        {
            string[] surviving = onObservation != null
                ? candidate.SourceEventIds.Where(available.Contains).ToArray() : Array.Empty<string>();
            Observe(onObservation, Fact(candidate, ReflectionProjectionStage.CandidateSeen,
                false, surviving, Array.Empty<string>(), false, false));
            bool usable = IsAvailableNote(candidate, available, availableDays, isCurrent) && IsMentionAllowed(candidate);
            Observe(onObservation, Fact(candidate, ReflectionProjectionStage.AvailabilityChecked,
                usable, surviving, Array.Empty<string>(), false, false));
            if (!usable)
                continue;
            Observe(onObservation, Fact(candidate, ReflectionProjectionStage.AvailabilityPassed,
                true, surviving, Array.Empty<string>(), false, false));
            kept.Add(candidate);
        }
        if (onObservation == null && restoredNotes == null)
            SetRequestReadMemo("reflection_notes", kept);
        return kept.TakeLast(Math.Max(0,max)).Select(t => t.Copy()).ToList();
    }

    /// <summary>
    /// Thoughts she has finished with. They stay in the archive and stay
    /// reachable, so a later reflection can meet one again and see how she closed
    /// it; they are simply no longer part of what she is carrying.
    /// </summary>
    public IReadOnlyList<MemoryTurn> GetSettledNotes(int max)
        => SelectAvailableSettledNotes(max, null);

    private IReadOnlyList<MemoryTurn> SelectAvailableSettledNotes(int max,
        IReadOnlyDictionary<string, MemoryTurn>? restoredNotes)
    {
        if (restoredNotes == null && TryGetRequestReadMemo<List<MemoryTurn>>("settled_notes", out var cached))
            return cached.TakeLast(Math.Max(0, max)).Select(t => t.Copy()).ToList();
        HashSet<string> available = GetExperienceValidationTurns().Select(t => t.EventId).ToHashSet(StringComparer.Ordinal);
        var materialPermitted = CreateMaterialPermissionEvaluator();
        var isDayCurrent = CreateDayArtifactCurrentEvaluator(materialPermitted, restoredNotes);
        HashSet<string> availableDays = _dreams.Where(t => t.Kind is DayConsolidationKind or DayDreamKind
                ? IsMentionAllowed(t) && isDayCurrent(t) : IsDerivedMemoryVisible(t))
            .Select(t => t.EventId).ToHashSet(StringComparer.Ordinal);
        var settled = _reflectionNotes
            .Where(t => IsMentionAllowed(t) && IsSettledLifecycle(t) && t.Origin == "reflection"
                && t.SourceEventIds.Length > 0 && t.SourceEventIds.All(available.Contains)
                && t.SourceDayIds.All(availableDays.Contains))
            .ToList();
        if (restoredNotes == null) SetRequestReadMemo("settled_notes", settled);
        return settled.TakeLast(Math.Max(0, max)).Select(t => t.Copy()).ToList();
    }

    /// <summary>
    /// Thoughts a later revision replaced. They are never current, but a question
    /// about what she used to think must be able to reach them as the versions
    /// they were, together with the revision that replaced them.
    /// </summary>
    public IReadOnlyList<MemoryTurn> GetSupersededNotes(int max)
    {
        if (TryGetRequestReadMemo<List<MemoryTurn>>("superseded_notes", out var cached))
            return cached.TakeLast(Math.Max(0, max)).Select(t => t.Copy()).ToList();
        HashSet<string> available = GetExperienceValidationTurns().Select(t => t.EventId).ToHashSet(StringComparer.Ordinal);
        HashSet<string> availableDays = _dreams.Where(IsDerivedMemoryVisible).Select(t => t.EventId).ToHashSet(StringComparer.Ordinal);
        var superseded = _reflectionNotes
            .Where(t => IsMentionAllowed(t) && IsSupersededLifecycle(t) && t.Origin == "reflection"
                && t.SourceEventIds.Length > 0 && t.SourceEventIds.All(available.Contains)
                && t.SourceDayIds.All(availableDays.Contains))
            .ToList();
        SetRequestReadMemo("superseded_notes", superseded);
        return superseded.TakeLast(Math.Max(0, max)).Select(t => t.Copy()).ToList();
    }

    public IReadOnlyList<MemoryTurn> GetReflectionLessons(int max)
        => SelectAvailableReflectionLessons(max, null);

    private IReadOnlyList<MemoryTurn> SelectAvailableReflectionLessons(int max,
        IReadOnlyDictionary<string, MemoryTurn>? restoredNotes)
    {
        if (restoredNotes == null && TryGetRequestReadMemo<List<MemoryTurn>>("reflection_lessons", out var cached))
            return cached.TakeLast(Math.Max(0, max)).Select(t => t.Copy()).ToList();
        HashSet<string> available = GetExperienceValidationTurns().Select(t => t.EventId).ToHashSet(StringComparer.Ordinal);
        HashSet<string> availableDays = AvailableDayArtifactIds(restoredNotes);
        HashSet<string> availableNotes = CurrentBasisNoteIds(available, availableDays, restoredNotes);
        var activeCorrections = ActiveCorrectionList();
        var lessons = _reflectionLessons
            .Where(t => IsMentionAllowed(t) && IsActiveLifecycle(t) && t.Kind == "reflection.lesson"
                && t.SourceNoteIds.Length > 0
                && t.SourceNoteIds.All(id => ResolveNoteCitation(id, availableNotes).Length > 0)
                && t.SourceEventIds.Length > 0 && t.SourceEventIds.All(available.Contains)
                && !HasUnincorporatedCorrectionFast(t, activeCorrections))
            .ToList();
        if (restoredNotes == null) SetRequestReadMemo("reflection_lessons", lessons);
        return lessons.TakeLast(Math.Max(0, max)).Select(t => t.Copy()).ToList();
    }

    /// <summary>
    /// Historical citation and current support are different. Settling a thought
    /// does not falsify it; replacing it retires conclusions resting on that exact
    /// version from default supply. No stored lifecycle is rewritten or cascaded.
    /// </summary>
    private HashSet<string> CurrentBasisNoteIds(HashSet<string> available, HashSet<string> availableDays,
        IReadOnlyDictionary<string, MemoryTurn>? restoredNotes = null)
    {
        var isCurrent = CreateCurrentBasisEvaluator(available, restoredNotes);
        return ReflectionNoteView(restoredNotes).Where(t => (IsActiveLifecycle(t) || IsSettledLifecycle(t))
            && t.Origin == "reflection" && isCurrent(t) && t.SourceDayIds.All(availableDays.Contains))
            .Select(t => t.EventId).ToHashSet(StringComparer.Ordinal);
    }

    private string ResolveNoteCitation(string noteId, HashSet<string> availableNotes) =>
        availableNotes.Contains(noteId) ? noteId : "";

    /// <summary>
    /// Active readings of the relationship whose supporting thoughts are still
    /// available. Newest last; never user evidence.
    /// </summary>
    public IReadOnlyList<MemoryTurn> GetRelationshipReadings(int max)
        => SelectAvailableRelationshipReadings(max, null);

    private IReadOnlyList<MemoryTurn> SelectAvailableRelationshipReadings(int max,
        IReadOnlyDictionary<string, MemoryTurn>? restoredNotes)
    {
        if (restoredNotes == null && TryGetRequestReadMemo<List<MemoryTurn>>("relationship_readings", out var cached))
            return cached.TakeLast(Math.Max(0, max)).Select(t => t.Copy()).ToList();
        HashSet<string> available = GetExperienceValidationTurns().Select(t => t.EventId).ToHashSet(StringComparer.Ordinal);
        HashSet<string> availableDays = AvailableDayArtifactIds(restoredNotes);
        HashSet<string> availableNotes = CurrentBasisNoteIds(available, availableDays, restoredNotes);
        var activeCorrections = ActiveCorrectionList();
        var readings = _relationshipReadings
            .Where(t => IsMentionAllowed(t) && IsActiveLifecycle(t) && t.Kind == RelationshipReadingKind
                && t.SourceNoteIds.Length > 0
                && t.SourceNoteIds.All(id => ResolveNoteCitation(id, availableNotes).Length > 0)
                && t.SourceEventIds.Length > 0 && t.SourceEventIds.All(available.Contains)
                && !HasUnincorporatedCorrectionFast(t, activeCorrections))
            .ToList();
        if (restoredNotes == null) SetRequestReadMemo("relationship_readings", readings);
        return readings.TakeLast(Math.Max(0, max)).Select(t => t.Copy()).ToList();
    }

    /// <summary>
    /// Self-locking signal over her recent cognitive artifacts. A high value means
    /// she keeps producing variations of the same thought, which the AutoPersonas
    /// work identifies as a runtime failure mode rather than growth.
    /// </summary>
    public double MeasureReflectionRepetition(int window = 16) =>
        MemorySemantics.RepetitionRatio(
            GetReflectionNotes(window).Select(t => t.Text)
                .Concat(GetReflectionLessons(window).Select(t => t.Text)),
            0.75);

    /// <summary>
    /// Cheap monotonic-ish fingerprint of user-fact activity: any new explicit
    /// record, correction or removal changes it, so a reflection can be worth it
    /// even when only one utterance arrived.
    /// </summary>
    public long MeasureMemoryActivity() =>
        _autonomous.Count + _autonomous.Sum(t => (long)t.Revision);

    /// <summary>
    /// True when at least one of the supplied utterances says something her
    /// current thoughts do not already cover. With no thoughts yet, everything
    /// is new.
    /// </summary>
    public bool IsNovelVersusNotes(IEnumerable<string> texts, double threshold = 0.6)
    {
        var notes = GetReflectionNotes(32).Select(t => t.Text).ToList();
        foreach (string text in texts ?? Enumerable.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(text))
                continue;
            if (notes.Count == 0)
                return true;
            if (notes.All(note => MemorySemantics.Similarity(note, text) < threshold))
                return true;
        }
        return false;
    }

    // Resolve only explicit raw-source links. This is provenance, not a semantic
    // judgement of the thought and not a new persisted state or save restriction.
    internal string DescribeSourceAttribution(MemoryTurn derived)
    {
        if (derived.SourceEventIds.Length == 0) return "";
        var ids = derived.SourceEventIds.ToHashSet(StringComparer.Ordinal);
        var raw = GetExperienceValidationTurns().Where(t => ids.Contains(t.EventId)).ToArray();
        var notes = new List<string>();
        if (raw.Any(t => t.Origin == LocalFallbackOrigin))
            notes.Add("含程序本地台词，不代表自主选择");
        var reportLabels = raw.Select(t => UtteranceAttribution.Reported(t.Role, t.Text)?.source_label)
            .Where(l => !string.IsNullOrEmpty(l)).Distinct(StringComparer.Ordinal).Take(3).ToArray();
        if (reportLabels.Length > 0)
            notes.Add("含转述前缀“" + string.Join("”“", reportLabels) + "”，身份未核实");
        if (raw.Length != ids.Count)
            notes.Add("部分原始来源未核实");
        if (raw.Length == ids.Count && raw.Length > 0 && raw.All(t => t.Role == "assistant"))
            notes.Add("仅来自莉莉丝自己的回复，不作玩家自述证据");
        return notes.Count == 0 ? "" : "（来源注记：" + string.Join("；", notes) + "）";
    }

    public ReflectionRequestSnapshot? CaptureReflectionRequest(IReadOnlyList<MemoryTurn>? requestedWindow = null)
        => CaptureReflectionRequestCore(requestedWindow, null, null, null);

    internal ReflectionRequestSnapshot? CaptureReflectionRequest(IReadOnlyList<MemoryTurn> requestedWindow,
        SemanticRecallResult? semantic, ReflectionPreparation owner, ReflectionPendingIntent[]? intents = null) => CaptureReflectionRequestCore(requestedWindow, semantic, owner, intents);

    private ReflectionRequestSnapshot? CaptureReflectionRequestCore(IReadOnlyList<MemoryTurn>? requestedWindow,
        SemanticRecallResult? semantic, ReflectionPreparation? preparation, ReflectionPendingIntent[]? intents)
    {
        lock (_experienceLock) return CaptureReflectionRequestLocked(requestedWindow, semantic, preparation, intents);
    }
    private ReflectionRequestSnapshot? CaptureReflectionRequestLocked(IReadOnlyList<MemoryTurn>? requestedWindow,
        SemanticRecallResult? semantic, ReflectionPreparation? preparation, ReflectionPendingIntent[]? intents)
    {
        if (!_longTermEnabled || !_experienceArchiveEnabled) return null;
        long selectionGeneration = CurrentB1SelectionGeneration();
        if (selectionGeneration < 0) return null;
        var requestedIds = requestedWindow?.Select(t=>t.EventId).ToHashSet(StringComparer.Ordinal);
        // Resolve from the owning store now: a captured pending window is never
        // authority to resurrect deleted, withdrawn or changed raw material.
        var recent = requestedIds == null
            ? GetRecentHistory(12).Where(t => t.DeliveryState is "received" or "delivered").ToList()
            : GetExperienceValidationTurns().Where(t=>requestedIds.Contains(t.EventId)
                && t.DeliveryState is "received" or "delivered" && IsMentionAllowed(t) && IsNativeSupplyAllowed(t)).Select(t=>t.Copy()).ToList();
        if (recent.Count == 0 || recent.Any(t => t.Role == "user" && !ReflectionDeliverySettled(t))) return null;
        if (preparation != null && !preparation.Matches(_root, _sessionId, recent)) return null;
        if (preparation?.Owns(semantic) != true) semantic = null;
        IReadOnlyList<MemoryTurn> notes = SelectNotesForReflection(recent, semantic);
        IReadOnlyList<MemoryTurn> lessons = GetReflectionLessons(4);
        IReadOnlyList<MemoryTurn> readings = GetRelationshipReadings(2);
        IReadOnlyList<MemoryTurn> revisionTargets = CaptureRevisionTargets(recent.Concat(notes));
        // Archived settled records retain their recorded closure, without making
        // a claim about her present attitude towards their contents.
        IReadOnlyList<MemoryTurn> settled = SelectSettledForReflection(recent, semantic);
        // Her own day-level artifacts are read-only context for a later reflection:
        // she may re-read a day she organised, or a dream she had, and think again.
        // They are never evidence, and they never replace a raw utterance as the
        // basis of a thought (see ApplyNoteCore).
        IReadOnlyList<MemoryTurn> dayArtifacts = CaptureDayArtifactsForReflection();
        var ids = ReflectionPurposeWindow(recent, intents).Select(t => t.EventId).Concat(notes.SelectMany(t => t.SourceEventIds))
            .Concat(settled.SelectMany(t => t.SourceEventIds))
            .Concat(revisionTargets.SelectMany(t => t.SourceEventIds))
            .Concat(CorrectionInputIds(revisionTargets)).ToHashSet(StringComparer.Ordinal);
        var evidence = GetExperienceValidationTurns().Where(t => ids.Contains(t.EventId)).Select(t=>t.Copy()).ToList();
        if (evidence.Count == 0) return null;
        var aliases = new ShortAliasTable();
        aliases.RegisterAll("e", evidence);
        aliases.RegisterAll("n", notes);
        aliases.RegisterAll("s", settled);
        aliases.RegisterAll("l", lessons);
        aliases.RegisterAll("r", readings);
        aliases.RegisterAll("d", dayArtifacts);
        aliases.RegisterAll("t", revisionTargets);
        var history = CaptureNoteHistory(notes, aliases);
        var relationshipHistory = CaptureRelationshipHistory(readings, evidence, notes, history.Notes, settled, aliases);
        var generated = CaptureReflectionGeneratedPublic(recent, evidence, aliases);
        string source = JsonSerializer.Serialize(new
        {
            now = DateTimeOffset.UtcNow,
            discovery_status = preparation?.SemanticStatus ?? "not_requested",
            // The consumed window and companion evidence have different owners.
            // Old note sources may precede the pending window by months.
            window_start = recent.FirstOrDefault()?.Timestamp,
            window_end = recent.LastOrDefault()?.Timestamp,
            window_event_ids = aliases.AliasOrIds(recent.Select(t => t.EventId)),
            turn_count = evidence.Count,
            utterances = evidence.Select(t => new
            {
                event_id=aliases.AliasOrId(t.EventId), speaker=t.Role, origin=t.Origin, text=t.Text, at=t.Timestamp,
                source_revision_context = NaturalMemoryContext(t),
                reported_speech=UtteranceAttribution.Reported(t.Role,t.Text),
                exchange_delivery = t.Role == "user" ? ReflectionDeliveryDescription(t) : "",
                source_label = t.Origin == LocalFallbackOrigin
                    ? "本地程序生成并实际显示的兜底台词，不代表模型当时的思考、意图或自发决定。"
                    : ""
            }),
            generated_public_context = generated.Projection,
            note_history = history.Projection,
            relationship_history = relationshipHistory.Projection,
            previous_notes = notes.Select(t => new
            {
                note_id=aliases.AliasOrId(t.EventId),
                state=t.LifecycleState,
                revision=t.Revision,
                revises=aliases.AliasOrRegister("h", t.SourceEventId),
                text=t.Text,
                source_event_ids=aliases.AliasOrIds(t.SourceEventIds),
                source_context=DescribeSourceAttribution(t),
                at=t.Timestamp,
                surfaced_usage=DescribeReflectionUsageLocked(t.EventId),
                // Textual similarity is a selection hint, not a present belief.
                label = MemorySemantics.Similarity(ReflectionTopic(recent), t.Text) >= 0.75
                    ? "这条记录与本次交流的文本主题较接近；如有新的理解，可以修订，也可以不修改。"
                    : ""
            }),
            previous_lessons = lessons.Select(t => new { lesson_id=aliases.AliasOrId(t.EventId), text=t.Text,
                source_note_ids=aliases.AliasOrRegisterAll("h", t.SourceNoteIds), at=t.Timestamp,
                source_context=DescribeSourceAttribution(t),
                surfaced_usage=DescribeReflectionUsageLocked(t.EventId) })
            ,previous_readings = readings.Select(t => new { reading_id=aliases.AliasOrId(t.EventId), text=t.Text,
                source_note_ids=aliases.AliasOrRegisterAll("h", t.SourceNoteIds), at=t.Timestamp,
                source_context=DescribeSourceAttribution(t) })
            ,settled_notes = settled.Select(t => new
            {
                note_id = aliases.AliasOrId(t.EventId),
                state = t.LifecycleState,
                revision = t.Revision,
                revises = aliases.AliasOrRegister("h", t.SourceEventId),
                text = t.Text,
                at = t.Timestamp,
                settled_at = t.SettledAt,
                settle_reason = t.SettleReason,
                source_context = DescribeSourceAttribution(t),
                label = "这条记录曾被收起，时间和理由见 settled_at / settle_reason；可以重新考虑。"
            })
            ,day_artifacts = dayArtifacts.Select(t => new
            {
                day_id = aliases.AliasOrId(t.EventId),
                kind = t.Kind,
                at = t.Timestamp,
                label = t.Kind == DayDreamKind
                    ? "你当时的梦／想象，不是真实发生过的事"
                    : "你当时对那一天的归拢，是整理不是新事实",
                text = t.Kind == DayDreamKind ? CleanDreamDisplayText(t.Text) : t.Text, source_context = DescribeSourceAttribution(t)
            })
            ,revision_targets = revisionTargets.Select(t => new
            {
                target_id=aliases.AliasOrId(t.EventId), kind=t.Kind, revision=t.Revision, text=t.Text,
                source_event_ids=aliases.AliasOrRegisterAll("h", t.SourceEventIds),
                source_note_ids=aliases.AliasOrRegisterAll("h", t.SourceNoteIds),
                status=AssessMaterialSupply(t).Reason,
                use="仅供 revises 定位旧解释；原依据已变化，不能据此判断当前立场，也不可作为新来源或证据。修订须引用本次当前来源。"
            })
        }, DialogueMaterialEnvelope.DataJson);
        return new(Guid.NewGuid().ToString("N"),source,evidence,notes,lessons,readings,dayArtifacts,settled)
        {
            SelectionGeneration = selectionGeneration,
            GeneratedPublicContext = generated.Dependencies,
            Dependencies = CaptureReflectionVersions(evidence, notes.Concat(settled)
                .Concat(revisionTargets.Where(t => t.Kind == "reflection.note")),
                lessons.Concat(revisionTargets.Where(t => t.Kind == "reflection.lesson")),
                readings.Concat(revisionTargets.Where(t => t.Kind == RelationshipReadingKind)), dayArtifacts),
            RevisionTargets = revisionTargets,
            NoteHistory = history.Notes,
            NoteHistoryPermissions = history.Permissions,
            RelationshipHistoryRecords = relationshipHistory.Records,
            RelationshipHistoryVersions = relationshipHistory.Versions,
            ShortAliases = aliases.AliasToId
        };
    }

    private const int NoteHistoryByteLimit = 8192;
    private sealed record NoteHistoryCapture(JsonElement Projection, IReadOnlyList<MemoryTurn> Notes,
        IReadOnlyList<DaySourceDependency> Permissions);

    // Only explicit parent links form a family. Ancestors are resolved from the
    // full archive; neither root discovery nor ancestor resolution has a recency pre-cut.
    private NoteHistoryCapture CaptureNoteHistory(IReadOnlyList<MemoryTurn> roots, ShortAliasTable? aliases = null)
    {
        lock (_experienceLock)
        {
        var index = _reflectionNotes.GroupBy(t => t.EventId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        var readable = CreateNoteHistoryPermissionEvaluator(out _);
        var families = new List<(MemoryTurn Root, List<MemoryTurn> Ancestors)>();
        var omissions = new Dictionary<string, string>(StringComparer.Ordinal);
        var familyIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in roots.OrderByDescending(t => t.Timestamp).ThenBy(t => t.EventId, StringComparer.Ordinal))
        {
            if (root.SourceEventId.Length == 0) continue;
            var ancestors = new List<MemoryTurn>();
            var seen = new HashSet<string>(StringComparer.Ordinal) { root.EventId };
            var child = root;
            string reason = "";
            while (child.SourceEventId.Length > 0 && ancestors.Count < 2)
            {
                if (!index.TryGetValue(child.EventId, out var children) || children.Length != 1
                    || !index.TryGetValue(child.SourceEventId, out var parents) || parents.Length != 1
                    || !seen.Add(child.SourceEventId))
                { reason = "chain_broken"; break; }
                var parent = parents[0];
                if (child.Kind != "reflection.note" || parent.Kind != "reflection.note"
                    || child.Origin != "reflection" || parent.Origin != "reflection"
                    || child.RecallId.Length > 0 && parent.RecallId.Length > 0 && child.RecallId != parent.RecallId
                    || child.Revision > 0 && parent.Revision > 0 && child.Revision <= parent.Revision)
                { reason = "chain_broken"; break; }
                if (!IsSupersededLifecycle(parent) || !readable("note", parent.EventId))
                { reason = "history_unavailable"; break; }
                ancestors.Add(parent.Copy());
                child = parent;
            }
            if (reason.Length == 0 && child.SourceEventId.Length > 0) reason = "count_limit";
            if (reason.Length > 0) omissions[root.EventId] = reason;
            if (ancestors.Count == 0) continue;
            string family = root.RecallId.Length > 0 ? "recall:" + root.RecallId : "note:" + root.EventId;
            if (families.Count == 2 || !familyIds.Add(family))
            { omissions[root.EventId] = "family_limit"; continue; }
            families.Add((root, ancestors));
        }
        var selected = new List<(MemoryTurn Root, MemoryTurn Note)>();
        var stopped = new HashSet<string>(StringComparer.Ordinal);
        string Alias(string? id) => aliases?.AliasOrRegister("h", id) ?? id ?? "";
        string[] AliasList(IEnumerable<string>? ids) => ids == null ? Array.Empty<string>() : ids.Select(Alias).Where(s => s.Length > 0).ToArray();
        JsonElement Project() => JsonSerializer.SerializeToElement(new
        {
            entries = selected.OrderBy(p => families.FindIndex(f => f.Root.EventId == p.Root.EventId))
                .ThenBy(p => p.Note.Timestamp).ThenBy(p => p.Note.Revision).ThenBy(p => p.Note.EventId, StringComparer.Ordinal)
                .Select(p => new
                {
                    current_note_id = Alias(p.Root.EventId), note_id = Alias(p.Note.EventId),
                    revision = p.Note.Revision, revises = Alias(p.Note.SourceEventId), at = p.Note.Timestamp,
                    lifecycle = p.Note.LifecycleState, text = p.Note.Text,
                    source_event_ids = AliasList(p.Note.SourceEventIds), source_day_ids = AliasList(p.Note.SourceDayIds),
                    source_context = DescribeSourceAttribution(p.Note),
                    qualification = HasUnincorporatedCorrection(p.Note)
                        ? "这是当时的理解；原依据已有后续更正，不可作为当前事实。仅作历史背景，不增加引用或修改权限。"
                        : "这是当时的理解，仅凭该旧版本不能判断当前立场或事实。仅作历史背景，不增加引用或修改权限。"
                }),
            omissions = omissions.Select(p => new { current_note_id = Alias(p.Key), reason = p.Value,
                use = "更旧部分未展开；未提供的经过不要补写。" })
        });
        int Bytes() => JsonSerializer.SerializeToUtf8Bytes(new { note_history = Project() }).Length;
        // Reserve the finite omission metadata before trying whole bodies. Even a
        // malformed oversized identity cannot make the new fragment exceed budget.
        foreach (var family in families) omissions.TryAdd(family.Root.EventId, "byte_budget");
        while (Bytes() > NoteHistoryByteLimit && omissions.Count > 0) omissions.Remove(omissions.Keys.Last());
        for (int depth = 0; depth < 2; depth++)
        foreach (var family in families)
        {
            if (stopped.Contains(family.Root.EventId) || family.Ancestors.Count <= depth) continue;
            selected.Add((family.Root, family.Ancestors[depth]));
            if (Bytes() <= NoteHistoryByteLimit) continue;
            selected.RemoveAt(selected.Count - 1);
            omissions[family.Root.EventId] = "byte_budget";
            stopped.Add(family.Root.EventId);
        }
        foreach (var family in families.Where(f => !stopped.Contains(f.Root.EventId)))
            if (omissions.GetValueOrDefault(family.Root.EventId) == "byte_budget") omissions.Remove(family.Root.EventId);
        while (Bytes() > NoteHistoryByteLimit && omissions.Count > 0) omissions.Remove(omissions.Keys.Last());
        // Only the bodies actually supplied acquire a read-permission footprint.
        var finalReadable = CreateNoteHistoryPermissionEvaluator(out var finalFootprint);
        var supplied = selected.Select(p => p.Note).ToArray();
        foreach (var note in supplied) finalReadable("note", note.EventId);
        return new(Project(), supplied, finalFootprint.ToArray());
        }
    }

    /// <summary>
    /// Which of her own thoughts she is shown: the ones most related to what just
    /// happened, plus the newest ones — so an old thought on the same subject is
    /// as reachable as a recent one. Counts are constants here on purpose: the
    /// window can be widened later without changing the input shape.
    /// </summary>
    private const int ReflectionRelatedNotes = 2;
    private const int ReflectionRecentNotes = 2;
    private readonly Queue<string> _recentChatLessons = new();
    private readonly Queue<string> _recentChatDreams = new();
    private readonly Queue<string> _recentChatSparkNotes = new();
    private int _chatSparkTurnCounter;
    private int _lastChatSparkTurn = -1;

    internal void RecordReflectionDeliveryUsage(IReadOnlyList<MemoryTurn> suppliedReflections, string? deliveredText, string? requestId = null)
    {
        if (suppliedReflections == null || suppliedReflections.Count == 0 || string.IsNullOrWhiteSpace(deliveredText))
            return;
        var replyTokens = MemorySemantics.MatchingTokens(deliveredText);
        if (replyTokens.Count == 0)
            return;
        lock (_experienceLock)
        {
            var suppressed = SuppressedExperienceIds();
            var availableIds = _reflectionNotes.Concat(_reflectionLessons)
                .Where(t => IsNativeSupplyAllowed(t) && !suppressed.Contains(t.EventId))
                .Select(t => t.EventId)
                .ToHashSet(StringComparer.Ordinal);
            var records = new List<MemoryTurn>();
            foreach (MemoryTurn thought in suppliedReflections)
            {
                if (thought == null || thought.EventId.Length == 0 || thought.Kind is not ("reflection.note" or "reflection.lesson"))
                    continue;
                if (!availableIds.Contains(thought.EventId) || SuppressedTextMatches(thought.Text))
                    continue;
                var thoughtTokens = MemorySemantics.MatchingTokens(thought.Text);
                var match = MemorySemantics.CompareMatchingTokens(thoughtTokens, replyTokens);
                bool usedInDelivered = thoughtTokens.Count > 0 && (match.Relevant || match.Overlap >= 3);
                var sourceIds = thought.SourceEventIds
                    .Concat(thought.SourceNoteIds)
                    .Append(thought.EventId)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                records.Add(new MemoryTurn
                {
                    EventId = Guid.NewGuid().ToString("N"),
                    SourceEventId = thought.EventId,
                    SourceEventIds = sourceIds,
                    Role = "assistant",
                    Kind = ReflectionDeliveryUsageKind,
                    Tier = "recall",
                    Source = "program_delivery_usage",
                    RequestKind = "main_chat",
                    DeliveryState = "bound",
                    Evidence = usedInDelivered ? "used_in_delivered" : "supplied_only",
                    Text = "",
                    OperationId = requestId ?? "",
                    Timestamp = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                });
            }
            if (records.Count == 0)
                return;
            var backup = _recallLog.ToList();
            _recallLog.AddRange(records);
            TrimRecallLedgerLocked();
            if (!SaveList("v4-native-recall-ledger.json", _recallLog))
            {
                _recallLog.Clear();
                _recallLog.AddRange(backup);
                return;
            }
            NativeEventLog.Trace("reflection.delivery_usage", "mod", "system", "",
                "count=" + records.Count + ";used=" + records.Count(r => r.Evidence == "used_in_delivered"));
        }
    }

    private object DescribeReflectionUsageLocked(string eventId)
    {
        int supplied = 0;
        int used = 0;
        DateTimeOffset? lastSuppliedAt = null;
        foreach (MemoryTurn r in _recallLog)
        {
            if (r.Kind != ReflectionDeliveryUsageKind || !string.Equals(r.SourceEventId, eventId, StringComparison.Ordinal))
                continue;
            supplied++;
            if (string.Equals(r.Evidence, "used_in_delivered", StringComparison.Ordinal))
                used++;
            if (lastSuppliedAt == null || r.Timestamp > lastSuppliedAt.Value)
                lastSuppliedAt = r.Timestamp;
        }
        return new
        {
            supplied_count = supplied,
            used_in_delivered_count = used,
            last_supplied_at = lastSuppliedAt
        };
    }

    private static void RememberSurfaced(Queue<string> ring, string eventId, int capacity = 4)
    {
        if (string.IsNullOrEmpty(eventId) || ring.Contains(eventId, StringComparer.Ordinal)) return;
        while (ring.Count >= capacity) ring.Dequeue();
        ring.Enqueue(eventId);
    }

    internal (MemoryTurn? Consolidation, MemoryTurn? Dream) SelectDayArtifactsForChat(
        IReadOnlyList<MemoryTurn> allowedArtifacts, string? query)
    {
        if (allowedArtifacts == null || allowedArtifacts.Count == 0)
            return (null, null);
        MemoryTurn? consolidation = allowedArtifacts
            .Where(t => t.Kind == DayConsolidationKind)
            .OrderByDescending(t => string.IsNullOrWhiteSpace(query) ? 0d : MemorySemantics.Similarity(query, t.Text))
            .ThenByDescending(t => t.Timestamp)
            .FirstOrDefault();
        var dreams = allowedArtifacts.Where(t => t.Kind == DayDreamKind).ToList();
        if (dreams.Count == 0)
            return (consolidation, null);
        MemoryTurn? dream;
        lock (_experienceLock)
        {
            int seed = Math.Abs(StringComparer.Ordinal.GetHashCode((query ?? "") + "|" + _recentChatDreams.Count));
            dream = dreams
                .OrderByDescending(t =>
                {
                    double sim = string.IsNullOrWhiteSpace(query) ? 0d : MemorySemantics.Similarity(query, t.Text);
                    double recentBonus = dreams.IndexOf(t) >= Math.Max(0, dreams.Count - 3) ? 0.08 : 0d;
                    double repeatPenalty = dreams.Count > 1 && _recentChatDreams.Contains(t.EventId, StringComparer.Ordinal) ? -0.35 : 0d;
                    double jitter = ((seed + Math.Abs(StringComparer.Ordinal.GetHashCode(t.EventId))) % 7) * 0.005;
                    return sim * 1.4 + recentBonus + repeatPenalty + jitter;
                })
                .FirstOrDefault();
            if (dream != null)
                RememberSurfaced(_recentChatDreams, dream.EventId);
        }
        return (consolidation, dream);
    }

    private IReadOnlyList<MemoryTurn> SelectNotesForReflection(IReadOnlyList<MemoryTurn> recent, SemanticRecallResult? semantic = null)
    {
        var timing = Stopwatch.StartNew();
        // Eligibility is owned by the existing getter. Rank its whole qualified
        // pool before applying the finite request-supply limits below.
        IReadOnlyList<MemoryTurn> available = GetReflectionNotes(int.MaxValue).Where(IsNativeSupplyAllowed).ToArray();
        double scanMs = timing.Elapsed.TotalMilliseconds;
        if (available.Count == 0)
            return Array.Empty<MemoryTurn>();
        List<MemoryTurn> newest = available.OrderByDescending(t => t.Timestamp)
            .ThenBy(t => t.EventId, StringComparer.Ordinal)
            .Take(ReflectionRecentNotes).ToList();
        var newestIds = newest.Select(t => t.EventId).ToHashSet(StringComparer.Ordinal);
        string topic = ReflectionTopic(recent);
        // Reserve 1 slot for top topic-related and 1 slot for a distant
        // cross-time spark note from remaining active notes so reflection can
        // connect current life with older enduring motifs instead of only today's window.
        var remaining = available.Where(t => !newestIds.Contains(t.EventId)).ToArray();
        var related = SelectReflectionRelated(remaining, topic, semantic, 1);
        var relatedIds = related.Select(t => t.EventId).ToHashSet(StringComparer.Ordinal);
        var now = DateTimeOffset.UtcNow;
        var topicTokens = MemorySemantics.MatchingTokens(topic);
        MemoryTurn? spark = remaining
            .Where(t => !relatedIds.Contains(t.EventId))
            .OrderByDescending(t =>
            {
                double ageDays = Math.Min(60d, Math.Max(0d, (now - t.Timestamp).TotalDays));
                double maxSimNewest = newest.Select(n => MemorySemantics.Similarity(t.Text, n.Text)).DefaultIfEmpty().Max();
                double novelty = maxSimNewest <= 0.45 ? 0.35 : 0d;
                double cueOverlap = topicTokens.Count > 0 && MemorySemantics.MatchingTokens(t.Text).Any(topicTokens.Contains) ? 0.25 : 0d;
                return novelty + cueOverlap + (ageDays / 60d) * 0.40;
            })
            .ThenBy(t => t.Timestamp)
            .ThenBy(t => t.EventId, StringComparer.Ordinal)
            .FirstOrDefault();
        var chosen = new List<MemoryTurn>(related);
        if (spark != null) chosen.Add(spark);
        chosen.AddRange(newest);
        var result = chosen.OrderBy(t => t.Timestamp).ThenBy(t => t.EventId, StringComparer.Ordinal).ToList();
        TraceReflectionSelection("active", available.Count, result.Count, scanMs, timing.Elapsed.TotalMilliseconds - scanMs);
        return result;
    }

    /// <summary>Related settled records from the whole qualified pool; finite supply.</summary>
    private IReadOnlyList<MemoryTurn> SelectSettledForReflection(IReadOnlyList<MemoryTurn> recent, SemanticRecallResult? semantic = null)
    {
        var timing = Stopwatch.StartNew();
        IReadOnlyList<MemoryTurn> settled = GetSettledNotes(int.MaxValue).Where(IsNativeSupplyAllowed).ToArray();
        double scanMs = timing.Elapsed.TotalMilliseconds;
        if (settled.Count == 0)
            return Array.Empty<MemoryTurn>();
        string topic = ReflectionTopic(recent);
        var result = SelectReflectionRelated(settled, topic, semantic, 2);
        TraceReflectionSelection("settled", settled.Count, result.Count, scanMs, timing.Elapsed.TotalMilliseconds - scanMs);
        return result;
    }

    private List<MemoryTurn> SelectReflectionRelated(IReadOnlyList<MemoryTurn> pool, string topic,
        SemanticRecallResult? semantic, int limit)
    {
        // Zero overlap is not evidence of relevance. Do not fill these discovery
        // slots with arbitrary old notes (and all of their raw sources). The two
        // newest active notes still provide the existing associative opening;
        // typed discovery remains eligible without any lexical overlap or age cut.
        var lexical = pool.Select(t => new { Turn = t, Score = MemorySemantics.Similarity(topic, t.Text) })
            .Where(t => t.Score > 0).OrderByDescending(t => t.Score)
            .ThenByDescending(t => t.Turn.SettledAt ?? t.Turn.Timestamp)
            .ThenBy(t => t.Turn.EventId, StringComparer.Ordinal).Select(t => t.Turn).ToList();
        var typed = SemanticResultMatches(topic, semantic, SemanticDomain.Thoughts)
            ? semantic!.Candidates.Where(c => c.Kind == "note").OrderByDescending(c => c.Score)
                .ThenBy(c => c.EventId, StringComparer.Ordinal)
                .Select(c => pool.FirstOrDefault(t => c.Version == SemanticSourceVersion(t) && SemanticCandidateMatches(c, t, "note")))
                .Where(t => t != null).Cast<MemoryTurn>().DistinctBy(t => t.EventId).ToList() : new List<MemoryTurn>();
        var chosen = new List<MemoryTurn>();
        void Add(MemoryTurn? turn) { if (turn != null && chosen.Count < limit && chosen.All(t => t.EventId != turn.EventId)) chosen.Add(turn); }
        Add(lexical.FirstOrDefault());
        Add(typed.FirstOrDefault(t => chosen.All(n => n.EventId != t.EventId)));
        foreach (var turn in lexical) Add(turn);
        NativeEventLog.Trace("memory.reflection.discovery", "mod", "system", "separate lexical and typed discovery opportunities",
            "qualified=" + pool.Count + ";typed_valid=" + typed.Count + ";selected=" + string.Join(",", chosen.Select(t => t.EventId)));
        return chosen;
    }

    private void TraceReflectionSelection(string kind, int qualified, int selected, double scanMs, double sortMs) =>
        NativeEventLog.Trace("memory.reflection.selection", "mod", "system", "bounded selection from qualified pool",
            "kind=" + kind + ";scan_count=" + _reflectionNotes.Count + ";qualified=" + qualified + ";selected=" + selected
            + ";scan_ms=" + scanMs.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)
            + ";sort_ms=" + sortMs.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>
    /// The last two user-led exchanges from this window only. Receipts identify
    /// displayed ranges; unknown legacy groups require exact source/session links.
    /// </summary>
    private string ReflectionTopic(IReadOnlyList<MemoryTurn> recent)
    {
        var focus = SelectReflectionFocus(recent, includeCurrentInput: false);
        var parts = new List<string>();
        foreach (var user in focus.Where(t => t.Role == "user"))
        {
            parts.Add(user.Text);
            // Display fragmentation must not insert lexical boundaries inside a
            // logical reply. This concatenation is ranking-only, never history.
            string reply = string.Concat(focus.Where(t => t.Role == "assistant" && t.SessionId == user.SessionId
                && t.SourceEventId == user.EventId).Select(t => t.Text));
            if (reply.Length > 0) parts.Add(reply);
        }
        return string.Join(" ", parts);
    }

    internal IReadOnlyList<MemoryTurn> SelectReflectionFocus(IReadOnlyList<MemoryTurn> window, bool includeCurrentInput)
    {
        var visible = window.Where(t => t.Role == "user" && t.DeliveryState == "received"
            || t.Role == "assistant" && t.DeliveryState == "delivered").ToList();
        var users = visible.Where(t => t.Role == "user").ToList();
        var current = includeCurrentInput ? users.LastOrDefault() : null;
        var selectedUsers = users.Where(t => !ReferenceEquals(t, current)).TakeLast(2).ToList();
        if (current != null) selectedUsers.Add(current);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var user in selectedUsers)
        {
            ids.Add(user.EventId);
            var replies = visible.Where(t => t.Role == "assistant" && t.SessionId == user.SessionId
                && t.SourceEventId == user.EventId).ToList();
            if (replies.Count == 0) continue;
            if (ReflectionDeliveries().TryGetValue(user.EventId, out var receipt) && receipt.SessionId == user.SessionId
                && receipt.Outcome != "open" && receipt.ShownChunks > 0
                && receipt.LastShownEventId == replies[^1].EventId)
            {
                int suffix = receipt.LastShownEventId.LastIndexOf(':');
                if (suffix >= 0)
                {
                    string key = receipt.LastShownEventId[..suffix];
                    var shown = Enumerable.Range(0, receipt.ShownChunks).Select(i => key + ":" + i).ToHashSet(StringComparer.Ordinal);
                    foreach (var reply in replies.Where(t => shown.Contains(t.EventId))) ids.Add(reply.EventId);
                    continue;
                }
            }
            // Missing/stale receipts do not prove completeness. Use the final
            // actually displayed reply owner, never adjacency or a generated body.
            string ReplyKey(MemoryTurn turn)
            {
                int suffix = turn.EventId.LastIndexOf(':');
                return suffix >= 0 && int.TryParse(turn.EventId[(suffix + 1)..], out int chunk) && chunk >= 0
                    ? turn.EventId[..suffix] : turn.EventId;
            }
            string lastOwner = ReplyKey(replies[^1]);
            foreach (var reply in replies.Where(t => ReplyKey(t) == lastOwner)) ids.Add(reply.EventId);
        }
        return visible.Where(t => ids.Contains(t.EventId)).ToList();
    }

    /// <summary>
    /// The most recent day-level organisation and dream, at most one of each, as
    /// read-only material for a later reflection. Newest last.
    /// </summary>
    private IReadOnlyList<MemoryTurn> CaptureDayArtifactsForReflection()
    {
        if (!_dreamsEnabled)
            return Array.Empty<MemoryTurn>();
        var artifacts = new List<MemoryTurn>();
        IReadOnlyList<MemoryTurn> visible = GetDayArtifacts(6);
        MemoryTurn? consolidation = visible.LastOrDefault(t => t.Kind == DayConsolidationKind);
        MemoryTurn? dream = visible.LastOrDefault(t => t.Kind == DayDreamKind);
        if (consolidation != null) artifacts.Add(consolidation);
        if (dream != null) artifacts.Add(dream);
        return artifacts;
    }

    public bool ApplyReflectionNote(ReflectionResult result, ReflectionRequestSnapshot request, out string reason)
        => ApplyReflectionNote(result, request, out reason, out _);

    /// <summary>
    /// Same decision as the plain overload; additionally reports what each of the
    /// three chains did, so the runtime can trace note / lesson / relationship
    /// separately without changing any save rule.
    /// </summary>
    public bool ApplyReflectionNote(ReflectionResult result, ReflectionRequestSnapshot request,
        out string reason, out ReflectionChainOutcome outcome)
        => ApplyReflectionNote(result, request, null, out reason, out outcome);

    private bool ApplyReflectionNote(ReflectionResult result, ReflectionRequestSnapshot request,
        ReflectionChainOutcome? progress, out string reason, out ReflectionChainOutcome outcome)
    {
        string localReason = "selection_changed";
        var localOutcome = new ReflectionChainOutcome(false, localReason, false, localReason,
            false, localReason, false, localReason, request.Notes.Count, request.Lessons.Count,
            request.Readings.Count, request.DayArtifacts.Count, request.Settled.Count);
        bool saved = false;
        if (!ReflectionGeneratedPublicCurrent(request)) localReason = "generated_public_context_changed";
        else if (result.SelectionSnapshot == null || result.SelectionSnapshot.Generation == request.SelectionGeneration)
            TryUseB1SelectionGeneration(request.SelectionGeneration, () =>
            {
                saved = ApplyReflectionNoteCore(result, request, progress, out localReason, out localOutcome);
                return true;
            });
        reason = localReason; outcome = localOutcome; return saved;
    }

    private bool ApplyReflectionNoteCore(ReflectionResult result, ReflectionRequestSnapshot request,
        ReflectionChainOutcome? progress, out string reason, out ReflectionChainOutcome outcome)
    {
        lock (_experienceLock)
        {
            ShortAliasTable.ResolveReflectionResultAliases(result, request.ShortAliases);
            bool noteAttempted = progress?.NoteProposed ?? result?.Note != null;
            bool settleAttempted = progress?.SettleProposed ?? Clean(result?.Note?.Settles ?? "").Length > 0;
            bool lessonAttempted = progress?.LessonProposed ?? result?.Lesson != null;
            bool readingAttempted = progress?.ReadingProposed ?? result?.Relationship != null;
            string? noteDone = CompletedChain(progress?.NoteReason) ??
                (_reflectionNotes.Any(t => t.OperationId == request.RequestId
                    || t.ReflectionSettlementOperationId == request.RequestId) ? "already_applied" : null);
            string? lessonDone = CompletedChain(progress?.LessonReason) ??
                (_reflectionLessons.Any(t => t.OperationId == request.RequestId
                    || t.ReflectionSettlementOperationId == request.RequestId) ? "already_applied" : null);
            string? readingDone = CompletedChain(progress?.ReadingReason) ??
                (_relationshipReadings.Any(t => t.OperationId == request.RequestId) ? "already_applied" : null);
            // Completed chains (including an accepted all-null result) have no
            // remaining origin-dependent mutation. Preserve their receipt path.
            if ((noteDone == null && noteAttempted || lessonDone == null && lessonAttempted || readingDone == null && readingAttempted)
                && (!ReflectionRequestOriginCurrent(request) || !ReflectionPurposesCurrent(request)))
            {
                reason = "reflection_request_origin_changed";
                outcome = new(noteAttempted, noteAttempted ? reason : "no_new_note",
                    settleAttempted, settleAttempted ? reason : "no_settle",
                    lessonAttempted, lessonAttempted ? reason : "no_lesson",
                    readingAttempted, readingAttempted ? reason : "no_reading",
                    request.Notes.Count, request.Lessons.Count, request.Readings.Count,
                    request.DayArtifacts.Count, request.Settled.Count);
                return false;
            }
            if ((noteDone == null && noteAttempted || lessonDone == null && lessonAttempted || readingDone == null && readingAttempted)
                && (!NoteHistoryStillReadable(request, out string historyReason)
                    || !RelationshipHistoryStillReadable(request, out historyReason)))
            {
                reason = historyReason;
                outcome = new(noteAttempted, noteAttempted ? reason : "no_new_note",
                    settleAttempted, settleAttempted ? reason : "no_settle",
                    lessonAttempted, lessonAttempted ? reason : "no_lesson",
                    readingAttempted, readingAttempted ? reason : "no_reading",
                    request.Notes.Count, request.Lessons.Count, request.Readings.Count,
                    request.DayArtifacts.Count, request.Settled.Count);
                return false;
            }
            string noteReason = noteDone ?? "no_new_note", lessonReason = lessonDone ?? "no_lesson", readingReason = readingDone ?? "no_reading";
            bool noteSaved = noteDone == null && ApplyNoteCore(result?.Note, request, out noteReason);
            bool lessonSaved = lessonDone == null && ApplyLessonCore(result?.Lesson, request, out lessonReason);
            bool readingSaved = readingDone == null && ApplyRelationshipReadingCore(result?.Relationship, request, out readingReason);
            // The note proposal carries both a possible new thought and a possible
            // decision to close one; report them separately.
            string settleResult = !settleAttempted ? "no_settle"
                : _reflectionNotes.Any(t => t.ReflectionSettlementOperationId == request.RequestId) ? "settled"
                : noteReason;
            outcome = new ReflectionChainOutcome(
                noteAttempted, noteReason,
                settleAttempted, settleResult,
                lessonAttempted, lessonReason,
                readingAttempted, readingReason,
                request?.Notes.Count ?? 0, request?.Lessons.Count ?? 0, request?.Readings.Count ?? 0,
                request?.DayArtifacts.Count ?? 0, request?.Settled.Count ?? 0);
            if (noteSaved || lessonSaved || readingSaved)
            {
                reason = "saved";
                return true;
            }
            if (noteAttempted)
            {
                reason = noteReason;
                return false;
            }
            if (lessonAttempted)
            {
                reason = lessonReason;
                return false;
            }
            if (readingAttempted)
            {
                reason = readingReason;
                return false;
            }
            reason = "no_new_note";
            return false;
        }
    }

    private static string? CompletedChain(string? reason) =>
        reason != null && !ReflectionWindow.RetainsForRetry(reason) ? reason : null;

    internal static string CleanDreamDisplayText(string? raw)
    {
        string text = Clean(raw);
        if (text.StartsWith("梦/想象，不是事实或新事件：", StringComparison.Ordinal))
            text = text["梦/想象，不是事实或新事件：".Length..].TrimStart();
        if (text.StartsWith("（梦/想象，不是真实发生的事）", StringComparison.Ordinal))
            text = text["（梦/想象，不是真实发生的事）".Length..].TrimStart();
        return text;
    }

    private bool ApplyNoteCore(ReflectionNoteProposal? proposal, ReflectionRequestSnapshot request, out string reason)
    {
        lock (_experienceLock)
        {
            if (proposal == null) { reason="no_new_note"; return false; }
            string text = Clean(proposal.Text);
            bool settling = Clean(proposal.Settles).Length > 0;
            string settleReason = Clean(proposal.SettleReason);
            // A pass may write a thought, close one, or both — but "nothing at all"
            // is not a note. The basis of a written thought is unchanged: at least
            // one raw utterance (spec 4.5).
            if (!_longTermEnabled || text.Length > 300)
            { reason="invalid_note"; return false; }
            if (text.Length == 0 && !settling)
            { reason="invalid_note"; return false; }
            if (text.Length == 0 && proposal.Revises.Length > 0)
            { reason="invalid_revision"; return false; }
            if (text.Length > 0 && proposal.SourceEventIds.Length == 0)
            { reason="invalid_note"; return false; }
            if (_reflectionNotes.Any(t => t.OperationId == request.RequestId))
            { reason="already_applied"; return false; }
            MemoryTurn? settleTarget = null;
            if (settling)
            {
                if (settleReason.Length == 0 || settleReason.Length > 160)
                { reason="settle_without_reason"; return false; }
                if (_reflectionNotes.Any(t => t.EventId == proposal.Settles
                    && IsSettledLifecycle(t) && t.SettleReason == settleReason))
                { reason="already_settled"; return false; }
                settleTarget = _reflectionNotes.SingleOrDefault(
                    t => t.EventId == proposal.Settles && IsActiveLifecycle(t));
                if (settleTarget == null || !request.Notes.Any(t => t.EventId == settleTarget.EventId))
                { reason="stale_settle"; return false; }
            }
            string[] sources = proposal.SourceEventIds.Distinct(StringComparer.Ordinal).ToArray();
            var captured = request.Evidence.ToDictionary(t => t.EventId,StringComparer.Ordinal);
            var current = ReadExperiences().ToDictionary(t => t.EventId,StringComparer.Ordinal);
            if (sources.Any(id => !captured.TryGetValue(id,out MemoryTurn? prior)
                || !current.TryGetValue(id,out MemoryTurn? now) || prior.Text != now.Text || prior.Role != now.Role
                || prior.Origin != now.Origin || prior.DeliveryState != now.DeliveryState))
            { reason="unavailable_source"; return false; }
            // Her own day-level artifacts may be cited as what she re-read this
            // thought through, but only the ones she was actually shown, and never
            // instead of a raw utterance: the basis of a thought stays a real
            // exchange, so reflection cannot amplify itself (spec 4.5).
            string[] dayIds = (proposal.SourceDayIds ?? Array.Empty<string>())
                .Where(id => id.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
            HashSet<string> capturedDays = request.DayArtifacts.Select(t => t.EventId)
                .ToHashSet(StringComparer.Ordinal);
            if (dayIds.Any(id => !capturedDays.Contains(id)))
            { reason="unavailable_day_artifact"; return false; }
            if (dayIds.Any(id => !AvailableDayArtifactIds().Contains(id)))
            { reason="unavailable_day_artifact"; return false; }
            MemoryTurn? parent = null;
            if (proposal.Revises.Length > 0)
            {
                // A settled thought can be picked up again: revising it reopens the
                // chain instead of pretending it never existed.
                parent = _reflectionNotes.SingleOrDefault(t => t.EventId == proposal.Revises
                    && (IsActiveLifecycle(t) || IsSettledLifecycle(t)));
                if (parent == null
                    || !(request.Notes.Any(t => t.EventId == parent.EventId)
                        || request.Settled.Any(t => t.EventId == parent.EventId)
                        || request.RevisionTargets.Any(t => t.EventId == parent.EventId && t.Kind == "reflection.note")))
                { reason="stale_revision"; return false; }
                // The repair route is not a new read permission: if the parent's recorded
                // basis was withdrawn or purged after capture, its old body may not be
                // reactivated through a same-text revision either.
                if (!RecordPermitted(parent)) { reason="target_withdrawn"; return false; }
                // SourceEventId preserves the parent history; only this proposal's
                // explicit raw/day support belongs to the replacement.
                // The replacement declares its own day context. The historical
                // version parent is a revision link, not inherited current support.
            }
            if (parent == null && text.Length > 0 && _reflectionNotes.Any(t => IsActiveLifecycle(t) && (t.Text == text
                || MemorySemantics.NormalizeComparableText(t.Text) == MemorySemantics.NormalizeComparableText(text)
                || (MemorySemantics.Similarity(t.Text, text) >= 0.75
                    && MemorySemantics.MatchingTokens(t.Text).Count(MemorySemantics.MatchingTokens(text).Contains) >= 3))))
            { reason="duplicate_note"; return false; }
            if (!ReflectionVersionsMatch(request, sources, dayIds, new[] { proposal.Revises, proposal.Settles }))
            { reason="source_version_changed"; return false; }
            if (text.Length > 0 && HasUnincorporatedCorrection(new MemoryTurn { SourceEventIds = sources }))
            { reason="correction_not_incorporated"; return false; }
            if (parent != null && IsActiveLifecycle(parent) && !settling && text == parent.Text
                && SameSupport(sources, parent.SourceEventIds) && SameSupport(dayIds, parent.SourceDayIds))
            { reason="no_change"; return false; }
            MemoryTurn? note = null;
            string parentState = parent?.LifecycleState ?? "";
            string settleState = settleTarget?.LifecycleState ?? "";
            string priorSettleReason = settleTarget?.SettleReason ?? "";
            DateTimeOffset? priorSettledAt = settleTarget?.SettledAt;
            string priorSettlementOperation = settleTarget?.ReflectionSettlementOperationId ?? "";
            if (text.Length > 0)
            {
                note = new MemoryTurn
                {
                    EventId=Guid.NewGuid().ToString("N"), RecallId=parent?.RecallId ?? Guid.NewGuid().ToString("N"),
                    Revision=(parent?.Revision ?? 0)+1, OperationId=request.RequestId,
                    SourceEventId=proposal.Revises, SourceEventIds=sources, SourceDayIds=dayIds,
                    Role="assistant", Kind="reflection.note", Origin="reflection", Source="model_reflection",
                    Text=text, Validity="inferred", LifecycleState="active", SessionId=_sessionId
                };
                _reflectionNotes.Add(note);
            }
            if (parent != null) parent.LifecycleState="superseded";
            if (settleTarget != null)
            {
                // A revision wins for the same target. The requested closing
                // reason remains history, never a way to resurrect old support.
                settleTarget.LifecycleState = ReferenceEquals(settleTarget, parent) ? "superseded" : "settled";
                settleTarget.SettleReason = settleReason;
                settleTarget.SettledAt = DateTimeOffset.UtcNow;
                settleTarget.ReflectionSettlementOperationId = request.RequestId;
            }
            if (!SaveList("v4-reflection-notes.json",_reflectionNotes))
            {
                if (note != null) _reflectionNotes.Remove(note);
                if (parent != null) parent.LifecycleState = parentState;
                if (settleTarget != null)
                {
                    settleTarget.LifecycleState = settleState;
                    settleTarget.SettleReason = priorSettleReason;
                    settleTarget.SettledAt = priorSettledAt;
                    settleTarget.ReflectionSettlementOperationId = priorSettlementOperation;
                }
                reason="persistence_failed"; return false;
            }
            reason = text.Length > 0 ? "saved" : "settled";
            return true;
        }
    }

    private bool ResolveReflectionNoteCitations(string[]? rawCited, ReflectionRequestSnapshot request,
        string emptyReason, string missingSourceReason, bool allowSamePassFallback,
        out string[] resolvedCited, out string[] sources, out string[] preExistingCited, out string reason)
    {
        resolvedCited = Array.Empty<string>();
        sources = Array.Empty<string>();
        preExistingCited = Array.Empty<string>();
        reason = "";
        string[] cited = (rawCited ?? Array.Empty<string>())
            .Where(id => id.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        var available = GetReflectionNotes(int.MaxValue).Where(IsNativeSupplyAllowed)
            .ToDictionary(t => t.EventId, StringComparer.Ordinal);
        MemoryTurn? samePassNote = _reflectionNotes.FirstOrDefault(t => t.OperationId == request.RequestId && IsActiveLifecycle(t));
        MemoryTurn? samePassSettled = _reflectionNotes.FirstOrDefault(t =>
            t.ReflectionSettlementOperationId == request.RequestId && IsSettledLifecycle(t)
            && IsNativeSupplyAllowed(t) && RecordPermitted(t));
        if (cited.Length == 0 && allowSamePassFallback && samePassNote != null && available.ContainsKey(samePassNote.EventId))
            cited = new[] { samePassNote.EventId };
        if (cited.Length == 0)
        { reason = emptyReason; return false; }
        var shown = request.Notes.ToDictionary(t => t.EventId, StringComparer.Ordinal);
        var resolved = new List<string>(cited.Length);
        var citedTurns = new List<MemoryTurn>(cited.Length);
        var samePassResolved = new HashSet<string>(StringComparer.Ordinal);
        foreach (string id in cited)
        {
            if (samePassNote != null && available.TryGetValue(samePassNote.EventId, out var currentNewNote)
                && (id == currentNewNote.EventId || (!available.ContainsKey(id) && currentNewNote.SourceEventId == id && shown.ContainsKey(id))))
            {
                resolved.Add(currentNewNote.EventId);
                samePassResolved.Add(currentNewNote.EventId);
                citedTurns.Add(currentNewNote);
                continue;
            }
            if (samePassSettled != null && id == samePassSettled.EventId && shown.ContainsKey(id))
            {
                resolved.Add(id);
                samePassResolved.Add(id);
                citedTurns.Add(samePassSettled);
                continue;
            }
            if (!shown.ContainsKey(id))
            { reason = "note_not_in_request"; return false; }
            if (!available.TryGetValue(id, out var existingNote))
            { reason = "unavailable_note"; return false; }
            resolved.Add(id);
            citedTurns.Add(existingNote);
        }
        resolvedCited = resolved.Distinct(StringComparer.Ordinal).ToArray();
        sources = citedTurns.SelectMany(t => t.SourceEventIds).Distinct(StringComparer.Ordinal).ToArray();
        if (sources.Length == 0)
        { reason = missingSourceReason; return false; }
        preExistingCited = resolvedCited.Where(id => !samePassResolved.Contains(id)).ToArray();
        return true;
    }

    /// <summary>
    /// Applies an optional lesson. It must cite thoughts the model was actually
    /// shown, it must keep their raw sources, and a revision may correct an interpretation of the same sources.
    /// </summary>
    private bool ApplyLessonCore(ReflectionLessonProposal? proposal, ReflectionRequestSnapshot request, out string reason)
    {
        reason = "no_lesson";
        if (proposal == null)
            return false;
        string text = Clean(proposal.Text);
        bool settling = Clean(proposal.Settles).Length > 0;
        string settleReason = Clean(proposal.SettleReason);
        if (!_longTermEnabled || text.Length > 240)
        { reason="invalid_lesson"; return false; }
        if (text.Length == 0 && !settling)
        { reason="invalid_lesson"; return false; }
        if (text.Length == 0 && proposal.Revises.Length > 0)
        { reason="invalid_lesson"; return false; }
        if (_reflectionLessons.Any(t => (t.OperationId == request.RequestId || t.ReflectionSettlementOperationId == request.RequestId) && t.Kind == "reflection.lesson"))
        { reason="already_applied"; return false; }
        MemoryTurn? settleTarget = null;
        if (settling)
        {
            if (settleReason.Length == 0 || settleReason.Length > 160)
            { reason="settle_without_reason"; return false; }
            if (_reflectionLessons.Any(t => t.EventId == proposal.Settles
                && IsSettledLifecycle(t) && t.SettleReason == settleReason))
            { reason="already_settled"; return false; }
            settleTarget = _reflectionLessons.SingleOrDefault(
                t => t.EventId == proposal.Settles && IsActiveLifecycle(t));
            if (settleTarget == null || !(request.Lessons.Concat(request.RevisionTargets.Where(t => t.Kind == "reflection.lesson"))
                .Any(t => t.EventId == settleTarget.EventId)))
            { reason="stale_lesson"; return false; }
        }
        string[] cited = Array.Empty<string>();
        string[] sources = Array.Empty<string>();
        string[] preExistingCited = Array.Empty<string>();
        if (text.Length > 0 && !ResolveReflectionNoteCitations(proposal.SourceNoteIds, request,
                "lesson_without_note", "lesson_without_source", allowSamePassFallback: true,
                out cited, out sources, out preExistingCited, out reason))
            return false;
        MemoryTurn? parent = null;
        if (proposal.Revises.Length > 0)
        {
            parent = _reflectionLessons.FirstOrDefault(
                t => t.EventId == proposal.Revises && (IsActiveLifecycle(t) || IsSettledLifecycle(t)));
            if (parent == null || !(request.Lessons.Concat(request.RevisionTargets.Where(t => t.Kind == "reflection.lesson"))
                .Any(t => t.EventId == parent.EventId)))
            { reason="stale_lesson"; return false; }
            if (!RecordPermitted(parent)) { reason="target_withdrawn"; return false; }

            // SourceEventId keeps the historical parent; only current explicit citations support this revision.
        }
        if (parent == null && text.Length > 0 && _reflectionLessons.Any(t => IsActiveLifecycle(t)
                && MemorySemantics.Similarity(t.Text, text) >= 0.75))
        { reason="duplicate_lesson"; return false; }
        if (!ReflectionVersionsMatch(request, sources, Array.Empty<string>(), preExistingCited, proposal.Revises))
        { reason="source_version_changed"; return false; }
        if (settling && proposal.Settles != proposal.Revises
            && !ReflectionVersionsMatch(request, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), proposal.Settles))
        { reason="source_version_changed"; return false; }
        if (parent != null && IsActiveLifecycle(parent) && !settling && text == parent.Text && SameSupport(cited, parent.SourceNoteIds))
        { reason="no_change"; return false; }
        MemoryTurn? lesson = null;
        string parentState = parent?.LifecycleState ?? "";
        string settleState = settleTarget?.LifecycleState ?? "";
        string priorSettleReason = settleTarget?.SettleReason ?? "";
        DateTimeOffset? priorSettledAt = settleTarget?.SettledAt;
        string priorSettlementOperation = settleTarget?.ReflectionSettlementOperationId ?? "";
        if (text.Length > 0)
        {
            lesson = new MemoryTurn
            {
                EventId=Guid.NewGuid().ToString("N"), RecallId=parent?.RecallId ?? Guid.NewGuid().ToString("N"),
                Revision=(parent?.Revision ?? 0)+1, OperationId=request.RequestId,
                SourceEventId=proposal.Revises, SourceNoteIds=cited, SourceEventIds=sources,
                Role="assistant", Kind="reflection.lesson", Origin="reflection", Source="model_reflection",
                Text=text, Validity="inferred", LifecycleState="active", SessionId=_sessionId
            };
            _reflectionLessons.Add(lesson);
        }
        if (parent != null) parent.LifecycleState="superseded";
        if (settleTarget != null)
        {
            settleTarget.LifecycleState = ReferenceEquals(settleTarget, parent) ? "superseded" : "settled";
            settleTarget.SettleReason = settleReason;
            settleTarget.SettledAt = DateTimeOffset.UtcNow;
            settleTarget.ReflectionSettlementOperationId = request.RequestId;
        }
        if (!SaveList("v4-reflection-lessons.json",_reflectionLessons))
        {
            if (lesson != null) _reflectionLessons.Remove(lesson);
            if (parent != null) parent.LifecycleState = parentState;
            if (settleTarget != null)
            {
                settleTarget.LifecycleState = settleState;
                settleTarget.SettleReason = priorSettleReason;
                settleTarget.SettledAt = priorSettledAt;
                settleTarget.ReflectionSettlementOperationId = priorSettlementOperation;
            }
            reason="persistence_failed"; return false;
        }
        reason = text.Length > 0 ? "lesson_saved" : "settled";
        return true;
    }

    /// <summary>
    /// Applies an optional reading of the relationship. It must rest on thoughts
    /// the model was shown, keeps their raw sources, can be revised on the same evidence,
    /// and never becomes user evidence.
    /// </summary>
    private bool ApplyRelationshipReadingCore(RelationshipReadingProposal? proposal,
        ReflectionRequestSnapshot request, out string reason)
    {
        reason = "no_reading";
        if (proposal == null)
            return false;
        string text = Clean(proposal.Text);
        if (!_longTermEnabled || text.Length == 0 || text.Length > 200)
        { reason = "invalid_reading"; return false; }
        if (_relationshipReadings.Any(t => t.OperationId == request.RequestId
                && t.Kind == RelationshipReadingKind))
        { reason = "already_applied"; return false; }
        if (!ResolveReflectionNoteCitations(proposal.SourceNoteIds, request,
                "reading_without_note", "reading_without_source", allowSamePassFallback: false,
                out string[] cited, out string[] sources, out string[] preExistingCited, out reason))
            return false;
        MemoryTurn? parent = null;
        if (proposal.Revises.Length > 0)
        {
            parent = _relationshipReadings.FirstOrDefault(
                t => t.EventId == proposal.Revises && IsActiveLifecycle(t));
            if (parent == null || !(request.Readings.Concat(request.RevisionTargets.Where(t => t.Kind == RelationshipReadingKind))
                .Any(t => t.EventId == parent.EventId)))
            { reason = "stale_reading"; return false; }
            if (!RecordPermitted(parent)) { reason = "target_withdrawn"; return false; }

            // SourceEventId keeps the historical parent; only current explicit citations support this revision.
        }
        if (parent == null && _relationshipReadings.Any(t => IsActiveLifecycle(t)
                && MemorySemantics.Similarity(t.Text, text) >= 0.75))
        { reason = "duplicate_reading"; return false; }
        if (!ReflectionVersionsMatch(request, sources, Array.Empty<string>(), preExistingCited, proposal.Revises))
        { reason="source_version_changed"; return false; }
        if (parent != null && text == parent.Text && SameSupport(cited, parent.SourceNoteIds))
        { reason="no_change"; return false; }
        var reading = new MemoryTurn
        {
            EventId = Guid.NewGuid().ToString("N"),
            RecallId = parent?.RecallId ?? Guid.NewGuid().ToString("N"),
            Revision = (parent?.Revision ?? 0) + 1,
            OperationId = request.RequestId,
            SourceEventId = proposal.Revises,
            SourceNoteIds = cited,
            SourceEventIds = sources,
            Role = "assistant",
            Kind = RelationshipReadingKind,
            Origin = "reflection",
            Source = "model_reflection",
            Text = text,
            Validity = "inferred",
            LifecycleState = "active",
            SessionId = _sessionId
        };
        if (parent != null) parent.LifecycleState = "superseded";
        _relationshipReadings.Add(reading);
        if (!SaveList("v4-relationship-readings.json", _relationshipReadings))
        {
            _relationshipReadings.Remove(reading);
            if (parent != null) parent.LifecycleState = "active";
            reason = "persistence_failed"; return false;
        }
        reason = "reading_saved";
        return true;
    }

    public ThoughtRecallBoundary CaptureThoughtRecallBoundary(string query, bool reflectionSupplied = true,SemanticRecallResult? semantic=null) =>
        new(MemorySemantics.IsHistoricalThoughtQuery(query)
            && (!reflectionSupplied || SelectThoughtVersions(query,semantic).Count == 0), query);

    private IReadOnlyList<MemoryTurn> SelectReflectionNotes(string query)
    {
        string topic = ThoughtTopic(query);
        return GetReflectionNotes(24)
            .Select(t => new { Turn=t, Score=MemorySemantics.Similarity(t.Text,topic) })
            // This path only runs for an explicit question about her own thoughts,
            // so the ordinary-chat relevance floor does not apply here.
            .Where(t => topic.Length < 2 || t.Score > 0)
            .OrderByDescending(t => t.Score).ThenByDescending(t => t.Turn.Timestamp)
            .Take(2).Select(t => t.Turn).ToList();
    }

    private static string ThoughtTopic(string? query) =>
        Regex.Replace(query ?? "",
            @"(?:莉莉丝|对于|关于|当时|那时|刚才|之前|以前|现在|曾经|到底|怎么想|怎么看|想法|看法|什么|这件事|怎么|的|你|我|了|说|是|有|吗|呢|[\s，。！？?,.!])",
            "");

    /// <summary>Chains (old + current version) shown for one explicit question.</summary>
    private const int ThoughtVersionChains = 2;
    /// <summary>Upper bound on version lines, so the block stays small.</summary>
    private const int ThoughtVersionMax = 4;

    /// <summary>
    /// Versions of her thoughts that answer a question about what she used to
    /// think: the current ones plus the closed and replaced ones that carry the
    /// history of the same subject. A version always comes with its counterpart
    /// (the parent it revised, or the revision that replaced it) so the model can
    /// tell her current understanding from the version it replaced. Everything
    /// still has to cite material that is available; deleted or suppressed
    /// sources keep the version out of the block.
    /// </summary>
    private IReadOnlyList<MemoryTurn> SelectThoughtVersions(string query,SemanticRecallResult? semantic=null)
    {
        var pool = new List<MemoryTurn>();
        pool.AddRange(GetReflectionNotes(int.MaxValue));
        pool.AddRange(GetSettledNotes(int.MaxValue));
        pool.AddRange(GetSupersededNotes(int.MaxValue));
        pool.AddRange(GetReflectionLessons(int.MaxValue));
        pool=pool.Where(IsNativeSupplyAllowed).DistinctBy(t=>t.EventId).ToList();
        if (pool.Count == 0)
            return Array.Empty<MemoryTurn>();
        string topic = ThoughtTopic(query);
        var ranked = pool
            .Select(turn => new { Turn = turn, Score = MemorySemantics.Similarity(turn.Text, topic) })
            .Where(item => topic.Length < 2 || item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Turn.Timestamp)
            .Select(item => item.Turn)
            .ToList();
        var typed=SemanticResultMatches(query,semantic,SemanticDomain.Thoughts)
            ? semantic!.Candidates.Where(c=>c.Kind is "note" or "lesson").OrderByDescending(c=>c.Score)
                .Select(c=>pool.FirstOrDefault(t=>SemanticCandidateMatches(c,t,B1Kind(t))))
                .Where(t=>t!=null).Cast<MemoryTurn>().Where(t=>SemanticThoughtQueryScope(query,t)).ToList():new List<MemoryTurn>();
        ranked=ranked.Take(1).Concat(typed.Take(ThoughtVersionChains)).Concat(ranked.Skip(1))
            .DistinctBy(t=>t.EventId).ToList();
        PreparationTiming.Current?.Value("semantic_thought_candidates",typed.Count);
        PreparationTiming.Current?.Value("semantic_thought_rejected",Math.Max(0,(semantic?.Candidates.Count(c=>c.Kind is "note" or "lesson")??0)-typed.Count));
        var chosen = new List<MemoryTurn>();
        void Add(MemoryTurn? turn)
        {
            if (turn != null && chosen.Count < ThoughtVersionMax
                && chosen.All(t => t.EventId != turn.EventId))
                chosen.Add(turn);
        }
        foreach (MemoryTurn turn in ranked.Take(ThoughtVersionChains))
        {
            Add(turn);
            Add(ResolveVersionParent(turn, pool));
            Add(ResolveVersionChild(turn, pool));
        }
        return chosen.OrderBy(t => t.Timestamp).ToList();
    }

    private bool SemanticThoughtQueryScope(string query,MemoryTurn thought)
    {
        var ids=MemorySemantics.RecallIdentifiers(query);
        var quotes=UtteranceAttribution.LiteralQuotes(query);
        if(ids.Length==0&&quotes.Length==0)return true;
        string available=thought.Text+" "+string.Join(" ",ReadExperiences()
            .Where(t=>thought.SourceEventIds.Contains(t.EventId)).Select(t=>t.Text));
        return ids.All(id=>MemorySemantics.ContainsRecallIdentifier(available,id))
            &&quotes.All(q=>available.Contains(q,StringComparison.Ordinal));
    }

    /// <summary>The version this one was written from, when it cites a thought.</summary>
    private static MemoryTurn? ResolveVersionParent(MemoryTurn turn, IReadOnlyList<MemoryTurn> pool) =>
        turn.SourceEventId.Length == 0
            ? null
            : pool.FirstOrDefault(t => t.EventId == turn.SourceEventId);

    /// <summary>The newest version that revised this one, when there is one.</summary>
    private static MemoryTurn? ResolveVersionChild(MemoryTurn turn, IReadOnlyList<MemoryTurn> pool) =>
        pool.Where(t => t.SourceEventId == turn.EventId)
            .OrderByDescending(t => t.Revision).ThenByDescending(t => t.Timestamp)
            .FirstOrDefault();

    /// <summary>
    /// One natural-language version line for main chat recall. Retains time,
    /// lifecycle status and source attribution without dumping machine field names
    /// or raw 32-char UUIDs, while matching CaptureRevisitBaseTargets verbatim.
    /// </summary>
    private string DescribeThoughtVersion(MemoryTurn note, IReadOnlyList<MemoryTurn> pool)
    {
        string time = note.Timestamp == default ? "" : note.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        var tags = new List<string>();
        if (note.Kind == "reflection.lesson") tags.Add("互动手艺");
        if (string.Equals(note.LifecycleState, "superseded", StringComparison.Ordinal))
            tags.Add("已被后续想法更替");
        else if (IsSettledLifecycle(note))
            tags.Add("曾收起" + (note.SettleReason.Length > 0 ? "：" + note.SettleReason : ""));
        if (note.Revision > 1) tags.Add("由早先想法修订");
        string meta = string.Join("；", tags);
        string head = meta.Length > 0
            ? (time.Length > 0 ? $"[{time} | {meta}] " : $"[{meta}] ")
            : (time.Length > 0 ? $"[{time}] " : "");
        string sourceContext = DescribeSourceAttribution(note);
        return head + note.Text + (sourceContext.Length > 0 ? " " + sourceContext : "");
    }

    public string BuildReflectionRecallContext(string query, IEnumerable<string>? suppliedEventIds = null,
        Action<IReadOnlyList<MemoryTurn>>? onSupplied = null, bool allowChatProjection = false,
        Action<ReflectionProjectionObservation>? onObservation = null,
        IReadOnlyDictionary<string, DirectRecallRank>? directRanks = null,SemanticRecallResult? semantic=null,
        IEnumerable<string>? suppliedHistoryEventIds = null)
    {
        // Explicit questions about her thoughts receive the full block, including
        // the raw sources. Ordinary conversation only receives a previous thought
        // when the experiences it cites are themselves relevant to what the player
        // just said. Factual recall never gains a source of supposed user statements.
        if (!MemorySemantics.IsThoughtQuery(query))
            return allowChatProjection
                ? BuildChatReflectionContext(query, suppliedEventIds, onSupplied, onObservation, directRanks,
                    suppliedHistoryEventIds, semantic)
                : "";
        IReadOnlyList<MemoryTurn> relevant = FilterMentionSupply(SelectThoughtVersions(query,semantic));
        PreparationTiming.Current?.Value("semantic_thought_supplied",relevant.Count);
        if (relevant.Count == 0 && semantic!=null && (semantic.Unavailable.Length>0||!semantic.IndexComplete))
            return "[旧想法检索状态] 语义补充检索未完成；当前可用材料不足以断言过去没有相关想法记录。";
        if (relevant.Count == 0)
            return "[旧想法检索结果] 没有找回你当时留下的想法记录。原始对话只能证明当时说了什么。可以从现在的视角重新理解，但不能把现在的推想说成当时确实想过的内容。";
        var builder = new StringBuilder(ThoughtBlockHeader + "：含现行、曾收起或已被更替的版本，是当时记下的理解，不是玩家证言]\n");
        builder.AppendLine("这些是当时回看时写下的理解（若有「曾收起」或「已被更替」仅表示当时的记录状态，你仍可坚持、延伸、重拾或推翻改口；未展开的对应原话附在后面）。");
        foreach (MemoryTurn note in relevant)
            builder.AppendLine(DescribeThoughtVersion(note, relevant));
        var ids = relevant.SelectMany(t => t.SourceEventIds).ToHashSet(StringComparer.Ordinal);
        ids.ExceptWith(suppliedEventIds ?? Array.Empty<string>());
        var raw = ReadExperiences().Where(t => ids.Contains(t.EventId) && IsNativeSupplyAllowed(t)).ToList();
        builder.AppendLine(BuildExperienceContext(raw));
        onSupplied?.Invoke(relevant.Concat(raw).ToList());
        return builder.ToString();
    }

    /// <summary>
    /// Light projection for ordinary conversation. Her own earlier understanding is
    /// offered next to the experience it came from, labelled as her thought rather
    /// than the player's words, and explicitly optional to mention.
    /// </summary>
    private string BuildChatReflectionContext(string? query,
        IEnumerable<string>? suppliedEventIds,
        Action<IReadOnlyList<MemoryTurn>>? onSupplied,
        Action<ReflectionProjectionObservation>? onObservation,
        IReadOnlyDictionary<string, DirectRecallRank>? directRanks,
        IEnumerable<string>? suppliedHistoryEventIds, SemanticRecallResult? semantic)
    {
        if (string.IsNullOrWhiteSpace(query))
            return "";
        var supplied = (suppliedEventIds ?? Array.Empty<string>()).ToHashSet(StringComparer.Ordinal);
        var scope = MemorySemantics.ParseExperienceRecallQuery(query);
        string[] identifiers = MemorySemantics.RecallIdentifiers(query);
        string[] quotes = UtteranceAttribution.LiteralQuotes(query);
        bool strictLookupQuery = scope != null || identifiers.Length > 0 || quotes.Length > 0
            || MemorySemantics.IsLowInfoGreetingOrFiller(query);
        if (semantic?.RelevancePlan != null && semantic.RelevancePlan.Selected.Any(i => i.Thought != null) != true)
            return "";
        if (semantic?.RelevancePlan == null && supplied.Count == 0 && strictLookupQuery)
            return "";
        var visible = (suppliedHistoryEventIds ?? Array.Empty<string>()).Where(supplied.Contains)
            .ToHashSet(StringComparer.Ordinal);
        var raw = ReadExperiences().Where(t => supplied.Contains(t.EventId))
            .ToDictionary(t => t.EventId, StringComparer.Ordinal);
        bool InScope(string id) => raw.TryGetValue(id, out var source)
            && (scope == null || scope.AllowsSourceScope(source))
            && identifiers.All(part => MemorySemantics.ContainsRecallIdentifier(source.Text, part))
            && quotes.All(part => source.Text.Contains(part, StringComparison.Ordinal));
        DirectRecallRank Rank(MemoryTurn turn) => turn.SourceEventIds
            .Where(id => supplied.Contains(id) && directRanks?.ContainsKey(id) == true)
            .Select(id => directRanks![id]).OrderBy(r => r.Priority).ThenBy(r => r.Rank)
            .FirstOrDefault() ?? new(2, int.MaxValue);
        bool typedScope = SemanticResultMatches(query, semantic, SemanticDomain.Thoughts)
            && semantic!.DataRoot == _root && semantic.SessionId == _sessionId;
        (string[] Visible, string[] Retrieved, string[] Matched, double Score, double SourceScore, int TypedRank) Basis(MemoryTurn turn)
        {
            bool textRelevant = IsRelevantRecallCached(query, turn.Text);
            if(semantic?.RelevancePlan is {} admission)
            {
                string[] own=admission.Admits(turn.EventId)?turn.SourceEventIds.Where(supplied.Contains).ToArray():Array.Empty<string>();
                return (Array.Empty<string>(),own,own,admission.Admits(turn.EventId)?1:0,0,int.MaxValue);
            }
            var typed = typedScope ? semantic!.Candidates.FirstOrDefault(c => c.Version == SemanticSourceVersion(turn)
                && SemanticCandidateMatches(c, turn, turn.Kind == "reflection.lesson" ? "lesson" : "note")) : null;
            int typedRank = typed == null ? int.MaxValue : Math.Max(1, typed.Rank);
            string[] current = turn.SourceEventIds.Where(id => visible.Contains(id) && InScope(id)
                && (textRelevant || typed != null)).ToArray();
            // A retrieved neighbour is available context, not a direct relevance
            // claim. Only a supplied direct hit gives this independent reason.
            string[] retrieved = turn.SourceEventIds.Where(id => supplied.Contains(id)
                && directRanks?.ContainsKey(id) == true && InScope(id)).ToArray();
            string[] matched = current.Concat(retrieved).Distinct(StringComparer.Ordinal).ToArray();
            double score = SimilarityCached(query, turn.Text);
            double sourceScore = matched.Select(id => SimilarityCached(query, raw[id].Text)).DefaultIfEmpty().Max();
            return (current, retrieved, matched, score, sourceScore, typedRank);
        }
        var pool = GetReflectionNotes(int.MaxValue, onObservation).Concat(GetSettledNotes(int.MaxValue))
            .Where(IsNativeSupplyAllowed).DistinctBy(t => t.EventId).ToList();
        var versions = _reflectionNotes.GroupBy(t => t.EventId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        string Family(MemoryTurn turn)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (turn.RecallId.Length == 0 && turn.SourceEventId.Length > 0 && seen.Add(turn.EventId)
                && versions.TryGetValue(turn.SourceEventId, out var parent) && parent.Kind == turn.Kind)
                turn = parent;
            return turn.RecallId.Length > 0 ? "recall:" + turn.RecallId : "note:" + turn.EventId;
        }
        // Ordinary conversation gets one eligible head per actual version family,
        // never historical versions presented as two independent viewpoints.
        var heads = pool.GroupBy(Family, StringComparer.Ordinal).Select(g => g.OrderByDescending(t => t.Revision)
            .ThenByDescending(t => t.Timestamp).ThenBy(t => t.EventId, StringComparer.Ordinal).First()).ToList();
        if(semantic?.RelevancePlan is {} selectedThoughts)heads=pool.Where(t=>selectedThoughts.Admits(t.EventId)).ToList();
        bool IndependentlyAdmitted(MemoryTurn t)=>semantic?.RelevancePlan?.Admits(t.EventId)==true;
        var considered = heads.Select(t => (Turn: t, Basis: Basis(t))).ToList();
        foreach (var candidate in considered)
        {
            string[] matched = candidate.Basis.Matched;
            bool anyRelevant = matched.Length > 0 || IndependentlyAdmitted(candidate.Turn);
            if (IsSettledLifecycle(candidate.Turn))
                Observe(onObservation, Fact(candidate.Turn, ReflectionProjectionStage.AvailabilityPassed,
                    true, candidate.Turn.SourceEventIds, Array.Empty<string>(), false, false));
            Observe(onObservation, Fact(candidate.Turn, ReflectionProjectionStage.RelevanceChecked,
                true, candidate.Turn.SourceEventIds, matched, anyRelevant, false));
            if (anyRelevant)
                Observe(onObservation, Fact(candidate.Turn, ReflectionProjectionStage.RelevancePassed,
                    true, candidate.Turn.SourceEventIds, matched, true, false));
        }
        var eligible = considered.Where(c => c.Basis.Matched.Length > 0 || IndependentlyAdmitted(c.Turn))
            .OrderByDescending(c => c.Basis.Score).ThenByDescending(c => c.Turn.Timestamp)
            .ThenBy(c => c.Turn.EventId, StringComparer.Ordinal).ToList();
        // Both reasons can supply a slot. A missing retrieval rank never demotes
        // the visible-source lane, and timestamps only break relevance ties.
        var chosen = new List<MemoryTurn>();
        MemoryTurn? retrievedNote = eligible.Where(c => c.Basis.Retrieved.Length > 0)
            .OrderByDescending(c => c.Basis.Score).ThenBy(c => c.Basis.TypedRank).ThenByDescending(c => c.Basis.SourceScore)
            .ThenBy(c => Rank(c.Turn).Priority).ThenBy(c => Rank(c.Turn).Rank)
            .ThenByDescending(c => c.Turn.Timestamp).ThenBy(c => c.Turn.EventId, StringComparer.Ordinal).FirstOrDefault().Turn;
        if (retrievedNote != null) chosen.Add(retrievedNote);
        MemoryTurn? focusNote = eligible.Where(c => c.Basis.Visible.Length > 0
                && (retrievedNote == null || Family(c.Turn) != Family(retrievedNote)))
            .OrderByDescending(c => c.Basis.Score).ThenBy(c => c.Basis.TypedRank).ThenByDescending(c => c.Basis.SourceScore)
            .ThenByDescending(c => c.Turn.Timestamp).ThenBy(c => c.Turn.EventId, StringComparer.Ordinal).FirstOrDefault().Turn;
        if (focusNote != null) chosen.Add(focusNote);
        if(semantic?.RelevancePlan is {} relevance)
            chosen=eligible.Where(c=>relevance.Admits(c.Turn.EventId)).Select(c=>c.Turn).ToList();
        MemoryTurn? sparkNote = semantic?.RelevancePlan == null && !strictLookupQuery
            ? TrySelectChatSparkNote(heads, chosen, Family, query)
            : null;
        if (sparkNote != null)
        {
            if (chosen.Count < 2) chosen.Add(sparkNote);
            else chosen[1] = sparkNote;
        }
        if (chosen.Count == 0 && supplied.Count == 0) return "";
        var lessonPool = GetReflectionLessons(int.MaxValue).Where(IsNativeSupplyAllowed).ToArray();
        MemoryTurn? lesson = null;
        if (lessonPool.Length > 0)
        {
            var queryTokens = ExperienceBodyTokens(query);
            lock (_experienceLock)
            {
                lesson = lessonPool
                    .Select(t =>
                    {
                        var basis = Basis(t);
                        bool explicitIndep = IndependentlyAdmitted(t);
                        double textSim = SimilarityCached(query, t.Text);
                        int tokenHits = queryTokens.Count == 0 ? 0
                            : ExperienceBodyTokens(t.Text).Count(queryTokens.Contains);
                        double basisBoost = (basis.Matched.Length > 0 ? 0.30 : 0d)
                            + (basis.TypedRank < int.MaxValue ? 0.35 : 0d)
                            + basis.SourceScore * 0.40;
                        double repeatPenalty = lessonPool.Length > 1
                            && _recentChatLessons.Contains(t.EventId, StringComparer.Ordinal) ? -0.35 : 0d;
                        double score = (explicitIndep ? 2.0 : 0d) + textSim * 1.2 + tokenHits * 0.12 + basisBoost + repeatPenalty;
                        return (Turn: t, Score: score);
                    })
                    .OrderByDescending(x => x.Score)
                    .ThenBy(x => Rank(x.Turn).Priority)
                    .ThenBy(x => Rank(x.Turn).Rank)
                    .ThenByDescending(x => x.Turn.Timestamp)
                    .Select(x => x.Turn)
                    .FirstOrDefault();
                if (lesson != null && semantic?.RelevancePlan == null)
                    RememberSurfaced(_recentChatLessons, lesson.EventId);
            }
        }
        if (chosen.Count == 0 && lesson == null) return "";
        var builder = new StringBuilder();
        builder.AppendLine("下面是你过往留下的理解与相处心得，供这次交流参考。知道这些记录不等于眼前非要照搬；是否采用、换个主意或放到一边，由你结合眼前的气氛自己拿主意。");
        if (chosen.Any(IsSettledLifecycle))
            builder.AppendLine("注：标记「曾收起」的想法只代表当时先放下了，如今仍可重新斟酌。");
        int appendedCount = 0;
        bool AppendWhole(string text, string id)
        {
            if (semantic?.RelevancePlan==null && (builder.Length + text.Length > ChatThoughtCharLimit
                || Encoding.UTF8.GetByteCount(builder.ToString()) + Encoding.UTF8.GetByteCount(text) > ChatThoughtByteLimit))
            {
                NativeEventLog.Trace("memory.reflection.chat_omission", "mod", "system", "whole candidate exceeds thought-block budget", "id=" + id + ";reason=block_budget");
                return false;
            }
            builder.Append(text);appendedCount++;return true;
        }
        string lastNoteHeader = "";
        foreach (MemoryTurn note in chosen)
        {
            bool isSpark = ReferenceEquals(note, sparkNote);
            string time = note.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            string settleTag = IsSettledLifecycle(note)
                ? " | 曾收起" + (note.SettleReason.Length > 0 ? "：" + note.SettleReason : "")
                : isSpark ? " | 偶发联想（未严格匹配当前字面，可接可不接）" : "";
            string sourceAttr = DescribeSourceAttribution(note);
            string header = IsSettledLifecycle(note) ? SettledNoteHeader : ChatNoteHeader;
            string headerPrefix = header != lastNoteHeader ? header + Environment.NewLine : "";
            string fragment = headerPrefix
                + "[" + time + settleTag + "] " + note.Text
                + (sourceAttr.Length > 0 ? Environment.NewLine + sourceAttr : "")
                + Environment.NewLine;
            var basis = Basis(note);
            string reason = semantic?.RelevancePlan is {} admission ? "independent_"+admission.Role(note.EventId)
                : isSpark ? "associative_spark"
                : ReferenceEquals(note, retrievedNote) ? "retrieved_source" : "focus_exchange_source";
            bool appended = AppendWhole(fragment, note.EventId);
            Observe(onObservation, Fact(note, appended ? ReflectionProjectionStage.Selected : ReflectionProjectionStage.BudgetOmitted,
                true, note.SourceEventIds, isSpark ? Array.Empty<string>() : basis.Matched, true, appended) with
                { FocusSourceIds = basis.Visible, RetrievedSourceIds = basis.Retrieved, SupplyReason = reason,
                    OmissionReason = appended ? "" : "block_budget" });
            if (!appended) continue;
            lastNoteHeader = header;
            onSupplied?.Invoke(new[] { note });
        }
        var selectedLessons=semantic?.RelevancePlan is {} lessonAdmission
            ?GetReflectionLessons(int.MaxValue).Where(t=>IsNativeSupplyAllowed(t)&&lessonAdmission.Admits(t.EventId)).ToArray()
            :lesson==null?Array.Empty<MemoryTurn>():new[]{lesson};
        bool emittedLessonHeader = false;
        foreach (var selectedLesson in selectedLessons)
        {
            lesson=selectedLesson;
            var fragment = new StringBuilder();
            string time = lesson.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            string sourceContext = DescribeSourceAttribution(lesson);
            if (!emittedLessonHeader) fragment.AppendLine(LessonHeader);
            fragment.AppendLine("[" + time + "] " + lesson.Text);
            if (sourceContext.Length > 0) fragment.AppendLine(sourceContext);
            if (AppendWhole(fragment.ToString(), lesson.EventId))
            {
                emittedLessonHeader = true;
                onSupplied?.Invoke(new[] { lesson });
            }
        }
        return appendedCount > 0 ? builder.ToString() : "";
    }
    private MemoryTurn? TrySelectChatSparkNote(IReadOnlyList<MemoryTurn> heads, IReadOnlyList<MemoryTurn> chosen,
        Func<MemoryTurn, string> family, string query)
    {
        var candidates = heads.Where(h => !IsSettledLifecycle(h)
            && chosen.All(c => family(c) != family(h))).ToList();
        lock (_experienceLock)
        {
            int sparkTurn = ++_chatSparkTurnCounter;
            bool cooldownReady = _lastChatSparkTurn < 0 || sparkTurn - _lastChatSparkTurn >= 3;
            if (!cooldownReady || candidates.Count == 0)
                return null;
            var queryTokens = MemorySemantics.MatchingTokens(query);
            DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
            MemoryTurn? pick = candidates
                .Select(t =>
                {
                    double novelty = _recentChatSparkNotes.Contains(t.EventId, StringComparer.Ordinal) ? -1.0 : 0.50;
                    double cue = queryTokens.Count > 0 && MemorySemantics.MatchingTokens(t.Text).Any(queryTokens.Contains) ? 0.25 : 0d;
                    double ageDays = Math.Clamp((nowUtc - t.Timestamp).TotalDays, 0d, 60d);
                    return (Turn: t, Score: novelty + cue + (ageDays / 60d) * 0.15);
                })
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.Turn.Timestamp)
                .ThenBy(x => x.Turn.EventId, StringComparer.Ordinal)
                .Select(x => x.Turn)
                .FirstOrDefault();
            if (pick != null)
            {
                _lastChatSparkTurn = sparkTurn;
                RememberSurfaced(_recentChatSparkNotes, pick.EventId, 4);
            }
            return pick;
        }
    }

    internal const int ChatThoughtCharLimit = 2400;
    internal const int ChatThoughtByteLimit = 8192;
}
