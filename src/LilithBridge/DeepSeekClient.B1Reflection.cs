using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LilithBridge;

// This envelope is created only by the real auxiliary transport adapter. The
// public content is preserved unchanged before the optional choice is applied.
internal sealed record B1ReflectionAccepted(string RequestId, string AttemptId, int HttpAttempts,
    string Outcome, string RawContent, B1Choice? Choice, bool ParsedChoice = true, long SelectionGeneration = 0);

internal sealed partial class DeepSeekClient
{
    internal async Task<B1ReflectionAccepted?> RunB1ReflectionAsync(BridgeConfig config,
        string material, CancellationToken cancellation, ReflectionDispatch dispatch, MemoryStore? selectionStore = null)
    {
        var envelope = new ApiResult();
        CaptureSelection(config, envelope, selectionStore);
        string? raw = await SendTextAsync(config, config.ReflectionModel,
            MemoryStore.B1ReconsiderationOwner.Contract +
            " This is a B1-only reflection, not MAIN, dialogue, note, lesson, relationship, or day_task. " +
            "Use only the supplied prior judgment. Its original sources are NOT supplied. " +
            "Return exactly JSON {\"b1\":null} or {\"b1\":{\"v\":3,\"text\":\"...\",\"scope\":\"...\",\"conditions\":\"...\",\"nature\":\"hypothesis\",\"about\":[],\"sources\":[\"source_ref\"],\"relation\":\"revises\",\"target\":\"source_ref\"}}. " +
            "Maintaining the prior judgment without a new entry is a complete answer, not failure.",
            material, config.ReflectionMaxTokens, 0.2, false, cancellation,
            "reflection", reflectionDispatch: dispatch, envelopeOverride: envelope).ConfigureAwait(false);
        if (cancellation.IsCancellationRequested || raw == null || raw.Length > 32768
            || envelope.Response.Outcome != "accepted" || envelope.IsLocalFallback
            || envelope.Response.HttpSendStartedMs == null || envelope.Response.AttemptId.Length == 0) return null;
        // A single reflection SendCompletion attempt; auxiliary transport has no
        // MAIN transport retry. The attempt count is observed, not caller input.
        B1ReflectionAccepted Invalid() => new(envelope.RequestId, envelope.Response.AttemptId, 1,
            envelope.Response.Outcome, raw, null, false, envelope.SelectionSnapshot?.Generation ?? 0);
        try
        {
            using var document = JsonDocument.Parse(StripJsonFences(raw).Trim());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1
                || !root.TryGetProperty("b1", out var b1)
                || b1.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null)) return Invalid();
            if (b1.ValueKind == JsonValueKind.Object)
            {
                var allowed = new[] { "v", "text", "scope", "conditions", "nature", "about", "sources", "relation", "target", "kind" };
                var names = b1.EnumerateObject().Select(p => p.Name).ToArray();
                if (names.Distinct(StringComparer.Ordinal).Count() != names.Length || names.Any(n => !allowed.Contains(n))) return Invalid();
            }
            var choice = b1.ValueKind == JsonValueKind.Null ? null : b1.Deserialize<B1Choice>();
            return new(envelope.RequestId, envelope.Response.AttemptId, 1, envelope.Response.Outcome, raw, choice, true, envelope.SelectionSnapshot?.Generation ?? 0);
        }
        catch (JsonException) { return Invalid(); }
    }
}
