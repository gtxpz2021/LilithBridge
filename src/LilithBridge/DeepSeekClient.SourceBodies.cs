using System;
using System.Collections.Generic;
using System.Linq;

namespace LilithBridge;

internal sealed record LiveTailMessage(string Role, string Content);

internal sealed record HybridTailSplit(
    IReadOnlyList<ProjectedHistoryTurn> EnvelopeHistory,
    IReadOnlyList<LiveTailMessage> LiveTailMessages);

internal sealed partial class DeepSeekClient
{
    private readonly object _liveTailAnchorLock = new();
    private string _unboundTailAnchor = "";

    private HybridTailSplit SplitHybridTail(
        IReadOnlyList<ProjectedHistoryTurn> turns,
        BridgeConfig config,
        MemoryStore? historyOwner,
        bool eligibleContext)
    {
        if (!eligibleContext || turns.Count < 2)
            return new(turns, Array.Empty<LiveTailMessage>());

        int eligibleStart = turns.Count;
        while (eligibleStart > 0 && IsLiveTailEligibleTurn(turns[eligibleStart - 1]))
            eligibleStart--;

        if (turns.Count - eligibleStart < 2)
            return new(turns, Array.Empty<LiveTailMessage>());

        bool IsValidSplit(int idx)
        {
            if (idx < eligibleStart || idx >= turns.Count) return false;
            if (turns[idx].Role != "user") return false;
            for (int j = idx + 1; j < turns.Count; j++)
                if (turns[j].Role == "assistant") return true;
            return false;
        }

        const int targetTail = 6;
        const int minTail = 4;
        int stride = Math.Max(0, config.PromptCacheHistoryStride);
        int maxTail = stride > 1 ? targetTail + Math.Clamp(stride, 2, 8) : targetTail;

        int PickDefaultSplit()
        {
            int eligibleCount = turns.Count - eligibleStart;
            int best = -1;
            int bestScore = int.MaxValue;
            for (int i = eligibleStart; i < turns.Count; i++)
            {
                if (!IsValidSplit(i)) continue;
                int len = turns.Count - i;
                if (len > maxTail) continue;
                bool meetsMin = len >= minTail || eligibleCount < minTail;
                int score = (meetsMin ? 0 : 1000) + Math.Abs(len - targetTail);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = i;
                }
            }
            return best;
        }

        static string FirstEventId(ProjectedHistoryTurn entry) =>
            entry.Records.Count > 0 ? entry.Records[0].EventId : entry.Record?.EventId ?? "";

        int splitIdx = -1;
        lock (_liveTailAnchorLock)
        {
            string anchorId = historyOwner != null
                ? historyOwner.GetLiveTailAnchorEventId()
                : _unboundTailAnchor;

            if (stride > 1 && anchorId.Length > 0)
            {
                for (int i = eligibleStart; i < turns.Count; i++)
                {
                    if (FirstEventId(turns[i]) != anchorId) continue;
                    int len = turns.Count - i;
                    bool meetsMin = len >= minTail || turns.Count - eligibleStart < minTail;
                    if (IsValidSplit(i) && len <= maxTail && meetsMin)
                        splitIdx = i;
                    break;
                }
            }

            if (splitIdx < 0)
            {
                splitIdx = PickDefaultSplit();
                if (splitIdx < 0) return new(turns, Array.Empty<LiveTailMessage>());
                string nextAnchor = FirstEventId(turns[splitIdx]);
                if (historyOwner != null)
                    historyOwner.SetLiveTailAnchorEventId(nextAnchor);
                else
                    _unboundTailAnchor = nextAnchor;
            }
        }

        var envelope = turns.Take(splitIdx).ToArray();
        var coalesced = new List<LiveTailMessage>(turns.Count - splitIdx);
        for (int i = splitIdx; i < turns.Count; i++)
        {
            var entry = turns[i];
            string role = entry.Role == "assistant" ? "assistant" : "user";
            if (coalesced.Count > 0 && coalesced[^1].Role == role)
                coalesced[^1] = new(role, coalesced[^1].Content + "\n" + entry.Text);
            else
                coalesced.Add(new(role, entry.Text));
        }
        return new(envelope, coalesced);
    }

    private static bool IsLiveTailEligibleTurn(ProjectedHistoryTurn entry) =>
        entry.Role is "user" or "assistant"
        && !DialogueMaterialEnvelope.IsNativeTurnRecords(entry.Records)
        && UtteranceAttribution.Reported(entry.Role, entry.Text) == null
        && !(entry.Coverage != null && entry.Coverage.Contains("missing", StringComparison.Ordinal))
        && !string.IsNullOrWhiteSpace(entry.Text)
        && !entry.Text.StartsWith("[程序提供的近期对话与背景", StringComparison.Ordinal)
        && !ContainsB1Context(entry.Text);

    private static void ValidateSceneBodyReferences(DialogueRequest? request, string context,
        IReadOnlyList<RequestSourceBody> actualBodies)
    {
        if (request == null) return;
        foreach (var reference in request.SceneBodyReferencesFor(context))
        {
            var matches = actualBodies.Where(body => body.Qualified && reference.Qualified
                && body.EventId == reference.EventId && body.Kind == reference.Kind
                && body.Text == reference.Text && body.Role == reference.Role
                && body.Origin == reference.Origin && body.DeliveryState == reference.DeliveryState
                && body.Sha256 == reference.Sha256 && body.TextStart == reference.TextStart
                && body.Chars == reference.Chars
                && body.Location.StartsWith("history[", StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1)
                throw new SemanticMaterialException("source_body_reference_changed");
        }
    }

    // Locations are inside the actual source-envelope data message; character
    // ranges are UTF-16, matching the programme's immutable displayed strings.
    // This is deliberately not a map of the wider discovery/permission footprint.
    internal static IReadOnlyList<RequestSourceBody> SourceEnvelopeBodies(
        IEnumerable<ProjectedHistoryTurn> outgoingHistory, MemoryTurn? currentUser,
        string actualCurrentText, bool sourceEnvelope)
    {
        if (!sourceEnvelope) return Array.Empty<RequestSourceBody>();
        var bodies = new List<RequestSourceBody>();
        int index = 0;
        foreach (var entry in outgoingHistory)
        {
            bool current = currentUser != null && entry.Record?.EventId == currentUser.EventId
                && entry.Role == "user" && MemoryStore.Clean(entry.Text) == MemoryStore.Clean(actualCurrentText);
            if (current) continue;
            // Fail closed on a projection that transforms/inserts any text. A
            // reference must resolve to the exact source body, not an approximation.
            if (entry.Text == string.Concat(entry.Records.Select(t => t.Text)))
            {
                int start = 0;
                foreach (var record in entry.Records)
                {
                    bodies.Add(new RequestSourceBody("utterance", record.EventId, record.Text,
                        "history[" + index + "].text", record.Role, record.Origin,
                        record.DeliveryState, start, record.Text.Length));
                    start += record.Text.Length;
                }
            }
            index++;
        }
        // The source envelope intentionally excludes the current input from its
        // history. Its one complete body is the separate actual user message.
        if (currentUser != null && currentUser.Text == actualCurrentText)
            bodies.Add(new RequestSourceBody("utterance", currentUser.EventId, currentUser.Text,
                "current_input", currentUser.Role, currentUser.Origin,
                currentUser.DeliveryState, 0, currentUser.Text.Length));
        return bodies;
    }
}
