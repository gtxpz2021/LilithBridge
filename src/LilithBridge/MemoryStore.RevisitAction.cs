using System;
using System.Collections.Generic;
using System.Linq;

namespace LilithBridge;

// Public, bounded purpose text from an actual accepted MAIN tool call. It is
// not private reasoning, user evidence, or a fabricated model reflect tag.
internal sealed record MainRevisitOrigin(string RequestId, string UserEventId, string ToolCallId,
    string Reason, ReflectionReasonBasis? Basis);

internal sealed partial class MemoryStore
{
    internal const string MainRevisitEntry = "main_tool_call";
    private ThoughtRevisitTarget? RevisitVisibleTarget(MemoryTurn note, string body, string location)
    {
        if (note.Kind != "reflection.note" || body.Length == 0 || note.Origin != "reflection"
            || !(IsActiveLifecycle(note) || IsSettledLifecycle(note))) return null;
        var target = SourceVersion("note", note);
        return CaptureExplicitRevisitTarget(note.EventId) == target ? new(target, body, location) : null;
    }

    // The existing renderer supplies a complete labelled thought line. Merely
    // appearing in a permission footprint, an ID, or a truncated block is not a body.
    internal void CaptureRevisitBaseTargets(DialogueRequest request, string actualContext)
    {
        lock (_experienceLock)
        {
            var pool = request.MethodRuntimeSources.Concat(request.B1HostFootprint)
                .Where(t => t.Kind == "reflection.note").DistinctBy(t => (t.EventId, SourceVersion("note", t))).ToArray();
            request.RevisitBaseTargets = pool.Where(t => actualContext.Contains(DescribeThoughtVersion(t, pool), StringComparison.Ordinal))
                .Select(t => RevisitVisibleTarget(t, t.Text, "current_context:note=" + t.EventId))
                .Where(t => t != null).Cast<ThoughtRevisitTarget>().ToArray();
        }
    }

    // Called after final page fitting, in the same source-checked transcript
    // append as the read result. Metadata-only and omitted bodies grant nothing.
    internal void AppendRevisitToolTargets(DialogueRequest request, ReviewToolOutcome outcome, string callId)
    {
        lock (_experienceLock)
        {
            void AddTarget(MemoryTurn turn, string body, string location)
            {
                if (RevisitVisibleTarget(turn, body, location) is {} target
                    && !request.RevisitToolTargets.Any(t => t.Target == target.Target)) request.RevisitToolTargets.Add(target);
            }
            var bodies = new List<(MemoryTurn Turn, string Text)>();
            if (outcome.RevisitRead is {} revisit) bodies.AddRange(revisit.Items.Select(i => (i.Turn, i.Text)));
            if (outcome.ChatHistory is {} history) bodies.AddRange(history.Items.Where(i => i.BodyIncluded).Select(i => (i.Turn, i.Text)));
            if (outcome.DayArtifacts is {} days) bodies.AddRange(days.Items.Select(i => (i.Turn, i.Text)));
            if (outcome.ThoughtRead is {} thoughts && thoughts.Options.Scope == "record")
                foreach (var item in thoughts.Items)
                    if (request.AppendRevisitThoughtPage(item, callId) is {} location)
                        AddTarget(item.Turn, item.Turn.Text, location);
            foreach (var body in bodies) AddTarget(body.Turn, body.Text, "tool:" + callId);
            var basis = CaptureReflectionReasonSources(bodies.Where(b => b.Text.Length > 0).Select(b => b.Turn)
                .Concat(outcome.ThoughtRead?.Items.Select(i => i.Turn) ?? Array.Empty<MemoryTurn>())
                .Concat(outcome.ThoughtRead?.AnchorTurn is {} anchor ? new[] { anchor } : Array.Empty<MemoryTurn>()),
                Array.Empty<MethodSource>(), null, true);
            var previous = request.RevisitToolBasis;
            request.RevisitToolBasis = previous == null ? basis : new(
                previous.Dependencies.Concat(basis.Dependencies).Distinct().ToArray(), previous.Complete && basis.Complete,
                previous.GeneratedSources);
        }
    }

    internal ReflectionRevisitInvocation MainRevisitInvocation(ThoughtRevisitChoice choice, string requestId,
        string userEventId, string toolCallId, ReflectionReasonBasis? basis) =>
        new(MainRevisitEntry, requestId, DateTimeOffset.UtcNow, _root, _sessionId, choice.Target,
            new(requestId, userEventId, toolCallId, choice.Reason, basis));

    private static bool ValidMainRevisitOrigin(MainRevisitOrigin r) => r != null
        && !string.IsNullOrWhiteSpace(r.RequestId) && r.RequestId.Length <= 256
        && !string.IsNullOrWhiteSpace(r.UserEventId) && r.UserEventId.Length <= 256
        && !string.IsNullOrWhiteSpace(r.ToolCallId) && r.ToolCallId.Length <= 256
        && !string.IsNullOrWhiteSpace(r.Reason) && r.Reason.Length <= 600
        && r.Basis is { Dependencies: not null };

    private bool MainRevisitOriginPermitted(MainRevisitOrigin? origin, ReflectionRequestSnapshot? request = null) => origin == null
        || ValidMainRevisitOrigin(origin) && IsMentionTextAllowed(origin.Reason) && !SuppressedTextMatches(origin.Reason)
            && ReflectionReasonBasisCurrent(origin.Basis, request);

    private static bool MainRevisitOriginReferences(MainRevisitOrigin? origin, HashSet<string> ids) => origin != null
        && (ids.Contains(origin.RequestId) || ids.Contains(origin.UserEventId)
            || origin.Basis?.Dependencies.Any(d => ids.Contains(d.Id)) == true
            || origin.Basis?.GeneratedSources?.Any(d => ids.Contains(d.Key)) == true);
}
