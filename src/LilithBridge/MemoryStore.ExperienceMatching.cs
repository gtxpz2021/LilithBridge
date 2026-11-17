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
        internal readonly Dictionary<string, string> ComparableTexts = new(StringComparer.Ordinal);
        internal readonly Dictionary<(string Kind, long Stamp, MentionReadPolicy? Mention, NativeDialoguePolicy? Native), object> ReadMemos = new();
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
            current.ComparableTexts.Clear();
            current.ReadMemos.Clear();
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
    private string ComparableBodyText(string text)
    {
        var current = _experienceMatchingPreparation.Value;
        if (current?.IsCurrent != true) return MemorySemantics.NormalizeComparableText(text);
        if (!current.ComparableTexts.TryGetValue(text, out var comparable))
            current.ComparableTexts.Add(text, comparable = MemorySemantics.NormalizeComparableText(text));
        return comparable;
    }
    internal double SimilarityCached(string? left, string? right) =>
        MemorySemantics.CompareMatchingTokens(
            ExperienceBodyTokens(left ?? ""),
            ExperienceBodyTokens(right ?? "")).Similarity;
    internal bool IsRelevantRecallCached(string? query, string? text)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        return MemorySemantics.CompareMatchingTokens(
            ExperienceBodyTokens(query),
            ExperienceBodyTokens(text ?? "")).Relevant;
    }
    internal bool AreNearDuplicateTextsCached(string? first, string? second, double similarityThreshold = 0.72d)
    {
        string a = ComparableBodyText(first ?? "");
        string b = ComparableBodyText(second ?? "");
        if (a.Length == 0 || b.Length == 0) return false;
        if (a == b) return true;
        int minLen = Math.Min(a.Length, b.Length);
        int maxLen = Math.Max(a.Length, b.Length);
        if (minLen >= 10 && maxLen <= minLen * 1.45d && (a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal)))
            return true;
        return SimilarityCached(first, second) >= similarityThreshold;
    }

    private bool CanUseRequestReadMemo =>
        _experienceMatchingPreparation.Value is { IsCurrent: true }
        && _b1BorrowedArchive == null
        && _nativeDialoguePolicy.Value?.Authorization == null;

    private bool TryGetRequestReadMemo<T>(string kind, out T value) where T : class
    {
        value = null!;
        if (!CanUseRequestReadMemo || _experienceMatchingPreparation.Value is not { IsCurrent: true } current)
            return false;
        var key = (kind, Volatile.Read(ref _sourceMutationStamp), _mentionReadPolicy.Value, _nativeDialoguePolicy.Value);
        if (current.ReadMemos.TryGetValue(key, out var raw) && raw is T typed)
        {
            value = typed;
            return true;
        }
        return false;
    }
    private void SetRequestReadMemo<T>(string kind, T value) where T : class
    {
        if (!CanUseRequestReadMemo || _experienceMatchingPreparation.Value is not { IsCurrent: true } current)
            return;
        var key = (kind, Volatile.Read(ref _sourceMutationStamp), _mentionReadPolicy.Value, _nativeDialoguePolicy.Value);
        current.ReadMemos[key] = value;
    }

    private bool TryGetCachedDayArtifacts(out IReadOnlyList<MemoryTurn> list) => TryGetRequestReadMemo("day_artifacts", out list);
    private void CacheDayArtifacts(IReadOnlyList<MemoryTurn> list) => SetRequestReadMemo("day_artifacts", list);
}