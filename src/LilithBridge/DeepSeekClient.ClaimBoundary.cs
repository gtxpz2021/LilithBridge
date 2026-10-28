using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LilithBridge;

internal sealed class ClaimCheck
{
    [JsonPropertyName("claim")] public string Claim { get; set; } = "";
    [JsonPropertyName("verdict")] public string Verdict { get; set; } = "";
    [JsonPropertyName("evidence")] public List<ClaimCitation> Evidence { get; set; } = new();
}
internal sealed class ClaimCitation
{
    [JsonPropertyName("event_id")] public string EventId { get; set; } = "";
    [JsonPropertyName("quote")] public string Quote { get; set; } = "";
}

internal sealed partial class DeepSeekClient
{
    // Candidate utterances remain available as source material; they impose no
    // response schema, verdict, replacement sentence or correction request.
    private static string BuildClaimSourceContext(ClaimRecallBoundary boundary) =>
        "[本轮相关原话] " + JsonSerializer.Serialize(new
        {
            claim = boundary.Claim,
            candidates = boundary.Sources.Select(t => new { event_id = t.EventId, text = t.Text,
                speaker = t.Role, origin = t.Origin, kind = t.Kind, at = t.Timestamp })
        }, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
}
