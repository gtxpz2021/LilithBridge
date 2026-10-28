using System;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
namespace LilithBridge;

internal sealed partial class BridgeController
{
    private SharedActivityRuntime? _sharedActivity;
    private readonly NativeSharedActivityWindow _sharedWindow = new();
    private SharedAttempt? _sharedWaitingAttempt;
    private SharedActivityResponse? _sharedWaitingResponse;
    private int _sharedActivityAuxRequests;
    private bool SharedActivityRunning => _sharedActivity?.Running == true;
    private bool SharedActivityUiBusy => _sharedWindow.OwnsKeyboard || _sharedWindow.IsEditing;
    private bool SharedActivityNativeBusy => HasActiveNativeStoryForProactive() || _sessionState.PlayerOptionsShowing
        || NativeStateInspector.IsSleeping || _lastDrag || _leftDragging || ActionDirector.NativeBusy(_sessionState);
    private bool SharedActivityIdle => !_listening && !_inputField.IsOpen && !_historyWindow.IsOpen
        && !_sharedWindow.IsEditing && !_requestPending && !_storyRecallInProgress && !_memoryRecallInProgress
        && !_annotationInProgress && !_noteSupplementInProgress && !_reflectionInProgress && !_dayTaskInProgress
        && System.Threading.Volatile.Read(ref _sharedActivityAuxRequests) == 0
        && !_aiSpeechActive && _queuedReply == null && !NativeBubblePresenter.HasPendingReply && !SharedActivityNativeBusy;
    private SharedActivityRuntime SharedRuntime => _sharedActivity ??= new SharedActivityRuntime(
        new SharedActivityStore(Path.Combine(DataPaths.Root(), "shared-activity")));
    private void OpenSharedActivity()
    {
        if (!_config.SharedActivityEnabled || SharedActivityNativeBusy) return;
        try
        {
            SharedRuntime.SetEnabled(true);
            _inputField.SuspendForHistory(true); _listening = false;
            _sharedWindow.Show(transform, _inputField.StyleText, SharedRuntime, CloseSharedActivity,
                ToggleSharedActivityChat, () => _conversationGeneration);
        }
        catch (Exception error)
        { _sharedWindow.Hide(); _inputField.SuspendForHistory(false); _listening = true;
            Plugin.PluginLog.LogWarning("Shared activity unavailable: " + error.GetType().Name); }
    }
    private void ToggleSharedActivityChat()
    {
        if (_sharedWindow.OwnsKeyboard)
        {
            _sharedWindow.UseCompactView(); _inputField.SuspendForHistory(false); _listening = true;
            _ignoreEnterUntil = Time.unscaledTime + .35f;
        }
        else OpenSharedActivity();
    }
    private void CloseSharedActivity()
    {
        _sharedActivity?.Stop(); _sharedWindow.Hide(); _sharedWaitingAttempt = null; _sharedWaitingResponse = null;
        // The original TMP field retains its draft and review authorization.
        if (!SharedActivityNativeBusy) { _inputField.SuspendForHistory(false); _listening = true; }
        _ignoreEnterUntil = Time.unscaledTime + .35f;
    }
    private void InterruptSharedActivity()
    {
        _sharedActivity?.Interrupt(_conversationGeneration); _sharedWaitingAttempt = null; _sharedWaitingResponse = null;
    }
    private void TickSharedActivity()
    {
        // Default off: no store is constructed, opened, migrated or requested.
        if (_sharedActivity == null) return;
        _sharedActivity.SetEnabled(_config.SharedActivityEnabled);
        if (!_config.SharedActivityEnabled) { if (_sharedWindow.IsOpen) CloseSharedActivity(); return; }
        if (SharedActivityNativeBusy)
        {
            InterruptSharedActivity();
            if (_sharedWindow.IsOpen)
            {
                _sharedActivity.Stop();
                _inputField.StageReplyFailure(_inputField.Text, false, "");
                _sharedWindow.Hide();
            }
            return;
        }
        _sharedWindow.Tick();
        if (_sharedWaitingAttempt != null && SharedActivityIdle)
        {
            var attempt = _sharedWaitingAttempt; var response = _sharedWaitingResponse!;
            _sharedWaitingAttempt = null; _sharedWaitingResponse = null;
            if (response.Raw == null) _sharedActivity.Fail(attempt, response.Error);
            else _sharedActivity.Commit(attempt, response.Raw, DateTimeOffset.UtcNow, Time.unscaledTime);
            _sharedWindow.Render();
        }
        var next = _sharedActivity.TryBegin(Time.unscaledTime, _conversationGeneration,
            SharedActivityIdle, _config.ProactiveCooldownSeconds);
        if (next == null) return;
        BridgeConfig config = _config;
        Task.Run(async () =>
        {
            var response = await _client.SendSharedActivityAsync(config, _sharedActivity, next).ConfigureAwait(false);
            _mainThread.Enqueue(() =>
            {
                if (!_sharedActivity.IsCurrent(next)) return;
                _sharedWaitingAttempt = next; _sharedWaitingResponse = response;
            });
        });
    }
    private SharedActivitySupply? CaptureSharedActivitySupply(string requestId)
        => _config.SharedActivityEnabled ? _sharedActivity?.CaptureSupply(requestId) : null;
    private void DestroySharedActivity()
    { _sharedActivity?.SetEnabled(false); _sharedWaitingAttempt = null; _sharedWaitingResponse = null; _sharedWindow.Destroy(); }
}
