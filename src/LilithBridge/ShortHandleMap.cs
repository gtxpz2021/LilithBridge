using System;
using System.Collections.Generic;
using System.Linq;

namespace LilithBridge;

/// <summary>
/// Bi-directional short alias table mapping model-facing handles (e.g. e1, n1, d1)
/// to full internal EventIds while C# retains strict ID and version verification.
/// </summary>
internal sealed class ShortAliasTable
{
    public Dictionary<string, string> AliasToId { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> IdToAlias { get; } = new(StringComparer.Ordinal);

    public static bool TryFromAliasToId(IReadOnlyDictionary<string, string>? aliasToId, out ShortAliasTable? table)
    {
        if (aliasToId == null)
        {
            table = null;
            return true;
        }
        var result = new ShortAliasTable();
        foreach (var pair in aliasToId)
        {
            if (string.IsNullOrEmpty(pair.Key) || string.IsNullOrEmpty(pair.Value)
                || !result.AliasToId.TryAdd(pair.Key, pair.Value)
                || !result.IdToAlias.TryAdd(pair.Value, pair.Key))
            {
                table = null;
                return false;
            }
        }
        table = result;
        return true;
    }

    public string Register(string prefix, string eventId)
    {
        if (string.IsNullOrEmpty(eventId)) return "";
        if (IdToAlias.TryGetValue(eventId, out string? existing)) return existing;
        int n = 1;
        while (AliasToId.ContainsKey(prefix + n)) n++;
        string alias = prefix + n;
        AliasToId[alias] = eventId;
        IdToAlias[eventId] = alias;
        return alias;
    }

    public void RegisterAll(string prefix, IEnumerable<MemoryTurn> items)
    {
        foreach (var item in items) Register(prefix, item.EventId);
    }

    public string AliasOrRegister(string prefix, string? eventId) =>
        string.IsNullOrEmpty(eventId) ? ""
            : IdToAlias.TryGetValue(eventId, out string? existing) ? existing
            : Register(prefix, eventId);

    public string[] AliasOrRegisterAll(string prefix, IEnumerable<string>? eventIds) =>
        eventIds == null ? Array.Empty<string>()
            : eventIds.Select(id => AliasOrRegister(prefix, id)).Where(s => s.Length > 0).ToArray();

    public string AliasOrId(string? eventId) =>
        string.IsNullOrEmpty(eventId) ? "" : IdToAlias.TryGetValue(eventId, out string? alias) ? alias : eventId;

    public string[] AliasOrIds(IEnumerable<string>? eventIds) =>
        eventIds == null ? Array.Empty<string>() : eventIds.Select(AliasOrId).Where(s => s.Length > 0).ToArray();

    public static string Resolve(IReadOnlyDictionary<string, string>? aliasToId, string? value)
    {
        string trimmed = value?.Trim() ?? "";
        if (trimmed.Length == 0 || aliasToId == null) return trimmed;
        return aliasToId.TryGetValue(trimmed, out string? resolved) ? resolved : trimmed;
    }

    public static string[] ResolveAll(IReadOnlyDictionary<string, string>? aliasToId, IEnumerable<string>? values) =>
        values == null ? Array.Empty<string>()
            : values.Select(v => Resolve(aliasToId, v)).Where(s => s.Length > 0).ToArray();

    public static void ResolveReflectionResultAliases(ReflectionResult? result, IReadOnlyDictionary<string, string>? aliasToId)
    {
        if (result == null || aliasToId == null || aliasToId.Count == 0) return;
        if (result.Note != null)
        {
            result.Note.SourceEventIds = ResolveAll(aliasToId, result.Note.SourceEventIds);
            result.Note.SourceDayIds = ResolveAll(aliasToId, result.Note.SourceDayIds);
            result.Note.Revises = Resolve(aliasToId, result.Note.Revises);
            result.Note.Settles = Resolve(aliasToId, result.Note.Settles);
        }
        if (result.Lesson != null)
        {
            result.Lesson.SourceNoteIds = ResolveAll(aliasToId, result.Lesson.SourceNoteIds);
            result.Lesson.Revises = Resolve(aliasToId, result.Lesson.Revises);
            result.Lesson.Settles = Resolve(aliasToId, result.Lesson.Settles);
        }
        if (result.Relationship != null)
        {
            result.Relationship.SourceNoteIds = ResolveAll(aliasToId, result.Relationship.SourceNoteIds);
            result.Relationship.Revises = Resolve(aliasToId, result.Relationship.Revises);
        }
    }
}

internal sealed record ReviewCandidateHandle(string Mode, string HistoryRef, string RootVersion, string VersionFingerprint);
