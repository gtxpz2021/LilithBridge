using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace LilithBridge;

internal sealed record GeneratedReplyMaterial(string Block, IReadOnlyList<GeneratedReplySnapshot> Sources,
    IReadOnlyList<RequestSourceBody> HistoryReferences,
    IReadOnlyList<RequestSourceBody>? BodySources = null,
    IReadOnlyList<MemoryTurn>? CoveredOriginals = null,
    GeneratedReadableView? ReadableView = null, string ReadableFallback = "");

internal sealed partial class MemoryStore
{
    // This is a known, source-free legacy echo produced by this bridge, not an
    // original utterance. Keep its stored audit record; ordinary context uses
    // the qualified dialogue and generated-answer owners instead.
    internal static bool IsLegacyAssistantReplyEcho(MemoryTurn t) => t.Kind == "interaction.memory"
        && t.Role == "lilith" && t.EventId.Length == 0 && t.SourceEventId.Length == 0
        && t.Text.StartsWith("莉莉丝说：", StringComparison.Ordinal)
        && t.Text.EndsWith(" [assistant_reply]", StringComparison.Ordinal);

    internal GeneratedReplyMaterial BuildGeneratedReplyMaterial(string query, BridgeConfig config,
        IReadOnlyList<MemoryTurn> history, string currentUserId, string currentUserText,
        SemanticRecallResult? semantic, bool allowHistoryReferences, bool relevanceForRequest=true)
    {
        bool relevanceActive=config.ContextRelevanceEnabled&&relevanceForRequest&&semantic?.RelevanceBypassed!=true;
        if (MemorySemantics.DeclinesArchiveRecall(query) || !relevanceActive&&MemorySemantics.RequestedHistorySpeaker(query) == "user")
            return new("", Array.Empty<GeneratedReplySnapshot>(), Array.Empty<RequestSourceBody>());
        var available = GetQualifiedGeneratedReplies(query, config);
        var ids = history.Select(t => t.EventId).ToHashSet(StringComparer.Ordinal);
        var owners = LogicalDialogueCatalog.Build(history).Units.Where(u => u.IsMessage)
            .Select(u => u.Key).ToHashSet(StringComparer.Ordinal);
        var selected = available.Where(s => owners.Contains(s.LogicalOwnerKey)
            || s.SourceUserId.Length > 0 && ids.Contains(s.SourceUserId)).ToList();
        if (relevanceActive)
        {
            var plan=semantic?.RelevancePlan;
            bool CarriesAdmittedMessage(GeneratedReplySnapshot source)
            {
                var units=plan?.Selected.Where(i=>i.Unit?.IsMessage==true&&i.Unit.Key==source.LogicalOwnerKey).Select(i=>i.Unit!).ToArray();
                if(units==null||units.Length==0)return false;
                var owner=new ContextRelevanceAnswerOwner(source,GeneratedMappedOriginals(source));
                return units.Any(owner.Covers);
            }
            foreach(var source in available.Where(s=>plan?.Admits("generated/"+s.Key)==true||CarriesAdmittedMessage(s)))
                if(selected.All(s=>s.Key!=source.Key))selected.Add(source);
        }
        else if (SemanticResultMatches(query, semantic, SemanticDomain.Events))
            foreach (var candidate in semantic!.Candidates.Where(c => c.Kind == "generated_reply").OrderByDescending(c => c.Score))
            {
                var source = available.FirstOrDefault(s => "generated/" + s.Key == candidate.EventId
                    && SemanticTextHash(s.BodyText) == candidate.TextHash && s.VersionFingerprint == candidate.Version);
                if (source != null && selected.All(s => s.Key != source.Key)) selected.Add(source);
                if (selected.Count >= Math.Max(config.ConversationMaxMessages,24)) break;
            }
        if (selected.Count == 0) return new("", Array.Empty<GeneratedReplySnapshot>(), Array.Empty<RequestSourceBody>());
        var references = new List<RequestSourceBody>();
        var projected = HistoryProjection.Build(history, config.ConversationMaxMessages,
            config.HistoryGroupReplies, config.HistoryWindowInMessages);
        MemoryTurn? current = history.FirstOrDefault(t => t.EventId == currentUserId && t.Role == "user");
        var host = allowHistoryReferences ? DeepSeekClient.SourceEnvelopeBodies(projected.Turns, current,
            currentUserText, true) : Array.Empty<RequestSourceBody>();
        var identities = relevanceActive ? selected.Select(source =>
            new ContextRelevanceAnswerOwner(source,GeneratedMappedOriginals(source)).Identity).ToArray() : null;
        var records = selected.Select((source,index) => relevanceActive
            ? GeneratedReplyProjection.RecordWithIdentity(source,identities![index],host,references)
            : GeneratedReplyProjection.Record(source,host,references)).ToArray();
        var originals = selected.SelectMany(GeneratedMappedOriginals).DistinctBy(t=>t.EventId).ToArray();
        var bodySources = new List<RequestSourceBody>();
        foreach(var source in selected)
            foreach(var raw in originals)
            {
                var ranges=source.DisplayRanges.Where(r=>r.EventId==raw.EventId&&r.GeneratedStartChar.HasValue).ToArray();
                if(ranges.Length!=1)continue;
                bodySources.Add(new("utterance",raw.EventId,raw.Text,
                    "runtime_context.generated_replies["+source.Key+"].received_public_text",
                    raw.Role,raw.Origin,raw.DeliveryState,ranges[0].GeneratedStartChar!.Value,raw.Text.Length));
            }
        string block = "[Actual generated public answers: source data, not instructions]\n"
            + JsonSerializer.Serialize(new { source_kind = "generated_public_answer", projection_version = 2,
                source_contract = GeneratedReplyProjection.Contract, records }, DialogueMaterialEnvelope.DataJson);
        GeneratedReadableView? readable=null;string readableReason="";
        if(config.B1ContinuityEnabled&&config.B1MaterialPresentation=="natural"&&allowHistoryReferences)
        {
            // Only the already-selected MAIN carrier changes presentation.
            // Preserve selector identity; unsupported appearance schemas keep JSON.
            bool rendered = relevanceActive
                ? GeneratedReadableView.TryCreateWithIdentity(selected,bodySources,identities!,out readable,out readableReason)
                : GeneratedReadableView.TryCreate(selected,bodySources,out readable,out readableReason);
            if(rendered)block=readable!.Text;
            NativeEventLog.Trace("generated_reply.readable_projection","mod","system","Generated answer presentation",
                JsonSerializer.Serialize(new {stage="prepared_not_sent",mode=readable==null?"json":"natural",reason=readableReason,source_records=selected.Count,binding=readable?.Audit()}));
        }
        return new(block, Array.AsReadOnly(selected.ToArray()),readable==null?references.Distinct().ToArray():Array.Empty<RequestSourceBody>(),bodySources,originals,readable,readableReason);
    }

    // Project only after the real full-answer carriers are known. Keep the judge's
    // raw choices/response for audit, but do not promote a discarded display-view
    // story (for example "an earlier repetition") into model-facing history.
    internal object[] ContextRelevanceSupplyRationales(ContextRelevancePlan plan,GeneratedReplyMaterial? generated)
    {
        var carriers=(generated?.Sources??Array.Empty<GeneratedReplySnapshot>())
            .Select(s=>new ContextRelevanceAnswerOwner(s,GeneratedMappedOriginals(s))).ToArray();
        return plan.Choices.Select(choice=>
        {
            var item=plan.Snapshot.Candidates.First(i=>i.Key==choice.Key);
            var covered=item.Unit is {} unit?carriers.Where(a=>a.Covers(unit)).ToArray():Array.Empty<ContextRelevanceAnswerOwner>();
            return (object)new {source_id=item.Id,identity=item.Identity?.Wire(),role=choice.Role,current_quote=choice.CurrentQuote,
                rationale_status=covered.Length>0?"display_view_carried_by_received_original;selector_reason_withheld":"unverified_selection_hypothesis;not_historical_fact",
                canonical_source_ids=covered.Select(a=>ContextRelevanceViewIdentity.GeneratedRef(a.Source)).ToArray(),
                reason=covered.Length>0?"This selected displayed view is carried by the qualified received original from the same exact response owner. It is not a separate earlier occurrence; the selector's free historical interpretation is withheld.":choice.Reason};
        }).ToArray();
    }

    // Only exact plan-to-body-to-archived-utterance mappings qualify. A missing
    // receipt or local rewrite does not turn generated language into speech.
    internal IReadOnlyList<MemoryTurn> GeneratedMappedOriginals(GeneratedReplySnapshot source)
    {
        var raw=ReadExperiences();var result=new List<MemoryTurn>();
        foreach(var range in source.DisplayRanges.Where(r=>r.EventId.Length>0&&r.GeneratedStartChar.HasValue))
        {
            var row=raw.FirstOrDefault(t=>t.EventId==range.EventId);
            int at=range.GeneratedStartChar!.Value;
            if(row==null||at<0||at>source.BodyText.Length||row.Text.Length>source.BodyText.Length-at
                ||source.BodyText.Substring(at,row.Text.Length)!=row.Text
                ||!RequestSourceBody.Hash(row.Text).Equals(range.ActualTextHash,StringComparison.OrdinalIgnoreCase)
                ||LogicalDialogueCatalog.RawFingerprint(row)!=range.RawFingerprint)continue;
            result.Add(row.Copy());
        }
        return result;
    }
}

internal static class GeneratedReplyProjection
{
    // Model-facing projection only. Full plan/receipt hashes, fingerprints,
    // timestamps and revisions remain in the immutable qualified host snapshot.
    internal const string Contract = "Selected qualified received public answers, not a complete generation inventory or hidden reasoning. "
        + "body_parts reconstruct the exact received text in order; all offsets/counts are UTF-16. "
        + "confirmed_ranges refer only to exact same-text displayed fragments; renderer-declared whitespace omissions are excluded, never proof the player read them. "
        + "not_confirmed_ranges are the remaining received body ranges: not established as told, and may be unshown or display-unknown; "
        + "terminal unknown/abandoned counts are separate evidence and do not assign those counts to particular ranges. "
        + "other_display_records are not confirmed expression of this generated body. No obligation to say the remainder. "
        + "Network completion does not establish remote generation extent. Generated records are not added to delivered history; "
        + "qualified message read remains available. Player reading is unknown, never inferred from generation, display or openable history. "
        + "recorded_at_utc is the per-display observation timestamp, not when the player read it; "
        + "delivery_ended_at_utc is the delivery terminal timestamp, with null meaning unknown.";

    internal sealed record ConfirmedRange(int Start, int Chars, string EventId, int ChunkIndex, DateTimeOffset RecordedAtUtc);
    internal static bool TryConfirmedRanges(GeneratedReplySnapshot source, GeneratedReplyMetadata metadata,
        GeneratedReplyDisplayRange range, out List<ConfirmedRange> pieces)
    {
        pieces = new();
        int start = range.GeneratedStartChar ?? -1, count = range.GeneratedChars ?? range.Chars;
        if (range.MappingState != "exact_display_plan" || range.EventId.Length == 0
            || range.RawFingerprint.Length == 0 || start < 0 || count <= 0 || range.Chars <= 0
            || start > source.BodyText.Length || count > source.BodyText.Length - start) return false;
        int end = start + count, at = start;
        foreach (var omission in metadata.WhitespaceOmissions.OrderBy(o => o.GeneratedStartChar))
        {
            int omitStart = omission.GeneratedStartChar;
            long omitEndLong = (long)omitStart + omission.GeneratedChars;
            if (omitEndLong <= start || omitStart >= end) continue;
            // Only the exact renderer-declared whitespace gaps may be removed.
            // A normalized/re-written body is not an exact displayed fragment.
            if (range.PlanVersion.Length == 0 || range.PlanVersion != metadata.DisplayPlanVersion
                || omitStart < 0 || omission.GeneratedChars <= 0 || omitEndLong > source.BodyText.Length
                || !source.BodyText.Substring(omitStart, omission.GeneratedChars).All(char.IsWhiteSpace)) return false;
            int left = Math.Max(start, omitStart), right = Math.Min(end, (int)omitEndLong);
            if (left > at) pieces.Add(new(at, left - at, range.EventId, range.ChunkIndex, range.RecordedAtUtc));
            at = Math.Max(at, right);
        }
        if (at < end) pieces.Add(new(at, end - at, range.EventId, range.ChunkIndex, range.RecordedAtUtc));
        string actual = string.Concat(pieces.Select(piece => source.BodyText.Substring(piece.Start, piece.Chars)));
        if (actual.Length == range.Chars && RequestSourceBody.Hash(actual)
            .Equals(range.ActualTextHash, StringComparison.OrdinalIgnoreCase)) return true;
        pieces.Clear(); return false;
    }

    private static object DisplayEvidence(GeneratedReplySnapshot source)
    {
        var metadata = source.Metadata;
        var confirmed = new List<ConfirmedRange>();
        var other = new List<object>(); int confirmedRecords = 0;
        foreach (var range in metadata.ShownRanges)
        {
            if (TryConfirmedRanges(source, metadata, range, out var pieces))
            { confirmed.AddRange(pieces); confirmedRecords++; }
            else other.Add(new { event_id = range.EventId, chunk_index = range.ChunkIndex, recorded_at_utc = range.RecordedAtUtc, actual_chars = range.Chars,
                mapping_state = range.MappingState, same_text_mapping_verified = false });
        }
        var remaining = new List<object>(); int at = 0;
        foreach (var range in confirmed.OrderBy(r => r.Start))
        {
            if (range.Start > at) remaining.Add(new { start_char = at, char_count = range.Start - at });
            at = Math.Max(at, range.Start + range.Chars);
        }
        if (at < source.BodyText.Length)
            remaining.Add(new { start_char = at, char_count = source.BodyText.Length - at });
        return new {
            mapping_state = metadata.DisplayMappingState, confirmed_display_records = confirmedRecords,
            confirmed_ranges = confirmed.Select(r => new { start_char = r.Start, char_count = r.Chars, utterance_event_id = r.EventId,
                chunk_index = r.ChunkIndex, recorded_at_utc = r.RecordedAtUtc }).ToArray(),
            not_confirmed_ranges = remaining,
            other_display_records = other,
            terminal = metadata.DeliveryTerminal, outcome = metadata.DeliveryOutcome, reason = metadata.DeliveryReason,
            delivery_ended_at_utc = metadata.DeliveryEndedAtUtc,
            unknown_chunks = metadata.UnknownChunks, unknown_chars = metadata.UnknownChars,
            abandoned_chunks = metadata.AbandonedChunks, abandoned_chars = metadata.AbandonedChars
        };
    }

    internal static object Record(GeneratedReplySnapshot source, IReadOnlyList<RequestSourceBody>? host = null,
        List<RequestSourceBody>? used = null, int offset = 0, int chars = -1)
    {
        string body = source.BodyText;
        int count = chars < 0 ? body.Length - offset : Math.Min(chars, body.Length - offset);
        var parts = new List<object>(); int at = offset, end = offset + count;
        foreach (var shown in source.DisplayRanges.Where(r => r.GeneratedStartChar.HasValue)
            .OrderBy(r => r.GeneratedStartChar))
        {
            int start = shown.GeneratedStartChar!.Value;
            if (start < at || shown.Chars > end - start || start >= end) continue;
            var match = host?.Where(b => b.Qualified && b.EventId == shown.EventId
                && b.Chars == shown.Chars && b.Text == body.Substring(start, shown.Chars)
                && RequestSourceBody.Hash(b.Text).Equals(shown.ActualTextHash,StringComparison.OrdinalIgnoreCase)).ToArray();
            if (match?.Length != 1) continue;
            if (start > at) parts.Add(new { generated_offset = at, chars = start-at, text = body[at..start] });
            parts.Add(new { generated_offset = start, chars = shown.Chars, text_ref = match[0].Reference(),
                expression = "confirmed_displayed_fragment;not_a_read_receipt" });
            used?.Add(match[0]); at = start + shown.Chars;
        }
        if (at < end) parts.Add(new { generated_offset = at, chars = end-at, text = body[at..end] });
        var metadata = source.Metadata;
        var record = new {
            message_ref = "generated/" + source.Key, source_kind = "generated_public_answer",
            original_message_ref = source.LogicalOwnerKey,
            source_user_event_id = source.SourceUserId, origin = source.Origin, captured_at_utc = metadata.CapturedAtUtc,
            state_version = source.VersionFingerprint, body_sha256 = source.BodyHash,
            received_body_complete = metadata.NetworkBodyComplete, network_outcome = metadata.NetworkOutcome,
            network_incomplete_reason = metadata.NetworkIncompleteReason,
            total_chars = body.Length, offset, supplied_chars = count, body_parts = parts,
            display_evidence = DisplayEvidence(source)
        };
        if(source.FinalProgress is {} progress)
        {
            var projected=JsonSerializer.SerializeToElement(record,DialogueMaterialEnvelope.DataJson).EnumerateObject()
                .ToDictionary(p=>p.Name,p=>p.Value.Clone(),StringComparer.Ordinal);
            var state=progress.Metadata.PublicExpression!;
            projected["public_final_continuation"]=JsonSerializer.SerializeToElement(new {
                generation_id=state.FinalProgress!.GenerationId,checkpoint_key=progress.Key,checkpoint_revision=state.FinalProgress.Revision,
                state=state.FinalProgress.State,appearance_ranges=state.Ranges,observed_appearance=state.StreamAppearances,
                meaning="Same complete answer, with early whole-bubble appearance evidence. These ranges are distinct from raw-history confirmed_ranges; not a second answer, independent memory, player-read receipt or automatic replay."
            },DialogueMaterialEnvelope.DataJson);
            return projected;
        }
        return metadata.PublicExpression==null?record:PublicExpressionProjection.Enrich(record,source);
    }

    // Opt-in projection only. Keep Record's original five-argument reflection
    // entry and exact legacy object shape unchanged, including absent fields.
    internal static object RecordWithIdentity(GeneratedReplySnapshot source,ContextRelevanceViewIdentity identity,
        IReadOnlyList<RequestSourceBody>? host=null,List<RequestSourceBody>? used=null,int offset=0,int chars=-1)
    {
        var record=JsonSerializer.SerializeToElement(Record(source,host,used,offset,chars),DialogueMaterialEnvelope.DataJson)
            .EnumerateObject().ToDictionary(p=>p.Name,p=>p.Value.Clone(),StringComparer.Ordinal);
        record.Add("generation_attempt_id",JsonSerializer.SerializeToElement(source.AttemptId));
        record.Add("identity",JsonSerializer.SerializeToElement(identity.Wire(),DialogueMaterialEnvelope.DataJson));
        return record;
    }
}

internal sealed partial class DialogueRequest
{
    private readonly object _generatedSupplyLock = new();
    private GeneratedReplySnapshot[] _generatedCurrent = Array.Empty<GeneratedReplySnapshot>();
    private readonly Dictionary<string,GeneratedReplySnapshot> _generatedSeen = new(StringComparer.Ordinal);
    private bool _generatedGuardInstalled;
    internal bool HasGeneratedReplySupplyGuard => _generatedGuardInstalled;
    internal GeneratedReplySnapshot[] GeneratedReplyAllSeen { get { lock(_generatedSupplyLock)return _generatedSeen.Values.ToArray(); } }
    private GeneratedReplyMaterial? _generatedMaterial;
    internal IReadOnlyList<GeneratedReplySnapshot> GeneratedReplySupply {
        get { lock (_generatedSupplyLock) return _generatedCurrent.ToArray(); }
    }
    internal void BindGeneratedReplyMaterial(MemoryStore store, GeneratedReplyMaterial material, string query, BridgeConfig config, bool append = false)
    {
        lock (_generatedSupplyLock)
        {
            if (HttpAttempts == 0) _generatedSeen.Clear();
            _generatedCurrent = (append ? _generatedCurrent.Concat(material.Sources) : material.Sources)
                .GroupBy(s => s.Key, StringComparer.Ordinal).Select(g => g.Last()).ToArray();
            foreach (var source in _generatedCurrent) _generatedSeen[source.Key] = source;
            if(!append)_generatedMaterial=material;
            if (_generatedGuardInstalled) return;
            _generatedGuardInstalled = true;
            AddStorySupply(() => {
                GeneratedReplySnapshot[] current, seen;
                lock (_generatedSupplyLock) { current = _generatedCurrent.ToArray(); seen = _generatedSeen.Values.ToArray(); }
                // Current state must match the rendered receipt. Earlier body
                // exposure remains a live permission dependency as state advances.
                if(ReviewDataRoot.Length>0&&ReviewDataRoot!=DataPaths.Root())return false;
                return current.All(s => store.TryUseGeneratedReply(s,query,config,()=>true))
                    && seen.All(s => store.GetExactGeneratedReply(s.Key,query,config) is { } now
                        && now.BodyHash == s.BodyHash && now.BodyText == s.BodyText);
            });
        }
    }
    internal GeneratedReadableView? CurrentGeneratedReadable
    {get{lock(_generatedSupplyLock)return _generatedMaterial?.ReadableView;}}
    internal IReadOnlyList<GeneratedReadableBody> GeneratedReadableBodiesFor(string actualContext)
    {lock(_generatedSupplyLock)return _generatedMaterial?.ReadableView is {} view&&view.ContainsExactlyOnce(actualContext)?view.Bodies:Array.Empty<GeneratedReadableBody>();}
    internal IReadOnlyList<RequestSourceBody> JsonCompatibleBodySources(IReadOnlyList<RequestSourceBody> bodies,string actualContext)
    {
        lock(_generatedSupplyLock)
        {
            if(_generatedMaterial?.ReadableView is not {} view||!view.ContainsExactlyOnce(actualContext))return bodies;
            // These virtual JSON locators no longer exist in the natural host.
            // Keep the bodies for program tracking, but JSON fallback must inline.
            return view.JsonCompatibleBodies(bodies);
        }
    }
    internal IReadOnlyList<RequestSourceBody> GeneratedBodySourcesFor(string actualContext)
    {
        lock(_generatedSupplyLock)
            return _generatedMaterial is {} material&&material.Block.Length>0
                &&actualContext.Contains(material.Block,StringComparison.Ordinal)
                ?material.BodySources??Array.Empty<RequestSourceBody>():Array.Empty<RequestSourceBody>();
    }
}

internal sealed partial class BridgeController
{
    // ApiResult.Text is the parsed public answer field. Never inspect protocol
    // RawText or provider reasoning to reconstruct unavailable public text.
    private void CaptureReceivedPublicReply(ApiResult? result)
    {
        if (result == null || result.IsLocalFallback || result.IsMemoryOperation || result.Text.Length == 0
            || result.Response.AttemptId.Length == 0) return;
        try {
            _memory.CaptureGeneratedReply(result);
            if (result.DialogueRequest?.IsCancelled == true)
                _memory.RecordGeneratedEnd(result,"owner_cancelled_before_expression");
        }
        catch (Exception error) { NativeEventLog.Trace("generated_reply.capture_error","mod","system",
            "Public answer capture failed", error.GetType().Name); }
    }
}
