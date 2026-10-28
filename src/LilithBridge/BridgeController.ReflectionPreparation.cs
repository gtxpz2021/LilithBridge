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
    private bool ReflectionMayPrepare() =>
        !(_requestPending && (_activeDialogue == null || _activeDialogue.HttpAttempts == 0
            || _activeDialogue.WaitStatus.Text == RequestWaitStatus.Preparing))
        && !_annotationInProgress && !_noteSupplementInProgress
        && !SharedActivityRunning && !SharedActivityUiBusy
        && !_sessionState.NativeDialogueActive && !_sessionState.PlayerOptionsShowing;

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

    private bool QueueReflectionPreparation(ReflectionHistorySelection selection)
    {
        if (_reflectionPreparation != null) return false;
        var total = Stopwatch.StartNew();
        var memory = _memory;
        var config = _config;
        if (config.SemanticRecallEnabled) TickSemanticRuntime();
        double runtimeMs = total.Elapsed.TotalMilliseconds;
        var owner = memory.PrepareReflection(selection.History, ++_reflectionGeneration);
        if (owner == null) return false;
        long coveredFactActivity = memory.MeasureMemoryActivity();
        long coveredModelRequest = _reflectionWindow.ModelRequestVersion;
        double windowMs = total.Elapsed.TotalMilliseconds - runtimeMs;
        _reflectionPreparation = owner; _reflectionInProgress = true;
        // Reuse the existing cancellable ready/index/query budget. This work is
        // background; foreground preparation can defer its next short step.
        int budget = config.SemanticQueryTimeoutMs;
        var worker = _semanticWorker;
        var snapshotTiming = Stopwatch.StartNew();
        Task<SemanticRecallResult>? discovery = config.SemanticRecallEnabled && worker != null && owner.Query.Length > 0
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
                var snapshot = memory.CaptureReflectionRequest(owner.Window, owner.SemanticResult, owner);
                if (snapshot == null) { CancelReflectionPreparation("window_no_longer_available"); return; }
                NativeEventLog.Trace("memory.reflection.prepared", "mod", "system", "reflection materials prepared without consuming the window",
                    "owner=" + owner.Id + ";status=" + owner.SemanticStatus + ";budget_ms=" + budget
                    + ";runtime_tick_ms=" + runtimeMs.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)
                    + ";window_capture_ms=" + windowMs.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)
                    + ";snapshot_ms=" + snapshotMs.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)
                    + ";discovery_elapsed_ms=" + workerMs.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)
                    + ";owner_queue_ms=" + ownerQueueMs.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)
                    + ";final_capture_ms=" + finalTiming.Elapsed.TotalMilliseconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)
                    + ";owner_total_ms=" + total.Elapsed.TotalMilliseconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
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
                                // Only a real outbound attempt stages consumption.
                                memory.StageReflectionWindow(_reflectionWindow, selection, coveredFactActivity, coveredModelRequest); owner.Staged = true;
                                _nextReflectionAt = UnityEngine.Time.unscaledTime + EffectiveReflectionGapSeconds();
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
                    ReflectionResult? response = null;
                    try { response = await _client.RunReflectionAsync(config, snapshot.Source, owner.Token, dispatch).ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                    catch (Exception error) { NativeEventLog.Trace("memory.reflection.error", "mod", "system", error.GetType().Name, "window_retained=true"); }
                    _mainThread.Enqueue(() => HoldReflectionStep(owner, memory, () => ReflectionMayApply(response), () =>
                    {
                        if (!ReferenceEquals(owner, _reflectionPreparation)) { owner.Dispose(); return; }
                        if (!CurrentReflectionPreparation(owner, memory)) { CancelReflectionPreparation("owner_changed_before_result"); return; }
                        if (owner.Staged)
                        {
                            // Recheck ALL material actually read, not only the
                            // proposal's citations. New append-only exchanges do
                            // not invalidate this old window; changed versions do.
                            if (!memory.TryUseReflectionRequest(owner, snapshot, () =>
                                { OnReflectionResult(response, snapshot); return true; }))
                            { CancelReflectionPreparation("source_or_version_changed_before_apply"); return; }
                        }
                        else
                        {
                            _nextReflectionPreparationAt = UnityEngine.Time.unscaledTime + ReflectionPreparationRetrySeconds;
                            NativeEventLog.Trace("memory.reflection.not_sent", "mod", "system", "reflection window remains unconsumed", "owner=" + owner.Id);
                        }
                        _reflectionPreparation = null; _reflectionInProgress = false;
                        owner.Dispose();
                    }));
                });
            }));
        });
        return true;
    }

    private bool CurrentReflectionPreparation(ReflectionPreparation owner, MemoryStore memory) =>
        ReferenceEquals(owner, _reflectionPreparation) && ReferenceEquals(memory, _memory) && !owner.Token.IsCancellationRequested
        && !_semanticDestroyed && _config.ReflectionEnabled
        && memory.SemanticDataRoot == owner.DataRoot && memory.ReviewSessionIdentity == owner.SessionId;

    private void CancelReflectionPreparation(string reason)
    {
        var owner = _reflectionPreparation;
        if (owner == null) return;
        _reflectionPreparation = null; _reflectionContinuation = null; owner.Cancel(); _reflectionInProgress = false;
        if (owner.Staged) _nextReflectionAt = UnityEngine.Time.unscaledTime + EffectiveReflectionGapSeconds();
        else _nextReflectionPreparationAt = UnityEngine.Time.unscaledTime + ReflectionPreparationRetrySeconds;
        if (owner.Staged) _reflectionWindow.EndPass(null);
        NativeEventLog.Trace("memory.reflection.preparation_cancelled", "mod", "system", "reflection window retained",
            "owner=" + owner.Id + ";reason=" + reason + ";staged=" + owner.Staged);
        owner.Dispose();
    }
}
