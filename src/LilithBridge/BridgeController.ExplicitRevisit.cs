using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace LilithBridge;

internal sealed partial class BridgeController
{
    // Explicit control and accepted MAIN actions share one execution owner;
    // neither timer nor a read action invokes this capability.
    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    internal bool TryBeginExplicitThoughtRevisit(DaySourceDependency target, string causeId, out string reason)
        => TryBeginExplicitThoughtRevisit(_memory.ExplicitRevisitInvocation(target, causeId), out reason, out _);

    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private bool TryBeginExplicitThoughtRevisit(ReflectionRevisitInvocation invocation, out string reason, out string operationId)
    {
        operationId = "";
        reason = "not_ready";
        if (!_systemReady || !_config.ReflectionEnabled || !_reflectionWindow.InitializationSucceeded) return false;
        reason = "busy";
        if (_reflectionPreparation != null || _reflectionInProgress || _reflectionWindow.Snapshot.Work != null
            || _reflectionWindow.PassActive || _memory.ReadB1ReflectionJournal() is not { Work: null }
            || !ReflectionMayPrepare() || ReflectionClock() < _nextReflectionAt || ReflectionClock() < _nextReflectionPreparationAt) return false;
        reason = "not_configured_or_provider_wait";
        if (string.IsNullOrWhiteSpace(_config.ApiKey) || string.IsNullOrWhiteSpace(_config.ReflectionModel)
            || !_client.CanStartOpenAIBackground(_config, "reflection")) return false;
        var memory = _memory; var config = _config;
        var owner = new ReflectionPreparation(++_reflectionGeneration, memory.SemanticDataRoot,
            memory.ReviewSessionIdentity, "", Array.Empty<MemoryTurn>());
        operationId = owner.Id;
        var snapshot = memory.CaptureExplicitRevisitRequest(owner.Id, invocation, out reason);
        if (snapshot == null)
        {
            memory.RecordBlockedExplicitRevisit(_reflectionWindow, owner.Id, invocation, reason);
            owner.Dispose(); return false;
        }
        _reflectionPreparation = owner; _reflectionInProgress = true;
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
                    Task<HttpResponseMessage>? sent = null;
                    bool allowed = memory.TryUseReflectionRequest(owner, snapshot, () =>
                    {
                        if (!memory.BeginExplicitRevisitWork(_reflectionWindow, snapshot)) return false;
                        owner.Staged = true;
                        _reflectionDialogueMayBypassCooldown = false;
                        _nextReflectionAt = ReflectionClock() + EffectiveReflectionGapSeconds();
                        sent = send(); return true;
                    });
                    completion.TrySetResult(allowed ? sent : null);
                }
                catch (Exception error) { completion.TrySetException(error); }
                finally { registration.Dispose(); }
            }));
            return completion.Task;
        };
        Task.Run(async () =>
        {
            ReflectionResult? result = null;
            try { result = await _client.RunReflectionAsync(config, snapshot.Source, owner.Token, dispatch).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception error) { NativeEventLog.Trace("memory.revisit.error", "mod", "system", error.GetType().Name); }
            _mainThread.Enqueue(() => HoldReflectionStep(owner, memory, () => ReflectionMayApply(result),
                () => CompleteExplicitRevisitResponse(owner, memory, snapshot, result)));
        });
        reason = "accepted_for_one_attempt"; return true;
    }

    private void CompleteExplicitRevisitResponse(ReflectionPreparation owner, MemoryStore memory,
        ReflectionRequestSnapshot snapshot, ReflectionResult? result)
    {
        if (!ReferenceEquals(owner, _reflectionPreparation)) { owner.Dispose(); return; }
        if (!CurrentReflectionPreparation(owner, memory)) { CancelReflectionPreparation("revisit_owner_changed"); return; }
        if (owner.Staged && (_reflectionWindow.Snapshot.Work is not { Purpose: ReflectionWorkPurpose.ExplicitRevisit } current
            || current.Request.RequestId != snapshot.RequestId))
        { FinishExplicitRevisitOwner(owner); return; } // Deleted/revoked work must not be recreated by a late response.
        if (owner.Staged && result != null && !memory.SaveReflectionResponse(_reflectionWindow, snapshot, result))
        {
            // Keep this received value in the same owner while saving is blocked.
            // Waiting/retrying the local save cannot make another HTTP request.
            _nextReflectionPreparationAt = ReflectionClock() + ReflectionPreparationRetrySeconds;
            HoldReflectionStep(owner, memory, () => ReflectionClock() >= _nextReflectionPreparationAt && ReflectionMayApply(result),
                () => CompleteExplicitRevisitResponse(owner, memory, snapshot, result));
            return;
        }
        if (owner.Staged) memory.ResumeExplicitRevisit(_reflectionWindow, out _);
        else memory.RecordBlockedExplicitRevisit(_reflectionWindow, snapshot.RequestId, snapshot.RevisitContext!, "not_dispatched");
        FinishExplicitRevisitOwner(owner);
    }

    private void FinishExplicitRevisitOwner(ReflectionPreparation owner)
    {
        _nextReflectionAt = ReflectionClock() + EffectiveReflectionGapSeconds();
        _nextReflectionPreparationAt = ReflectionClock() + ReflectionPreparationRetrySeconds;
        _reflectionDialogueMayBypassCooldown = false;
        _reflectionPreparation = null; _reflectionContinuation = null; _reflectionInProgress = false;
        owner.Dispose();
    }
}
