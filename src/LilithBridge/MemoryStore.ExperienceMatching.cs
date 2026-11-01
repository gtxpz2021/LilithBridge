using System;
using System.Collections.Generic;
using System.Threading;

namespace LilithBridge;

internal sealed partial class MemoryStore
{
    // Instance-local, synchronous preparation only. No candidate/permission
    // result is retained; a changed body is a different pure-computation key.
    private readonly AsyncLocal<ExperienceMatchingPreparation?> _experienceMatchingPreparation = new();
    private sealed class ExperienceMatchingPreparation
    {
        internal readonly int ThreadId = Environment.CurrentManagedThreadId;
        internal readonly Dictionary<string, HashSet<string>> Bodies = new(StringComparer.Ordinal);
        internal bool Active = true;
        internal bool IsCurrent => Active && ThreadId == Environment.CurrentManagedThreadId;
    }
    internal IDisposable BeginExperienceMatchingPreparation()
    {
        var previous = _experienceMatchingPreparation.Value;
        if (previous?.IsCurrent == true) return new MentionScope(() => { });
        var current = new ExperienceMatchingPreparation();
        _experienceMatchingPreparation.Value = current;
        return new MentionScope(() =>
        {
            current.Active = false;
            current.Bodies.Clear();
            _experienceMatchingPreparation.Value = previous;
        });
    }
    private HashSet<string> ExperienceBodyTokens(string text)
    {
        var current = _experienceMatchingPreparation.Value;
        if (current?.IsCurrent != true) return MemorySemantics.MatchingTokens(text);
        if (!current.Bodies.TryGetValue(text, out var tokens))
            current.Bodies.Add(text, tokens = MemorySemantics.MatchingTokens(text));
        return tokens;
    }
}