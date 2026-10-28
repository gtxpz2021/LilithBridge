using System;
using System.Collections.Generic;
using System.Linq;

namespace LilithBridge;

internal interface IRecallSource
{
    IReadOnlyList<MemoryTurn> GetRecallCandidates(string? query, int max);
}

internal sealed class NormalRecallSource : IRecallSource
{
    private readonly MemoryStore _memory;

    public NormalRecallSource(MemoryStore memory)
    {
        _memory = memory;
    }

    public IReadOnlyList<MemoryTurn> GetRecallCandidates(string? query, int max)
    {
        return _memory.GetRecallCandidates(query, max);
    }
}

internal sealed class CausalRecallTarget
{
    public string CandidateId { get; init; } = "";
    public string AuthorityDecisionId { get; init; } = "";
    public string MemoryWriteId { get; init; } = "";
    public string MemoryText { get; init; } = "";
    public string Evidence { get; init; } = "";
}

/// <summary>
/// Target-only recall projection.
///
/// It intentionally does not:
///   - rank by query similarity
///   - read profile / dream / summary / history / legacy archive
///   - trigger lifecycle or SaveList
///   - fallback to flat recall
///
/// It returns exactly the one approved target, or nothing.
/// </summary>
internal sealed class CausalRecallSource : IRecallSource
{
    private readonly MemoryStore _memory;
    private readonly CausalRecallTarget _target;

    public CausalRecallSource(MemoryStore memory, CausalRecallTarget target)
    {
        _memory = memory;
        _target = target ?? throw new ArgumentNullException(nameof(target));
    }

    public IReadOnlyList<MemoryTurn> GetRecallCandidates(string? query, int max)
    {
        MemoryTurn? target = _memory.GetAutonomousMemoriesReadOnly()
            .FirstOrDefault(
                turn => string.Equals(turn.Text, _target.MemoryText, StringComparison.Ordinal)
                    && string.Equals(turn.Evidence, _target.Evidence, StringComparison.Ordinal));
        return target == null
            ? Array.Empty<MemoryTurn>()
            : new[] { target };
    }
}
