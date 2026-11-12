using System.Text.Json;

namespace LilithBridge;

internal sealed partial class MemoryStore
{
    // A conservative version of the complete current-session read domain,
    // including visibility. New/changed/withdrawn material invalidates a repeat
    // even if it would happen to return the same highest-ranked record.
    internal string ReviewReadDataVersion()
    {
        try
        {
            lock (_experienceLock)
            {
                var suppressed = SuppressedExperienceIds();
                var data = ReadExperiences(includeSuppressed: true).Where(t => t.SessionId == _sessionId)
                    .OrderBy(t => t.EventId, StringComparer.Ordinal)
                    .Select(t => new { record = t, suppressed = suppressed.Contains(t.EventId),
                        mention = IsMentionAllowed(t), native = IsNativeSupplyAllowed(t) });
                return B1Hash(JsonSerializer.Serialize(data, DialogueMaterialEnvelope.DataJson));
            }
        }
        catch { return ""; } // Uncertain versions disable deduplication only.
    }
}
