using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace LilithBridge;

// A program-owned locator for an exact raw body already carried by this request.
// It never changes a source, grants permission, or survives as stored content.
// Offsets/counts are UTF-16 characters, as used by the host history serializer.
internal sealed record RequestSourceBody(string Kind, string EventId, string Text, string Location,
    string Role, string Origin, string DeliveryState, int TextStart = 0, int TextChars = -1)
{
    internal int Chars => TextChars < 0 ? Text.Length : TextChars;
    internal string Sha256 => Hash(Text);
    internal static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    internal bool Qualified => Kind == "utterance" && EventId.Length > 0 && Location.Length > 0
        && TextStart >= 0 && Chars == Text.Length
        && (Role == "user" && DeliveryState == "received" && Origin is "user" or "game"
            || Role == "assistant" && DeliveryState == "delivered" && Origin is "model" or "game" or "local_fallback");

    internal static RequestSourceBody? Match(IReadOnlyList<RequestSourceBody>? bodies, B1Material material)
    {
        if (bodies == null || material.Ref.Kind != "utterance" || material.Ref.Version.Length == 0
            || material.Ref.Sha256.Length == 0 || material.Ref.Sha256 != Hash(material.Text)) return null;
        // Contradictory locators are not resolved by picking a convenient one.
        var matches = bodies.Where(b => b.Qualified && b.Kind == material.Ref.Kind
            && b.EventId == material.Ref.Version && b.Text == material.Text
            && b.Origin == material.Origin && b.Sha256 == material.Ref.Sha256).Distinct().ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    internal object Reference() => new { location = Location, start_char = TextStart, char_count = Chars,
        source_kind = Kind, source_event_id = EventId, sha256 = Sha256,
        qualification = "exact_source_version_body_already_in_this_request" };
}
