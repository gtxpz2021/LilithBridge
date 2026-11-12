using System;
using System.Collections.Generic;
using System.Linq;

namespace LilithBridge;

internal static partial class PreviousStoryArchive
{
    private static string[] MatchingAliases(Entry entry, string query) => entry.Lines == null
        ? Array.Empty<string>() : entry.Lines.SelectMany(line => line.Aliases).SelectMany(alias => alias.Terms)
            .Distinct(StringComparer.OrdinalIgnoreCase).Where(term => query.Contains(term, StringComparison.OrdinalIgnoreCase)).ToArray();

    // An exact source locator remains strongest. Explicit curated search aliases
    // then outrank incidental overlap in conversational n-grams. Otherwise retain
    // the old ordering: a rarity-only prototype regressed first-meeting queries.
    // This uses existing data, not scene names or newly invented answer rules.
    private static IEnumerable<Entry> RankStoryEntries(Entry[] entries, string query)
    {
        var ranked = entries.Select(entry => (Entry: entry,
            Locator: entry.Lines?.Any(line => query.Contains(line.Script + "#" + line.LineId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(query, line.LineId, StringComparison.OrdinalIgnoreCase)) == true,
            Alias: MatchingAliases(entry, query).Sum(term => Math.Min(term.Length, 12)),
            Score: entry.Terms.Where(term => query.Contains(term, StringComparison.OrdinalIgnoreCase)).Sum(term => Math.Min(term.Length, 12))))
        .Where(x => x.Score > 0).ToArray();
        // Preserve remaining lexical candidates for separately asked details.
        // A curated name is a discovery hint, never a closed list of scenes.
        return ranked.OrderByDescending(x => x.Locator).ThenByDescending(x => x.Alias)
            .ThenByDescending(x => x.Score).ThenBy(x => x.Entry.Id, StringComparer.Ordinal).Select(x => x.Entry);
    }

    private static Entry ExpandCollectionEntry(Entry candidate, Entry[] entries, string query)
    {
        var lines = candidate.Lines!;
        var groups = lines.SelectMany(line => line.Aliases)
            .Where(alias => alias.Terms.Any(term => query.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .Select(alias => alias.EntryId).ToHashSet(StringComparer.Ordinal);
        if (groups.Count == 0) return candidate;
        var first = lines[0]; var last = lines[^1];
        var available = entries.Where(entry => entry.Lines != null).SelectMany(entry => entry.Lines!)
            .Where(line => line.Game == first.Game && line.Script == first.Script).ToDictionary(line => line.Row);
        int begin = first.Row, end = last.Row;
        // Only extend to nearby lines carrying the SAME observed curated alias
        // group, plus two source neighbors. Never cross commands, blanks, missing
        // rows or scripts, and never replace the original candidate's own lines.
        for (int direction = -1; direction <= 1; direction += 2)
        {
            int edge = direction < 0 ? first.Row : last.Row, reached = edge;
            for (int step = 1; step <= 6; step++)
            {
                int row = edge + direction * step;
                if (!available.TryGetValue(row, out var line)) break;
                if (line.Aliases.Any(alias => groups.Contains(alias.EntryId))) reached = row;
            }
            if (reached == edge) continue;
            for (int step = 0; step < 2 && available.ContainsKey(reached + direction); step++) reached += direction;
            if (direction < 0) begin = reached; else end = reached;
        }
        return begin == first.Row && end == last.Row ? candidate :
            CollectionEntry(Enumerable.Range(begin, end - begin + 1).Select(row => available[row]).ToArray());
    }

    private static bool CollectionOverlaps(Entry a, Entry b) => a.Lines != null && b.Lines != null
        && a.Lines.Any(x => b.Lines.Any(y => x.Game == y.Game && x.Script == y.Script && x.Row == y.Row));
}
