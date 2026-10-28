using System;
using System.Collections.Generic;
using System.Linq;

namespace LilithBridge;

/// <summary>
/// Execution facts for one note that the chat projection actually considered.
/// Facts are recorded where the existing filter evaluates the candidate; the
/// label is derived afterwards, so a future change to the order of checks does
/// not invalidate what was observed. Ids and booleans only — never note text.
/// </summary>
internal enum ReflectionProjectionStage
{
    CandidateSeen,
    AvailabilityChecked,
    AvailabilityPassed,
    RelevanceChecked,
    RelevancePassed,
    Selected,
    SettledFallbackUsed,
    BudgetOmitted
}

internal sealed record ReflectionProjectionObservation(
    string NoteId,
    IReadOnlyList<string> NoteSourceEventIds,
    IReadOnlyList<string> AvailableSourceIds,
    IReadOnlyList<string> RelevantSourceIds,
    bool AllAvailable,
    bool AnyRelevant,
    bool ProjectionAttempted,
    bool NoteAddedToProjection,
    ReflectionProjectionStage Stage)
{
    // Filled in by the layer that owns those ids (ContextBuilder: what this
    // request actually supplied). A probe never fills these.
    public IReadOnlyList<string> SuppliedHistoryEventIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> RetrievedExperienceEventIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> SuppliedExperienceIds { get; init; } = Array.Empty<string>();
    // Selection evidence only, never a claim that the model adopted the record.
    public IReadOnlyList<string> FocusSourceIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> RetrievedSourceIds { get; init; } = Array.Empty<string>();
    public string SupplyReason { get; init; } = "";
    public string OmissionReason { get; init; } = "";
}

internal static class ReflectionProjectionDecision
{
    /// <summary>Maps the observed facts onto the label the trace reports.</summary>
    public static string Label(ReflectionProjectionObservation observation)
    {
        if (observation.OmissionReason.Length > 0) return "omitted_" + observation.OmissionReason;
        if (observation.NoteAddedToProjection)
            return observation.Stage == ReflectionProjectionStage.SettledFallbackUsed
                ? "settled_fallback"
                : "included";
        if (!observation.ProjectionAttempted)
            return "projection_not_attempted";
        if (!observation.AllAvailable)
            return "rejected_unavailable_source";
        if (!observation.AnyRelevant)
            return "rejected_not_relevant";
        return "not_selected";
    }
}

/// <summary>
/// One trace line per Build call. The caller owns correlation; note identity
/// stays in the observations, and "did it reach the payload" is answered at
/// section level (the header the composer writes), never by note text. A broken
/// sink is swallowed: this is a bypass, it decides nothing.
/// </summary>
internal static class ReflectionProjectionTrace
{
    public const string Kind = "self_position.supply_chain";

    public static bool SectionInContext(string? context) =>
        !string.IsNullOrEmpty(context)
        && (context.Contains(MemoryStore.ChatNoteHeader, StringComparison.Ordinal)
            || context.Contains(MemoryStore.SettledNoteHeader, StringComparison.Ordinal)
            || context.Contains(MemoryStore.LessonHeader, StringComparison.Ordinal)
            || context.Contains(MemoryStore.ThoughtBlockHeader, StringComparison.Ordinal));

    public static string Describe(string requestId,
        IReadOnlyList<ReflectionProjectionObservation> observations, string? finalContext)
    {
        bool section = SectionInContext(finalContext);
        var records = new List<string>();
        foreach (IGrouping<string, ReflectionProjectionObservation> group in
                 observations.GroupBy(o => o.NoteId))
        {
            ReflectionProjectionObservation terminal = group.Last();
            records.Add("note=" + terminal.NoteId
                + ";sources=" + Join(terminal.NoteSourceEventIds)
                + ";available=" + Join(terminal.AvailableSourceIds)
                + ";relevant=" + Join(terminal.RelevantSourceIds)
                + ";all_available=" + Bool(terminal.AllAvailable)
                + ";any_relevant=" + Bool(terminal.AnyRelevant)
                + ";attempted=" + Bool(terminal.ProjectionAttempted)
                + ";added=" + Bool(terminal.NoteAddedToProjection)
                + ";stages=" + string.Join(">", group.Select(o => o.Stage.ToString()))
                + ";stage=" + terminal.Stage
                + ";label=" + ReflectionProjectionDecision.Label(terminal)
                + ";basis=" + terminal.SupplyReason
                + ";focus_sources=" + Join(terminal.FocusSourceIds)
                + ";retrieved_sources=" + Join(terminal.RetrievedSourceIds)
                + ";omission=" + terminal.OmissionReason
                + ";in_context=" + Bool(section && terminal.NoteAddedToProjection));
        }
        IReadOnlyList<string>? history = observations.Count > 0
            ? observations[0].SuppliedHistoryEventIds : null;
        IReadOnlyList<string>? retrieved = observations.Count > 0
            ? observations[0].RetrievedExperienceEventIds : null;
        IReadOnlyList<string>? supplied = observations.Count > 0
            ? observations[0].SuppliedExperienceIds : null;
        return "request_id=" + requestId
            + ";section_in_context=" + Bool(section)
            + ";notes=" + records.Count
            + ";supplied_history=" + Join(history)
            + ";retrieved=" + Join(retrieved)
            + ";supplied=" + Join(supplied)
            + ";candidates=" + string.Join("|", records);
    }

    public static void Write(string requestId,
        IReadOnlyList<ReflectionProjectionObservation> observations, string? finalContext)
    {
        if (observations.Count == 0)
            return;
        NativeEventLog.Trace(Kind, "mod", "system",
            "reflection projection supply chain",
            Describe(requestId, observations, finalContext));
    }

    private static string Join(IReadOnlyList<string>? ids) =>
        ids == null || ids.Count == 0 ? "" : string.Join(",", ids);

    private static string Bool(bool value) => value ? "true" : "false";
}
