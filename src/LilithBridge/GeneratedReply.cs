using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace LilithBridge;

internal sealed record GeneratedReplyWriteResult(bool Succeeded, string Reason, string Key = "", string BodyHash = "");
internal sealed record GeneratedReplyQualification(bool Permitted, string Reason);
internal sealed record GeneratedReplyDisplayRange(string EventId, string RawFingerprint, int ChunkIndex,
    string ActualTextHash, int Chars, string PlanVersion, string MappingState,
    int? GeneratedStartChar, int? GeneratedChars, int? DisplayStartChar, DateTimeOffset RecordedAtUtc);
internal sealed record GeneratedReplyPlanChunk(int Index, int StartChar, int GeneratedChars,
    int DisplayStartChar, int Chars, string TextHash);

// Public answer body only. This schema has no raw protocol or reasoning field.
// Display evidence refers to exact raw utterances; it never promotes an unseen
// suffix into something the player heard, or into a B1 source write pool.
internal sealed class GeneratedReplyMetadata
{
    public int SchemaVersion { get; set; } = 1;
    public string Key { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string RequestId { get; set; } = "";
    public string AttemptId { get; set; } = "";
    public string SourceUserEventId { get; set; } = "";
    public string SourceUserFingerprint { get; set; } = "";
    public string Origin { get; set; } = "model";
    public string BodyHash { get; set; } = "";
    public int BodyChars { get; set; }
    public int BodyUtf8Bytes { get; set; }
    public DateTimeOffset CapturedAtUtc { get; set; }
    public string PublicTextExtent { get; set; } = "received_public_text_only;remote_generation_extent_not_inferred";
    public bool NetworkBodyComplete { get; set; }
    public string NetworkOutcome { get; set; } = "";
    public string NetworkTerminalEvent { get; set; } = "";
    public string NetworkFinishReason { get; set; } = "";
    public string NetworkIncompleteReason { get; set; } = "";
    public long Revision { get; set; } = 1;
    public string DisplayPlanVersion { get; set; } = "";
    public string DisplayMappingState { get; set; } = "no_display_plan_observed";
    public GeneratedReplyPlanChunk[] PlanChunks { get; set; } = Array.Empty<GeneratedReplyPlanChunk>();
    public BubbleWhitespaceOmission[] WhitespaceOmissions { get; set; } = Array.Empty<BubbleWhitespaceOmission>();
    public GeneratedReplyDisplayRange[] ShownRanges { get; set; } = Array.Empty<GeneratedReplyDisplayRange>();
    public bool DeliveryTerminal { get; set; }
    public string DeliveryOutcome { get; set; } = "not_observed";
    public string DeliveryReason { get; set; } = "";
    public DateTimeOffset? DeliveryEndedAtUtc { get; set; }
    public int UnknownChunks { get; set; }
    public int UnknownChars { get; set; }
    public int AbandonedChunks { get; set; }
    public int AbandonedChars { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public PublicExpressionArchiveState? PublicExpression { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public PublicExpressionFinalClosure? PublicExpressionFinalSources { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public PublicStreamItemIdentity? PublicResponseItem {get;set;}
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public PublicItemReceipt[]? PublicResponseItems {get;set;}
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public PublicFinalCanonical? FinalPublicCanonical {get;set;}
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public StreamAppearanceEvidence[]? StreamAppearanceImports {get;set;}
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public PresenterEffectsReceipt? PresenterEffects {get;set;}
    public GeneratedReplyMetadata Copy() => JsonSerializer.Deserialize<GeneratedReplyMetadata>(JsonSerializer.Serialize(this))!;
}

internal sealed class GeneratedReplySnapshot
{
    private readonly GeneratedReplyMetadata _metadata;
    internal GeneratedReplySnapshot(GeneratedReplyMetadata metadata, string body,GeneratedReplySnapshot? finalProgress=null)
    {
        _metadata = metadata.Copy(); Body = body;
        FinalProgress=finalProgress;
        VersionFingerprint = finalProgress==null?MemoryStore.SemanticTextHash(JsonSerializer.Serialize(_metadata)):
            MemoryStore.SemanticTextHash(JsonSerializer.Serialize(new{metadata=_metadata,linked_progress_version=finalProgress.VersionFingerprint}));
    }
    internal GeneratedReplySnapshot? FinalProgress {get;}
    public GeneratedReplyMetadata Metadata => _metadata.Copy();
    public string Key => _metadata.Key;
    public string Body { get; }
    public string BodyText => Body;
    public string Text => Body;
    public string BodyHash => _metadata.BodyHash;
    public string RequestId => _metadata.RequestId;
    public string AttemptId => _metadata.AttemptId;
    public string SessionId => _metadata.SessionId;
    public string SourceUserId => _metadata.SourceUserEventId;
    public string SourceUserEventId => _metadata.SourceUserEventId;
    public string Origin => _metadata.Origin;
    public string LogicalOwnerKey => "message/" + MemoryStore.SemanticTextHash(JsonSerializer.Serialize(
        _metadata.PublicExpression == null||_metadata.PublicExpression.FinalProgress is {} progress&&progress.State!="deferred_item_closed" ? new[] { SessionId, RequestId, Origin, SourceUserId }
            : new[] { SessionId, RequestId, Origin, SourceUserId, _metadata.PublicExpression.SegmentId }));
    public string Completeness => _metadata.PublicTextExtent + ";network_body_complete=" + _metadata.NetworkBodyComplete.ToString().ToLowerInvariant();
    public string VersionFingerprint { get; }
    public IReadOnlyList<GeneratedReplyDisplayRange> ShownRanges => Array.AsReadOnly(_metadata.ShownRanges.ToArray());
    public IReadOnlyList<GeneratedReplyDisplayRange> DisplayRanges => ShownRanges;
    public string EpistemicStatus => "generated_public_reply;not_player_speech;unshown_ranges_not_told;not_read_receipt";
}
