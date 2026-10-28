using System;
using Il2CppInterop.Runtime.Attributes;

namespace LilithBridge;

internal sealed partial class BridgeController
{
    [HideFromIl2Cpp]
    internal bool TryHandleMemoryCommand(MemoryTurn? input, out UserMemoryOperationResult? naturalOutcome)
    {
        naturalOutcome = null;
        if (input == null)
            return false;
        NativeEventLog.Flush();
        UserMemoryOperationResult? outcome = _memory.TryApplyUserMemoryCommand(input);
        if (outcome == null)
            return false;
        if (outcome.ContinueDialogue)
        {
            naturalOutcome = outcome;
            NativeEventLog.Trace("memory.natural_result", "mod", "system", outcome.Reason,
                "applied=" + outcome.Succeeded + ";candidates=" + outcome.Candidates
                + ";target=" + outcome.TargetId + ";revision=" + outcome.Revision);
            return false;
        }
        if (outcome.RemovedTexts is { Length: > 0 } texts)
        {
            _sessionState.RecentEvents.RemoveAll(e => _memory.IsExperienceSuppressed(e.EventId)
                || Array.Exists(texts,t => t.Length >= 4 && e.Details.Contains(t,StringComparison.Ordinal)));
            _sessionState.LastUserText = "";
            _sessionState.LastAssistantText = "";
            _sessionState.ConversationTopic = "";
            _sessionState.Relationship.RemoveTextReferences(texts);
            RemovePendingPaperReferences(texts);
        }
        QueueGeneratedReply(outcome.CreateReply(input));
        _queuedReplyBlockedByStory = HasActiveNativeStory();
        _proactiveRequest = false;
        // Ids and counters only: the matched text never enters the log (proposal §6).
        NativeEventLog.Trace("memory.explicit_result", "mod", "system", outcome.Reason,
            "match=" + (outcome.Match.Length > 0 ? outcome.Match : "direct")
            + ";candidates=" + outcome.Candidates
            + ";applied=" + (outcome.Succeeded ? "true" : "false")
            + (outcome.TargetId.Length > 0 ? ";target=" + outcome.TargetId : ""));
        return true;
    }

}
