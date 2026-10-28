using System;
using System.Collections.Generic;
using System.Linq;

namespace LilithBridge;

// Request-only evidence of public wording. It is not a claim about hidden
// mental experience, nor permission to retrieve provider reasoning.
internal sealed record PublicViewSource(string Id, string Kind, string Text);

internal sealed partial class DeepSeekClient
{
    private static IReadOnlyList<PublicViewSource> CapturePublicViewSources(MemoryStore? owner,
        HistoryProjectionResult projection, string requestedSpeaker, B1Packet? packet)
    {
        // Unbound history/nativeContext strings cannot manufacture evidence.
        if (owner == null) return Array.Empty<PublicViewSource>();
        var sources = new List<PublicViewSource>();
        if (requestedSpeaker is "" or "assistant")
            foreach (var turn in projection.Turns.Where(t => t.Role == "assistant" && t.Records.Count > 0
                && t.Records.All(IsPublicViewUtterance)))
                sources.Add(new(string.Join("+", turn.Records.Select(t => t.EventId)), "delivered_public_reply", turn.Text));
        if (packet?.Availability == "ready")
            foreach (var material in packet.Materials)
                if (material.Ref.Kind == "b1")
                    sources.Add(new(material.Ref.Key, "retained_judgment_not_endorsement", material.Text));
                else if (material.Ref.Kind == "utterance" && material.Origin == "model"
                    && material.Scope.Contains("role=assistant;delivery=delivered", StringComparison.Ordinal))
                    sources.Add(new(material.Ref.Key, "delivered_public_reply", material.Text));
        return sources.Where(s => s.Id.Length > 0 && s.Text.Length > 0)
            .GroupBy(s => s.Id, StringComparer.Ordinal).Select(g => g.First()).ToArray();
    }
    private static bool IsPublicViewUtterance(MemoryTurn turn) => turn.Role == "assistant"
        && turn.Kind == "chat" && turn.Origin == "model" && turn.DeliveryState == "delivered";
    private static IReadOnlyList<PublicViewSource> MergeReviewPublicViewSources(IReadOnlyList<PublicViewSource> sources,
        DialogueRequest request) => sources.Concat(request.ReviewSourceViews
            .Where(pair => request.ReviewSourceRecords.TryGetValue(pair.Key, out var turn) && IsPublicViewUtterance(turn))
            .SelectMany(pair => pair.Value.Select(view => new PublicViewSource(
                request.ReviewSourceRecords[pair.Key].EventId + ":offset=" + view.Offset,
                "reviewed_public_reply_excerpt", view.Text))))
        .GroupBy(s => s.Id, StringComparer.Ordinal).Select(g => g.First()).ToArray();

    // Historical interpretation does not grant permission for hidden writes.
    // This request-level cleanup is independent of the wording of the reply.
    internal static void ClearHistoricalRequestControls(ApiResult result, bool requireVisibleText = true)
    {
        if (result.IsMemoryOperation || result.Error.Length > 0) return;
        if (result.ThoughtBoundary?.RequiresUncertainty != true
            && result.ClaimBoundary?.RequiresSourceCheck != true) return;
        result.MemoEntries.Clear(); result.ScratchEntries.Clear(); result.MemoAnnotations.Clear();
        result.NoteSupplement.Clear(); result.AutonomousMemories.Clear(); result.AutonomousMemoryRemovals.Clear();
        result.AutonomousMemoryPatches.Clear(); result.MemoryRecalls.Clear(); result.StoryRecalls.Clear();
        result.ReflectionRequested = false; result.ReflectionRequestReason = "";
        // Preserve the old empty-public-response failure structurally, without
        // treating any nonempty wording as a semantic failure. Hidden paper
        // tasks keep their existing no-visible-text contract.
        if (requireVisibleText && result.Text.Length == 0)
        {
            if (result.B1ChoiceProvided && result.B1 != null)
                result.B1 = new B1Choice { Kind = "invalid" };
            result.Action=""; result.Animation=""; result.Voice=""; result.Emotion="neutral";
            result.DesktopArrange=""; result.Actions.Clear();
            result.Error="Model reply contained no visible text";
            result.Response.Outcome="empty_visible_reply";
        }

    }
}
