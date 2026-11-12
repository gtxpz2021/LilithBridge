namespace LilithBridge;

internal sealed record NativeBubbleRequest(string Text, string Emotion, float Duration);
internal sealed record NativeBubbleAttempt(WorkDisplayOutcome Outcome, string Endpoint);
