namespace LilithBridge;

// A bounded checkpoint of ONE provider generation. Neither a bubble nor a
// checkpoint is an independent utterance, memory fact, or new read permission.
internal sealed record PublicFinalCheckpoint(string GenerationId,int Revision,string State,int ReadyChunks,
    string[] ChunkTexts,string Reason="");
internal sealed record PublicFinalCanonical(string GenerationId,string CheckpointKey);
internal sealed record StreamAppearanceEvidence(string SegmentId,int Index,string Text,string PriorPlanVersion,
    DateTimeOffset ObservedAtUtc,long ObservedAtTicks);

internal sealed partial class ApiResult
{
    internal PublicFinalDraft? FinalPublicDraft {get;set;}
}
internal sealed partial class DialogueRequest
{
    internal PublicFinalDraft? FinalPublicDraft {get;set;}
}
