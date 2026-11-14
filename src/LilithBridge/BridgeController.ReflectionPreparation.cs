using System.Diagnostics;
using System.Net.Http;
using System.Threading;

namespace LilithBridge;

internal delegate Task<Task<HttpResponseMessage>?> ReflectionDispatch(Func<Task<HttpResponseMessage>> send, CancellationToken token);

internal sealed partial class BridgeController
{
    private ReflectionPreparation? _reflectionPreparation;
    private Func<bool>? _reflectionContinuation;
    private long _reflectionGeneration;
    private float _nextReflectionPreparationAt;
    private const float ReflectionPreparationRetrySeconds = 2f;

    // Waiting for an existing foreground HTTP or showing its reply is not a
    // source invalidation. Yield only the foreground's preparation segment.
    internal Func<bool>? ReflectionPreparationReadyForTests;
    private bool ReflectionMayPrepare() => ReflectionPreparationReadyForTests?.Invoke() ?? (
        !(_requestPending && (_activeDialogue == null || _activeDialogue.HttpAttempts == 0
            || _activeDialogue.WaitStatus.Text == RequestWaitStatus.Preparing))
        && !_annotationInProgress && !_noteSupplementInProgress
        && !SharedActivityRunning && !SharedActivityUiBusy
        && !_sessionState.NativeDialogueActive && !_sessionState.PlayerOptionsShowing);

    private bool ReflectionMayApply(ReflectionResult? response)
    {
        bool changesExisting = response?.Note is { } note && (note.Revises.Length > 0 || note.Settles.Length > 0)
            || response?.Lesson?.Revises.Length > 0 || response?.Relationship?.Revises.Length > 0;
        return !changesExisting || !(_requestPending || _queuedReply != null || _aiSpeechActive
            || NativeBubblePresenter.HasPendingReply);
    }

    // Only one owner exists. Its one next step can wait; this is not another
    // request queue and never makes a foreground submission await reflection.
    private void HoldReflectionStep(ReflectionPreparation owner, MemoryStore memory, Func<bool> ready, Action work)
    {
        if (!CurrentReflectionPreparation(owner, memory))
        {
            if (ReferenceEquals(owner, _reflectionPreparation)) CancelReflectionPreparation("owner_changed");
            else owner.Dispose();
            return;
        }
        _reflectionContinuation = () =>
        {
            if (!ready()) return false;
            work(); return true;
        };
    }

    private void TickReflectionContinuation()
    {
        if (_reflectionPreparation is { } owner && !CurrentReflectionPreparation(owner, _memory))
            CancelReflectionPreparation("owner_or_scope_changed");
        var step = _reflectionContinuation;
        if (step != null && step() && ReferenceEquals(step, _reflectionContinuation))
            _reflectionContinuation = null;
    }

    private bool QueueReflectionPreparation(ReflectionHistorySelection selection, ReflectionWork? recovery = null)
        => QueueReflectionPreparation(selection, recovery, ordinaryCurrentOnly: false);

    private bool QueueReflectionPreparation(ReflectionHistorySelection selection, ReflectionWork? recovery, bool ordinaryCurrentOnly)
    {
        if (recovery is { Purpose: not ReflectionWorkPurpose.Dialogue }) return false;
        if (_reflectionPreparation != null) return false;
        // Recheck at the real queue boundary. Frozen ordinary/B1 work keeps its
        // recovery route; an explicit reflect request bypasses only this soft wait.
        if (recovery == null && _memory.ReadB1ReflectionJournal()?.Work == null
            && OrdinaryReflectionDeferred(_config.ReflectionTestTrigger && !_lastReflectionTestTrigger)) return false;
        // An opportunity that only the current material clock earned cannot
        // be handed to B1, old material or a recovered Work.
        if (ordinaryCurrentOnly && (recovery != null || !CurrentDialogueReflection(selection))) return false;
        if (!ordinaryCurrentOnly && recovery == null && _reflectionWindow.Snapshot.Work == null)
        {
            if (_memory.B1ReflectionOpportunityAlreadyUsed(_reflectionWindow)) return false;
            // Timely normal dialogue may yield a completed pass's cooldown;
            // this candidate does not extend that bypass to B1-only work.
            if (ReflectionClock() >= _nextReflectionAt && TryQueueB1Reflection(selection)) return true;
        }
        var total = Stopwatch.StartNew();
        var memory = _memory;
        var config = _config;
        if (config.SemanticRecallEnabled && recovery == null) TickSemanticRuntime();
        double runtimeMs = total.Elapsed.TotalMilliseconds;
        var owner = memory.PrepareReflection(selection.History, ++_reflectionGeneration);
        if (owner == null) return false;
        owner.RetryBudgetEnabled = config.ReflectionRetryBudgetEnabled;
        long coveredFactActivity = memory.MeasureMemoryActivity();
        long coveredModelRequest = _reflectionWindow.ModelRequestVersion;
        bool capturedModelRequest = _reflectionWindow.ModelRequested;
        bool progressiveService = recovery == null
            ? !capturedModelRequest && !CurrentDialogueReflection(selection)
            : recovery.Request.RequestOriginContext == null && recovery.Request.PurposeContexts.Length == 0
                && !(MemoryStore.CountReflectionDialogueLines(recovery.Window) >= 2
                    && recovery.Window.Where(t => t.Role == "user").All(t => t.ReflectionExchangeComplete
                        && t.SessionId == memory.ReviewSessionIdentity));
        var capturedRequestOrigin = _reflectionWindow.Snapshot.RequestOrigin;
        var capturedPurposes = _reflectionWindow.Snapshot.PendingIntents?.ToArray();
        owner.IntentTrace = null; // Recovery keeps its existing durable work ID; original trace may be absent.
        if (recovery == null) owner.IntentTrace = new(owner.Id, owner.Generation, coveredModelRequest,
            _reflectionWindow.ModelRequested, "normal_reflection", selection.Recent ? "recent" : "backlog",
            owner.Window.Select(t => t.EventId).ToArray());
        double windowMs = total.Elapsed.TotalMilliseconds - runtimeMs;
        _reflectionPreparation = owner; _reflectionInProgress = true;
        // Reuse the existing cancellable ready/index/query budget. This work is
        // background; foreground preparation can defer its next short step.
        int budget = config.SemanticQueryTimeoutMs;
        var worker = _semanticWorker;
        var snapshotTiming = Stopwatch.StartNew();
        Task<SemanticRecallResult>? discovery = recovery == null && config.SemanticRecallEnabled && worker != null && owner.Query.Length > 0
            ? memory.DiscoverSemanticAsync(owner.Id, owner.Generation, owner.Query, SemanticDomain.Thoughts,
                DateTimeOffset.UtcNow.AddMilliseconds(budget), owner.Token) : null;
        double snapshotMs = snapshotTiming.Elapsed.TotalMilliseconds;
        if (discovery == null) owner.Unavailable(!config.SemanticRecallEnabled ? "disabled" : worker == null ? "semantic_unavailable" : "empty_topic");
        Task.Run(async () =>
        {
            SemanticRecallResult? result = null;
            try { if (discovery != null) result = await discovery.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch { owner.Unavailable("semantic_discovery_failed"); }
            double workerMs = total.Elapsed.TotalMilliseconds;
            _mainThread.Enqueue(() => HoldReflectionStep(owner, memory, ReflectionMayPrepare, () =>
            {
                if (!CurrentReflectionPreparation(owner, memory))
                {
                    if (ReferenceEquals(owner, _reflectionPreparation)) CancelReflectionPreparation("owner_or_opportunity_changed");
                    else owner.Dispose();
                    return;
                }
                if (result != null)
                {
                    if (ReferenceEquals(worker, _semanticWorker)) owner.Accept(result, worker!.Epoch);
                    else owner.Unavailable("semantic_worker_changed");
                }
                if (!memory.ReflectionPreparationCurrent(owner))
                { CancelReflectionPreparation("window_or_scope_changed"); return; }
                double ownerQueueMs = total.Elapsed.TotalMilliseconds - workerMs;
                var finalTiming = Stopwatch.StartNew();
                var snapshot = recovery?.Request ?? memory.CaptureReflectionRequest(owner.Window, owner.SemanticResult, owner, capturedPurposes);
                if (snapshot == null) { CancelReflectionPreparation("window_no_longer_available"); return; }
                if (recovery == null)
                    snapshot = capturedPurposes is { Length: > 0 } ? memory.BindReflectionPurposes(snapshot, capturedPurposes)
                        : memory.BindReflectionRequestOrigin(snapshot, capturedRequestOrigin, coveredModelRequest, capturedModelRequest);
                NativeEventLog.Trace("memory.reflection.prepared", "mod", "system", "reflection materials prepared without consuming the window",
                    "owner=" + owner.Id + ";status=" + owner.SemanticStatus + ";budget_ms=" + budget
                    + ";runtime_tick_ms=" + runtimeMs.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)
                    + ";window_capture_ms=" + windowMs.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)
                    + ";snapshot_ms=" + snapshotMs.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)
                    + ";discovery_elapsed_ms=" + workerMs.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)
                    + ";owner_queue_ms=" + ownerQueueMs.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)
                    + ";final_capture_ms=" + finalTiming.Elapsed.TotalMilliseconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)
                    + ";owner_total_ms=" + total.Elapsed.TotalMilliseconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
                var retryAttempt = owner.RetryBudgetEnabled ? new ReflectionRetryAttempt() : null;
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
                            ReflectionRetryAttemptStep? retryStep = null;
                            bool allowed = memory.TryUseReflectionRequest(owner, snapshot, () =>
                            {
                                if (retryAttempt != null)
                                {
                                    retryStep = DispatchOrdinaryReflectionWithBudget(retryAttempt, owner, memory, snapshot,
                                        selection, recovery, coveredFactActivity, coveredModelRequest, progressiveService, token, send);
                                    sent = retryStep.Request;
                                    return true; // Deferral must not take the false => discard Work path.
                                }
                                // Only a real outbound attempt stages consumption.
                                if (recovery == null) memory.StageReflectionWindow(_reflectionWindow, selection, coveredFactActivity, coveredModelRequest);
                                else memory.StageRecoveredReflection(_reflectionWindow);
                                if (!memory.BeginReflectionWork(_reflectionWindow, snapshot, selection))
                                { _reflectionWindow.EndPass(null); return false; }
                                owner.Staged = true;
                                RefreshReflectionCadence();
                                _reflectionCadence.Started(snapshot.RequestId);
                                _reflectionProgressiveCadence.Started(snapshot.RequestId, progressiveService);
                                _reflectionDialogueMayBypassCooldown = false;
                                _nextReflectionAt = ReflectionClock() + EffectiveReflectionGapSeconds();
                                ReflectionIntentTrace.Emit(() => ReflectionIntentTrace.Dispatch(snapshot, owner.IntentTrace));
                                sent = send(); return true;
                            }, recovering: recovery != null);
                            if (!allowed && recovery != null) memory.CancelReflectionWork(_reflectionWindow);
                            if (allowed && retryStep != null && retryStep.Request == null)
                                DeferOrdinaryReflectionRetry(owner, memory, snapshot, retryStep);
                            else if (retryStep?.Kind == ReflectionRetryAttemptKind.Entered)
                                TraceReflectionRetry(snapshot.RequestId, "local_entry", "fact=" + retryStep.Fact);
                            completion.TrySetResult(allowed ? sent : null);
                        }
                        catch (Exception error) { completion.TrySetException(error); }
                        finally { registration.Dispose(); }
                    }));
                    return completion.Task;
                };
                Task.Run(async () =>
                {
                    ReflectionLocalOutcome outcome = ReflectionLocalOutcome.Unobserved;
                    try { outcome = await _client.RunReflectionOutcomeAsync(config, snapshot.Source, owner.Token, dispatch).ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                    catch (Exception error) { NativeEventLog.Trace("memory.reflection.error", "mod", "system", error.GetType().Name, "window_retained=true"); }
                    _mainThread.Enqueue(() => HoldReflectionStep(owner, memory, () => ReflectionMayApply(outcome.Result),
                        () => CompleteOrdinaryReflectionResponse(owner, memory, snapshot, outcome.Result, recovery != null, outcome)));
                });
            }));
        });
        return true;
    }

    private bool CurrentReflectionPreparation(ReflectionPreparation owner, MemoryStore memory) =>
        ReferenceEquals(owner, _reflectionPreparation) && ReferenceEquals(memory, _memory) && !owner.Token.IsCancellationRequested
        && !_semanticDestroyed && _config.ReflectionEnabled
        && (_b1ReflectionOwner == null || CurrentB1Reflection(owner, memory))
        && memory.SemanticDataRoot == owner.DataRoot && memory.ReviewSessionIdentity == owner.SessionId;

    private void CancelReflectionPreparation(string reason)
    {
        var owner = _reflectionPreparation;
        if (owner == null) return;
        _reflectionPreparation = null; _reflectionContinuation = null; owner.Cancel(); _reflectionInProgress = false;
        CancelB1Reflection(reason);
        if (owner.Staged)
        {
            _nextReflectionAt = ReflectionClock() + EffectiveReflectionGapSeconds();
            _reflectionDialogueMayBypassCooldown = false;
        }
        else _nextReflectionPreparationAt = ReflectionClock() + ReflectionPreparationRetrySeconds;
        if (owner.Staged) _reflectionWindow.EndPass(null);
        ReflectionIntentTrace.Emit(() => new { stage = "preparation_cancelled", capture = owner.IntentTrace,
            preparation_id = owner.Id, staged = owner.Staged, reason,
            coverage = "no_semantic_resolution_claim;existing_window_retention_unchanged" });
        NativeEventLog.Trace("memory.reflection.preparation_cancelled", "mod", "system", "reflection window retained",
            "owner=" + owner.Id + ";reason=" + reason + ";staged=" + owner.Staged);
        owner.Dispose();
    }
}
