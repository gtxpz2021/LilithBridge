using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace LilithBridge;

/// <summary>
/// Read-only bypass for the "he says he told me X" line (2026-09-20, phase: observation).
///
/// It records **numbers and identifiers only** — never the claim text, never his
/// words, never a prompt — so the offline tool can compare the rule that is live
/// today (H) with the frozen NLI pipeline (I) and their combination (OR).
///
/// Three hard rules, all enforced here rather than at the call site:
///   · it never writes dialogue text into the telemetry file;
///   · it never throws, so a broken path cannot break a reply;
///   · it is off unless the config turns it on.
/// </summary>
internal static class ClaimObservation
{
    public const string FileName = "claim-observation.jsonl";

    /// <summary>Pure builder, so the harness can assert exactly what would be written.</summary>
    public static string BuildRecord(
        DateTimeOffset now,
        string eventId,
        string form,
        int claimLength,
        double best,
        int matches,
        bool requiresCheck,
        long elapsedMs)
    {
        return JsonSerializer.Serialize(new
        {
            schema = 1,
            ts = now,
            event_id = eventId,
            form,
            claim_len = claimLength,
            h_best = Math.Round(best, 4),
            h_matches = matches,
            h_requires_check = requiresCheck,
            elapsed_ms = elapsedMs
        });
    }

    public static string PathFor(string root) =>
        Path.Combine(root, "telemetry", FileName);

    /// <summary>Appends one line; returns false instead of throwing, always.</summary>
    public static bool Append(string path, string record)
    {
        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            File.AppendAllText(path, record + Environment.NewLine, Encoding.UTF8);
            return true;
        }
        catch
        {
            // Observation is never allowed to disturb the game.
            return false;
        }
    }

    /// <summary>
    /// The single entry point the controller uses. Disabled, bad input or a broken
    /// path all end in "nothing happened" rather than an exception.
    /// </summary>
    public static bool TryRecord(
        bool enabled,
        string root,
        string eventId,
        string form,
        int claimLength,
        double best,
        int matches,
        bool requiresCheck,
        long elapsedMs)
    {
        if (!enabled || claimLength <= 0 || string.IsNullOrEmpty(root))
            return false;
        return Append(PathFor(root),
            BuildRecord(DateTimeOffset.UtcNow, eventId, form, claimLength, best,
                matches, requiresCheck, elapsedMs));
    }
}
