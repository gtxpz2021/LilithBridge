using System;

namespace LilithBridge;

/// <summary>
/// One decision of the delivery-time auxiliary extraction gate. Reason is a
/// short machine-readable string used by the aggregate diagnostic; it never
/// contains player text.
/// </summary>
internal sealed record AutonomousExtractionDecision(bool ShouldSchedule, string Reason)
{
    public string Decision => ShouldSchedule ? "schedule" : "skip";

    public string Describe() => "decision=" + Decision + ";reason=" + Reason;
}

/// <summary>
/// Delivery-time gate for the phase-2 auxiliary memory extractor.
///
/// The old controller condition skipped extraction whenever the main model had
/// produced any autonomous memory candidate, even when the program authority
/// rejected every one of them (for example because EvidenceSource=assistant is
/// never user evidence). This pure policy separates "proposed a candidate" from
/// "the candidate was actually written":
///
/// - a turn whose main-model candidates reached the configured writer at least
///   once, and that has no removals waiting at the existing legacy boundary, is
///   considered already handled and does not need the auxiliary pass;
/// - everything else keeps the previous switches: autonomous memory off, user
///   write off, no source user event, the per-turn extract switch and the
///   conservative "looks memory worthy" heuristic.
///
/// Authority, write scope and retry rules are not changed here; this only
/// decides whether the existing auxiliary request may be scheduled.
/// </summary>
internal static class AutonomousExtractionPolicy
{
    public static AutonomousExtractionDecision Evaluate(
        bool autonomousMemoryEnabled,
        bool memoryUserWriteEnabled,
        bool hasSourceUserEvent,
        int mainModelCandidateWrites,
        int removals,
        bool extractEveryTurn,
        bool looksMemoryWorthy)
    {
        if (!autonomousMemoryEnabled)
            return new AutonomousExtractionDecision(false, "autonomous_memory_disabled");
        if (!memoryUserWriteEnabled)
            return new AutonomousExtractionDecision(false, "user_write_disabled");
        if (!hasSourceUserEvent)
            return new AutonomousExtractionDecision(false, "no_source_user_event");
        // A successful main-model write already handled this turn's candidate.
        // Removals are still the auxiliary pass's job (legacy boundary), so a
        // turn that also asked for a removal is not considered covered.
        if (mainModelCandidateWrites > 0 && removals == 0)
            return new AutonomousExtractionDecision(false, "main_model_writes_already_processed");
        if (!extractEveryTurn && !looksMemoryWorthy)
            return new AutonomousExtractionDecision(false, "not_memory_worthy");
        if (removals > 0)
            return new AutonomousExtractionDecision(true, "removals_present_keep_auxiliary");
        return new AutonomousExtractionDecision(
            true, extractEveryTurn ? "extract_every_turn" : "memory_worthy");
    }
}
