using System;
using System.IO;
using System.Linq;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;

namespace LilithBridge;

internal sealed partial class BridgeController
{
    private readonly SpeechToTextSession _sttSession = new();
    private SpeechToTextWorker? _sttWorker;
    private BridgeConfig? _sttLastConfigRef;
    private string _sttWorkerKey = "";
    private string _sttLastStatus = "";
    private string _sttTransientNotice = "";
    private float _sttTransientNoticeUntil;
    private bool _sttDestroyed;
    private bool _sttCreationAttempted;
    private bool _sttHotkeyDown;

    private bool IsSttActive => _sttSession.IsActive;

    private void TickSttRuntime()
    {
        if (_sttDestroyed)
            return;

        bool uiDirty = false;
        if (!ReferenceEquals(_config, _sttLastConfigRef))
        {
            _sttLastConfigRef = _config;
            string dataRoot = _config.DataDirectory.Length > 0 ? _config.DataDirectory : DataPaths.Root();
            string replSig = _config.SttReplacements is { Count: > 0 }
                ? string.Join(";", System.Linq.Enumerable.OrderBy(_config.SttReplacements, p => p.Key, StringComparer.Ordinal)
                    .Select(p => p.Key + "=" + p.Value))
                : "";
            string key = string.Join("|",
                dataRoot,
                _config.SttPythonPath,
                _config.SttWorkerScript,
                _config.SttModelPath,
                _config.SttVadModelPath,
                _config.SttLanguage,
                _config.SttUseItn,
                _config.SttNumThreads,
                _config.SttPartialIntervalMs,
                _config.SttMaxDurationSeconds,
                _config.SttBuiltinLexicon,
                replSig);

            if (!_config.SttEnabled || key != _sttWorkerKey)
            {
                if (_sttWorker != null || _sttSession.IsActive)
                {
                    CancelSttRecording(restorePrefix: false);
                    _sttWorker?.Dispose();
                    _sttWorker = null;
                }
                _sttWorkerKey = key;
                _sttCreationAttempted = false;
                uiDirty = true;
            }

            if (_config.SttEnabled && _sttWorker == null && !_sttCreationAttempted)
            {
                _sttCreationAttempted = true;
                try
                {
                    _sttWorker = new SpeechToTextWorker(_config, dataRoot, OnSttWorkerEvent);
                    _sttWorker.Start();
                }
                catch (Exception ex)
                {
                    _sttLastStatus = "unavailable:" + ex.GetType().Name;
                }
                uiDirty = true;
            }
        }

        string status = _sttWorker?.Status ?? (_config.SttEnabled ? _sttLastStatus : "disabled");
        if (status != _sttLastStatus)
        {
            _sttLastStatus = status;
            NativeEventLog.Trace("stt.state", "mod", "system", "local stt worker state", status);
            uiDirty = true;
        }

        if (_sttTransientNotice.Length > 0 && Time.unscaledTime >= _sttTransientNoticeUntil)
        {
            _sttTransientNotice = "";
            uiDirty = true;
        }

        if (uiDirty)
            RefreshSttUi();
    }

    private void StopSttRuntime()
    {
        _sttDestroyed = true;
        CancelSttRecording(restorePrefix: false);
        _sttWorker?.Dispose();
        _sttWorker = null;
    }

    private bool TryHandleSttHotkey(bool gameHasInputFocus)
    {
        if (!_config.SttEnabled || !ConfigStore.TryParseSttHotkey(_config.SttHotkey, out KeyCode hotkey))
        {
            _sttHotkeyDown = false;
            return false;
        }

        bool down = NativeInput.IsKeyDown(hotkey);
        bool pressed = down && !_sttHotkeyDown && gameHasInputFocus;
        _sttHotkeyDown = down;
        if (!pressed)
            return false;

        if (_sttSession.IsRecordingOrStarting)
        {
            StopSttRecording();
            return true;
        }

        if (_sttSession.IsFinalizing)
            return true;

        if (!_listening)
        {
            if (!CanOpenReplyWindow())
                return false;
            OpenInput();
            if (!_listening)
                return false;
        }

        ToggleSttRecording();
        return true;
    }

    private bool HandleEnterWhileSttActive()
    {
        if (!_sttSession.IsActive)
            return false;
        // Never auto-submit while recording or finalizing; Enter during recording ends
        // dictation so the player can review and edit the recognized text first.
        if (_sttSession.IsRecordingOrStarting)
            StopSttRecording();
        return true;
    }

    private void ToggleSttRecording()
    {
        if (!_config.SttEnabled)
            return;

        if (_sttSession.IsRecordingOrStarting)
        {
            StopSttRecording();
            return;
        }

        if (_sttSession.IsFinalizing)
            return;

        if (_sttWorker == null)
        {
            ShowSttTransientNotice(SpeechToTextSession.HumanizeError("worker_unavailable"));
            return;
        }
        if (!_sttWorker.Ready)
        {
            string code = string.Equals(_sttWorker.Status, "initializing", StringComparison.Ordinal)
                ? "worker_not_ready"
                : "worker_unavailable";
            ShowSttTransientNotice(SpeechToTextSession.HumanizeError(code));
            return;
        }

        _sttTransientNotice = "";
        if (_config.TtsInterruptOnStt)
            CancelTtsSpeech("stt_recording");
        string recId = _sttSession.BeginRecording(_inputField.Text);
        _inputField.SetDictationReadOnly(true);
        _sttWorker.SendStartRecording(recId);
        NativeEventLog.Trace("stt.start", "mod", "player", "local voice recording started", recId);
        RefreshSttUi();
    }

    private void StopSttRecording()
    {
        if (!_sttSession.BeginStop(out string recId))
            return;
        _sttWorker?.SendStopRecording(recId);
        NativeEventLog.Trace("stt.stop", "mod", "player", "local voice recording stopped", recId);
        RefreshSttUi();
    }

    private void CancelSttRecording(bool restorePrefix)
    {
        if (!_sttSession.IsActive)
            return;
        string prefix = _sttSession.Cancel(out string recId);
        if (!string.IsNullOrEmpty(recId))
            _sttWorker?.SendCancelRecording(recId);
        if (restorePrefix && _inputField.IsOpen)
            _inputField.SetDictationText(prefix, restoreEditableFocus: true);
        else
            _inputField.SetDictationReadOnly(false);
        NativeEventLog.Trace("stt.cancel", "mod", "player", "local voice recording cancelled", recId);
        RefreshSttUi();
    }

    [HideFromIl2Cpp]
    private void OnSttWorkerEvent(SttWorkerEvent ev)
    {
        if (_sttDestroyed)
            return;
        _mainThread.Enqueue(() => HandleSttEventOnMainThread(ev));
    }

    [HideFromIl2Cpp]
    private void HandleSttEventOnMainThread(SttWorkerEvent ev)
    {
        if (_sttDestroyed)
            return;

        switch (ev.Kind)
        {
            case "ready":
                RefreshSttUi();
                break;

            case "recording_started":
                if (_sttSession.MarkRecordingStarted(ev.RecordingId))
                    RefreshSttUi();
                break;

            case "level":
                if (_sttSession.ApplyLevel(ev.RecordingId, ev.Level))
                    RefreshSttUi();
                break;

            case "partial":
            {
                SttTextUpdate update = _sttSession.ApplyPartial(ev.RecordingId, ev.Text, ev.Level);
                if (update.Applied && _listening && _inputField.IsOpen)
                    _inputField.SetDictationText(update.Text, restoreEditableFocus: false);
                RefreshSttUi();
                break;
            }

            case "transcribing":
                if (_sttSession.MarkTranscribing(ev.RecordingId))
                    RefreshSttUi();
                break;

            case "final":
            {
                SttTextUpdate update = _sttSession.ApplyFinal(ev.RecordingId, ev.Text, ev.Reason);
                if (update.Applied)
                {
                    if (_listening && _inputField.IsOpen)
                        _inputField.SetDictationText(update.Text, restoreEditableFocus: true);
                    else
                        _inputField.SetDictationReadOnly(false);
                    if (update.StatusMessage.Length > 0)
                        ShowSttTransientNotice(update.StatusMessage);
                    NativeEventLog.Trace("stt.final", "mod", "system", "local voice dictation finalized",
                        $"dur_ms={ev.DurationMs};infer_ms={ev.InferMs:F1};chars={ev.Text.Length};reason={ev.Reason}");
                }
                RefreshSttUi();
                break;
            }

            case "error":
            case "worker_exited":
            {
                SttTextUpdate update = _sttSession.ApplyFailure(ev.RecordingId, ev.ErrorCode);
                if (update.Applied)
                {
                    if (_listening && _inputField.IsOpen)
                        _inputField.SetDictationText(update.Text, restoreEditableFocus: true);
                    else
                        _inputField.SetDictationReadOnly(false);
                    if (update.StatusMessage.Length > 0)
                        ShowSttTransientNotice(update.StatusMessage);
                }
                RefreshSttUi();
                break;
            }
        }
    }

    private void ShowSttTransientNotice(string message)
    {
        _sttTransientNotice = message ?? "";
        _sttTransientNoticeUntil = Time.unscaledTime + 3.5f;
        RefreshSttUi();
    }

    private void RefreshSttUi()
    {
        if (!_config.SttEnabled)
        {
            _inputField.RefreshSttVisual(SttButtonVisualState.Hidden);
            return;
        }

        string hotkeyHint = ConfigStore.TryParseSttHotkey(_config.SttHotkey, out KeyCode parsedKey)
            ? "或按 " + parsedKey + " "
            : "";

        if (_sttSession.IsRecordingOrStarting)
        {
            string msg = _sttSession.Phase == SttPhase.Starting
                ? "正在打开麦克风…"
                : $"正在录音… 再点「停」{hotkeyHint}结束并编辑";
            _inputField.RefreshSttVisual(SttButtonVisualState.Recording, msg, _sttSession.LastLevel);
            return;
        }

        if (_sttSession.IsFinalizing)
        {
            _inputField.RefreshSttVisual(SttButtonVisualState.Finalizing, "正在整理识别结果…");
            return;
        }

        if (_sttWorker == null)
        {
            _inputField.RefreshSttVisual(SttButtonVisualState.Unavailable, _sttTransientNotice);
            return;
        }

        if (_sttWorker.Ready)
        {
            _inputField.RefreshSttVisual(SttButtonVisualState.Ready, _sttTransientNotice);
            return;
        }

        if (string.Equals(_sttWorker.Status, "initializing", StringComparison.Ordinal))
            _inputField.RefreshSttVisual(SttButtonVisualState.Initializing, _sttTransientNotice);
        else
            _inputField.RefreshSttVisual(SttButtonVisualState.Unavailable, _sttTransientNotice);
    }
}
