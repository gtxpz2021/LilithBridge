namespace LilithBridge;

// No source bodies, protocol envelopes or reasoning are stored in this closure.
internal sealed record PublicExpressionGeneratedSource(string Key, string BodyHash, string ImmutableIdentity);
internal sealed record PublicExpressionSourceFootprint(bool Complete, string Reason, MethodSource[] Sources,
    PublicExpressionGeneratedSource[] GeneratedSources, bool RequiresReview, DateTimeOffset? OriginalDeadline);
internal sealed class PublicExpressionArchiveState
{
    public string Kind { get; set; } = "temporary_public_expression";
    public string SegmentId { get; set; } = "";
    public string WriterSessionId { get; set; } = "";
    public string SourceScopeRoot { get; set; } = "";
    public long ReceiptRevision { get; set; }
    public string WorkOutcome { get; set; } = "running";
    public string SupersededBy { get; set; } = "";
    public string ImmutableSourceHash { get; set; } = "";
    public WorkExpressionRange[] Ranges { get; set; } = Array.Empty<WorkExpressionRange>();
    public PublicExpressionSourceFootprint Sources { get; set; } = new(false,"source_tracking_unavailable",Array.Empty<MethodSource>(),Array.Empty<PublicExpressionGeneratedSource>(),false,null);
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public PublicStreamItemIdentity? StreamItem {get;set;}
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? StreamSegmentCapacity {get;set;}
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public PublicFinalCheckpoint? FinalProgress {get;set;}
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public StreamAppearanceEvidence[]? StreamAppearances {get;set;}
}
internal sealed record PublicExpressionFinalClosure(string SourceScopeRoot,PublicExpressionSourceFootprint Sources,string ImmutableSourceHash);

internal sealed partial class DialogueRequest
{
    internal GeneratedReplySnapshot[] PublicExpressionCarrySources { get; set; } = Array.Empty<GeneratedReplySnapshot>();
    internal string PublicExpressionCarryText { get; set; } = "";
    internal bool PublicExpressionCarryTracked { get; set; } = true;
    internal bool PublicExpressionExternalPermissionSupplied { get; set; }
    internal bool PublicExpressionExtraContextTracked(string text)
    {
        if (!PublicExpressionCarryTracked) return false;
        if (PublicExpressionCarryText.Length == 0) return string.IsNullOrWhiteSpace(text);
        int at = text.IndexOf(PublicExpressionCarryText,StringComparison.Ordinal);
        return at >= 0 && at == text.LastIndexOf(PublicExpressionCarryText,StringComparison.Ordinal)
            && string.IsNullOrWhiteSpace(text.Remove(at,PublicExpressionCarryText.Length));
    }
}

internal sealed partial class ApiResult
{
    internal PublicExpressionSourceFootprint? PublicExpressionFootprint { get; set; }
}
