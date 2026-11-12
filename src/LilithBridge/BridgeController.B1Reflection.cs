using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace LilithBridge;

internal sealed partial class BridgeController
{
    private MemoryStore.B1ReconsiderationOwner? _b1ReflectionOwner;
    private string _b1ReflectionInputStamp = "";
    private long _b1ReflectionModeGeneration;
    private long _b1ReflectionConversationGeneration;
    private string _b1ReflectionDataRoot = "";
    // A clock seam only; actual scheduling still uses the existing Unity clock.
    internal Func<float>? ReflectionClockForTests;
    internal Action<string>? B1ReflectionCheckpointForTests;
    private float ReflectionClock() => ReflectionClockForTests?.Invoke() ?? UnityEngine.Time.unscaledTime;

    private bool CurrentB1Reflection(ReflectionPreparation owner, MemoryStore memory)
        => _b1ReflectionOwner is {} b1 && owner.Generation == _reflectionGeneration
            && _b1ReflectionConversationGeneration == _conversationGeneration
            && _config.B1ReflectionEnabled && _config.ReflectionEnabled
            && ReferenceEquals(_config.B1CapabilityOwner, b1.Capability)
            && b1.Capability.ReflectionPermits(_b1ReflectionModeGeneration)
            && memory.ReflectionPreparationCurrent(owner)
            && memory.B1ReflectionInputStamp() == _b1ReflectionInputStamp && b1.Live();

    private bool B1ReflectionMayApply() => ReflectionMayPrepare()
        && !_requestPending && !_aiSpeechActive && _queuedReply == null;

    private void CancelB1Reflection(string reason)
    {
        if (_b1ReflectionOwner is not {} b1) return;
        // Explicit invalidation is not a crash. Do not replay its accepted text
        // as a success or no-op under a new cancellation token.
        b1.Store.InvalidateB1Reflection(b1.OpportunityId, reason, _b1ReflectionDataRoot);
        b1.Dispose(); _b1ReflectionOwner = null;
    }

    private void InvalidateB1ReflectionForInput()
    {
        if (_b1ReflectionOwner != null) CancelReflectionPreparation("new_input_generation");
        else if (_memory.ReadB1ReflectionJournal()?.Work is {} pending)
            _memory.InvalidateB1Reflection(pending.OperationId, "new_input_generation", pending.DataRoot);
    }

    private bool TryQueueB1Reflection(ReflectionHistorySelection selection)
    {
        var memory = _memory; var config = _config;
        var state = memory.ReadB1ReflectionJournal();
        if (state == null) return false; // outer gate pauses new work; existing normal recovery stays first
        var pending = state.Work;
        if (pending != null && (memory.B1ReflectionWasInvalidated(pending.OperationId) || !B1CapabilityOwner.ReflectionEnabled(config)
            || pending.Accepted == null))
        {
            // No accepted durable response: the old attempt may have sent.
            // Never manufacture a second model request to finish this opportunity.
            memory.EndB1ReflectionWork(pending.OperationId, memory.B1ReflectionWasInvalidated(pending.OperationId) ? "invalidated_prior_owner" : !config.B1ReflectionEnabled
                ? "invalidated_mode_disabled" : "interrupted_without_accepted_response");
            return true;
        }
        if (pending == null && (!B1CapabilityOwner.ReflectionEnabled(config)
            || !memory.B1ReflectionMaySelect(_reflectionWindow))) return false;
        var window = pending == null ? selection.History : memory.B1ReflectionRecoveryWindow(pending);
        var owner = memory.PrepareReflection(window, ++_reflectionGeneration);
        if (owner == null)
        {
            if (pending != null) { memory.InvalidateB1Reflection(pending.OperationId, "opportunity", pending.DataRoot); return true; }
            return false;
        }
        var b1 = pending == null ? memory.SelectB1Reflection(config, owner, state.LastLogicalId)
            : memory.RecaptureB1Reflection(config, owner, pending);
        if (b1 == null)
        {
            owner.Dispose();
            if (pending != null) { memory.InvalidateB1Reflection(pending.OperationId, "source", pending.DataRoot); return true; }
            return false;
        }
        if (pending == null) owner.IntentTrace = new(owner.Id, owner.Generation, _reflectionWindow.ModelRequestVersion,
            _reflectionWindow.ModelRequested, "b1_only_reconsideration", selection.Recent ? "recent" : "backlog",
            owner.Window.Select(t => t.EventId).ToArray());
        _reflectionPreparation = owner; _reflectionInProgress = true; _b1ReflectionOwner = b1;
        _b1ReflectionInputStamp = memory.B1ReflectionInputStamp();
        _b1ReflectionModeGeneration = config.B1ReflectionCapabilityGeneration;
        _b1ReflectionConversationGeneration = _conversationGeneration; _b1ReflectionDataRoot = owner.DataRoot;
        if (pending != null)
        {
            _mainThread.Enqueue(() => HoldReflectionStep(owner, memory, B1ReflectionMayApply,
                () => ApplyB1Reflection(owner, b1, memory, pending)));
            return true;
        }
        ReflectionDispatch dispatch = (send, token) =>
        {
            var completion = new TaskCompletionSource<Task<HttpResponseMessage>?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var registration = token.Register(() => completion.TrySetCanceled(token));
            _mainThread.Enqueue(() => HoldReflectionStep(owner, memory, ReflectionMayPrepare, () =>
            {
                try
                {
                    if (!CurrentReflectionPreparation(owner, memory) || token.IsCancellationRequested)
                    { completion.TrySetCanceled(token.IsCancellationRequested ? token : new CancellationToken(true)); return; }
                    // Source/capability boundary includes journal and actual synchronous
                    // handler dispatch, never the awaited response.
                    var sent = memory.DispatchB1Reflection(owner, b1, _reflectionWindow, _b1ReflectionInputStamp, send);
                    if (sent != null)
                    {
                        owner.Staged = true;
                        _reflectionDialogueMayBypassCooldown = false;
                        _nextReflectionAt = ReflectionClock() + EffectiveReflectionGapSeconds();
                    }
                    completion.TrySetResult(sent);
                }
                catch (Exception error) { completion.TrySetException(error); }
                finally { registration.Dispose(); }
            }));
            return completion.Task;
        };
        Task.Run(async () =>
        {
            B1ReflectionAccepted? accepted = null;
            try { accepted = await _client.RunB1ReflectionAsync(config,
                MemoryStore.B1ReflectionMaterial(b1), owner.Token, dispatch, memory).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception error) { NativeEventLog.Trace("memory.b1_reflection.error", "mod", "system", error.GetType().Name); }
            _mainThread.Enqueue(() => HoldReflectionStep(owner, memory, B1ReflectionMayApply, () =>
            {
                if (!CurrentReflectionPreparation(owner, memory)) { CancelReflectionPreparation("owner_changed_before_b1_result"); return; }
                if (accepted == null)
                {
                    ReflectionIntentTrace.Emit(() => new { stage = "service_outcome_observed", capture = owner.IntentTrace,
                        operation_id = b1.OpportunityId, service_kind = "b1_only_reconsideration", outcome = "no_accepted_response",
                        coverage = "no_semantic_resolution_claim" });
                    memory.EndB1ReflectionWork(b1.OpportunityId, "no_accepted_response"); FinishB1Reflection(owner, b1); return;
                }
                if (!memory.SaveB1ReflectionAccepted(b1.OpportunityId, accepted))
                {
                    ReflectionIntentTrace.Emit(() => new { stage = "service_outcome_observed", operation_id = b1.OpportunityId,
                        capture = owner.IntentTrace, outcome = "accepted_response_not_saved_or_owner_changed",
                        coverage = "no_commit_claim;existing_journal_result_unchanged" });
                    FinishB1Reflection(owner, b1); return;
                }
                B1ReflectionCheckpointForTests?.Invoke("after_accepted_saved");
                // Read the original durable accepted result, not a mutable caller
                // replacement. No B1 write can precede this checkpoint.
                var work = memory.ReadB1ReflectionJournal()?.Work;
                if (work == null) { FinishB1Reflection(owner, b1); return; }
                ApplyB1Reflection(owner, b1, memory, work);
            }));
        });
        return true;
    }

    private void ApplyB1Reflection(ReflectionPreparation owner, MemoryStore.B1ReconsiderationOwner b1,
        MemoryStore memory, B1ReflectionWork work)
    {
        if (!CurrentReflectionPreparation(owner, memory)) { CancelReflectionPreparation("owner_changed_before_b1_commit"); return; }
        var committed = memory.ApplyB1ReflectionAccepted(b1, work, owner);
        ReflectionIntentTrace.Emit(() => ReflectionIntentTrace.B1Outcome(work, committed));
        if (committed.Saved) B1ReflectionCheckpointForTests?.Invoke("after_b1_commit");
        // A persistence error keeps the exact operation and accepted response for
        // a fresh short-lived owner. Other failures are not converted into null.
        if (committed.Saved) memory.EndB1ReflectionWork(work.OperationId, committed.Outcome);
        else if (committed.Outcome is not ("save_failed_or_uncertain" or "store_busy_or_io"))
            memory.InvalidateB1Reflection(work.OperationId, "rejected_" + committed.Outcome, work.DataRoot);
        NativeEventLog.Trace("memory.b1_reflection.done", "mod", "system", "B1-only opportunity handled",
            "operation=" + work.OperationId + ";outcome=" + committed.Outcome + ";saved=" + committed.Saved);
        FinishB1Reflection(owner, b1);
    }
    private void FinishB1Reflection(ReflectionPreparation owner, MemoryStore.B1ReconsiderationOwner b1)
    {
        _reflectionDialogueMayBypassCooldown = false;
        _nextReflectionAt = ReflectionClock() + EffectiveReflectionGapSeconds();
        _reflectionPreparation = null; _reflectionContinuation = null; _reflectionInProgress = false;
        _b1ReflectionOwner = null; b1.Dispose(); owner.Dispose();
    }
}
