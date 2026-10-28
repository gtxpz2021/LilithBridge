using System;
using System.Text.Json.Serialization;

namespace LilithBridge;

/// <summary>
/// A tiny, auditable snapshot of what mattered at the end of a session.
/// It is deliberately NOT a chat transcript and NOT a long-term memory entry.
/// It answers "how does this session resume" rather than "what did we discuss".
/// </summary>
internal sealed class LastSceneSummary
{
    /// <summary>Too-old snapshots become ghosts and must not be injected.</summary>
    public const double MaxAgeHours = 24.0;

    public int SchemaVersion { get; set; } = 1;
    public string[] SourceEventIds { get; set; } = Array.Empty<string>();
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public string NotableEvent { get; set; } = "";
    public string UnfinishedTopic { get; set; } = "";
    // v1/v2's name did not establish that a topic was unfinished. Retain it
    // only for reading old snapshots; v3 writes neutral, source-backed material.
    public string RecentTopic { get; set; } = "";
    public string UserText { get; set; } = "";
    public string AssistantText { get; set; } = "";
    public DateTimeOffset? AssistantTimestamp { get; set; }
    [JsonIgnore]
    public string TopicText => SchemaVersion >= 3 ? RecentTopic : UnfinishedTopic;
    public string BoundaryHint { get; set; } = "";
    public string EmotionalResidue { get; set; } = "";
    // Only this exact structured slot was emitted by the retired synthetic
    // vitality channel. Historical dialogue and all other residue stay intact.
    [JsonIgnore]
    private string CurrentEmotionalResidue => EmotionalResidue == "有一点没精神" ? "" : EmotionalResidue;
    public string Source { get; set; } = "";

    public bool HasContent =>
        !string.IsNullOrWhiteSpace(NotableEvent)
        || !string.IsNullOrWhiteSpace(TopicText)
        || !string.IsNullOrWhiteSpace(BoundaryHint)
        || !string.IsNullOrWhiteSpace(CurrentEmotionalResidue);

    public bool IsExpired(DateTimeOffset now)
    {
        if (Timestamp == default)
            return true;
        return (now - Timestamp).TotalHours > MaxAgeHours;
    }

    public LastSceneSummary Copy() => new()
    {
        SchemaVersion = SchemaVersion, SourceEventIds = (string[])SourceEventIds.Clone(),
        Timestamp = Timestamp, NotableEvent = NotableEvent, UnfinishedTopic = UnfinishedTopic,
        RecentTopic = RecentTopic, UserText = UserText, AssistantText = AssistantText,
        AssistantTimestamp = AssistantTimestamp, BoundaryHint = BoundaryHint,
        EmotionalResidue = CurrentEmotionalResidue, Source = Source
    };
}
