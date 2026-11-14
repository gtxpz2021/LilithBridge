using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace LilithBridge;

internal sealed record ThoughtRevisitTarget(DaySourceDependency Target, string VisibleText, string Location);
internal sealed record ThoughtRevisitChoice(DaySourceDependency Target, string Reason);
internal sealed record ThoughtRevisitReceipt(string Status, string Reason, string OperationId = "");

// One voluntary admission attempt per MAIN owner, including a busy/refused
// attempt. Resending its protocol never manufactures another background job.
internal sealed class ThoughtRevisitAttempt
{
    private readonly object _gate = new();
    private ThoughtRevisitChoice? _choice;
    private Task<ThoughtRevisitReceipt>? _result;
    internal bool Used { get { lock (_gate) return _result != null; } }
    internal Task<ThoughtRevisitReceipt> Run(ThoughtRevisitChoice choice, Func<Task<ThoughtRevisitReceipt>> admit)
    {
        lock (_gate)
        {
            if (_result != null) return _choice == choice ? _result
                : Task.FromResult(new ThoughtRevisitReceipt("not_accepted", "request_already_attempted"));
            _choice = choice;
            var completion = new TaskCompletionSource<ThoughtRevisitReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
            _result = completion.Task; // Publish before entering an asynchronous/re-entrant callback.
            _ = Finish();
            return _result;
            async Task Finish()
            {
                try { completion.TrySetResult(await admit().ConfigureAwait(false)); }
                catch (OperationCanceledException) { completion.TrySetResult(new("not_accepted", "owner_cancelled")); }
                catch { completion.TrySetResult(new("not_accepted", "admission_failed")); }
            }
        }
    }
}

internal sealed partial class DialogueRequest
{
    internal ThoughtRevisitAttempt RevisitAttempt { get; } = new();
    internal Func<ThoughtRevisitChoice, string, ReflectionReasonBasis?, Func<bool>, Task<ThoughtRevisitReceipt>>? RevisitAction { get; set; }
    internal ThoughtRevisitTarget[] RevisitBaseTargets { get; set; } = Array.Empty<ThoughtRevisitTarget>();
    internal List<ThoughtRevisitTarget> RevisitToolTargets { get; } = new();
    internal ReflectionReasonBasis? RevisitToolBasis { get; set; }
    // Ranges belong only to this native transcript. Store offsets and locators,
    // not another body copy; an exact-version contiguous prefix must be complete.
    private readonly Dictionary<DaySourceDependency, (int End, string Location)> _revisitThoughtPrefixes = new();
    internal string? AppendRevisitThoughtPage(ThoughtReadItem item, string callId)
    {
        if (item.Material.Ref.Kind != "note" || item.Text.Length == 0) return null;
        var key = new DaySourceDependency("note", item.Turn.EventId, item.Material.VersionFingerprint);
        _revisitThoughtPrefixes.TryGetValue(key, out var prefix);
        if (item.Offset > prefix.End) return null; // A missing earlier span is not supplied.
        int end = item.Offset + item.Text.Length;
        if (end > prefix.End)
        {
            prefix = (end, prefix.End == 0 ? "tool:" + callId : prefix.Location + ", tool:" + callId);
            _revisitThoughtPrefixes[key] = prefix;
        }
        return prefix.End == item.Turn.Text.Length ? prefix.Location : null;
    }
    internal void ResetRevisitToolContext()
    {
        RevisitToolTargets.Clear(); RevisitToolBasis = null; _revisitThoughtPrefixes.Clear();
    }
    internal ReflectionReasonBasis? RevisitActionBasis() => MergeReflectionBases(ReflectionReasonBasis, RevisitToolBasis);
    internal void WithRevisitAdmission(Action admission) { lock (_b1Ownership) admission(); }
    internal ThoughtRevisitTarget[] RevisitActionOffer() => RevisitAction == null || RevisitAttempt.Used
        || IsCancelled || !RequestConfig.ReflectionEnabled || !CanChooseNativeTool
        ? Array.Empty<ThoughtRevisitTarget>()
        : RevisitBaseTargets.Concat(RevisitToolTargets)
            .GroupBy(t => t.Target).Select(g => g.First()).OrderBy(t => t.Target.EventId, StringComparer.Ordinal)
            .Take(32).ToArray();
}
