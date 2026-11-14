using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace LilithBridge;

internal sealed record ContextDiscoveryOptions(string Cursor = "", int PageBytes = 24576, ContextDiscoveryScope? Scope = null);

// Discovery is an inspection page, not a relevance verdict or writable source.
// Both owners retain their original identities: generated public text never
// becomes a delivered utterance merely because MAIN found it in this page.
internal sealed record ContextDiscoveryItem(string Reference, LogicalDialogueUnit? Unit,
    GeneratedReplySnapshot? Generated, int Offset, string Text,
    LogicalDialogueUnit? SourceQuestion = null, bool SourceQuestionBodyIncluded = true, ContextDiscoveryMatch? ScopeMatch = null)
{
    internal string Body => Unit?.Text ?? Generated!.BodyText;
    internal string Version => Unit?.VersionFingerprint ?? Generated!.VersionFingerprint;
    internal string Kind => Unit == null ? "generated_public_answer" : Unit.IsMessage ? "logical_message" : "independent_utterance";
    internal object Wire()
    {
        var payload = new {
        message_ref = Reference, source_kind = Kind, version_fingerprint = Version,
        original_message_ref=Generated?.LogicalOwnerKey,
        body_sha256 = MemoryStore.SemanticTextHash(Body), role = Unit?.Role ?? "assistant",
        origin = Unit?.Origin ?? "generated_public_answer",
        session_id = Unit?.SessionId ?? Generated!.Metadata.SessionId,
        time = ScopeMatch == null ? (Unit?.Timestamp ?? Generated!.Metadata.CapturedAtUtc).ToString("O") : ScopeMatch.ViewAt?.ToString("O"),
        source_user_event_id = Unit?.SourceEventId ?? Generated!.SourceUserId,
        original_user_question_status = SourceQuestion == null ? "not_available_or_not_applicable;not_proof_of_absence"
            : SourceQuestionBodyIncluded ? "exact_qualified_original;historical_context_not_new_input" : "body_omitted_page_capacity;not_proof_of_absence",
        original_user_question = SourceQuestion == null ? null : new {
            event_id = SourceQuestion.AnchorEventId, message_ref = SourceQuestion.Key,
            role = SourceQuestion.Role, origin = SourceQuestion.Origin, session_id = SourceQuestion.SessionId,
            time = ScopeMatch == null ? SourceQuestion.Timestamp.ToString("O") : ScopeMatch.QuestionAt?.ToString("O"), version_fingerprint = SourceQuestion.VersionFingerprint,
            body_sha256 = SourceQuestion.TextHash, total_chars = SourceQuestion.Text.Length,
            body_included = SourceQuestionBodyIncluded, text = SourceQuestionBodyIncluded ? SourceQuestion.Text : null,
            qualification = "exact_source_user_id_context_dependency;not_a_new_recall_seed_or_writable_source_alias",
            next_read = new { mode = "message", message_ref = SourceQuestion.Key, message_body_chars = 0 }
        },
        coverage = Unit?.Coverage ?? "received_public_text;not_all_confirmed_said;player_reading_unknown",
        head_known = Unit?.HeadKnown, tail_known = Unit?.TailKnown, has_gaps = Unit?.HasGaps,
        text_basis = Unit == null ? "exact_received_public_text_utf16" : "qualified_available_logical_view_utf16",
        program_gaps = Unit?.Gaps.Where(g => g.OffsetInUnit < Offset + Text.Length && g.OffsetInUnit + g.Chars > Offset)
            .Select(g => new { offset = g.OffsetInUnit, chars = g.Chars, reason = g.Reason,
                qualification = "program_marker_not_source_text" }).ToArray(),
        offset = Offset, chars = Text.Length, total_chars = Body.Length, text = Text,
        excerpt_complete = Offset == 0 && Text.Length == Body.Length,
        citation_eligibility = "only_supplied_excerpt;locator_not_read_permission;no_writable_source_alias",
        next_read = new { mode = "message", message_ref = Reference, message_body_chars = 0 }
    };
        return ScopeMatch == null ? payload : ContextDiscoveryScope.Attach(payload, "recorded_match", ScopeMatch.Wire());
    }
}

internal sealed record ContextDiscoverySnapshot(string Root, string Session, string PermissionQuery,
    string SearchQuery, string Fingerprint, string SemanticStatus, int CandidateCount, int Start,
    IReadOnlyList<ContextDiscoveryItem> Items, LogicalMessageReadPacket LogicalMessages,
    string NextCursor, int MaxUtf8Bytes, int UnreadableRecords, ContextDiscoveryScope? Scope = null)
{
    internal const int MinPageBytes = 4096, MaxPageBytes = 65536;
    // Repeated on every small inspection page, so keep this bounded. Exact maps
    // stay host-side; existing view/original refs carry request-owner identity,
    // never a claim that all displayed text is covered by one received answer.
    internal const string OwnerViewContract="Generated original_message_ref names its logical request owner; message_ref names this view. Same owner is not another occurrence or an HTTP attempt, nor proof of full displayed-view coverage: exact host event/fingerprint mapping is required. Shared text/user/time never joins owners. Generated tails are not speech/read receipts.";
    internal string CursorAt(int offset) => Fingerprint + ":" + offset;
    internal string Json => Render();
    internal string Render(bool omitted = false)
    {
        var payload = new {
        status = omitted ? "body_omitted" : Items.Count == 0 ? Scope == null ? "no_candidates_in_available_snapshot" : "no_candidates_in_requested_scope" : NextCursor.Length > 0 ? "candidate_page" : "candidate_snapshot_exhausted",
        mode = "discover", reason = omitted ? "existing_history_request_budget" : "main_requested_independent_archive_discovery",
        query = SearchQuery, snapshot_id = Fingerprint,
        read_domain = "qualified_available_archive_dialogue_and_generated_public_answers_read_only",
        owner_view_schema=ContextRelevanceViewIdentity.Schema,owner_view_contract=OwnerViewContract,
        relevance_status = "candidates_only;MAIN_decides;automatic_judge_not_consulted",
        ordering = "semantic_and_lexical_rank_fusion_only;no_relevance_threshold;no_per_kind_quota",
        semantic_status = SemanticStatus, archive_coverage = "unknown;available_qualified_snapshot_only;not_proof_of_absence",
        unreadable_or_malformed_experience_records = UnreadableRecords,
        candidate_count_at_capture = CandidateCount, page_start = Start,
        supplied_candidates = omitted ? 0 : Items.Count,
        utf8_byte_limit = MaxUtf8Bytes,
        candidates = omitted ? Array.Empty<object>() : Items.Select(i => i.Wire()).ToArray(),
        more = omitted ? Start < CandidateCount : NextCursor.Length > 0,
        next_cursor = omitted ? CursorAt(Start) : NextCursor,
        continuation = Scope == null ? "Use discovery_cursor unchanged with the same query. Changed source scope/order expires the cursor. Calls still share the existing per-turn budget."
            : "Copy discovery_cursor with the same query and all scope fields. Changed scope/source/order expires it. Existing per-turn budget applies.",
        completeness_note = "No fixed rank or relevance floor removes candidates. This measured page is an inspection capacity, not all history. Missing, restricted, corrupt, generated-but-not-received, and not-yet-expanded text remains unknown. Read a located message to inspect its full available original; hashes and locators do not mean unseen text was supplied. Generated text is not a told/read receipt. No B1 writing authority is created."
    };
        return ReviewContext.Serialize(Scope == null ? payload : ContextDiscoveryScope.Attach(payload, "requested_scope", Scope.Wire()));
    }
}
