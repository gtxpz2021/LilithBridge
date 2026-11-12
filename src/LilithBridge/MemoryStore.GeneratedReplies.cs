using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LilithBridge;

internal sealed partial class MemoryStore
{
    private readonly object _generatedRepliesLock = new();
    // Parsed immutable storage only, never source qualification. Each load still
    // reads both current files and compares exact bytes, including same-size or
    // timestamp-preserving edits. Access is serialized by _generatedRepliesLock.
    private sealed record GeneratedReadCacheEntry(byte[] MetadataBytes, byte[] BodyBytes,
        GeneratedReplyMetadata Metadata, GeneratedReplySnapshot Snapshot);
    private readonly Dictionary<string, GeneratedReadCacheEntry> _generatedReadCache = new(StringComparer.Ordinal);
    private long _generatedReadCacheBytes;
    private void RememberGeneratedRead(string key, byte[] metadataBytes, byte[] bodyBytes,
        GeneratedReplyMetadata metadata, GeneratedReplySnapshot snapshot)
    {
        long bytes = (long)metadataBytes.Length + bodyBytes.Length;
        if (bytes > 8 * 1024 * 1024) return;
        if (_generatedReadCache.Remove(key, out var old))
            _generatedReadCacheBytes -= (long)old.MetadataBytes.Length + old.BodyBytes.Length;
        if (_generatedReadCache.Count >= 64 || _generatedReadCacheBytes + bytes > 8 * 1024 * 1024)
        { _generatedReadCache.Clear(); _generatedReadCacheBytes = 0; }
        _generatedReadCache[key] = new(metadataBytes, bodyBytes, metadata, snapshot);
        _generatedReadCacheBytes += bytes;
    }
    private static readonly UTF8Encoding GeneratedUtf8 = new(false, true);
    private string GeneratedRepliesDirectory => Path.Combine(_root, "sessions", "generated-public-v1");
    internal static string GeneratedReplyKey(string session, string request, string attempt, string sourceUser)
        => "generated/" + SemanticTextHash(JsonSerializer.Serialize(new[] { session, request, attempt, sourceUser }));
    private string GeneratedKey(ApiResult reply) => GeneratedReplyKey(_sessionId, reply.RequestId,
        reply.Response.AttemptId, reply.SourceUserEventId);
    private static bool ValidGeneratedKey(string key) => key != null && key.Length == 74 && key.StartsWith("generated/", StringComparison.Ordinal)
        && key.AsSpan(10).ToString().All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private string GeneratedPath(string key, string extension) => Path.Combine(GeneratedRepliesDirectory, key[10..] + extension);
    private static string GeneratedHash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    // Call before Clean/rewrite. Only ApiResult.Text is accepted; provider raw
    // responses, transport buffers and ReasoningContent are never inspected.
    internal GeneratedReplyWriteResult CaptureGeneratedReply(ApiResult reply, BubbleDisplayPlan? plan = null)
    {
        if (!GeneratedCaptureScopeMatches(reply)) return new(false, "policy_scope_changed");
        if (!GeneratedConfigAllows(reply.GenerationConfig)) return new(false, "policy_storage_disabled");
        if (reply.IsLocalFallback) return new(false, "local_expression_not_provider_generation");
        if (string.IsNullOrEmpty(reply.Text)) return new(false, "no_received_public_text");
        if (string.IsNullOrEmpty(reply.RequestId) || string.IsNullOrEmpty(reply.Response.AttemptId)) return new(false, "identity_unavailable");
        string key = GeneratedKey(reply);
        lock (_experienceLock)
        lock (_generatedRepliesLock)
        {
            try
            {
                byte[] body = GeneratedUtf8.GetBytes(reply.Text); string hash = GeneratedHash(body);
                Directory.CreateDirectory(GeneratedRepliesDirectory);
                if (File.Exists(GeneratedPath(key, ".json")))
                {
                    var old = LoadGenerated(key);
                    if (old != null && old.BodyHash == hash && old.Body == reply.Text)
                    { LinkReflectionGeneratedPublic(reply, key); return new(true, "already_saved", key, hash); }
                    return new(false, "immutable_body_conflict_or_unreadable", key, hash);
                }
                string bodyPath = GeneratedPath(key, ".body.txt");
                bool sourceBound=reply.PublicExpressionFootprint?.GeneratedSources.Length>0;
                if (File.Exists(bodyPath))
                {
                    if (!File.ReadAllBytes(bodyPath).SequenceEqual(body)) return new(false, "orphan_body_conflict", key, hash);
                }
                else if(!sourceBound) AtomicGeneratedWrite(bodyPath, body, overwrite: false);
                var metadata = new GeneratedReplyMetadata {
                    SchemaVersion=sourceBound?2:1,
                    Key = key, SessionId = _sessionId, RequestId = reply.RequestId, AttemptId = reply.Response.AttemptId,
                    SourceUserEventId = reply.SourceUserEventId, SourceUserFingerprint = GeneratedSourceUserFingerprint(reply.SourceUserEventId),
                    Origin = reply.IsLocalFallback ? LocalFallbackOrigin : "model",
                    BodyHash = hash, BodyChars = reply.Text.Length, BodyUtf8Bytes = body.Length,
                    CapturedAtUtc = DateTimeOffset.UtcNow, NetworkBodyComplete = reply.Response.BodyComplete,
                    NetworkOutcome = reply.Response.Outcome, NetworkTerminalEvent = reply.Response.TerminalEvent,
                    NetworkFinishReason = reply.Response.FinishReason, NetworkIncompleteReason = reply.Response.IncompleteReason,
                    PublicResponseItem=reply.PublicResponseItem,PublicResponseItems=reply.PublicResponseItems,FinalPublicCanonical=reply.Text==reply.FinalPublicDraft?.Body?reply.FinalPublicDraft.Canonical:null
                };
                if(sourceBound)metadata.PublicExpressionFinalSources=new(_root,reply.PublicExpressionFootprint!,SemanticTextHash(JsonSerializer.Serialize(reply.PublicExpressionFootprint)));
                if(sourceBound&&reply.PublicExpressionFootprint!.Complete&&!PublicExpressionCaptureSourcesCurrent(reply.PublicExpressionFootprint))
                    return new(false,"expression_sources_changed_before_final_archive",key);
                ApplyGeneratedPlan(metadata, reply.Text, plan);
                SaveGeneratedMetadata(metadata);
                if(sourceBound&&!File.Exists(bodyPath))AtomicGeneratedWrite(bodyPath,body,overwrite:false);
                LinkReflectionGeneratedPublic(reply, key);
                return new(true, "saved", key, hash);
            }
            catch (Exception error) { return GeneratedFailure("capture", key, error); }
        }
    }

    internal GeneratedReplyWriteResult RecordGeneratedDisplay(ApiResult reply, MemoryTurn? rawTurn, int chunkIndex, string actualText)
    {
        string key = GeneratedKey(reply);
        if (!GeneratedConfigAllows(reply.GenerationConfig)) return new(false, "policy_storage_disabled", key);
        lock (_generatedRepliesLock)
        {
            try
            {
                var snapshot = LoadGenerated(key);
                if (snapshot == null) return new(false, "body_not_saved", key);
                var metadata = snapshot.Metadata; var plan = reply.Delivery?.DisplayPlan;
                if(reply.Delivery?.ImportedStreamAppearance.Count>0)metadata.StreamAppearanceImports=reply.Delivery.ImportedStreamAppearance.ToArray();
                ApplyGeneratedPlan(metadata, snapshot.Body, plan);
                var shown = reply.Delivery?.ShownChunks.LastOrDefault(c => c.Index == chunkIndex && c.Text == actualText);
                bool exactRaw = rawTurn != null && rawTurn.EventId == reply.RequestId + ":" + chunkIndex
                    && rawTurn.SourceEventId == metadata.SourceUserEventId && rawTurn.SessionId == metadata.SessionId
                    && rawTurn.Role == "assistant" && rawTurn.DeliveryState == "delivered" && rawTurn.Text == actualText;
                bool mapped = plan is { IsValid: true } && plan.GeneratedText == snapshot.Body
                    && shown != null && shown.PlanVersion == plan.Version && shown.StartChar >= 0
                    && plan.Chunks.Any(c => c.Index == chunkIndex && c.Text == actualText && c.StartChar == shown.StartChar
                        && c.GeneratedChars == shown.GeneratedChars && c.DisplayStartChar == shown.DisplayStartChar);
                string state = !exactRaw ? "raw_event_identity_unverified" : mapped ? "exact_display_plan"
                    : plan != null && plan.GeneratedText != snapshot.Body ? "different_final_expression" : "display_mapping_unverified";
                string eventId = exactRaw ? rawTurn!.EventId : "";
                var range = new GeneratedReplyDisplayRange(eventId, exactRaw ? LogicalDialogueCatalog.RawFingerprint(rawTurn!) : "",
                    chunkIndex, GeneratedHash(GeneratedUtf8.GetBytes(actualText)), actualText.Length, shown?.PlanVersion ?? plan?.Version ?? "",
                    state, mapped ? shown!.StartChar : null, mapped ? shown!.GeneratedChars : null,
                    mapped ? shown!.DisplayStartChar : null, reply.Delivery?.ImportedStreamAppearance.FirstOrDefault(e=>e.Index==chunkIndex&&e.Text==actualText)?.ObservedAtUtc??DateTimeOffset.UtcNow);
                var same = metadata.ShownRanges.Where(r => r.ChunkIndex == range.ChunkIndex
                    && r.ActualTextHash == range.ActualTextHash && r.PlanVersion == range.PlanVersion).ToArray();
                if (same.Any(r => r.EventId == range.EventId && r.MappingState == range.MappingState)
                    || !exactRaw && same.Any(r => r.EventId.Length > 0))
                    return new(true, "display_already_recorded", key, snapshot.BodyHash);
                // A presenter callback can precede the raw archive callback.
                // Only that exact chunk/hash/plan can be upgraded; conflicting
                // shown evidence is retained independently and fails supply.
                var pending = exactRaw ? same.FirstOrDefault(r => r.EventId.Length == 0) : null;
                metadata.ShownRanges = pending != null
                    ? metadata.ShownRanges.Select(r => ReferenceEquals(r,pending) ? range with { RecordedAtUtc = r.RecordedAtUtc } : r).ToArray()
                    : metadata.ShownRanges.Append(range).ToArray(); metadata.Revision++;
                SaveGeneratedMetadata(metadata);
                return new(true, "display_recorded", key, snapshot.BodyHash);
            }
            catch (Exception error) { return GeneratedFailure("display", key, error); }
        }
    }

    internal GeneratedReplyWriteResult RecordGeneratedEnd(ApiResult reply, string outcome)
    {
        string key = GeneratedKey(reply);
        if (!GeneratedConfigAllows(reply.GenerationConfig)) return new(false, "policy_storage_disabled", key);
        lock (_generatedRepliesLock)
        {
            try
            {
                var snapshot = LoadGenerated(key); if (snapshot == null) return new(false, "body_not_saved", key);
                var metadata = snapshot.Metadata; var delivery = reply.Delivery;
                ApplyGeneratedPlan(metadata, snapshot.Body, delivery?.DisplayPlan);
                bool terminal = delivery?.IsTerminal == true || !string.IsNullOrEmpty(outcome);
                if (!terminal) return new(false, "terminal_not_observed", key, snapshot.BodyHash);
                string effectiveOutcome = delivery?.IsTerminal == true ? delivery.Outcome : outcome;
                if (metadata.DeliveryTerminal && metadata.DeliveryOutcome == effectiveOutcome
                    && metadata.DeliveryReason == (delivery?.Reason ?? "delivery_record_unavailable")
                    && metadata.UnknownChunks == (delivery?.UnknownChunks ?? 0) && metadata.UnknownChars == (delivery?.UnknownChars ?? 0)
                    && metadata.AbandonedChunks == (delivery?.AbandonedChunks ?? 0) && metadata.AbandonedChars == (delivery?.AbandonedChars ?? 0)
                    && snapshot.Metadata.DisplayPlanVersion == metadata.DisplayPlanVersion
                    && snapshot.Metadata.PresenterEffects == reply.PresenterEffects)
                    return new(true,"end_already_recorded",key,snapshot.BodyHash);
                metadata.DeliveryTerminal = true; metadata.DeliveryOutcome = effectiveOutcome;
                metadata.DeliveryReason = delivery?.Reason ?? "delivery_record_unavailable";
                metadata.DeliveryEndedAtUtc = delivery?.CompletedAtUtc ?? DateTimeOffset.UtcNow;
                metadata.UnknownChunks = delivery?.UnknownChunks ?? 0; metadata.UnknownChars = delivery?.UnknownChars ?? 0;
                metadata.AbandonedChunks = delivery?.AbandonedChunks ?? 0; metadata.AbandonedChars = delivery?.AbandonedChars ?? 0;
                metadata.PresenterEffects=reply.PresenterEffects;
                metadata.Revision++; SaveGeneratedMetadata(metadata);
                return new(true, "end_recorded", key, snapshot.BodyHash);
            }
            catch (Exception error) { return GeneratedFailure("end", key, error); }
        }
    }

    private static void ApplyGeneratedPlan(GeneratedReplyMetadata metadata, string body, BubbleDisplayPlan? plan)
    {
        if (plan == null) return;
        metadata.DisplayPlanVersion = plan.Version;
        metadata.DisplayMappingState = plan.GeneratedText != body ? "different_final_expression"
            : plan.IsValid ? "exact_display_plan" : "display_plan_invalid";
        metadata.PlanChunks = plan.Chunks.Select(c => new GeneratedReplyPlanChunk(c.Index,c.StartChar,c.GeneratedChars,
            c.DisplayStartChar,c.Chars,SemanticTextHash(c.Text))).ToArray();
        metadata.WhitespaceOmissions = plan.WhitespaceOmissions.ToArray();
    }

    private void AtomicGeneratedWrite(string path, byte[] bytes, bool overwrite)
    {
        InvalidateSourceValidation();
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.Write(bytes,0,bytes.Length); file.Flush(true); }
            File.Move(temporary,path,overwrite);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private void SaveGeneratedMetadata(GeneratedReplyMetadata metadata) => AtomicGeneratedWrite(GeneratedPath(metadata.Key,".json"),
        GeneratedUtf8.GetBytes(JsonSerializer.Serialize(metadata)),true);
    private GeneratedReplyWriteResult GeneratedFailure(string stage, string key, Exception error)
    {
        // Diagnostic only; failures cannot block display or claim durable save.
        NativeEventLog.Trace("generated_reply.store_failure","mod","system","generated public reply storage failed",
            "stage="+stage+";key="+key+";error="+error.GetType().Name);
        return new(false,stage+"_io_or_validation_failure:"+error.GetType().Name,key);
    }
    private GeneratedReplySnapshot? LoadGenerated(string key) => LoadGeneratedForValidation(key);
    private GeneratedReplySnapshot? LoadGeneratedCore(string key)
    {
        lock (_generatedRepliesLock) return LoadGeneratedCoreLocked(key);
    }
    private GeneratedReplySnapshot? LoadGeneratedCoreLocked(string key)
    {
        if (!ValidGeneratedKey(key)) return null;
        byte[] metadataBytes = File.ReadAllBytes(GeneratedPath(key,".json"));
        byte[] bytes = File.ReadAllBytes(GeneratedPath(key,".body.txt"));
        if (_generatedReadCache.TryGetValue(key, out var cached)
            && metadataBytes.AsSpan().SequenceEqual(cached.MetadataBytes)
            && bytes.AsSpan().SequenceEqual(cached.BodyBytes))
        {
            // Canonical links depend on another live file version. Revalidate
            // that link even when this record's own bytes are unchanged.
            if (!ValidatePublicFinalCanonical(cached.Metadata, cached.Snapshot.Body, out var linked)) return null;
            if (linked?.VersionFingerprint == cached.Snapshot.FinalProgress?.VersionFingerprint)
                return cached.Snapshot;
            var revised = new GeneratedReplySnapshot(cached.Metadata, cached.Snapshot.Body, linked);
            RememberGeneratedRead(key, metadataBytes, bytes, cached.Metadata, revised);
            return revised;
        }
        // Preserve the original StreamReader BOM/encoding behavior on a miss.
        using var reader = new StreamReader(new MemoryStream(metadataBytes), GeneratedUtf8, true);
        var metadata = JsonSerializer.Deserialize<GeneratedReplyMetadata>(reader.ReadToEnd());
        if (metadata == null || metadata.SchemaVersion is not (1 or 2) || metadata.Key != key || metadata.Revision < 1
            || metadata.SessionId.Length == 0 || metadata.RequestId.Length == 0 || metadata.AttemptId.Length == 0
            || metadata.ShownRanges == null || metadata.PlanChunks == null || metadata.WhitespaceOmissions == null
            || (metadata.PublicExpression?.FinalProgress is {} progress?PublicFinalCheckpointKey(progress):metadata.SchemaVersion==2&&metadata.PublicExpression?.StreamItem is {} stream
                ?PublicStreamKey(metadata.SessionId,metadata.RequestId,metadata.AttemptId,metadata.SourceUserEventId,stream)
                :GeneratedReplyKey(metadata.SessionId,metadata.RequestId,metadata.AttemptId,metadata.SourceUserEventId)) != key) return null;
        string body = GeneratedUtf8.GetString(bytes);
        if (!(body.Length == metadata.BodyChars && bytes.Length == metadata.BodyUtf8Bytes && GeneratedHash(bytes) == metadata.BodyHash
            && ValidatePublicFinalCanonical(metadata,body,out var related)
            && (metadata.SchemaVersion == 1 ? metadata.PublicExpression == null&&metadata.PublicExpressionFinalSources==null
                : metadata.PublicExpression!=null&&metadata.PublicExpressionFinalSources==null?ValidatePublicExpression(metadata,body)
                : metadata.PublicExpression==null&&ValidatePublicExpressionFinalClosure(metadata)))) return null;
        var snapshot = new GeneratedReplySnapshot(metadata, body, related);
        RememberGeneratedRead(key, metadataBytes, bytes, metadata, snapshot);
        return snapshot;
    }

    internal GeneratedReplySnapshot? ReadGeneratedReply(string key)
    {
        using var scope=BeginMentionRequest(null);
        lock (_experienceLock)
        lock (_generatedRepliesLock)
        {
            using var validation=BeginSourceValidation();
            try { var snapshot=LoadGenerated(key); return snapshot != null && QualifyGeneratedReply(snapshot).Permitted ? snapshot : null; }
            catch { return null; }
        }
    }
    internal IReadOnlyList<GeneratedReplySnapshot> ReadGeneratedReplies()
    {
        if (!_longTermEnabled || !_historyArchiveRequired || !Directory.Exists(GeneratedRepliesDirectory)) return Array.Empty<GeneratedReplySnapshot>();
        lock (_experienceLock)
        lock (_generatedRepliesLock)
        {
            using var mention=BeginMentionRequest(null);
            using var validation=BeginSourceValidation();
            try { return Directory.EnumerateFiles(GeneratedRepliesDirectory,"*.json").Select(p=>"generated/"+Path.GetFileNameWithoutExtension(p))
                .Select(ReadGeneratedReply).Where(s=>s!=null).Select(s=>s!)
                .GroupBy(s=>s.Metadata.FinalPublicCanonical?.GenerationId??s.Metadata.PublicExpression?.FinalProgress?.GenerationId??s.Key)
                .Select(g=>g.OrderByDescending(s=>s.Metadata.FinalPublicCanonical!=null).ThenByDescending(s=>s.Metadata.PublicExpression?.FinalProgress?.Revision??0).ThenByDescending(s=>s.Metadata.CapturedAtUtc).First())
                .OrderBy(s=>s.Metadata.CapturedAtUtc).ToArray(); }
            catch { return Array.Empty<GeneratedReplySnapshot>(); }
        }
    }
    internal GeneratedReplyQualification QualifyGeneratedReply(GeneratedReplySnapshot snapshot)
    {
        if (!_longTermEnabled || !_historyArchiveRequired) return new(false,"policy_storage_disabled");
        using var scope=BeginMentionRequest(null);
        lock (_experienceLock)
        lock (_generatedRepliesLock)
        {
            using var validation=BeginSourceValidation();
            return QualifyGeneratedReplyInScope(snapshot);
        }
    }
    private GeneratedReplyQualification QualifyGeneratedReplyCore(GeneratedReplySnapshot snapshot)
    {
            try
            {
                var fresh=LoadGenerated(snapshot.Key);
                if (fresh == null || fresh.VersionFingerprint != snapshot.VersionFingerprint || fresh.Body != snapshot.Body)
                    return new(false,"stored_body_or_state_changed");
                var m=fresh.Metadata; var denied=SuppressedExperienceIds();
                if (m.SchemaVersion == 2 && !PublicExpressionSourcesPermitted(fresh))
                    return new(false,"public_expression_source_unavailable_or_not_transferable");
                var raw=GetExperienceValidationView();
                // Display metadata can fail independently after actual display.
                // Exact owner-linked raw events therefore remain a permission
                // closure even when their display receipt was not durable. This
                // does not invent an attempt-level display or read receipt.
                var ownerRaw=raw.GetOwnerReplyEvents(m.SessionId,m.SourceUserEventId,m.Origin,m.RequestId);
                var ids=m.ShownRanges.Select(r=>r.EventId).Concat(ownerRaw.Select(t=>t.EventId)).Append(m.SourceUserEventId)
                    .Where(id=>id.Length>0).Distinct(StringComparer.Ordinal).ToArray();
                if (ids.Any(denied.Contains) || denied.Any(id=>id.StartsWith(m.RequestId+":",StringComparison.Ordinal))
                    || SuppressedTextMatches(fresh.Body)) return new(false,"source_suppressed_or_withdrawn");
                if (m.ShownRanges.Any(r=>r.EventId.Length==0 || r.RawFingerprint.Length==0)) return new(false,"shown_source_identity_unverified");
                foreach(string id in ids)
                {
                    if(!raw.TryGetUnique(id,out var match) || !IsMentionAllowed(match) || !IsNativeSupplyAllowed(match)) return new(false,"source_unavailable_or_not_permitted");
                    string fingerprint=LogicalDialogueCatalog.RawFingerprint(match);
                    if (id == m.SourceUserEventId && (m.SourceUserFingerprint.Length == 0 || match.Role != "user"
                        || fingerprint != m.SourceUserFingerprint)) return new(false,"source_user_changed_or_unverified");
                    foreach(var shown in m.ShownRanges.Where(r=>r.EventId==id))
                        if (fingerprint!=shown.RawFingerprint) return new(false,"shown_source_changed");
                }
                var qualificationView=new MemoryTurn { EventId=m.Key,SourceEventId=m.SourceUserEventId,SourceEventIds=ids,
                    SessionId=m.SessionId,Role="assistant",Origin=m.Origin,Kind="generated.public",Text=fresh.Body,DeliveryState="generated_not_spoken" };
                return IsMentionAllowed(qualificationView)&&IsNativeSupplyAllowed(qualificationView)
                    ? new(true,"generated_public_only;display_ranges_separate;not_told_or_read_receipt") : new(false,"body_not_permitted");
            }
            catch { return new(false,"store_or_permission_unavailable"); }
    }

    private string GeneratedSourceUserFingerprint(string id)
    {
        if (id.Length == 0) return "";
        var fingerprints = _history.Concat(ReadExperiences(includeSuppressed:true)).Where(t=>t.EventId==id && t.Role=="user")
            .Select(LogicalDialogueCatalog.RawFingerprint).Distinct(StringComparer.Ordinal).ToArray();
        return fingerprints.Length == 1 ? fingerprints[0] : "";
    }
    private bool GeneratedConfigAllows(BridgeConfig? config)
    {
        if(!_longTermEnabled||!_historyArchiveRequired||config!=null&&(!config.LongTermMemoryEnabled||!config.ExperienceArchiveEnabled))return false;
        if(string.IsNullOrEmpty(config?.DataDirectory))return true;
        try{return string.Equals(DataPaths.CanonicalPath(config.DataDirectory),DataPaths.CanonicalPath(_root),StringComparison.OrdinalIgnoreCase);}
        catch{return false;}
    }
    private bool GeneratedCaptureScopeMatches(ApiResult reply)
    {
        if (!string.IsNullOrEmpty(reply.DialogueRequest?.ReviewSessionIdentity)
            && reply.DialogueRequest.ReviewSessionIdentity != _sessionId) return false;
        // Late callbacks may outlive a configuration/data-root switch. Explicit
        // request/config provenance may veto this store, never retarget it. Empty
        // evidence stays unknown; relative/invalid evidence is not resolved by
        // guessing a working directory or reviving an earlier store.
        foreach (string? sourceRoot in new[] { reply.DialogueRequest?.ReviewDataRoot,
            reply.DialogueRequest?.ReviewConfigRoot, reply.GenerationConfig?.DataDirectory })
        {
            if (string.IsNullOrEmpty(sourceRoot)) continue;
            try
            {
                if (!string.Equals(DataPaths.CanonicalPath(sourceRoot), DataPaths.CanonicalPath(_root), StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            catch { return false; }
        }
        return true;
    }
    internal IReadOnlyList<GeneratedReplySnapshot> GetQualifiedGeneratedReplies(string? query = null, BridgeConfig? config = null)
    {
        if (!GeneratedConfigAllows(config) || MemorySemantics.DeclinesArchiveRecall(query)) return Array.Empty<GeneratedReplySnapshot>();
        using var scope=BeginMentionRequest(query);
        return ReadGeneratedReplies();
    }
    internal GeneratedReplySnapshot? GetExactGeneratedReply(string key, string? query = null, BridgeConfig? config = null)
    {
        if (!GeneratedConfigAllows(config) || MemorySemantics.DeclinesArchiveRecall(query)) return null;
        using var scope=BeginMentionRequest(query);
        return ReadGeneratedReply(key);
    }
    internal bool TryUseGeneratedReply(GeneratedReplySnapshot snapshot, string? query, BridgeConfig? config, Func<bool> use)
    {
        if (!GeneratedConfigAllows(config)) return false;
        using var scope=BeginMentionRequest(query);
        // Same order as capture/read: source lifecycle then generated state.
        // Revalidate both under the final use boundary; no global last snapshot.
        lock (_experienceLock)
        lock (_generatedRepliesLock)
        {
            if (!QualifyGeneratedReply(snapshot).Permitted) return false;
            // The consumer can mutate memory or generated storage. Its nested
            // reads must start from that state, not this validation's memo.
            return UseAfterSourceValidation(use);
        }
    }
}
