using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace LilithBridge;

/// <summary>
/// Neutral, body-free anti-repetition ledger for proactive read-only B1 supply.
/// Prevents the exact same historical proposal from being placed into consecutive
/// proactive packets without grading Lilith's speech or assigning hidden scores.
/// </summary>
internal sealed record B1ProactiveExposureEntry(
    int SuppliedCount = 0,
    long LastSuppliedOpportunity = 0,
    DateTimeOffset LastSuppliedAtUtc = default,
    long CooldownUntilOpportunity = 0,
    DateTimeOffset CooldownUntilUtc = default);

internal sealed record B1ProactiveExposureLedger(
    int Version = 1,
    long OpportunityCounter = 0,
    Dictionary<string, B1ProactiveExposureEntry>? Entries = null);

internal sealed partial class MemoryStore
{
    private const int ExposureRotationOpportunities = 4;
    private static readonly TimeSpan ExposureRotationDuration = TimeSpan.FromHours(6);

    private string B1ProactiveExposurePath => Path.Combine(_root, "v4-b1-proactive-exposure.json");
    private B1ProactiveExposureLedger? _b1ProactiveExposureCache;
    private string _b1ProactiveExposureStamp = "";

    private B1ProactiveExposureLedger LoadB1ProactiveExposureLocked()
    {
        string stamp = FileStamp(B1ProactiveExposurePath);
        if (_b1ProactiveExposureCache != null && _b1ProactiveExposureStamp == stamp)
            return _b1ProactiveExposureCache;
        if (stamp == "absent")
        {
            var fresh = new B1ProactiveExposureLedger(1, 0, new Dictionary<string, B1ProactiveExposureEntry>(StringComparer.Ordinal));
            _b1ProactiveExposureCache = fresh;
            _b1ProactiveExposureStamp = stamp;
            return fresh;
        }
        try
        {
            if (new FileInfo(B1ProactiveExposurePath).Length > 262144)
                return new B1ProactiveExposureLedger(1, 0, new Dictionary<string, B1ProactiveExposureEntry>(StringComparer.Ordinal));
            var parsed = JsonSerializer.Deserialize<B1ProactiveExposureLedger>(File.ReadAllText(B1ProactiveExposurePath));
            var entries = parsed?.Entries != null
                ? new Dictionary<string, B1ProactiveExposureEntry>(parsed.Entries, StringComparer.Ordinal)
                : new Dictionary<string, B1ProactiveExposureEntry>(StringComparer.Ordinal);
            var normalized = new B1ProactiveExposureLedger(1, Math.Max(0, parsed?.OpportunityCounter ?? 0), entries);
            _b1ProactiveExposureCache = normalized;
            _b1ProactiveExposureStamp = stamp;
            return normalized;
        }
        catch
        {
            return new B1ProactiveExposureLedger(1, 0, new Dictionary<string, B1ProactiveExposureEntry>(StringComparer.Ordinal));
        }
    }

    private void SaveB1ProactiveExposureLocked(B1ProactiveExposureLedger ledger)
    {
        try
        {
            Directory.CreateDirectory(_root);
            string json = JsonSerializer.Serialize(ledger);
            if (Encoding.UTF8.GetByteCount(json) > 262144) return;
            string tmp = B1ProactiveExposurePath + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(tmp, json);
            File.Move(tmp, B1ProactiveExposurePath, true);
            _b1ProactiveExposureCache = ledger;
            _b1ProactiveExposureStamp = FileStamp(B1ProactiveExposurePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Supply rotation is best-effort and never blocks dialogue or memory integrity.
        }
    }

    internal bool IsB1ProactiveProposalEligible(string version, DateTimeOffset nowUtc)
    {
        lock (_experienceLock)
        {
            var ledger = LoadB1ProactiveExposureLocked();
            if (ledger.Entries == null || !ledger.Entries.TryGetValue(version, out var entry))
                return true;
            return ledger.OpportunityCounter >= entry.CooldownUntilOpportunity
                || nowUtc >= entry.CooldownUntilUtc;
        }
    }

    internal int GetB1ProactiveSuppliedCount(string version)
    {
        lock (_experienceLock)
        {
            var ledger = LoadB1ProactiveExposureLocked();
            return ledger.Entries != null && ledger.Entries.TryGetValue(version, out var entry)
                ? entry.SuppliedCount : 0;
        }
    }

    internal void RecordB1ProactiveSupply(IEnumerable<string>? suppliedVersions, DateTimeOffset? nowOverride = null)
    {
        lock (_experienceLock)
        {
            DateTimeOffset now = nowOverride ?? DateTimeOffset.UtcNow;
            var ledger = LoadB1ProactiveExposureLocked();
            long opp = ledger.OpportunityCounter + 1;
            var map = new Dictionary<string, B1ProactiveExposureEntry>(
                ledger.Entries ?? new Dictionary<string, B1ProactiveExposureEntry>(StringComparer.Ordinal),
                StringComparer.Ordinal);
            if (suppliedVersions != null)
            {
                foreach (string version in suppliedVersions.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.Ordinal))
                {
                    var prev = map.GetValueOrDefault(version) ?? new B1ProactiveExposureEntry();
                    map[version] = new B1ProactiveExposureEntry(
                        SuppliedCount: prev.SuppliedCount + 1,
                        LastSuppliedOpportunity: opp,
                        LastSuppliedAtUtc: now,
                        CooldownUntilOpportunity: opp + ExposureRotationOpportunities,
                        CooldownUntilUtc: now + ExposureRotationDuration);
                }
            }
            SaveB1ProactiveExposureLocked(new B1ProactiveExposureLedger(1, opp, map));
        }
    }

    private void PurgeB1ProactiveExposure(HashSet<string> removedVersions)
    {
        if (removedVersions.Count == 0 || !File.Exists(B1ProactiveExposurePath))
            return;
        lock (_experienceLock)
        {
            var ledger = LoadB1ProactiveExposureLocked();
            if (ledger.Entries == null || !ledger.Entries.Keys.Any(removedVersions.Contains))
                return;
            var map = ledger.Entries.Where(kv => !removedVersions.Contains(kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            SaveB1ProactiveExposureLocked(ledger with { Entries = map });
        }
    }
}
