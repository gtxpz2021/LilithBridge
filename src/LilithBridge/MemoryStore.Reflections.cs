using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LilithBridge;

internal sealed record ReflectionRequestSnapshot(string RequestId, string Source,
    IReadOnlyList<MemoryTurn> Evidence, IReadOnlyList<MemoryTurn> Notes,
    IReadOnlyList<MemoryTurn> Lessons, IReadOnlyList<MemoryTurn> Readings,
    IReadOnlyList<MemoryTurn> DayArtifacts, IReadOnlyList<MemoryTurn> Settled)
{
    // Programme-owned stamps of the exact versions supplied. Not model labels.
    public IReadOnlyList<DaySourceDependency> Dependencies { get; init; } = Array.Empty<DaySourceDependency>();
    public IReadOnlyList<MemoryTurn> RevisionTargets { get; init; } = Array.Empty<MemoryTurn>();
    // Read-only historical context, never an additional citation/revision pool.
    public IReadOnlyList<MemoryTurn> NoteHistory { get; init; } = Array.Empty<MemoryTurn>();
    public IReadOnlyList<DaySourceDependency> NoteHistoryPermissions { get; init; } = Array.Empty<DaySourceDependency>();
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
    internal const string ThoughtBlockHeader = "[莉莉丝的想法记录";
    internal const string ChatNoteHeader = "[她以前留下的想法：可以重新考虑，也可以不提起]";
    internal const string SettledNoteHeader = "[她以前收起的想法记录：可以重新考虑，也可以不提]";
    internal const string LessonHeader = "[她自己的经验法则：可以推翻，也不必遵照]";
    internal const string ThoughtLifecycleContract = "state=active 表示记录尚未被收起或后继版本取代；state=settled 表示记录曾被收起，时间和理由保留在 settled_at / settle_reason；state=superseded 表示该版本已被后继版本取代，关联见 revises / revised_by。这些是记录生命周期，仅凭状态不能判断她此刻是否赞同、正在想或已经不再在意。可以采纳、提出异议、重新考虑，也可以不提起。";

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
    private HashSet<string> AvailableDayArtifactIds() =>
        GetDayArtifacts(int.MaxValue).Select(t => t.EventId).ToHashSet(StringComparer.Ordinal);

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
    {
        HashSet<string> available = ReadExperiences().Select(t => t.EventId).ToHashSet(StringComparer.Ordinal);
        HashSet<string> availableDays = AvailableDayArtifactIds();
        var isCurrent = CreateCurrentBasisEvaluator(available);
        var kept = new List<MemoryTurn>();
        foreach (MemoryTurn candidate in _reflectionNotes)
        {
            string[] surviving = candidate.SourceEventIds.Where(available.Contains).ToArray();
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
        return kept.TakeLast(Math.Max(0,max)).Select(t => t.Copy()).ToList();
    }

    /// <summary>
    /// Thoughts she has finished with. They stay in the archive and stay
    /// reachable, so a later reflection can meet one again and see how she closed
    /// it; they are simply no longer part of what she is carrying.
    /// </summary>
    public IReadOnlyList<MemoryTurn> GetSettledNotes(int max)
    {
        HashSet<string> available = ReadExperiences().Select(t => t.EventId).ToHashSet(StringComparer.Ordinal);
        HashSet<string> availableDays = _dreams.Where(IsDerivedMemoryVisible).Select(t => t.EventId).ToHashSet(StringComparer.Ordinal);
        return _reflectionNotes
            .Where(t => IsMentionAllowed(t) && IsSettledLifecycle(t) && t.Origin == "reflection"
                && t.SourceEventIds.Length > 0 && t.SourceEventIds.All(available.Contains)
                && t.SourceDayIds.All(availableDays.Contains))
            .TakeLast(Math.Max(0, max)).Select(t => t.Copy()).ToList();
    }

    /// <summary>
    /// Thoughts a later revision replaced. They are never current, but a question
    /// about what she used to think must be able to reach them as the versions
    /// they were, together with the revision that replaced them.
    /// </summary>
    public IReadOnlyList<MemoryTurn> GetSupersededNotes(int max)
    {
        HashSet<string> available = ReadExperiences().Select(t => t.EventId).ToHashSet(StringComparer.Ordinal);
        HashSet<string> availableDays = _dreams.Where(IsDerivedMemoryVisible).Select(t => t.EventId).ToHashSet(StringComparer.Ordinal);
        return _reflectionNotes
            .Where(t => IsMentionAllowed(t) && IsSupersededLifecycle(t) && t.Origin == "reflection"
                && t.SourceEventIds.Length > 0 && t.SourceEventIds.All(available.Contains)
                && t.SourceDayIds.All(availableDays.Contains))
            .TakeLast(Math.Max(0, max)).Select(t => t.Copy()).ToList();
    }

    public IReadOnlyList<MemoryTurn> GetReflectionLessons(int max)
    {
        HashSet<string> available = ReadExperiences().Select(t => t.EventId).ToHashSet(StringComparer.Ordinal);
        HashSet<string> availableDays = AvailableDayArtifactIds();
        HashSet<string> availableNotes = CurrentBasisNoteIds(available, availableDays);
        return _reflectionLessons
            .Where(t => IsMentionAllowed(t) && IsActiveLifecycle(t) && t.Kind == "reflection.lesson"
                && t.SourceNoteIds.Length > 0
                && t.SourceNoteIds.All(id => ResolveNoteCitation(id, availableNotes).Length > 0)
                && t.SourceEventIds.Length > 0 && t.SourceEventIds.All(available.Contains)
                && !HasUnincorporatedCorrection(t))
            .TakeLast(Math.Max(0, max)).Select(t => t.Copy()).ToList();
    }

    /// <summary>
    /// Historical citation and current support are different. Settling a thought
    /// does not falsify it; replacing it retires conclusions resting on that exact
    /// version from default supply. No stored lifecycle is rewritten or cascaded.
    /// </summary>
    private HashSet<string> CurrentBasisNoteIds(HashSet<string> available, HashSet<string> availableDays)
    {
        var isCurrent = CreateCurrentBasisEvaluator(available);
        return _reflectionNotes.Where(t => (IsActiveLifecycle(t) || IsSettledLifecycle(t))
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
    {
        HashSet<string> available = ReadExperiences().Select(t => t.EventId).ToHashSet(StringComparer.Ordinal);
        HashSet<string> availableDays = AvailableDayArtifactIds();
        HashSet<string> availableNotes = CurrentBasisNoteIds(available, availableDays);
        return _relationshipReadings
            .Where(t => IsMentionAllowed(t) && IsActiveLifecycle(t) && t.Kind == RelationshipReadingKind
                && t.SourceNoteIds.Length > 0
                && t.SourceNoteIds.All(id => ResolveNoteCitation(id, availableNotes).Length > 0)
                && t.SourceEventIds.Length > 0 && t.SourceEventIds.All(available.Contains)
                && !HasUnincorporatedCorrection(t))
            .TakeLast(Math.Max(0, max)).Select(t => t.Copy()).ToList();
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
        var raw = ReadExperiences().Where(t => ids.Contains(t.EventId)).ToArray();
        string[] local = raw.Where(t => t.Origin == LocalFallbackOrigin)
            .Select(t => t.EventId).Distinct(StringComparer.Ordinal).ToArray();
        string label = local.Length == 0 ? "" : "local_fallback_sources=" + string.Join(",", local)
            + "；这些程序台词只证明实际显示，不证明模型当时的心理、意图或自主选择。";
        var reports = raw.Select(t => new { event_id=t.EventId, reported_speech=UtteranceAttribution.Reported(t.Role,t.Text) })
            .Where(t=>t.reported_speech != null).ToArray();
        if (reports.Length > 0) label += " reported_sources=" + JsonSerializer.Serialize(reports, DialogueMaterialEnvelope.DataJson)
            + "；这些原文含发言标记或前缀标签，不能仅因提交者为 user 就当作玩家本人的自述；标签身份未验证，旧理解也不是独立证言。";
        if (raw.Length != ids.Count) label += " source_resolution=incomplete；部分原始来源不可用，不能把缺失来源当作已经核实的证言。";
        if (raw.Length == ids.Count && raw.Length > 0 && raw.All(t=>t.Role == "assistant")) label += " assistant_only_sources=true；来源只有莉莉丝发言，不能作为玩家自述的证据。";
        return label;
    }

    public ReflectionRequestSnapshot? CaptureReflectionRequest(IReadOnlyList<MemoryTurn>? requestedWindow = null)
        => CaptureReflectionRequestCore(requestedWindow, null, null);

    internal ReflectionRequestSnapshot? CaptureReflectionRequest(IReadOnlyList<MemoryTurn> requestedWindow,
        SemanticRecallResult? semantic, ReflectionPreparation owner) => CaptureReflectionRequestCore(requestedWindow, semantic, owner);

    private ReflectionRequestSnapshot? CaptureReflectionRequestCore(IReadOnlyList<MemoryTurn>? requestedWindow,
        SemanticRecallResult? semantic, ReflectionPreparation? preparation)
    {
        if (!_longTermEnabled || !_experienceArchiveEnabled) return null;
        var requestedIds = requestedWindow?.Select(t=>t.EventId).ToHashSet(StringComparer.Ordinal);
        // Resolve from the owning store now: a captured pending window is never
        // authority to resurrect deleted, withdrawn or changed raw material.
        var recent = requestedIds == null
            ? GetRecentHistory(12).Where(t => t.DeliveryState is "received" or "delivered").ToList()
            : ReadExperiences().Where(t=>requestedIds.Contains(t.EventId)
                && t.DeliveryState is "received" or "delivered" && IsMentionAllowed(t) && IsNativeSupplyAllowed(t)).ToList();
        if (recent.Count == 0 || recent.Any(t => t.Role == "user" && !ReflectionDeliverySettled(t))) return null;
        if (preparation != null && !preparation.Matches(_root, _sessionId, recent)) return null;
        if (preparation?.Owns(semantic) != true) semantic = null;
        IReadOnlyList<MemoryTurn> notes = SelectNotesForReflection(recent, semantic);
        var history = CaptureNoteHistory(notes);
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
        var ids = recent.Select(t => t.EventId).Concat(notes.SelectMany(t => t.SourceEventIds))
            .Concat(settled.SelectMany(t => t.SourceEventIds))
            .Concat(revisionTargets.SelectMany(t => t.SourceEventIds))
            .Concat(CorrectionInputIds(revisionTargets)).ToHashSet(StringComparer.Ordinal);
        var evidence = ReadExperiences().Where(t => ids.Contains(t.EventId)).ToList();
        if (evidence.Count == 0) return null;
        string source = JsonSerializer.Serialize(new
        {
            now = DateTimeOffset.UtcNow,
            discovery_status = preparation?.SemanticStatus ?? "not_requested",
            // The passage she looks back at: one continuous stretch of conversation,
            // in order, rather than a bag of separate lines.
            window_start = evidence.FirstOrDefault()?.Timestamp,
            window_end = evidence.LastOrDefault()?.Timestamp,
            turn_count = evidence.Count,
            utterances = evidence.Select(t => new
            {
                event_id=t.EventId, speaker=t.Role, origin=t.Origin, text=t.Text, at=t.Timestamp,
                source_revision_context = NaturalMemoryContext(t),
                reported_speech=UtteranceAttribution.Reported(t.Role,t.Text),
                exchange_delivery = t.Role == "user" ? ReflectionDeliveryDescription(t) : "",
                source_label = t.Origin == LocalFallbackOrigin
                    ? "本地程序生成并实际显示的兜底台词，不代表模型当时的思考、意图或自发决定。"
                    : ""
            }),
            note_history = history.Projection,
            previous_notes = notes.Select(t => new
            {
                note_id=t.EventId,
                state=t.LifecycleState,
                revision=t.Revision,
                revises=t.SourceEventId,
                text=t.Text,
                source_event_ids=t.SourceEventIds,
                source_context=DescribeSourceAttribution(t),
                at=t.Timestamp,
                // Textual similarity is a selection hint, not a present belief.
                label = MemorySemantics.Similarity(ReflectionTopic(recent), t.Text) >= 0.75
                    ? "这条记录与本次交流的文本主题较接近；如有新的理解，可以修订，也可以不修改。"
                    : ""
            }),
            previous_lessons = lessons.Select(t => new { lesson_id=t.EventId, text=t.Text, source_note_ids=t.SourceNoteIds, at=t.Timestamp,
                source_context=DescribeSourceAttribution(t) })
            ,previous_readings = readings.Select(t => new { reading_id=t.EventId, text=t.Text, source_note_ids=t.SourceNoteIds, at=t.Timestamp,
                source_context=DescribeSourceAttribution(t) })
            ,settled_notes = settled.Select(t => new
            {
                note_id = t.EventId,
                state = t.LifecycleState,
                revision = t.Revision,
                revises = t.SourceEventId,
                text = t.Text,
                at = t.Timestamp,
                settled_at = t.SettledAt,
                settle_reason = t.SettleReason,
                source_context = DescribeSourceAttribution(t),
                label = "这条记录曾被收起，时间和理由见 settled_at / settle_reason；可以重新考虑。"
            })
            ,day_artifacts = dayArtifacts.Select(t => new
            {
                day_id = t.EventId,
                kind = t.Kind,
                at = t.Timestamp,
                label = t.Kind == DayDreamKind
                    ? "她当时的梦／想象，不是真实发生过的事"
                    : "她当时对那一天的归拢，是整理不是新事实",
                text = t.Text, source_context = DescribeSourceAttribution(t)
            })
            ,revision_targets = revisionTargets.Select(t => new
            {
                target_id=t.EventId, kind=t.Kind, revision=t.Revision, text=t.Text,
                source_event_ids=t.SourceEventIds, source_note_ids=t.SourceNoteIds,
                status=AssessMaterialSupply(t).Reason,
                use="仅供 revises 定位旧解释；原依据已变化，不能据此判断当前立场，也不可作为新来源或证据。修订须引用本次当前来源。"
            })
        });
        return new(Guid.NewGuid().ToString("N"),source,evidence,notes,lessons,readings,dayArtifacts,settled)
        {
            Dependencies = CaptureReflectionVersions(evidence, notes.Concat(settled)
                .Concat(revisionTargets.Where(t => t.Kind == "reflection.note")),
                lessons.Concat(revisionTargets.Where(t => t.Kind == "reflection.lesson")),
                readings.Concat(revisionTargets.Where(t => t.Kind == RelationshipReadingKind)), dayArtifacts),
            RevisionTargets = revisionTargets,
            NoteHistory = history.Notes,
            NoteHistoryPermissions = history.Permissions
        };
    }

    private const int NoteHistoryByteLimit = 8192;
    private sealed record NoteHistoryCapture(JsonElement Projection, IReadOnlyList<MemoryTurn> Notes,
        IReadOnlyList<DaySourceDependency> Permissions);

    // Only explicit parent links form a family. Ancestors are resolved from the
    // full archive; neither root discovery nor ancestor resolution has a recency pre-cut.
    private NoteHistoryCapture CaptureNoteHistory(IReadOnlyList<MemoryTurn> roots)
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
        JsonElement Project() => JsonSerializer.SerializeToElement(new
        {
            entries = selected.OrderBy(p => families.FindIndex(f => f.Root.EventId == p.Root.EventId))
                .ThenBy(p => p.Note.Timestamp).ThenBy(p => p.Note.Revision).ThenBy(p => p.Note.EventId, StringComparer.Ordinal)
                .Select(p => new
                {
                    current_note_id = p.Root.EventId, note_id = p.Note.EventId, recall_id = p.Note.RecallId,
                    revision = p.Note.Revision, revises = p.Note.SourceEventId, at = p.Note.Timestamp,
                    lifecycle = p.Note.LifecycleState, text = p.Note.Text,
                    source_event_ids = p.Note.SourceEventIds, source_day_ids = p.Note.SourceDayIds,
                    source_context = DescribeSourceAttribution(p.Note),
                    qualification = HasUnincorporatedCorrection(p.Note)
                        ? "这是当时的理解；原依据已有后续更正，不可作为当前事实。仅作历史背景，不增加引用或修改权限。"
                        : "这是当时的理解，仅凭该旧版本不能判断当前立场或事实。仅作历史背景，不增加引用或修改权限。"
                }),
            omissions = omissions.Select(p => new { current_note_id = p.Key, reason = p.Value,
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
        // "Related" is drawn from what recency is NOT already showing: otherwise
        // both halves pick the same two notes and the window collapses to 2.
        var related = SelectReflectionRelated(available.Where(t => !newestIds.Contains(t.EventId)).ToArray(), topic,
            semantic, ReflectionRelatedNotes);
        var chosen = new List<MemoryTurn>(related);
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
        var lexical = pool.OrderByDescending(t => MemorySemantics.Similarity(topic, t.Text))
            .ThenByDescending(t => t.SettledAt ?? t.Timestamp).ThenBy(t => t.EventId, StringComparer.Ordinal).ToList();
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
    {
        lock (_experienceLock)
        {
            bool noteAttempted = result?.Note != null;
            bool settleAttempted = Clean(result?.Note?.Settles ?? "").Length > 0;
            bool lessonAttempted = result?.Lesson != null;
            bool readingAttempted = result?.Relationship != null;
            if ((noteAttempted || lessonAttempted || readingAttempted)
                && !NoteHistoryStillReadable(request, out string historyReason))
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
            bool noteSaved = ApplyNoteCore(result?.Note, request, out string noteReason);
            bool lessonSaved = ApplyLessonCore(result?.Lesson, request, out string lessonReason);
            bool readingSaved = ApplyRelationshipReadingCore(result?.Relationship, request, out string readingReason);
            // The note proposal carries both a possible new thought and a possible
            // decision to close one; report them separately.
            string settleResult = !settleAttempted ? "no_settle"
                : noteSaved ? "settled"
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
            if (parent == null && text.Length > 0 && _reflectionNotes.Any(t => IsActiveLifecycle(t) && t.Text == text))
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
                }
                reason="persistence_failed"; return false;
            }
            reason = text.Length > 0 ? "saved" : "settled";
            return true;
        }
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
        if (!_longTermEnabled || text.Length == 0 || text.Length > 240)
        { reason="invalid_lesson"; return false; }
        if (_reflectionLessons.Any(t => t.OperationId == request.RequestId && t.Kind == "reflection.lesson"))
        { reason="already_applied"; return false; }
        string[] cited = (proposal.SourceNoteIds ?? Array.Empty<string>())
            .Where(id => id.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        if (cited.Length == 0)
        { reason="lesson_without_note"; return false; }
        var shown = request.Notes.ToDictionary(t => t.EventId, StringComparer.Ordinal);
        if (cited.Any(id => !shown.ContainsKey(id)))
        { reason="note_not_in_request"; return false; }
        // New support must name the exact still-current notes that were supplied.
        var available = GetReflectionNotes(64).ToDictionary(t => t.EventId, StringComparer.Ordinal);
        if (cited.Any(id => !available.ContainsKey(id)))
        { reason="unavailable_note"; return false; }
        MemoryTurn? parent = null;
        if (proposal.Revises.Length > 0)
        {
            parent = _reflectionLessons.FirstOrDefault(
                t => t.EventId == proposal.Revises && IsActiveLifecycle(t));
            if (parent == null || !(request.Lessons.Concat(request.RevisionTargets.Where(t => t.Kind == "reflection.lesson"))
                .Any(t => t.EventId == parent.EventId)))
            { reason="stale_lesson"; return false; }
            if (!RecordPermitted(parent)) { reason="target_withdrawn"; return false; }

            // SourceEventId keeps the historical parent; only current explicit citations support this revision.
        }
        if (parent == null && _reflectionLessons.Any(t => IsActiveLifecycle(t)
                && MemorySemantics.Similarity(t.Text, text) >= 0.75))
        { reason="duplicate_lesson"; return false; }
        string[] sources = cited.SelectMany(id => available[id].SourceEventIds)
            .Distinct(StringComparer.Ordinal).ToArray();
        if (sources.Length == 0)
        { reason="lesson_without_source"; return false; }
        if (!ReflectionVersionsMatch(request, sources, Array.Empty<string>(), cited, proposal.Revises))
        { reason="source_version_changed"; return false; }
        if (parent != null && text == parent.Text && SameSupport(cited, parent.SourceNoteIds))
        { reason="no_change"; return false; }
        var lesson = new MemoryTurn
        {
            EventId=Guid.NewGuid().ToString("N"), RecallId=parent?.RecallId ?? Guid.NewGuid().ToString("N"),
            Revision=(parent?.Revision ?? 0)+1, OperationId=request.RequestId,
            SourceEventId=proposal.Revises, SourceNoteIds=cited, SourceEventIds=sources,
            Role="assistant", Kind="reflection.lesson", Origin="reflection", Source="model_reflection",
            Text=text, Validity="inferred", LifecycleState="active", SessionId=_sessionId
        };
        if (parent != null) parent.LifecycleState="superseded";
        _reflectionLessons.Add(lesson);
        if (!SaveList("v4-reflection-lessons.json",_reflectionLessons))
        {
            _reflectionLessons.Remove(lesson);
            if (parent != null) parent.LifecycleState="active";
            reason="persistence_failed"; return false;
        }
        reason="lesson_saved";
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
        string[] cited = (proposal.SourceNoteIds ?? Array.Empty<string>())
            .Where(id => id.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        if (cited.Length == 0)
        { reason = "reading_without_note"; return false; }
        var shown = request.Notes.ToDictionary(t => t.EventId, StringComparer.Ordinal);
        if (cited.Any(id => !shown.ContainsKey(id)))
        { reason = "note_not_in_request"; return false; }
        var available = GetReflectionNotes(64).ToDictionary(t => t.EventId, StringComparer.Ordinal);
        if (cited.Any(id => !available.ContainsKey(id)))
        { reason = "unavailable_note"; return false; }
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
        string[] sources = cited.SelectMany(id => available[id].SourceEventIds)
            .Distinct(StringComparer.Ordinal).ToArray();
        if (sources.Length == 0)
        { reason = "reading_without_source"; return false; }
        if (!ReflectionVersionsMatch(request, sources, Array.Empty<string>(), cited, proposal.Revises))
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
    /// One version line. state 说明记录的生命周期；revised_by /
    /// revises 让新旧两版互为参照，settled_at / settle_reason 保留记录收尾的
    /// 时间与理由。这些字段都来自已有记录，没有新增持久状态。
    /// </summary>
    private string DescribeThoughtVersion(MemoryTurn note, IReadOnlyList<MemoryTurn> pool)
    {
        MemoryTurn? parent = ResolveVersionParent(note, pool);
        MemoryTurn? child = ResolveVersionChild(note, pool);
        string state = string.IsNullOrWhiteSpace(note.LifecycleState) ? "active" : note.LifecycleState;
        var line = new StringBuilder();
        line.Append(note.Kind=="reflection.lesson"?"lesson=":"note=").Append(note.EventId)
            .Append("; state=").Append(state)
            .Append("; revision=").Append(note.Revision)
            .Append("; at=").Append(note.Timestamp.ToString("O"))
            .Append("; revises=").Append(parent?.EventId ?? note.SourceEventId);
        if (child != null)
            line.Append("; revised_by=").Append(child.EventId).Append('@').Append(child.Revision);
        if (IsSettledLifecycle(note) || note.SettledAt != null || note.SettleReason.Length > 0)
            line.Append("; settled_at=").Append(note.SettledAt?.ToString("O") ?? "")
                .Append("; settle_reason=").Append(note.SettleReason);
        line.Append("; thought=").Append(note.Text);
        string sourceContext = DescribeSourceAttribution(note);
        if (sourceContext.Length > 0) line.Append("; ").Append(sourceContext);
        return line.ToString();
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
            return "[旧想法检索结果] 没有找回莉莉丝当时留下的想法记录。原始对话只能证明当时说了什么。可以从现在的视角重新理解，但不能把现在的推想说成当时确实想过的内容。";
        var builder = new StringBuilder(ThoughtBlockHeader + "：含活动、收起或已被替换的版本，不是玩家证言]\n");
        builder.AppendLine(ThoughtLifecycleContract);
        builder.AppendLine("下面的想法是当时的理解，可能有误；只有随后列出的原始发言能支持谁说过什么。可以不采纳旧想法，不补写当时没有记录的心理史。");
        builder.AppendLine("at 是这段想法实际写下的时间；它对更早经历的解释属于后来回看，不证明早先那一刻确实如此想过。以玩家原话为准，不放大原话里的拒绝、承诺或动机。");
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
        if (supplied.Count == 0)
            return "";
        var visible = (suppliedHistoryEventIds ?? Array.Empty<string>()).Where(supplied.Contains)
            .ToHashSet(StringComparer.Ordinal);
        var raw = ReadExperiences().Where(t => supplied.Contains(t.EventId))
            .ToDictionary(t => t.EventId, StringComparer.Ordinal);
        var scope = MemorySemantics.ParseExperienceRecallQuery(query);
        string[] identifiers = MemorySemantics.RecallIdentifiers(query);
        string[] quotes = UtteranceAttribution.LiteralQuotes(query);
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
            bool textRelevant = MemorySemantics.IsRelevantRecall(query, turn.Text);
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
            double score = MemorySemantics.Similarity(query, turn.Text);
            double sourceScore = matched.Select(id => MemorySemantics.Similarity(query, raw[id].Text)).DefaultIfEmpty().Max();
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
        var considered = heads.Select(t => (Turn: t, Basis: Basis(t))).ToList();
        foreach (var candidate in considered)
        {
            string[] matched = candidate.Basis.Matched;
            bool anyRelevant = matched.Length > 0;
            if (IsSettledLifecycle(candidate.Turn))
                Observe(onObservation, Fact(candidate.Turn, ReflectionProjectionStage.AvailabilityPassed,
                    true, candidate.Turn.SourceEventIds, Array.Empty<string>(), false, false));
            Observe(onObservation, Fact(candidate.Turn, ReflectionProjectionStage.RelevanceChecked,
                true, candidate.Turn.SourceEventIds, matched, anyRelevant, false));
            if (anyRelevant)
                Observe(onObservation, Fact(candidate.Turn, ReflectionProjectionStage.RelevancePassed,
                    true, candidate.Turn.SourceEventIds, matched, true, false));
        }
        var eligible = considered.Where(c => c.Basis.Matched.Length > 0)
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
        MemoryTurn? lesson = GetReflectionLessons(int.MaxValue)
            .Where(IsNativeSupplyAllowed)
            .Select(t => (Turn: t, Basis: Basis(t))).Where(c => c.Basis.Matched.Length > 0)
            .OrderByDescending(c => c.Basis.Score).ThenBy(c => c.Basis.TypedRank).ThenByDescending(c => c.Basis.SourceScore)
            .ThenByDescending(c => c.Turn.Timestamp).Select(c => c.Turn)
            .FirstOrDefault();
        if (chosen.Count == 0 && lesson == null) return "";
        var builder = new StringBuilder();
        builder.AppendLine(ThoughtLifecycleContract);
        builder.AppendLine("下面是有来源的过往理解，供本次交流参考。知道这些记录不等于它们适用于眼前的话题；是否采用、提出异议或暂时搁置，由你结合这次交流判断。不必逐条评价，不补写当时没有记录的心理，也不把它们当作玩家说过的事实。");
        int appendedCount = 0;
        bool AppendWhole(string text, string id)
        {
            if (builder.Length + text.Length > ChatThoughtCharLimit
                || Encoding.UTF8.GetByteCount(builder.ToString()) + Encoding.UTF8.GetByteCount(text) > ChatThoughtByteLimit)
            {
                NativeEventLog.Trace("memory.reflection.chat_omission", "mod", "system", "whole candidate exceeds thought-block budget", "id=" + id + ";reason=block_budget");
                return false;
            }
            builder.Append(text);appendedCount++;return true;
        }
        foreach (MemoryTurn note in chosen)
        {
            string fragment = (IsSettledLifecycle(note) ? SettledNoteHeader : ChatNoteHeader) + Environment.NewLine
                + DescribeThoughtVersion(note, pool) + Environment.NewLine
                + "source_event_ids=" + string.Join(",", note.SourceEventIds) + Environment.NewLine;
            var basis = Basis(note);
            string reason = ReferenceEquals(note, retrievedNote) ? "retrieved_source" : "focus_exchange_source";
            bool appended = AppendWhole(fragment, note.EventId);
            Observe(onObservation, Fact(note, appended ? ReflectionProjectionStage.Selected : ReflectionProjectionStage.BudgetOmitted,
                true, note.SourceEventIds, basis.Matched, true, appended) with
                { FocusSourceIds = basis.Visible, RetrievedSourceIds = basis.Retrieved, SupplyReason = reason,
                    OmissionReason = appended ? "" : "block_budget" });
            if (!appended) continue;
            onSupplied?.Invoke(new[] { note });
        }
        if (lesson != null)
        {
            var fragment = new StringBuilder();
            fragment.AppendLine(LessonHeader);
            fragment.AppendLine("lesson=" + lesson.EventId + "; revision=" + lesson.Revision
                + "; state=" + lesson.LifecycleState + "; at=" + lesson.Timestamp.ToString("O") + "; text=" + lesson.Text);
            fragment.AppendLine("source_note_ids=" + string.Join(",", lesson.SourceNoteIds)
                + "; source_event_ids=" + string.Join(",", lesson.SourceEventIds));
            fragment.AppendLine("这只是她从过往经历里得出的做法，不是硬规则，也不是玩家说过的事实；可以不同意，也可以完全不提。");
            string sourceContext = DescribeSourceAttribution(lesson);
            if (sourceContext.Length > 0) fragment.AppendLine(sourceContext);
            if (AppendWhole(fragment.ToString(), lesson.EventId)) onSupplied?.Invoke(new[] { lesson });
        }
        return appendedCount > 0 ? builder.ToString() : "";
    }
    internal const int ChatThoughtCharLimit = 2400;
    internal const int ChatThoughtByteLimit = 8192;
}
