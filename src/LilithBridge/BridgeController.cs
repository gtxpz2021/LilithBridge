using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;

namespace LilithBridge;

internal sealed partial class BridgeController : MonoBehaviour
{
    internal static BridgeController? Instance { get; private set; }

    private readonly MemoryStore _memory = new();
    private RuntimeMemoryPipeline _runtimeMemory = null!;
    private readonly NativeInputField _inputField = new();
    private readonly NativeHistoryWindow _historyWindow = new();
    private readonly ConcurrentQueue<Action> _mainThread = new();
    private readonly DeepSeekClient _client = new();
    private readonly SessionState _sessionState = new();
    private BridgeConfig _config = ConfigStore.Load();
    private ApiResult? _queuedReply;

    private bool _systemReady;
    private bool _listening;
    private bool _requestPending;
    private bool _queuedReplyBlockedByStory;
    private readonly Queue<string> _testInputQueue = new();
    private bool _testInputLoaded;
    private float _lastTestInputSentAt = float.NegativeInfinity;
    private bool _aiSpeechActive;
    private bool _nativeWasActive;
    private int _lastNativeNodeId = -1;
    private CharacterHitAreaType _lastNativeHitArea;
    private float _lastNativeHitAreaAt;
    private float _replyWindowUntil = float.PositiveInfinity;
    private float _nextConfigReload;
    private float _nextStateCheck;
    private bool _lastLeft;
    private bool _lastRight;
    private float _lastScroll;
    private int _lastTouch;
    private bool _lastDrag;
    private bool _lastInteract;
    private LilithActionType _lastAction = LilithActionType.None;
    private bool _escapeDown;
    private float _ignoreEnterUntil;
    private string _currentUserText = "";
    private bool _proactiveRequest;
    private float _nextAppCheck;
    private float _systemReadyAt;
    private ForegroundAppInfo? _lastApp;
    private bool _focusLost;
    private float _focusLostAt;
    private float _lastReturnAt;
    private float _lastProactiveAt = float.NegativeInfinity;
    private readonly System.Random _rng = new();
    private string? _pendingProactiveReason;
    private float _nextProactiveLog;
    private float _lastAiSpeechEndAt = float.NegativeInfinity;
    private float _lastNativeDialogueEndAt = float.NegativeInfinity;
    private float _lastPlaybackDeferLogAt = float.NegativeInfinity;
    private const float PlaybackDeferLogIntervalSeconds = 5f;
    private float _lastNativeDoubleClickAt = float.NegativeInfinity;
    private Vector2? _pendingManualClick;
    private float _pendingManualClickAt = float.NegativeInfinity;
    private const float ManualClickFallbackSeconds = 0.5f;
    private const float NativeEventGraceSeconds = 1.2f;
    private bool _annotationInProgress;
    private float _nextMemoAnnotationAt;
    private bool _noteSupplementInProgress;
    private bool _actionInventoryGenerated;
    private bool _rawAnimationProbeGenerated;
    private bool _actionPoseClothingAuditGenerated;
    private bool _lastNoteTestTrigger;
    private float _nextNoteSupplementAt;
    private bool _reflectionInProgress;
    private bool _storyRecallInProgress;
    private bool _memoryRecallInProgress;
    private bool _lastReflectionTestTrigger;
    private float _nextReflectionAt;
    private readonly ReflectionWindowCursor _reflectionWindow = new();
    private bool _dayTaskInProgress;
    private string _pendingProactiveRecallQuery = "";
    private float _nextDreamAt;
    private float _nextPersonaSave;
    private float _nextSceneSummarySave;
    private float _nextInteractionSubscribe;
    private bool _debugProactiveTriggered;
    private bool _lastSleep;
    private bool _lastSit;
    private bool _lastLie;
    private bool _lastMusicRunning;
    private bool _lastRivalPet;
    private float _nextNativeStateCheck;
    private bool _leftDragging;
    private float _leftDownAt;
    private Vector2 _leftDownPosition;
    private bool _desktopArrangeProposePending;
    private float _desktopArrangeProposalAt;
    private float _lastDesktopArrangeProposalAt = float.NegativeInfinity;
    private bool _desktopArrangeExecutePending;
    private bool _desktopArrangeUndoPending;
    private bool _desktopArrangeOperationPending;
    private bool _snackSessionActive;
    private DesktopArrangeSnapshot? _lastDesktopArrangeSnapshot;
    private bool _desktopArrangeInspectPending;
    private void Awake()
    {
        Instance = this;
        try
        {
            DontDestroyOnLoad(gameObject);
            Application.runInBackground = true;
        }
        catch
        {
            // BepInEx's global object already persists.
        }
        _runtimeMemory = NaturalMemory.CreatePipeline(_memory, _config);
        TickSemanticRuntime();
        NativeEventLog.Trace("memory.natural.initialization", "mod", "system", "natural memory writer initialization",
            "writer=" + _config.MemoryUserWriteEnabled + ";extract_every_turn=" + _config.AutonomousMemoryExtractEveryTurn);
        NativeEventLog.Trace("bridge.start", "mod", "system",
            "LilithBridge ready; native interactions are preserved");
    }

    private void OnApplicationFocus(bool hasFocus)
    {
        if (!hasFocus)
        {
            _focusLost = true;
            _focusLostAt = Time.unscaledTime;
            return;
        }
        if (_focusLost && Time.unscaledTime - _focusLostAt >= 30f)
        {
            _sessionState.PlayerRecentlyReturned = true;
            _lastReturnAt = Time.unscaledTime;
        }
        _focusLost = false;
    }

    private readonly NativeRequestStatus _requestStatus = new();

    private void OnDestroy()
    {
        if (ReferenceEquals(Instance, this))
            Instance = null;
        CancelPendingDialogue();
        DestroySharedActivity();
        StopSemanticRuntime();
        _activeDialogue?.Dispose();
        _inputField.Destroy();
        _requestStatus.Destroy();
        _historyWindow.Destroy();
        ActionExecutor.Clear();
        NativeWindowPeekDirector.Clear();
        NativePoseGate.Clear();
        _client.Dispose();
        try
        {
            LastSceneSummary? summary = _sessionState.CreateLastSceneSummary();
            if (summary != null)
                _memory.SaveLastSceneSummary(summary);
        }
        catch
        {
            // Best-effort on shutdown.
        }
        NativeEventLog.Flush();
    }

    private void Update()
    {
        BeginPreparationFrame();
        if (Time.unscaledTime >= _nextConfigReload)
        {
            _nextConfigReload = Time.unscaledTime + 1f;
            _config = ConfigStore.Refresh();
            _inputField.RefreshThinkingMode(_config);
        }

        TickSemanticRuntime();
        if (!_systemReady)
            TryBecomeReady();
        if (_systemReady && Time.unscaledTime >= _nextInteractionSubscribe)
        {
            _nextInteractionSubscribe = Time.unscaledTime + 2f;
            NativeInteractionSubscriber.Subscribe();
        }

        TraceInput();
        TrackNativeDialogue();
        NativeBubblePresenter.Tick();
        TrackCharacterState();
        MaybeSavePersonaState();
        MaybeSaveLastSceneSummary();
        TrackForegroundApp();
        TrackNativeStateChanges();
        MaybeTriggerProactive();
        TickTestInput();
        TryScheduleMemoAnnotation();
        TryScheduleNoteSupplement();
        TryDeliverPendingPaper();
        TryScheduleReflection();
        TryConsolidateDream();
        TickSharedActivity();
        ObservePreparationBeforeDrain();
        DrainMain();
        _requestStatus.Tick(transform, _inputField.StyleText, _activeDialogue,
            _requestPending && !_listening && !_historyWindow.IsOpen
            && !_sessionState.PlayerOptionsShowing && !NativeStateInspector.IsSleeping
            && NativeInput.IsGameForeground());
        TickScreenCapture();
        ValidateReviewHandoffBoundary();
        UpdateVitalState();
        ActionExecutor.SetWaiting(_listening || _requestPending, _sessionState);
        MicroReactionDirector.Tick(_sessionState, Time.unscaledTime, _config);
        NativeWindowPeekDirector.Tick(Time.unscaledTime);
        NativePoseGate.Tick(Time.unscaledTime);
        if (!ActionDirector.NativeBusy(_sessionState))
            ActionExecutor.Tick(Time.unscaledTime);

        bool enterDown = NativeInput.IsKeyDown(KeyCode.Return)
            || NativeInput.IsKeyDown(KeyCode.KeypadEnter);
        bool escapeDown = NativeInput.IsKeyDown(KeyCode.Escape);
        bool gameHasInputFocus = DialogueUiFocus.AllowsKeyboard(NativeInput.IsGameForeground(),Application.isFocused,
            IsOverlayActive(),_historyWindow.IsOpen || _sharedWindow.IsOpen || _inputField.IsOpen);
        bool enter = ReadEnterKey(enterDown, gameHasInputFocus);
        bool escape = escapeDown && !_escapeDown && gameHasInputFocus;
        _escapeDown = escapeDown;
        if (_sharedWindow.OwnsKeyboard)
        {
            if (escape && Input.compositionString.Length == 0) CloseSharedActivity();
            return;
        }
        if (_historyWindow.IsOpen)
        {
            _historyWindow.Tick();
            if (escape && gameHasInputFocus) CloseDialogueHistory();
            return; // History search owns Enter; chat draft and dialogue owner are untouched.
        }
        if (_inputField.UiConsumesKeyboard)
        {
            if (escape && gameHasInputFocus) _inputField.CloseMenu();
            return; // Menu Enter/Esc and the closing click never submit the draft.
        }
        if (enter)
        {
            NativeStateInspector.TrySummonIfHidden("enter");
            NativeWindowPeekDirector.CancelForPlayer();
            NativeEventLog.Trace("input.enter", "game", "player",
                "Enter pressed", NativeInput.DescribeFocus());
        }
        if (escape)
        {
            NativeStateInspector.TrySummonIfHidden("escape");
            NativeWindowPeekDirector.CancelForPlayer();
            NativeEventLog.Trace("input.escape", "game", "player",
                "Escape pressed", NativeInput.DescribeFocus());
        }
        if (_listening)
        {
            _inputField.EnsureFocus();
            _inputField.RefreshLayout();
            bool shiftEnter = enter
                && _config.InputMultiLine
                && (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift));
            if (enter && gameHasInputFocus && Time.unscaledTime >= _ignoreEnterUntil && !shiftEnter)
            {
                if (_inputField.Text.Trim().Length > 0)
                {
                    _inputField.SubmitCurrent();
                }
                else
                {
                    // The authorization belongs to the submitted input attempt even
                    // when the text is empty; consume it and refresh the visible state.
                    ConsumeReviewAuthorization();
                    _inputField.RefreshReviewAuthorization(false);
                    NativeEventLog.Trace("input.empty", "mod", "player",
                        "empty Enter ignored; input stays open; review authorization consumed");
                }
            }
            else if (escape && gameHasInputFocus)
                CancelInput();
        }
        else if (escape && gameHasInputFocus && TryCancelPendingDelivery("escape"))
        {
            // The shared transition owns the input-independent state change; it
            // covers a pending request, a completed reply waiting for its
            // playback window, and a delivery that is currently displayed.
        }
        else if (enter)
        {
            if (gameHasInputFocus && CanOpenReplyWindow())
            {
                OpenInput();
            }
            else
            {
                NativeEventLog.Trace("input.blocked", "mod", "player",
                    "Enter pressed but input was not opened",
                    $"foreground={NativeInput.IsGameForeground()};unityFocus={Application.isFocused};overlay={IsOverlayActive()};" +
                    $"replyWindowLeft={Math.Max(0f, _replyWindowUntil - Time.unscaledTime):0.0};" +
                    $"story={HasActiveNativeStory()};pending={_requestPending};queued={_queuedReply != null}");
            }
        }

        if (_queuedReply != null)
        {
            if (!CanShowReplyNow())
            {
                TracePlaybackDeferred();
            }
            else
            {
                ActionDecision decision = ActionDirector.Decide(_queuedReply, _sessionState, _config);
                if (decision.Defer)
                {
                    TracePlaybackDeferred();
                    return;
                }
                if (!decision.Allow)
                {
                    NativeEventLog.Trace("action.rejected", "mod", "lilith",
                        _queuedReply.Action, decision.Reason);
                    CancelQueuedReply("action_rejected", "suppressed");
                    return;
                }
                // The UI-independent tail of this state transfer is shared with
                // the offline harness (Delivery.cs); the native playback gate
                // above stays here in Update.
                TryBeginQueuedReplyDisplay();
            }
        }
        TickDesktopArrange();
        FlushPendingManualClick();
    }

    internal void OnNativeOptionClick(string text, int index)
    {
        bool preservePose = NativeHooks.IsSnackMenuPoseHoldActive;
        if (!preservePose)
        {
            NativeStateInspector.TrySummonIfHidden("option");
            NativePoseGate.WakeForInput();
            NativeWindowPeekDirector.CancelForPlayer();
        }
        else
        {
            NativeHooks.ClearSnackMenuPoseHold();
        }
        string value = string.IsNullOrWhiteSpace(text) ? "option-" + index : text;
        _memory.AddNative("player", value, "player-option");
        _memory.AddEvent("player.option", "selected native option: " + value, "index=" + index);
        if (_snackSessionActive)
        {
            _memory.AddMemory(
                string.IsNullOrWhiteSpace(value)
                    ? "玩家选择了点心时间选项"
                    : "玩家选择了点心时间选项：" + value);
            if (value.IndexOf("结束", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("点心", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("snack", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                _snackSessionActive = false;
            }
        }
        _sessionState.AddSceneEvent(new LilithSceneEvent
        {
            Kind = SceneEventKind.OptionSelected,
            Actor = "player",
            Details = value + " (index=" + index + ")"
        });
    }

    internal void OnNativeOptionsShown(string joinedOptions)
    {
        if (!NativeHooks.IsSnackMenuPoseHoldActive)
        {
            NativeStateInspector.TrySummonIfHidden("options_shown");
            NativePoseGate.WakeForInput();
            NativeWindowPeekDirector.CancelForPlayer();
        }
        if (string.IsNullOrWhiteSpace(joinedOptions))
            return;
        _memory.AddEvent("player.options_shown",
            "native player options shown",
            joinedOptions);
        _sessionState.AddSceneEvent(new LilithSceneEvent
        {
            Kind = SceneEventKind.OptionShown,
            Actor = "game",
            Details = joinedOptions
        });
    }

    internal void OnNativeMenuAction(string kind, string text, string details = "")
    {
        // Opening a menu is an event, not evidence that snack-time began.
        _memory.AddEvent("native.menu." + kind, text, details, "player");
        _sessionState.AddSceneEvent(new LilithSceneEvent
        {
            Kind = SceneEventKind.StateChanged,
            Actor = "game",
            Details = "native_menu=" + kind + ";" + details
        });
        NativeEventLog.Trace("native.menu." + kind, "game", "player", text, details);
    }

    internal void OnNativeMemoWritten(string kind, string text)
    {
        _memory.AddEvent("native_memo." + kind, "memo written", text, "lilith");
        _sessionState.AddSceneEvent(new LilithSceneEvent
        {
            Kind = SceneEventKind.StateChanged,
            Actor = "lilith",
            Details = "memo_written=" + kind + ";" + text
        });
    }

    internal void OnNativeHitArea(CharacterHitAreaType hitArea, Vector3 clickWorldPoint)
    {
        NativeStateInspector.TrySummonIfHidden("hit_area");
        NativeWindowPeekDirector.CancelForPlayer();
        if (hitArea == CharacterHitAreaType.Unknown)
            return;
        _lastNativeHitArea = hitArea;
        _lastNativeHitAreaAt = Time.unscaledTime;
        _pendingManualClick = null;
        _pendingManualClickAt = float.NegativeInfinity;
        _memory.AddEvent("player.native_hit_area",
            "touched Lilith " + hitArea,
            string.Format("world={0:0},{1:0};native=true",
                clickWorldPoint.x, clickWorldPoint.y));
        _memory.AddInteractionMemory(
            "玩家触碰了莉莉丝的",
            hitArea.ToString(),
            "native_hit_area");
        _sessionState.AddSceneEvent(new LilithSceneEvent
        {
            Kind = SceneEventKind.NativeHitArea,
            Actor = "player",
            Area = hitArea.ToString(),
            Position = clickWorldPoint,
            Details = "native=true"
        });
    }

    internal void OnNativeDoubleClick()
    {
        NativeWindowPeekDirector.CancelForPlayer();
        _lastNativeDoubleClickAt = Time.unscaledTime;
        _pendingManualClick = null;
        _pendingManualClickAt = float.NegativeInfinity;
        _memory.AddEvent("player.native_double_click", "double-clicked Lilith", "native=true", "player");
        _sessionState.AddSceneEvent(new LilithSceneEvent
        {
            Kind = SceneEventKind.DoubleClick,
            Actor = "player",
            Details = "native=true"
        });
    }

    internal void OnNativeDragStart()
    {
        NativeStateInspector.TrySummonIfHidden("native_drag_start");
        NativePoseGate.WakeForInput();
        NativeWindowPeekDirector.CancelForPlayer();
        _memory.AddEvent("character.native_drag_start", "native drag started", "", "lilith");
    }

    internal void OnNativeDragEnd()
    {
        NativeStateInspector.TrySummonIfHidden("native_drag_end");
        NativePoseGate.WakeForInput();
        NativeWindowPeekDirector.CancelForPlayer();
        _memory.AddEvent("character.native_drag_end", "native drag ended", "", "lilith");
    }

    internal void OnNativeHoverEnter()
    {
        NativeEventLog.Trace("input.native_hover", "game", "player",
            "native hover entered Lilith");
    }

    internal void OnNativeHoverExit()
    {
        NativeEventLog.Trace("input.native_hover", "game", "player",
            "native hover left Lilith");
    }

    private void TryBecomeReady()
    {
        try
        {
            if (DialogueManager.instance == null
                || CharacterController.instance == null
                || UnityEngine.Object.FindObjectOfType<PlayerLineController>(true) == null)
                return;
            _systemReady = true;
            _systemReadyAt = Time.unscaledTime;
            if (_config.ActionInventoryEnabled && !_actionInventoryGenerated)
            {
                _actionInventoryGenerated = true;
                NativeActionInventory.TryWriteReport(_config, _sessionState);
            }
            if (_config.RawAnimationProbeEnabled && !_rawAnimationProbeGenerated)
            {
                _rawAnimationProbeGenerated = true;
                RawAnimationProbe.TryWriteReport(_config);
            }
            if (_config.ActionPoseClothingAuditEnabled && !_actionPoseClothingAuditGenerated)
            {
                _actionPoseClothingAuditGenerated = true;
                ActionPoseClothingResolverAudit.TryWriteReport(_config);
            }
            _memory.ApplyPersonaState(_sessionState);
            _nextPersonaSave = Time.unscaledTime + 8f;
            _desktopArrangeInspectPending = _config.DesktopArrangeTestInspect;
            if (_config.ReplyWindowSeconds <= 0)
                _replyWindowUntil = float.PositiveInfinity;
            else
                _replyWindowUntil = Time.unscaledTime + _config.ReplyWindowSeconds;
            NativeEventLog.Trace("bridge.ready", "mod", "system",
                "native dialogue, character and player-line systems are ready");
            NativeEventLog.Trace("input.available", "mod", "player",
                "Enter can open a short reply while Lilith is idle",
                "reply_window=" + (_config.ReplyWindowSeconds <= 0
                    ? "always"
                    : _config.ReplyWindowSeconds.ToString()));
            NativeInteractionSubscriber.Subscribe();
            _nextMemoAnnotationAt = Time.unscaledTime + _config.MemoAnnotationIntervalSeconds;
            // The interval is the cooldown between reflections, not a startup
            // delay: a short session may reflect as soon as it has real material.
            _nextReflectionAt = 0f;
            _memory.InitializeReflectionWindow(_reflectionWindow, _config.ReflectionEnabled);
            // Like reflection: the interval is a cooldown between passes, not a
            // startup delay. The first pass runs once the window has material.
            _nextDreamAt = 0f;
            NativeEventLog.Trace("bridge.config", "mod", "system",
                "runtime meta flags",
                RuntimeDirector.DescribeRuntime(_config));
            try
            {
                string memoSnapshot = NativeMemoContext.Build();
                NativeEventLog.Trace("native_memo.snapshot", "mod", "system",
                    memoSnapshot.Length > 0 ? memoSnapshot : "no memo data",
                    "read_only");
            }
            catch (Exception error)
            {
                NativeEventLog.Trace("native_memo.snapshot_error", "mod", "system",
                    error.Message);
            }
            try
            {
                string snackSnapshot = NativeSnackStatus.Build();
                NativeEventLog.Trace("native.snack.snapshot", "mod", "system",
                    snackSnapshot,
                    "read_only");
            }
            catch (Exception error)
            {
                NativeEventLog.Trace("native.snack.snapshot_error", "mod", "system",
                    error.Message);
            }
            if (_config.DebugProactive && !_debugProactiveTriggered)
            {
                _debugProactiveTriggered = true;
                _pendingProactiveReason = "程序测试：检测到玩家正在使用电脑。";
                NativeEventLog.Trace("meta.proactive.debug", "mod", "system",
                    "debug proactive trigger armed");
            }
        }
        catch
        {
            // Wait for the scene to finish loading.
        }
    }

    private void TrackNativeDialogue()
    {
        try
        {
            DialogueManager? manager = DialogueManager.instance;
            DialogueNode? node = manager?.CurrentNode;
            bool active = manager != null
                && (manager.IsDialogueActive || manager.IsBusyOrAwaitingResponse || SafeOptionsShowing());
            if (active && !_aiSpeechActive && !NativeBubblePresenter.HasPendingReply
                && node is { id: > 0 } && !_nativeLinkage.BlockOpen)
            {
                CancelNativeLinkage("new_native_block");
                _nativeLinkage.Begin();
                _memory.BeginCurrentNativeBlock();
            }

            if (active && !_aiSpeechActive && !NativeBubblePresenter.HasPendingReply
                && node != null && node.id > 0 && _nativeLinkage.ObserveNode(node.id))
            {
                _lastNativeNodeId = node.id;
                string role = ResolveNativeSpeaker(node);
                string action = node.actionType is not (LilithActionType.None or LilithActionType.Idle)
                    ? node.actionType.ToString()
                    : "";
                _memory.AddNative(role, node.text, action);
                string eventId = Guid.NewGuid().ToString("N");
                _memory.AddSpokenLine(role, node.text, eventId);
                _memory.RegisterCurrentNativeSource(eventId,
                    !role.Equals("lilith", StringComparison.OrdinalIgnoreCase)
                        && !role.Equals("player", StringComparison.OrdinalIgnoreCase));
                _nativeLinkage.AddSource(eventId);
                if (role.Equals("lilith", StringComparison.OrdinalIgnoreCase))
                {
                    _memory.AddInteractionMemory("莉莉丝当时说：" + node.text, "", "native_line");
                }
                if (_snackSessionActive || IsSnackDialogueNode(node))
                {
                    _snackSessionActive = true;
                    _memory.AddMemory(node.text, "点心时间台词", role);
                }
                NativeEventLog.Trace("native.dialogue_start", "game", role,
                    "native dialogue started",
                    "id=" + node.id + ";action=" + action);
                _sessionState.AddSceneEvent(new LilithSceneEvent
                {
                    EventId = eventId,
                    Kind = SceneEventKind.DialogueStart,
                    Actor = role,
                    Details = node.text
                });
            }

            if (_nativeWasActive && !active)
            {
                _nativeWasActive = false;
                _lastNativeNodeId = -1;
                _lastNativeDialogueEndAt = Time.unscaledTime;
                OnNativeDialogueEnd();
                _nativeLinkage.End(Time.unscaledTime, DateTimeOffset.UtcNow, _config.ReactToNativeDialogue && _config.ProactiveEnabled);
                _sessionState.AddSceneEvent(new LilithSceneEvent
                {
                    Kind = SceneEventKind.DialogueEnd,
                    Actor = "game"
                });
            }
            else if (active)
            {
                _nativeWasActive = true;
            }
            _sessionState.NativeDialogueActive = active;
            _sessionState.PlayerOptionsShowing = SafeOptionsShowing();
            _sessionState.BusyOrAwaitingResponse = manager?.IsBusyOrAwaitingResponse == true;
        }
        catch
        {
            // Native dialogue tracking is best effort.
        }
    }

    private void OnNativeDialogueEnd()
    {
        bool wasAiSpeech = _aiSpeechActive;
        NativeWindowPeekDirector.SuppressFor(60f);
        NativeEventLog.Trace("window_peek.protect", "mod", "system",
            "state-transition peek cooldown started",
            "seconds=60");
        _replyWindowUntil = _config.ReplyWindowSeconds <= 0
            ? float.PositiveInfinity
            : Time.unscaledTime + _config.ReplyWindowSeconds;
        bool moreChunks = NativeBubblePresenter.OnChunkFinished();
        if (moreChunks)
        {
            _aiSpeechActive = true;
            NativeEventLog.Trace("ai.speech_continue", "mod", "lilith",
                "AI bubble continued with next segment");
        }
        else if (_aiSpeechActive)
        {
            _aiSpeechActive = false;
            _lastAiSpeechEndAt = Time.unscaledTime;
            NativeEventLog.Trace("ai.speech_end", "mod", "lilith", "AI bubble finished");
        }
        NativeEventLog.Trace("dialogue.end", "game", "lilith",
            "native dialogue block ended; Enter may open a short reply",
            "window=" + _config.ReplyWindowSeconds
            + ";source=" + (wasAiSpeech ? "ai_bubble" : "native"));
    }

    private void TrackCharacterState()
    {
        if (Time.unscaledTime < _nextStateCheck)
            return;
        _nextStateCheck = Time.unscaledTime + 0.12f;

        try
        {
            var character = CharacterController.instance;
            var state = UnityEngine.Object.FindObjectOfType<LilithStateManager>(true);
            bool drag = state?.IsDrag == true || character?.IsDrag == true;
            bool interact = state?.IsInteracting == true || character?.IsInteracting == true;
            if (drag && !_lastDrag && !_leftDragging)
            {
                _memory.AddEvent("character.drag", "Lilith was dragged by the player", "", "lilith");
                _sessionState.AddSceneEvent(new LilithSceneEvent
                {
                    Kind = SceneEventKind.DragStart,
                    Actor = "lilith"
                });
            }
            if (!drag && _lastDrag)
            {
                _memory.AddEvent("character.drag_end", "the player released Lilith", "", "lilith");
                _sessionState.AddSceneEvent(new LilithSceneEvent
                {
                    Kind = SceneEventKind.DragEnd,
                    Actor = "lilith"
                });
            }
            _lastDrag = drag;

            if (interact && !_lastInteract)
            {
                _memory.AddEvent("character.interact", "the player is touching/interacting with Lilith", "", "lilith");
                _sessionState.AddSceneEvent(new LilithSceneEvent
                {
                    Kind = SceneEventKind.Touch,
                    Actor = "player",
                    Details = "interact_start"
                });
            }
            if (!interact && _lastInteract)
                _memory.AddEvent("character.interact_end", "the player stopped touching Lilith", "", "lilith");
            _lastInteract = interact;

            LilithActionType action = state?.ActionType ?? character?.ActionType ?? LilithActionType.None;
            if (action != _lastAction && IsInterestingAction(action))
            {
                _lastAction = action;
                _memory.AddEvent("character.action", "Lilith action: " + DescribeAction(action), "", "lilith");
                _sessionState.AddSceneEvent(new LilithSceneEvent
                {
                    Kind = SceneEventKind.CharacterAction,
                    Actor = "lilith",
                    Area = action.ToString(),
                    Details = DescribeAction(action)
                });
                ActionExecutor.RecordNativeAction(action);
            }
            else if (action != _lastAction)
            {
                _lastAction = action;
            }

            // An unavailable manager is not an observed false body state.
            // Keep the last readable baseline and snapshot until sampling resumes.
            if (state != null)
            {
                bool sleep = state.IsSleep;
                bool sit = state.IsSit;
                bool lie = state.IsLieDown;
                if (sleep != _lastSleep)
                {
                    _lastSleep = sleep;
                    _memory.AddEvent("character.state", sleep ? "Lilith fell asleep" : "Lilith woke up", "", "lilith");
                    _sessionState.AddSceneEvent(new LilithSceneEvent
                    {
                        Kind = SceneEventKind.NativeStateChanged,
                        Actor = "lilith",
                        Details = sleep ? "sleep" : "awake"
                    });
                }
                if (sit != _lastSit)
                {
                    _lastSit = sit;
                    _memory.AddEvent("character.state", sit ? "Lilith sat down" : "Lilith stood up", "", "lilith");
                    _sessionState.AddSceneEvent(new LilithSceneEvent
                    {
                        Kind = SceneEventKind.NativeStateChanged,
                        Actor = "lilith",
                        Details = sit ? "sit" : "stand"
                    });
                }
                if (lie != _lastLie)
                {
                    _lastLie = lie;
                    _memory.AddEvent("character.state", lie ? "Lilith lay down" : "Lilith got up", "", "lilith");
                    _sessionState.AddSceneEvent(new LilithSceneEvent
                    {
                        Kind = SceneEventKind.NativeStateChanged,
                        Actor = "lilith",
                        Details = lie ? "lie" : "getup"
                    });
                }

                _sessionState.Native.IsSleep = sleep;
                _sessionState.Native.IsLieDown = lie;
                _sessionState.Native.IsSit = sit;
                _sessionState.Native.IsSofaSit = state.IsSofaSit;
                _sessionState.Native.IsSitFrozen = state.IsSitFrozen;
            }
            _sessionState.Native.IsInteracting = interact;
            _sessionState.Native.IsDragging = drag;
            _sessionState.Native.IsSpeaking = character?.IsDialogueActive == true;
            _sessionState.Native.SecondsSinceDragEnd = state?.SecondsSinceDragEnd ?? 0f;
            _sessionState.Native.CharacterHeightWorld = state?.CharacterHeightWorld ?? 0f;
            _sessionState.Native.BubbleHeadDropWorldY = state?.BubbleHeadDropWorldY ?? 0f;
            _sessionState.Native.ActionType = action;
            _sessionState.Belief.ObserveNative(_sessionState.Native.Describe());
        }
        catch
        {
            // Character state tracking is best effort.
        }
    }

    private void RefreshTimeAndReturnState()
    {
        _sessionState.Now = DateTimeOffset.Now;
        _sessionState.UpdateTimePhase(DateTime.Now);
        if (_lastReturnAt > 0f && Time.unscaledTime - _lastReturnAt > 25f)
        {
            _sessionState.PlayerRecentlyReturned = false;
            _lastReturnAt = 0f;
        }
    }

    private void UpdateVitalState()
    {
        RefreshTimeAndReturnState();
        // Player words remain dialogue evidence. A keyword or mere non-empty
        // utterance does not establish warmth, complaint intent, or a mood.
        // This path owns only these exact derived mood/reason pairs and the
        // neutral initial state. Caring and other owners cannot be overwritten
        // by either a new short-lived candidate or its expiry.
        bool ownsDerivedMood = (_sessionState.Mood, _sessionState.MoodReason) is
            (MoodKind.Calm, "")
            or (MoodKind.Shy, "player_touched_recently")
            or (MoodKind.Happy, "player_returned")
            or (MoodKind.Tired, "late_night");
        if (ownsDerivedMood)
        {
            _sessionState.Mood = MoodKind.Calm;
            _sessionState.MoodReason = "";
            if (_sessionState.RecentEvents.Count > 0)
            {
                LilithSceneEvent last = _sessionState.RecentEvents[^1];
                if (last.Kind is SceneEventKind.Touch or SceneEventKind.NativeHitArea
                    && DateTimeOffset.UtcNow - last.Timestamp < TimeSpan.FromSeconds(90))
                {
                    _sessionState.Mood = MoodKind.Shy;
                    _sessionState.MoodReason = "player_touched_recently";
                }
                else if (_sessionState.PlayerRecentlyReturned)
                {
                    _sessionState.Mood = MoodKind.Happy;
                    _sessionState.MoodReason = "player_returned";
                }
                else if (_sessionState.TimePhase == TimePhase.LateNight)
                {
                    _sessionState.Mood = MoodKind.Tired;
                    _sessionState.MoodReason = "late_night";
                }
            }
        }

        if (_listening)
            _sessionState.Attention = AttentionTarget.Player;
        else if (_requestPending)
            _sessionState.Attention = AttentionTarget.Screen;
        else if (_sessionState.NativeDialogueActive)
            _sessionState.Attention = AttentionTarget.Native;
        else if (_sessionState.ForegroundApp != null
            && !_sessionState.ForegroundApp.Category.Equals("desktop", StringComparison.OrdinalIgnoreCase)
            && !_sessionState.ForegroundApp.Category.Equals("unknown", StringComparison.OrdinalIgnoreCase))
            _sessionState.Attention = AttentionTarget.Screen;
        else if (_sessionState.PlayerRecentlyReturned)
            _sessionState.Attention = AttentionTarget.Player;
        else
            _sessionState.Attention = _sessionState.TimePhase == TimePhase.LateNight
                ? AttentionTarget.Self
                : AttentionTarget.Idle;
        _sessionState.MomentListening = _listening;
        _sessionState.MomentAboutToSpeak = _queuedReply != null;
        _sessionState.MomentWaitingForPlayer = _sessionState.NativeDialogueActive
            || _sessionState.PlayerOptionsShowing
            || _sessionState.BusyOrAwaitingResponse;
        _sessionState.Emotions.Tick(
            DateTimeOffset.UtcNow,
            _sessionState.TimePhase,
            _sessionState.Native.IsSleep);
    }

    private void MaybeSavePersonaState()
    {
        if (!_systemReady || Time.unscaledTime < _nextPersonaSave)
            return;
        _nextPersonaSave = Time.unscaledTime + 30f;
        _memory.SavePersonaState(_sessionState);
    }

    private void MaybeSaveLastSceneSummary()
    {
        if (!_systemReady || Time.unscaledTime < _nextSceneSummarySave)
            return;
        _nextSceneSummarySave = Time.unscaledTime + 30f;
        LastSceneSummary? summary = _sessionState.CreateLastSceneSummary();
        if (summary != null)
            _memory.SaveLastSceneSummary(summary);
    }

    private static bool HasAny(string text, params string[] terms)
    {
        foreach (string term in terms)
        {
            if (text.Contains(term, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private void TrackForegroundApp()
    {
        if (!_config.AppAwarenessEnabled || Time.unscaledTime < _nextAppCheck)
            return;
        _nextAppCheck = Time.unscaledTime + 2f;
        ForegroundAppInfo? app = AppAwareness.GetCurrent();
        if (app == null)
            return;
        if (_lastApp != null && _lastApp.SameAs(app))
            return;

        _lastApp = app;
        _sessionState.ForegroundApp = app;
        _sessionState.Belief.ObserveForeground(app);
        try
        {
            AppAwareRemarkGate.TrackForeground(app.ProcessName);
        }
        catch
        {
            // Native remark tracking is optional.
        }
        _memory.AddEvent("meta.app_foreground", "foreground app changed", app.DescribeRedacted(), "system");
        _sessionState.AddSceneEvent(new LilithSceneEvent
        {
            Kind = SceneEventKind.AppForeground,
            Actor = "system",
            Details = app.DescribeRedacted()
        });
        NativeStateInspector.LogSnapshot("app_changed");
        return;
    }

    private void TryScheduleMemoAnnotation()
    {
        if (SharedActivityRunning || SharedActivityUiBusy) return;
        if (!_config.MemoAnnotationEnabled || !_config.MemoWriteEnabled)
            return;
        if (_annotationInProgress || _listening || _requestPending || _queuedReply != null)
            return;
        if (Time.unscaledTime < _nextMemoAnnotationAt)
            return;
        if (!_systemReady
            || NativeStateInspector.IsNativeQuietForMod()
            || NativeStateInspector.TimeSinceLastHardwareInputSeconds < 30d
            || HasActiveNativeStory()
            || ActionDirector.NativeBusy(_sessionState))
        {
            return;
        }

        _annotationInProgress = true;
        NativeEventLog.Trace("memo.annotation.schedule", "mod", "system",
            "idle annotation request scheduled", "interval_s=" + _config.MemoAnnotationIntervalSeconds);
        string recallQuery = _sessionState.LastUserText;
        IReadOnlyList<MemoryTurn>? recallUsed = null;
        using var mentionAnnotationScope = _memory.BeginMentionRequest("", false);
        string context = ContextBuilder.Build(
            _memory,
            _sessionState,
            _config,
            list => recallUsed = list);
        context += Environment.NewLine
            + "[annotation_mode] You are Lilith adding one short, natural annotation to one unannotated "
            + "notebook task. Do not rewrite the original note, do not create a new note, do not display "
            + "anything in a bubble. Output only an invisible <annotation>one or two sentences</annotation>.";
        BridgeConfig requestConfig = _config;
        Task.Run(async () =>
        {
            ApiResult result = await _client.SendAsync(
                requestConfig,
                "[idle memo annotation]",
                context,
                null,
                System.Threading.CancellationToken.None, requestPurpose: "memo_annotation",
                // This path passes only the composed context string, no history
                // window; the mode is explicitly none rather than an implicit guess.
                historyOwner: null).ConfigureAwait(false);
            if (recallUsed != null
                && recallUsed.Count > 0
                && result.Error.Length == 0)
            {
                _memory.RecordRecall(recallQuery, recallUsed, "memo_annotation");
            }
            _mainThread.Enqueue(() => OnMemoAnnotationResult(result));
        });
    }

    [HideFromIl2Cpp]
    private void OnMemoAnnotationResult(ApiResult result)
    {
        _annotationInProgress = false;
        _nextMemoAnnotationAt = Time.unscaledTime + _config.MemoAnnotationIntervalSeconds;
        if (result.Error.Length > 0)
        {
            NativeEventLog.Trace("memo.annotation_error", "mod", "system", result.Error);
            return;
        }
        NativeMemoWriter.Apply(result, _config);
        NativeEventLog.Trace("memo.annotation.done", "mod", "system",
            "idle annotation finished",
            "annotations=" + result.MemoAnnotations.Count
            + ";prompt_tokens=" + result.PromptTokens
            + ";completion_tokens=" + result.CompletionTokens
            + ";cache_hit=" + result.PromptCacheHitTokens
            + ";cache_miss=" + result.PromptCacheMissTokens);
    }

    private void TryScheduleNoteSupplement()
    {
        if (SharedActivityRunning || SharedActivityUiBusy) return;
        if (!_config.NoteSupplementEnabled)
        {
            _lastNoteTestTrigger = false;
            return;
        }

        if (_config.NoteSupplementTestTrigger && !_lastNoteTestTrigger)
        {
            _lastNoteTestTrigger = true;
            TryGenerateNoteSupplement(true);
            return;
        }
        _lastNoteTestTrigger = _config.NoteSupplementTestTrigger;
    }

    internal void OnNativeWriteNoteFailure()
    {
        if (_config.NoteSupplementEnabled)
            TryGenerateNoteSupplement(false);
    }

    private void TryGenerateNoteSupplement(bool manual)
    {
        if (_noteSupplementInProgress
            || !_systemReady
            || (!manual && NativeStateInspector.IsNativeQuietForMod())
            || (!manual && ActionDirector.NativeBusy(_sessionState))
            || (!manual && NativeStateInspector.TimeSinceLastHardwareInputSeconds
                < _config.NoteSupplementMinIdleSeconds)
            || (!manual && Time.unscaledTime < _nextNoteSupplementAt)
            || _listening
            || _requestPending
            || _queuedReply != null)
        {
            return;
        }

        _noteSupplementInProgress = true;
        NativeEventLog.Trace("native_note.schedule", "mod", "system",
            "AI supplemental note requested",
            "manual=" + manual);

        string recallQuery = _sessionState.LastUserText;
        IReadOnlyList<MemoryTurn>? recallUsed = null;
        BridgeConfig requestConfig = _config;
        var request = BuildNoteSupplementRequest(requestConfig, list => recallUsed = list);
        Task.Run(async () =>
        {
            ApiResult result = await _client.SendAsync(
                requestConfig,
                "[Lilith note supplement]",
                request.Context,
                null,
                System.Threading.CancellationToken.None, request.History, requestPurpose: "note_supplement",
                requireVisibleText: false, historyOwner: _memory).ConfigureAwait(false);
            if (recallUsed != null
                && recallUsed.Count > 0
                && result.Error.Length == 0)
            {
                _memory.RecordRecall(recallQuery, recallUsed, "note_supplement");
            }
            _mainThread.Enqueue(() => OnNoteSupplementResult(result));
        });
    }

    [HideFromIl2Cpp]
    private void OnNoteSupplementResult(ApiResult result)
    {
        _noteSupplementInProgress = false;
        _nextNoteSupplementAt = Time.unscaledTime + _config.NoteSupplementCooldownSeconds;
        if (result.Error.Length > 0)
        {
            NativeEventLog.Trace("native_note.error", "mod", "system", result.Error);
            return;
        }

        bool saved = TryDeliverReplyPaper(result);
        NativeEventLog.Trace("native_note.done", "mod", "system",
            "AI supplemental note process finished",
            "saved=" + saved
            + ";notes=" + result.NoteSupplement.Count
            + ";prompt_tokens=" + result.PromptTokens
            + ";completion_tokens=" + result.CompletionTokens
            + ";cache_hit=" + result.PromptCacheHitTokens
            + ";cache_miss=" + result.PromptCacheMissTokens);
    }

    private void TrackNativeStateChanges()
    {
        if (Time.unscaledTime < _nextNativeStateCheck)
            return;
        _nextNativeStateCheck = Time.unscaledTime + 10f;

        bool music = NativeStateInspector.IsMusicPlayerRunning;
        if (music != _lastMusicRunning)
        {
            _lastMusicRunning = music;
            _memory.AddEvent("meta.music_player", music ? "external music player running" : "external music player stopped", "",
                "system");
            _sessionState.AddSceneEvent(new LilithSceneEvent
            {
                Kind = SceneEventKind.StateChanged,
                Actor = "system",
                Details = music ? "music_player=running" : "music_player=stopped"
            });
            NativeEventLog.Trace("meta.music_player", "mod", "system",
                music ? "external music detected" : "external music stopped");
        }

        bool rival = NativeStateInspector.IsRivalPetRunning;
        if (rival != _lastRivalPet)
        {
            _lastRivalPet = rival;
            _memory.AddEvent("meta.rival_pet", rival ? "rival desktop pet running" : "rival desktop pet stopped", "",
                "system");
            _sessionState.AddSceneEvent(new LilithSceneEvent
            {
                Kind = SceneEventKind.StateChanged,
                Actor = "system",
                Details = rival ? "rival_pet=running" : "rival_pet=stopped"
            });
            NativeEventLog.Trace("meta.rival_pet", "mod", "system",
                rival ? "rival pet detected" : "rival pet stopped");
        }
    }

    private void MaybeTriggerProactive()
    {
        if (SharedActivityRunning || SharedActivityUiBusy) return;
        ObserveNativeConversation();
        TickNativeLinkage();
        if (!_config.ProactiveEnabled || !_systemReady)
            return;
        if (_systemReadyAt > 0f && Time.unscaledTime - _systemReadyAt < 30f)
            return;
        if (_aiSpeechActive || NativeBubblePresenter.HasPendingReply || _queuedReply != null || HasActiveNativeStoryForProactive())
        {
            if (Time.unscaledTime >= _nextProactiveLog)
            {
                _nextProactiveLog = Time.unscaledTime + 3f;
                NativeEventLog.Trace("meta.proactive.priority_gate", "mod", "system",
                    "proactive suppressed while native dialogue or AI playback is active",
                    "ai_speech=" + _aiSpeechActive
                    + ";queued=" + (_queuedReply != null)
                    + ";native_busy=" + HasActiveNativeStoryForProactive());
            }
            return;
        }
        if (Time.unscaledTime - _lastAiSpeechEndAt < _config.ProactiveAfterReplyCooldownSeconds
            || Time.unscaledTime - _lastNativeDialogueEndAt
                < _config.ProactiveAfterNativeDialogueCooldownSeconds)
        {
            if (Time.unscaledTime >= _nextProactiveLog)
            {
                _nextProactiveLog = Time.unscaledTime + 3f;
                NativeEventLog.Trace("meta.proactive.quiet_gate", "mod", "system",
                    "proactive suppressed during post-conversation quiet window",
                    "reply_seconds_left=" + Math.Max(0f,
                        _config.ProactiveAfterReplyCooldownSeconds
                            - (Time.unscaledTime - _lastAiSpeechEndAt)).ToString("0")
                    + ";native_seconds_left=" + Math.Max(0f,
                        _config.ProactiveAfterNativeDialogueCooldownSeconds
                            - (Time.unscaledTime - _lastNativeDialogueEndAt)).ToString("0"));
            }
            return;
        }
        if (NativeStateInspector.IsNativeQuietForMod())
        {
            if (Time.unscaledTime >= _nextProactiveLog)
            {
                _nextProactiveLog = Time.unscaledTime + 3f;
                NativeEventLog.Trace("meta.proactive.native_gate", "mod", "system",
                    "native quiet state suppressed proactive",
                    NativeStateInspector.Describe());
            }
            return;
        }
        if (_listening || _requestPending || _queuedReply != null || HasActiveNativeStoryForProactive())
        {
            if (_pendingProactiveReason != null && Time.unscaledTime >= _nextProactiveLog)
            {
                _nextProactiveLog = Time.unscaledTime + 3f;
                NativeEventLog.Trace("meta.proactive.deferred", "mod", "system",
                    "proactive message deferred",
                    "reason=" + _pendingProactiveReason
                    + ";listening=" + _listening
                    + ";pending=" + _requestPending
                    + ";queued=" + (_queuedReply != null)
                    + ";busy=" + HasActiveNativeStoryForProactive());
            }
            return;
        }
        if (Time.unscaledTime - _lastProactiveAt < _config.ProactiveCooldownSeconds)
        {
            if (_pendingProactiveReason != null && Time.unscaledTime >= _nextProactiveLog)
            {
                _nextProactiveLog = Time.unscaledTime + 3f;
                NativeEventLog.Trace("meta.proactive.cooldown", "mod", "system",
                    "proactive message in cooldown",
                    "reason=" + _pendingProactiveReason
                    + ";left=" + Math.Max(0f,
                        _config.ProactiveCooldownSeconds - (Time.unscaledTime - _lastProactiveAt))
                        .ToString("0"));
            }
            return;
        }

        if (_nativeLinkage.CanStart(Time.unscaledTime)
            && _memory.CurrentNativeSourcesAvailable(_nativeLinkage.SourceIds))
        {
            _nativeLinkage.Start(Time.unscaledTime);
            _pendingProactiveReason = _nativeLinkage.Reason;
            _pendingProactiveRecallQuery = "";
        }
        RefreshTimeAndReturnState();
        // Select from current state only after the existing wait gates release.
        if (_pendingProactiveReason == null)
        {
            bool appChangeAllowed = false;
            try
            {
                ForegroundAppInfo? foregroundApp = _sessionState.ForegroundApp;
                if (foregroundApp != null
                    && foregroundApp.Category is not ("desktop" or "unknown"))
                {
                    bool remarked = AppAwareRemarkGate.HasRemarkedThisSession(foregroundApp.Category);
                    bool ready = AppAwareRemarkGate.IsGlobalCooldownReady(SystemSensor.LocalTime);
                    appChangeAllowed = !remarked && ready;
                }
            }
            catch
            {
                // App-change remark gate is best effort.
            }
            InitiativeCandidate? candidate = InitiativeSource.PickNext(
                _sessionState,
                _memory.GetLastSceneSummary(),
                _config.ProactiveOnUnfinishedTopic,
                _config.ProactiveOnAfterIntimate,
                _config.ProactiveOnLateNight,
                _config.ProactiveOnReturn,
                _config.ProactiveOnAppChange,
                appChangeAllowed);
            if (candidate != null)
            {
                _pendingProactiveReason = candidate.Reason;
                _pendingProactiveRecallQuery = candidate.RecallQuery;
                NativeEventLog.Trace("meta.proactive.event_candidate", "mod", "system",
                    "event-driven proactive candidate selected",
                    "kind=" + candidate.Kind + ";priority=" + candidate.Priority.ToString("0.00"));
            }
        }
        if (_pendingProactiveReason == null)
            return;
        string reason = _pendingProactiveReason;
        _pendingProactiveReason = null;
        _lastProactiveAt = Time.unscaledTime;
        NativeEventLog.Trace("meta.proactive.send", "mod", "system",
            "proactive message sending", reason);
        SubmitProactive(reason);
    }

    private static bool HasActiveNativeStoryForProactive()
    {
        try
        {
            DialogueManager? manager = DialogueManager.instance;
            if (manager?.IsDialogueActive == true
                || SafeOptionsShowing())
                return true;
        }
        catch
        {
            return true;
        }
        return false;
    }

    private static bool SafeOptionsShowing()
    {
        try
        {
            return PlayerLineController.IsAnyOptionsShowing;
        }
        catch
        {
            return false;
        }
    }

    internal bool IsNativeBusyForActions()
    {
        return HasActiveNativeStoryForProactive()
            || _sessionState.NativeDialogueActive
            || _sessionState.PlayerOptionsShowing
            || _sessionState.BusyOrAwaitingResponse;
    }

    internal bool IsAiBubbleActive => _aiSpeechActive;

    private void TraceInput()
    {
        if (BlocksNativePointerInput)
        {
            _lastLeft=Input.GetMouseButton(0);_lastRight=Input.GetMouseButton(1);_leftDragging=false;
            return;
        }
        try
        {
            var mouse = Input.mousePosition;
            bool left = Input.GetMouseButton(0);
            bool right = Input.GetMouseButton(1);
            float scroll = Input.mouseScrollDelta.y;
            if (left != _lastLeft)
            {
                if (left)
                {
                    NativeWindowPeekDirector.CancelForPlayer();
                    _leftDownAt = Time.unscaledTime;
                    _leftDownPosition = mouse;
                    _leftDragging = false;
                }
                else
                {
                    EndLeftGesture(mouse);
                }
                _lastLeft = left;
            }
            else if (left
                && !_leftDragging
                && Vector2.Distance(mouse, _leftDownPosition) > 8f
                && IsOnLilith(mouse))
            {
                _leftDragging = true;
                _memory.AddEvent("character.drag", "Lilith was dragged by the player",
                    string.Format("start={0:0},{1:0};current={2:0},{3:0}",
                        _leftDownPosition.x, _leftDownPosition.y, mouse.x, mouse.y),
                    "lilith");
            }
            if (right != _lastRight)
            {
                if (right)
                {
                    NativeStateInspector.TrySummonIfHidden("right");
                    NativePoseGate.WakeForInput();
                    NativeWindowPeekDirector.CancelForPlayer();
                    string area = GetCharacterHitArea(mouse);
                    string detail = string.Format("x={0:0};y={1:0}", mouse.x, mouse.y);
                    NativeEventLog.Trace(
                        "system.tray_context_menu",
                        "game",
                        "player",
                        "right-click detected; original game uses this for the system tray context menu",
                        detail + ";area=" + area);
                }
                _lastRight = right;
            }
            if (Math.Abs(scroll - _lastScroll) > 0.01f)
            {
                NativeEventLog.Trace("input.scroll", "game", "player", "scroll",
                    string.Format("delta={0:0.##}", scroll));
                _lastScroll = scroll;
            }

            int touches = Input.touchCount;
            if (touches != _lastTouch)
            {
                for (int i = 0; i < touches; i++)
                {
                    Touch touch = Input.GetTouch(i);
                    NativeEventLog.Trace("input.touch", "game", "player", touch.phase.ToString(),
                        string.Format("finger={0};x={1:0};y={2:0}", touch.fingerId, touch.position.x, touch.position.y));
                    if (touch.phase is TouchPhase.Began or TouchPhase.Stationary or TouchPhase.Moved)
                    {
                        if (touch.phase == TouchPhase.Began)
                        {
                            NativeStateInspector.TrySummonIfHidden("touch");
                            NativePoseGate.WakeForInput();
                            NativeWindowPeekDirector.CancelForPlayer();
                        }
                        string detail = string.Format("finger={0};x={1:0};y={2:0}", touch.fingerId, touch.position.x, touch.position.y);
                        if (IsOnLilith(touch.position))
                        {
                            _memory.AddEvent("player.touch", "touched Lilith", detail);
                            _sessionState.AddSceneEvent(new LilithSceneEvent
                            {
                                Kind = SceneEventKind.Touch,
                                Actor = "player",
                                Position = touch.position,
                                Area = GetCharacterHitArea(touch.position),
                                Details = detail
                            });
                        }
                        else
                            NativeEventLog.Trace("input.touch", "game", "player", touch.phase.ToString(), detail);
                    }
                }
                _lastTouch = touches;
            }
        }
        catch
        {
            // Input trace is best effort.
        }
    }

    private void EndLeftGesture(Vector3 mouse)
    {
        float duration = Time.unscaledTime - _leftDownAt;
        Vector2 position = mouse;
        if (_leftDragging)
        {
            _memory.AddEvent("character.drag_end", "the player released Lilith",
                string.Format("start={0:0},{1:0};end={2:0},{3:0};duration_ms={4:0}",
                    _leftDownPosition.x, _leftDownPosition.y, position.x, position.y,
                    duration * 1000f),
                "lilith");
            _sessionState.AddSceneEvent(new LilithSceneEvent
            {
                Kind = SceneEventKind.DragEnd,
                Actor = "player",
                Position = position,
                Duration = duration,
                Area = GetCharacterHitArea(position)
            });
            _leftDragging = false;
            return;
        }

        if (!IsOnLilith(position))
        {
            NativeEventLog.Trace("input.left", "game", "player", "left-clicked",
                string.Format("x={0:0};y={1:0};area=", position.x, position.y));
            return;
        }

        if (duration >= 0.6f)
        {
            _memory.AddEvent("player.long_press", "long-pressed Lilith",
                string.Format("x={0:0};y={1:0};area={2};duration_ms={3:0}",
                    position.x, position.y, GetCharacterHitArea(position), duration * 1000f));
            _sessionState.AddSceneEvent(new LilithSceneEvent
            {
                Kind = SceneEventKind.LongPress,
                Actor = "player",
                Position = position,
                Duration = duration,
                Area = GetCharacterHitArea(position)
            });
        }
        else
        {
            if (Time.unscaledTime - _lastNativeDoubleClickAt <= NativeEventGraceSeconds)
            {
                NativeEventLog.Trace("interaction.merged", "game", "player",
                    "manual click suppressed after native double-click",
                    string.Format("x={0:0};y={1:0}", position.x, position.y));
            }
            else
            {
                _pendingManualClick = position;
                _pendingManualClickAt = Time.unscaledTime;
            }
        }
    }

    private void RecordCharacterTouch(string kind, Vector3 mouse)
    {
        if (RecentNativeHitArea()
            || Time.unscaledTime - _lastNativeDoubleClickAt <= NativeEventGraceSeconds)
        {
            NativeEventLog.Trace("interaction.merged", "game", "player",
                "manual touch fallback suppressed by a native hit/double-click event",
                string.Format("x={0:0};y={1:0}", mouse.x, mouse.y));
            return;
        }
        string area = GetCharacterHitArea(mouse);
        string detail = string.Format("x={0:0};y={1:0};area={2}", mouse.x, mouse.y, area);
        if (IsOnLilith(mouse))
        {
            _memory.AddEvent("player." + kind, "touched Lilith", detail);
            if (!RecentNativeHitArea())
            {
                _memory.AddInteractionMemory(
                    "玩家触碰了莉莉丝的",
                    area,
                    "manual_touch");
            }
            _sessionState.AddSceneEvent(new LilithSceneEvent
            {
                Kind = SceneEventKind.Touch,
                Actor = "player",
                Position = mouse,
                Area = area
            });
        }
        else
            NativeEventLog.Trace("input.left", "game", "player", "left-clicked", detail);
    }

    private void FlushPendingManualClick()
    {
        if (_pendingManualClick == null)
            return;
        if (Time.unscaledTime - _pendingManualClickAt < ManualClickFallbackSeconds)
            return;
        Vector2 position = _pendingManualClick.Value;
        _pendingManualClick = null;
        _pendingManualClickAt = float.NegativeInfinity;
        RecordCharacterTouch("single_click", position);
    }

    private static bool IsOnLilith(Vector2 position)
    {
        try
        {
            return TransparentWindowNew.IsPointOnCharacter(position);
        }
        catch
        {
            return false;
        }
    }

    private bool RecentNativeHitArea()
    {
        return _lastNativeHitArea != CharacterHitAreaType.Unknown
            && Time.unscaledTime - _lastNativeHitAreaAt <= 1f;
    }

    private static string GetCharacterHitArea(Vector2 position)
    {
        try
        {
            if (BridgeController.Instance != null
                && UnityEngine.Time.unscaledTime - BridgeController.Instance._lastNativeHitAreaAt <= 1f
                && BridgeController.Instance._lastNativeHitArea != CharacterHitAreaType.Unknown)
            {
                return BridgeController.Instance._lastNativeHitArea.ToString();
            }
            var interaction = UnityEngine.Object.FindObjectOfType<CharacterInteractionHandler>(true);
            if (interaction == null)
                return IsOnLilith(position) ? "character" : "";
            var overlapMethod = interaction.GetType().GetMethod(
                "OverlapMouseColliders",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            object? colliders = overlapMethod?.Invoke(interaction, null);
            if (colliders == null || GetColliderCount(colliders) == 0)
                return IsOnLilith(position) ? "character" : "";
            var method = interaction.GetType().GetMethod(
                "GetHitArea",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            object? area = method?.Invoke(interaction, new object[] { colliders });
            return area?.ToString() ?? "character";
        }
        catch
        {
            return IsOnLilith(position) ? "character" : "";
        }
    }

    private static int GetColliderCount(object colliders)
    {
        try
        {
            object? count = colliders.GetType().GetProperty("Length")?.GetValue(colliders);
            if (count != null)
                return Convert.ToInt32(count);
        }
        catch
        {
            // Fall through.
        }
        try
        {
            object? count = colliders.GetType().GetMethod("get_Length")?.Invoke(colliders, null);
            return count == null ? 0 : Convert.ToInt32(count);
        }
        catch
        {
            return 0;
        }
    }

    private bool CanShowReplyNow()
    {
        return _queuedReply != null
            && !_listening
            && !SharedActivityUiBusy
            && !_requestPending
            && !_storyRecallInProgress
            && !_aiSpeechActive
            && !NativeBubblePresenter.HasPendingReply
            && !HasActiveNativeStoryForProactive()
            && !NativeStateInspector.IsSleeping
            && !ActionDirector.NativeBusy(_sessionState);
    }

    [HideFromIl2Cpp]
    internal bool CanResumeAiDelivery() => !_listening && !SharedActivityUiBusy && !_requestPending && !_storyRecallInProgress
        && !HasActiveNativeStoryForProactive() && !NativeStateInspector.IsSleeping
        && !ActionDirector.NativeBusy(_sessionState);

    private void TracePlaybackDeferred()
    {
        if (_lastPlaybackDeferLogAt != float.NegativeInfinity
            && Time.unscaledTime - _lastPlaybackDeferLogAt < PlaybackDeferLogIntervalSeconds)
            return;
        _lastPlaybackDeferLogAt = Time.unscaledTime;
        string reason =
            _aiSpeechActive ? "ai_speech_active"
            : _sessionState.NativeDialogueActive ? "native_dialogue_active"
            : _sessionState.PlayerOptionsShowing ? "native_options_showing"
            : _requestPending ? "request_pending"
            : _storyRecallInProgress ? "story_recall_in_progress"
            : _listening ? "player_listening"
            : NativeStateInspector.IsSleeping ? "sleeping"
            : HasActiveNativeStoryForProactive() ? "native_busy"
            : "waiting";
        NativeEventLog.Trace("playback.deferred", "mod", "lilith",
            "AI reply held; native dialogue and current playback take priority",
            "reason=" + reason);
    }

    private static bool HasActiveNativeStory()
    {
        try
        {
            DialogueManager? manager = DialogueManager.instance;
            if (manager != null
                && (manager.IsDialogueActive
                    || manager.IsBusy
                    || manager.IsBusyOrAwaitingResponse))
                return true;
            if (SafeOptionsShowing())
                return true;
            if (CharacterController.instance?.IsDialogueActive == true)
                return true;
        }
        catch
        {
            // If the native state cannot be read, do not force a reply.
            return true;
        }
        return false;
    }

    private static bool IsOverlayActive()
    {
        try
        {
            return TransparentWindowNew.IsKeyboardInputActive
                || TransparentWindowNew.IsCursorOverCharacter;
        }
        catch
        {
            return false;
        }
    }

    internal bool BlocksNativePointerInput => NativeUiInputGate.PointerClaimed || _historyWindow.IsOpen || _sharedWindow.BlocksPointer(Input.mousePosition)
        || _inputField.IsMenuOpen || _inputField.ContainsPointer(Input.mousePosition);



    private void AuthorizeReviewTurn()
    {
        if (ReviewContextModes.Normalize(_config.ReviewContextMode) != ReviewContextModes.Request)
            return;
        _reviewAuthorization.Arm();
        _inputField.RefreshReviewAuthorization(true);
        NativeEventLog.Trace("review.authorized", "mod", "player",
            "review tool authorized for this turn by the visible control");
    }



    private void OpenInput()
    {
        if (_listening)
            return;
        // Every input session starts unauthorized; the visible control arms only
        // this session and the next submitted input consumes it.
        _reviewAuthorization.Reset();
        NativeStateInspector.TrySummonIfHidden("chat");
        if (!_inputField.Show(transform, _config, OnInputSubmitted, CancelInput, SelectThinkingMode, OpenDialogueHistory, AuthorizeReviewTurn, OpenSharedActivity))
        {
            NativeEventLog.Trace("input.open_failed", "mod", "system",
                "native-style TMP input could not be created");
            return;
        }
        CancelAiSpeech("input_open");
        _listening = true;
        _ignoreEnterUntil = Time.unscaledTime + 0.35f;
        NativeInput.BringGameToForeground();
        try
        {
            TransparentWindowNew.BeginKeyboardInput();
        }
        catch
        {
            // The native window may already be handling keyboard input.
        }
        NativeEventLog.Trace("input.open", "mod", "player",
            "in-game input opened; Enter sends, Esc cancels");
    }

    private void CancelInput()
    {
        _reviewAuthorization.Reset();
        _listening = false;
        _inputField.Hide();
        CancelPendingDialogue();
        ActionExecutor.Clear();
        try
        {
            TransparentWindowNew.EndKeyboardInput();
        }
        catch
        {
            // Ignore.
        }
        NativeEventLog.Trace("input.cancel", "mod", "player", "in-game input closed");
    }

    private void SelectThinkingMode(string mode)
    {
        try
        {
            _config = ConfigStore.SetDialogueThinkingMode(mode);
            _inputField.RefreshThinkingMode(_config, "");
        }
        catch (Exception error)
        {
            Plugin.PluginLog.LogWarning("Thinking mode could not be saved: " + error.GetType().Name);
            _inputField.RefreshThinkingMode(_config, "未保存，请重选");
        }
    }

    private void OpenDialogueHistory()
    {
        if (!_listening) return;
        try
        {
            _historyWindow.Show(transform, _inputField.StyleText, _memory.CaptureDialogueHistory(), CloseDialogueHistory);
            _inputField.SuspendForHistory(true);
        }
        catch (Exception error)
        {
            _historyWindow.Hide();
            Plugin.PluginLog.LogWarning("History window unavailable: " + error.GetType().Name);
        }
    }

    private void CloseDialogueHistory()
    {
        _historyWindow.Hide();
        _inputField.SuspendForHistory(false);
        _ignoreEnterUntil = Time.unscaledTime + .35f;
    }

    private void TickTestInput()
    {
        if (!_config.TestInputScriptEnabled || !_systemReady)
            return;
        if (!_testInputLoaded)
        {
            LoadTestInput();
            _testInputLoaded = true;
        }
        if (_testInputQueue.Count == 0)
            return;
        if (_requestPending || _storyRecallInProgress || _memoryRecallInProgress)
            return;
        if (Time.unscaledTime - _lastTestInputSentAt < _config.TestInputDelaySeconds)
            return;

        string text = _testInputQueue.Dequeue();
        NativeEventLog.Trace(
            "test.input.inject",
            "mod",
            "system",
            text,
            "queue_remaining=" + _testInputQueue.Count);
        InjectPlayerText(text);
    }

    private void LoadTestInput()
    {
        try
        {
            string path = string.IsNullOrWhiteSpace(_config.TestInputScriptFile)
                ? Path.Combine(DataPaths.Root(), "test-input.txt")
                : _config.TestInputScriptFile;
            if (!File.Exists(path))
                return;
            foreach (string line in File.ReadAllLines(path))
            {
                string value = MemoryStore.Clean(line);
                if (value.Length > 0)
                    _testInputQueue.Enqueue(value);
            }
            NativeEventLog.Trace(
                "test.input.loaded",
                "mod",
                "system",
                "scripted input loaded",
                "path=" + path + ";count=" + _testInputQueue.Count);
        }
        catch (Exception error)
        {
            NativeEventLog.Trace("test.input.error", "mod", "system", error.Message);
        }
    }

    [HideFromIl2Cpp]
    private void OnApiResult(ApiResult result)
    {
        result.DialogueRequest?.PreparationTrace?.Finish("api_result");
        if (!IsCurrentDialogue(result.DialogueRequest) || !NativeLinkageAllows(result)) return;
        if (result.NativeLinkage != null && (result.Error.Length > 0
            || result.Response.Outcome != "accepted"
            || string.IsNullOrWhiteSpace(result.Text) && !result.ExplicitEmptyText))
        {
            FinishNativeLinkage(result, "failed_or_invalid");
            _requestPending = false;
            _proactiveRequest = false;
            return; // Never manufacture a local in-character reply to this event.
        }
        NativeBubblePresenter.ObserveGenerated(result);
        RecordVisualSupply(result);
        _requestPending = false;
        if (result.Response.Outcome == "cancelled")
        {
            _memory.EndReflectionDelivery(result.SourceUserEventId, result.DialogueRequest?.Id ?? result.RequestId, "cancelled", false);
            return;
        }
        if (result.Error.Length > 0)
        {
            NativeEventLog.Trace(
                "deepseek.error",
                "mod",
                "system",
                PrivacySafety.Redact(result.Error),
                "request_id=" + result.RequestId + ";attempt_id=" + result.Response.AttemptId
                + ";outcome=" + result.Response.Outcome);
            if(TransportFailurePresentation.HandlesReplyFailure(result))
            {
                if(!_proactiveRequest)
                {
                    _inputField.StageReplyFailure(TransportFailurePresentation.Draft(result,_currentUserText),_listening || _historyWindow.IsOpen, TransportFailurePresentation.NoticeFor(result));
                    // Stage first; respect the normal reply-window gate before restoring focus.
                    if(!_historyWindow.IsOpen && NativeInput.IsGameForeground() && CanOpenReplyWindow())OpenInput();
                }
                _memory.EndReflectionDelivery(result.SourceUserEventId, result.DialogueRequest?.Id ?? result.RequestId, "failed", false);
                NativeEventLog.Trace(result.Response.Outcome == "empty_visible_reply" ? "api.empty_reply_notice" : "api.transport_notice",
                    "mod","system","reply failed; no character fallback",
                    "request_id="+result.RequestId+";attempt_id="+result.Response.AttemptId
                    +";transport_retry_count="+result.PreviousTransportAttempts.Count+";further_retry=false");
                return;
            }
            if (_config.ApiFallbackEnabled)
            {
                ApiResult fallback = RuntimeDirector.BuildLocalReply(
                    _currentUserText,
                    result,
                    _config);
                fallback.CopyRequestFrom(result);
                NativeEventLog.Trace(
                    "api.fallback",
                    "mod",
                    "lilith",
                    "API failed; local in-character fallback shown",
                    "kind=" + (result.Error.Contains("timeout", StringComparison.OrdinalIgnoreCase)
                        ? "timeout"
                        : result.Error.Contains("HTTP", StringComparison.OrdinalIgnoreCase)
                            ? "http"
                            : "other")
                    + ";request_id=" + result.RequestId + ";attempt_id=" + result.Response.AttemptId);
                ApplyAiResult(fallback);
            }
            else
                _memory.EndReflectionDelivery(result.SourceUserEventId, result.DialogueRequest?.Id ?? result.RequestId, "failed_or_empty", false);
            return;
        }
        if (TrySettleProactiveSilence(result)) return;
        if (string.IsNullOrWhiteSpace(result.Text))
        {
            NativeEventLog.Trace("api.empty", "mod", "system", "model returned empty content");
            if (_config.ApiFallbackEnabled)
            {
                ApiResult fallback = RuntimeDirector.BuildLocalReply(
                    _currentUserText,
                    result,
                    _config);
                fallback.CopyRequestFrom(result);
                ApplyAiResult(fallback);
            }
            else
                _memory.EndReflectionDelivery(result.SourceUserEventId, result.DialogueRequest?.Id ?? result.RequestId, "failed_or_empty", false);
            return;
        }

        try
        {
            ProtocolObservation observation = ProtocolMigrationMonitor.Observe(
                result,
                _sessionState,
                _config,
                DesktopArrangePolicy.AllowPropose(_config));
            NativeEventLog.Trace(
                "protocol.observation",
                "mod",
                "system",
                "model reply observed",
                observation.ToDetails());
            if (ProtocolMigrationMonitor.ShouldWriteReport())
                ProtocolMigrationMonitor.TryWriteReport(_config);
        }
        catch
        {
            // Observation must never change behavior or break a reply.
        }

        if (_config.CoherenceGuardEnabled
            && PersonaStateEngine.DetectBodyPartMismatch(
                result,
                _sessionState,
                out string mismatch))
        {
            NativeEventLog.Trace("body_part_mismatch.detected", "mod", "lilith",
                "touch event semantic mismatch detected", "mismatch=" + mismatch);
            if (_config.TouchEventRepairEnabled)
            {
                BridgeConfig requestConfig = _config;
                string originalReply = result.Text;
                string repairRecallQuery = _sessionState.LastUserText;
                IReadOnlyList<MemoryTurn>? repairRecallUsed = null;
                using var mentionRepairScope = _memory.BeginMentionRequest(_currentUserText, !_proactiveRequest);
                string repairContext = ContextBuilder.Build(
                    _memory,
                    _sessionState,
                    _config,
                    list => repairRecallUsed = list);
                _requestPending = true;
                NativeEventLog.Trace("coherence.mismatch", "mod", "lilith",
                    "response mentioned the wrong touch area", mismatch);
                Task.Run(async () =>
                {
                    try
                    {
                        string? repaired = await _client.RepairDialogueAsync(
                            requestConfig,
                            originalReply,
                            mismatch,
                            repairContext,
                            result.DialogueRequest?.Token ?? System.Threading.CancellationToken.None,
                            result.DialogueRequest).ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(repaired))
                        {
                            result.Text = MemoryStore.Clean(repaired);
                            _mainThread.Enqueue(() =>
                            {
                                if (!IsCurrentDialogue(result.DialogueRequest) || !NativeLinkageAllows(result)) return;
                                if (repairRecallUsed != null && repairRecallUsed.Count > 0)
                                    _memory.RecordRecall(repairRecallQuery, repairRecallUsed, "dialogue_repair");
                                OnRepairResult(result, true, mismatch);
                            });
                            return;
                        }
                    }
                    catch
                    {
                        // Repair is optional; the original reply is still usable.
                    }
                    _mainThread.Enqueue(() => OnRepairResult(result, false, mismatch));
                });
                return;
            }
            NativeEventLog.Trace("body_part_mismatch.repair_skipped", "mod", "system",
                "touch event repair disabled; original reply preserved", "mismatch=" + mismatch);
        }

        ApplyAiResult(result);
    }

    [HideFromIl2Cpp]
    private void OnRepairResult(ApiResult result, bool repaired, string mismatch)
    {
        if (!IsCurrentDialogue(result.DialogueRequest) || !NativeLinkageAllows(result)) return;
        if (result.NativeLinkage != null && (result.Error.Length > 0
            || result.Response.Outcome != "accepted"
            || string.IsNullOrWhiteSpace(result.Text) && !result.ExplicitEmptyText))
        {
            FinishNativeLinkage(result, "failed_or_invalid");
            _requestPending = false;
            _proactiveRequest = false;
            return; // Never manufacture a local in-character reply to this event.
        }
        NativeBubblePresenter.ObserveGenerated(result);
        _requestPending = false;
        ApplyAiResult(result, repaired, mismatch);
    }

    [HideFromIl2Cpp]
    private void ApplyAiResult(ApiResult result, bool repaired = false, string? mismatch = null)
    {
        if (!IsCurrentDialogue(result.DialogueRequest) || !NativeLinkageAllows(result)) return;
        ApiResult requestResult = result;
        if (_config.SafetyFallbackEnabled
            && PrivacySafety.IsCrisis(result?.Text))
        {
            NativeEventLog.Trace(
                "safety.local_guard",
                "mod",
                "system",
                "crisis phrasing in reply replaced by a local supportive line");
            result = new ApiResult
            {
                IsLocalFallback = true,
                Text = PrivacySafety.LocalSupportReply(),
                Emotion = "emoji_calm_1",
                LatencyMs = result?.LatencyMs ?? 0
            };
            repaired = false;
        }
        if (string.IsNullOrWhiteSpace(result?.Text))
        {
            if (!_config.ApiFallbackEnabled || !_config.SafetyFallbackEnabled)
            {
                _memory.EndReflectionDelivery(requestResult.SourceUserEventId,
                    requestResult.DialogueRequest?.Id ?? requestResult.RequestId, "empty", false);
                return;
            }
            result = new ApiResult
            {
                IsLocalFallback = true,
                Text = PrivacySafety.LocalFallbackReply(_currentUserText),
                Emotion = "emoji_calm_1",
                Action = "confuse"
            };
        }
        result.CopyRequestFrom(requestResult);
        result.CopyResponseFrom(requestResult);
        if (result.ReflectionRequested)
        {
            _reflectionWindow.MarkModelRequested();
            _memory.SaveReflectionWindow(_reflectionWindow.DurableSnapshot);
            NativeEventLog.Trace("memory.reflection.requested", "lilith", "lilith",
                "she asked for a quiet look back",
                result.ReflectionRequestReason.Length > 0
                    ? result.ReflectionRequestReason
                    : "unstated");
        }
        if (!_memoryRecallInProgress
            && result.MemoryRecalls.Count > 0
            && _config.MemoryHonestRecallEnabled)
        {
            string query = result.MemoryRecalls[0];
            using var mentionRecallScope = _memory.BeginMentionRequest(_currentUserText, !_proactiveRequest);
            using var nativeRecallScope = _memory.BeginNativeDialogueRequest(_currentUserText, !_proactiveRequest, _config, CaptureNativeDialogueAuthorization());
            result.MemoryRecalls.Clear();
            IReadOnlyList<MemoryTurn> candidates = _memory.GetRecallCandidates(
                query,
                _config.MemoryRecallMaxEntries);
            IReadOnlyList<MemoryTurn> requestHistory = _memory.GetRequestHistory(_config);
            IReadOnlyList<MemoryTurn> finalRequestHistory =
                _memory.RevalidateRequestHistory(requestHistory);
            IReadOnlyList<MemoryTurn> suppliedHistory =
                _memory.GetRequestHistorySelection(_config, finalRequestHistory);
            IReadOnlyList<MemoryTurn> experiences = _memory.GetExperienceCandidates(query,9,suppliedHistory.Select(t => t.EventId),
                requestedRole:MemorySemantics.RequestedHistorySpeaker(_currentUserText));
            {
                ApiResult originalResult = result;
                BridgeConfig requestConfig = _config;
                string userText = _currentUserText;
                string recallFollowUpQuery = _sessionState.LastUserText;
                IReadOnlyList<MemoryTurn>? recallFollowUpUsed = null;
                string expandedContext = ContextBuilder.Build(
                        _memory,
                        _sessionState,
                        _config,
                        list => recallFollowUpUsed = list,
                        imageAttached: false, includeVisionStatus: originalResult.DialogueRequest == null,
                        requestHistory: finalRequestHistory);
                expandedContext += Environment.NewLine + _sessionState.VisualSupply.Describe(suppliedHistory);
                var suppliedIds = new HashSet<string>((recallFollowUpUsed ?? Array.Empty<MemoryTurn>()).Select(t => t.EventId));
                var extraFacts = candidates.Where(t => t.EventId.Length == 0 || !suppliedIds.Contains(t.EventId)).ToList();
                var extraExperiences = experiences.Where(t => !suppliedIds.Contains(t.EventId)).ToList();
                expandedContext += Environment.NewLine + MemorySemantics.BuildRecallContext(extraFacts)
                    + Environment.NewLine + MemoryStore.BuildExperienceContext(extraExperiences);
                if (candidates.Count == 0 && experiences.Count == 0)
                    expandedContext += "\n[本次检索结果] 没有找到与本次检索词相关的额外记录；这不证明事情从未发生。请根据已有记录回答，不要承诺稍后再查。";
                recallFollowUpUsed = (recallFollowUpUsed ?? Array.Empty<MemoryTurn>()).Concat(extraFacts).Concat(extraExperiences).ToList();
                _memoryRecallInProgress = true;
                _requestPending = true;
                NativeEventLog.Trace(
                    "memory.recall.request",
                    "mod",
                    "system",
                    "Lilith requested a more precise memory recall",
                    "query=" + PrivacySafety.Redact(query)
                    + ";entries=" + candidates.Count);
                Task.Run(async () =>
                {
                    try
                    {
                        ApiResult recalled = await _client.SendAsync(
                            requestConfig,
                            userText,
                            expandedContext,
                            null,
                            System.Threading.CancellationToken.None,
                            finalRequestHistory,
                            false, originalResult.ThoughtBoundary,
                            claimBoundary: originalResult.ClaimBoundary,
                            requestPurpose: "memory_recall", requestId: originalResult.RequestId,
                            dialogueRequest: originalResult.DialogueRequest,
                            historyOwner: _memory).ConfigureAwait(false);
                        recalled.CopyRequestFrom(originalResult);
                        recalled.MemoryRecalls.Clear();
                        _mainThread.Enqueue(() =>
                        {
                            if (!IsCurrentDialogue(originalResult.DialogueRequest) || !NativeLinkageAllows(originalResult)) return;
                            if (recallFollowUpUsed != null && recallFollowUpUsed.Count > 0 && recalled.Error.Length == 0)
                                for (int i=0; i<recalled.SuccessfulModelRequests; i++)
                                    _memory.RecordRecall(recallFollowUpQuery, recallFollowUpUsed, "memory_recall");
                            OnMemoryRecallResult(recalled, originalResult, query);
                        });
                    }
                    catch (Exception error)
                    {
                        _mainThread.Enqueue(() => OnMemoryRecallResult(
                            new ApiResult { Error = error.Message },
                            originalResult,
                            query));
                    }
                });
                return;
            }
        }
        if (!_storyRecallInProgress
            && result.StoryRecalls.Count > 0
            && _config.PreviousStoryEnabled)
        {
            string query = result.StoryRecalls[0];
            using var mentionStoryScope = _memory.BeginMentionRequest(_currentUserText, !_proactiveRequest);
            using var nativeStoryScope = _memory.BeginNativeDialogueRequest(_currentUserText, !_proactiveRequest, _config, CaptureNativeDialogueAuthorization());
            ApiResult originalResult = result;
            result.StoryRecalls.Clear();
            StoryRecallResult recall = PreviousStoryArchive.Recall(query, _config);
            if (recall.Found)
            {
                BridgeConfig requestConfig = _config;
                string userText = _currentUserText;
                string storyRecallQuery = _sessionState.LastUserText;
                IReadOnlyList<MemoryTurn>? storyRecallUsed = null;
                IReadOnlyList<MemoryTurn> requestHistory = _memory.GetRequestHistory(requestConfig);
                IReadOnlyList<MemoryTurn> finalRequestHistory =
                    _memory.RevalidateRequestHistory(requestHistory);
                string expandedContext = ContextBuilder.Build(
                        _memory,
                        _sessionState,
                        _config,
                        list => storyRecallUsed = list,
                        imageAttached: false, includeVisionStatus: originalResult.DialogueRequest == null,
                        requestHistory: finalRequestHistory)
                    + Environment.NewLine
                    + Environment.NewLine
                    + PreviousStoryArchive.BuildRecallContext(recall, _config);
                expandedContext += Environment.NewLine + _sessionState.VisualSupply.Describe(
                    _memory.GetRequestHistorySelection(requestConfig, finalRequestHistory));
                _storyRecallInProgress = true;
                _requestPending = true;
                NativeEventLog.Trace("story.recall.request", "mod", "system",
                    "Lilith requested a full story-archive recall",
                    "query=" + query + ";chars=" + recall.Content.Length);
                Task.Run(async () =>
                {
                    try
                    {
                        ApiResult recalled = await _client.SendAsync(
                            requestConfig,
                            userText,
                            expandedContext,
                            null,
                            System.Threading.CancellationToken.None,
                            finalRequestHistory,
                            false, originalResult.ThoughtBoundary,
                            claimBoundary: originalResult.ClaimBoundary,
                            requestPurpose: "story_recall", requestId: originalResult.RequestId,
                            dialogueRequest: originalResult.DialogueRequest,
                            historyOwner: _memory).ConfigureAwait(false);
                        recalled.CopyRequestFrom(originalResult);
                        recalled.StoryRecalls.Clear();
                        _mainThread.Enqueue(() =>
                        {
                            if (!IsCurrentDialogue(originalResult.DialogueRequest) || !NativeLinkageAllows(originalResult)) return;
                            if (storyRecallUsed != null && storyRecallUsed.Count > 0 && recalled.Error.Length == 0)
                                for (int i=0; i<recalled.SuccessfulModelRequests; i++)
                                    _memory.RecordRecall(storyRecallQuery, storyRecallUsed, "story_recall");
                            OnStoryRecallResult(recalled, originalResult, query);
                        });
                    }
                    catch (Exception error)
                    {
                        _mainThread.Enqueue(() => OnStoryRecallResult(
                            new ApiResult { Error = error.Message },
                            originalResult,
                            query));
                    }
                });
                return;
            }
            NativeEventLog.Trace("story.recall.not_found", "mod", "system",
                "story archive recall returned no usable excerpt", query);
        }

        if (repaired)
        {
            NativeEventLog.Trace("coherence.repaired", "mod", "lilith",
                "touch area repaired",
                mismatch + " => " + result.Text);
        }
        ActionDirector.ApplyFallback(result, _currentUserText,
            ContextBuilder.Build(_memory, _sessionState, _config, playerDirected: !_proactiveRequest), _config, _sessionState);
        if (PersonaStateEngine.RepairsAction(result, _sessionState, _config, out string actionReason))
        {
            NativeEventLog.Trace("coherence.action", "mod", "lilith",
                "response action repaired for current pose", actionReason);
        }
        // Keep request control restrictions at the final handoff as well as in
        // the client, without judging or replacing ordinary public wording.
        DeepSeekClient.ClearHistoricalRequestControls(result);
        result.EnforceMemoryOperationOutcome();
        if (result.ClaimBoundary?.RequiresSourceCheck == true)
        {
            NativeEventLog.Trace("player_claim.boundary", "mod", "system",
                "player memory claim supplied with candidate utterances",
                "outcome=" + result.ClaimBoundaryOutcome
                    + ";claim_len=" + result.ClaimBoundary.Claim.Length
                    // Source metadata is diagnostic; it is not a public-wording verdict;
                    // reply length is only an expression diagnostic.
                    + ";reply_chars=" + result.Text.Length);
        }
        // Last text boundary before the display queue: the delivery record's
        // generated full text and deterministic segment range are fixed here.
        // Earlier observations (including the pre-repair text) stay diagnostic.
        NativeBubblePresenter.SyncGeneratedDelivery(result);
        if (_config.B1ContinuityEnabled && result.DialogueRequest is { B1Packet: not null } b1Owner)
        {
            // Commit may revoke and clear the active packet after a live
            // configuration change. Keep only its stable audit identity here.
            string b1StepId = b1Owner.B1Packet.Id;
            B1Commit retained = _memory.CommitB1(b1Owner, result, () => IsCurrentDialogue(b1Owner));
            NativeEventLog.Trace("b1.commit", "mod", "system", "ordinary reply retention",
                "step=" + b1StepId + ";outcome=" + retained.Outcome + ";version=" + retained.Version);
        }
        if (result.NativeLinkage == null)
        {
            HandleDesktopArrangeIntent(result);
            NativeMemoWriter.Apply(result, _config);
            HandleReplyPaper(result);
            ApplyAutonomousMemory(result);
        } // Native-event controls settle only after the first actual display.
        _proactiveRequest = false;
        QueueGeneratedReply(result);
        _queuedReplyBlockedByStory = HasActiveNativeStory();
        NativeEventLog.Trace("model.reply", "deepseek", "lilith", result.Text,
            $"request_id={result.RequestId};attempt_id={result.Response.AttemptId};" +
            $"response_outcome={result.Response.Outcome};" +
            $"latency_ms={result.LatencyMs};prompt_tokens={result.PromptTokens};" +
            $"completion_tokens={result.CompletionTokens};" +
            $"cache_hit={result.PromptCacheHitTokens};cache_miss={result.PromptCacheMissTokens}");
    }

    [HideFromIl2Cpp]
    private void OnStoryRecallResult(
        ApiResult recalledResult,
        ApiResult fallbackResult,
        string query)
    {
        if (!IsCurrentDialogue(fallbackResult.DialogueRequest) || !NativeLinkageAllows(fallbackResult)) return;
        if (recalledResult != null)
        {
            NativeBubblePresenter.ObserveGenerated(recalledResult);
            RecordVisualSupply(recalledResult);
        }
        _storyRecallInProgress = false;
        _requestPending = false;
        if (recalledResult == null
            || recalledResult.Error.Length > 0
            || string.IsNullOrWhiteSpace(recalledResult.Text))
        {
            NativeEventLog.Trace("story.recall.fallback", "mod", "system",
                "story recall regeneration failed; fallback to original reply",
                "query=" + query + ";error=" + (recalledResult?.Error ?? "empty"));
            ApplyAiResult(fallbackResult);
            return;
        }
        NativeEventLog.Trace("story.recall.done", "mod", "lilith",
            "Lilith used the full story archive and answered naturally",
            "query=" + query + ";text=" + recalledResult.Text);
        NativeBubblePresenter.CancelUndispatched(fallbackResult,
            "superseded", "replaced_by_story_recall_answer");
        ApplyAiResult(recalledResult);
    }

    [HideFromIl2Cpp]
    private void OnMemoryRecallResult(
        ApiResult recalledResult,
        ApiResult fallbackResult,
        string query)
    {
        if (!IsCurrentDialogue(fallbackResult.DialogueRequest) || !NativeLinkageAllows(fallbackResult)) return;
        if (recalledResult != null)
        {
            NativeBubblePresenter.ObserveGenerated(recalledResult);
            RecordVisualSupply(recalledResult);
        }
        _memoryRecallInProgress = false;
        _requestPending = false;
        if (recalledResult == null
            || recalledResult.Error.Length > 0
            || string.IsNullOrWhiteSpace(recalledResult.Text))
        {
            NativeEventLog.Trace(
                "memory.recall.fallback",
                "mod",
                "system",
                "memory recall regeneration failed; fallback to original reply",
                "query=" + PrivacySafety.Redact(query)
                + ";error=" + (recalledResult?.Error ?? "empty"));
            ApplyAiResult(fallbackResult);
            return;
        }
        NativeEventLog.Trace(
            "memory.recall.done",
            "mod",
            "lilith",
            "Lilith used the recalled memory and answered naturally",
            "query=" + PrivacySafety.Redact(query) + ";text=" + recalledResult.Text);
        foreach (MemoryPatch patch in fallbackResult.AutonomousMemoryPatches)
        {
            if (!recalledResult.AutonomousMemoryPatches.Any(item =>
                    string.Equals(item.Text, patch.Text, StringComparison.Ordinal)
                    && string.Equals(item.Action, patch.Action, StringComparison.Ordinal)))
            {
                recalledResult.AutonomousMemoryPatches.Add(patch);
            }
        }
        foreach (string text in fallbackResult.AutonomousMemories)
        {
            if (!recalledResult.AutonomousMemories.Contains(text, StringComparer.Ordinal))
                recalledResult.AutonomousMemories.Add(text);
        }
        foreach (string text in fallbackResult.AutonomousMemoryRemovals)
        {
            if (!recalledResult.AutonomousMemoryRemovals.Contains(text, StringComparer.Ordinal))
                recalledResult.AutonomousMemoryRemovals.Add(text);
        }
        NativeBubblePresenter.CancelUndispatched(fallbackResult,
            "superseded", "replaced_by_recall_answer");
        ApplyAiResult(recalledResult);
    }

    [HideFromIl2Cpp]
    private void HandleDesktopArrangeIntent(ApiResult result)
    {
        string intent = (result.DesktopArrange ?? "").Trim().ToLowerInvariant();
        if (intent.Length == 0)
            return;

        switch (intent)
        {
            case "propose":
                if (!_config.DesktopArrangeEnabled)
                {
                    NativeEventLog.Trace("desktop_arrange.disabled", "mod", "system",
                        "desktop arrange proposal ignored because feature is disabled");
                    return;
                }
                if (!DesktopArrangePolicy.AllowPropose(_config))
                {
                    DesktopArrangeEvaluation policy = DesktopArrangePolicy.Evaluate(_config);
                    NativeEventLog.Trace("desktop_arrange.proposal_denied", "mod", "system",
                        "desktop arrange proposal ignored by program policy",
                        policy.Describe());
                    return;
                }
                if (Time.unscaledTime - _lastDesktopArrangeProposalAt
                    < _config.DesktopArrangeProposalCooldownSeconds)
                {
                    NativeEventLog.Trace("desktop_arrange.cooldown", "mod", "system",
                        "desktop arrange proposal ignored by cooldown");
                    return;
                }
                _lastDesktopArrangeProposalAt = Time.unscaledTime;
                _desktopArrangeProposePending = true;
                _desktopArrangeProposalAt = Time.unscaledTime;
                NativeEventLog.Trace("desktop_arrange.propose", "mod", "lilith",
                    "Lilith proposed to arrange desktop icons");
                break;

            case "confirm":
            case "execute":
                if (!_config.DesktopArrangeEnabled)
                {
                    NativeEventLog.Trace("desktop_arrange.disabled", "mod", "system",
                        "desktop arrange request ignored because feature is disabled");
                    return;
                }
                _desktopArrangeProposePending = false;
                _desktopArrangeExecutePending = true;
                NativeEventLog.Trace("desktop_arrange.confirmed", "mod", "system",
                    "desktop arrange confirmed",
                    "intent=" + intent);
                break;

            case "cancel":
                _desktopArrangeProposePending = false;
                _desktopArrangeExecutePending = false;
                NativeEventLog.Trace("desktop_arrange.cancel", "mod", "system",
                    "desktop arrange cancelled");
                break;

            case "undo":
                _desktopArrangeProposePending = false;
                _desktopArrangeExecutePending = false;
                _desktopArrangeUndoPending = true;
                NativeEventLog.Trace("desktop_arrange.undo_request", "mod", "system",
                    "desktop arrange undo requested");
                break;
        }
    }

    [HideFromIl2Cpp]
    private void ApplyAutonomousMemory(ApiResult result)
    {
        // Counters describe this reply only; a previous turn's totals must never
        // leak into the delivery-time extraction gate.
        result.MainModelCandidateCount = 0;
        result.MainModelCandidateWrites = 0;
        if (result.IsMemoryOperation || !_config.AutonomousMemoryEnabled)
            return;

        int candidates = 0;
        int writes = 0;
        foreach (MemoryPatch patch in result.AutonomousMemoryPatches)
        {
            candidates++;
            RuntimeProposalResult candidateResult = SubmitMainModelPatch(patch);
            if (candidateResult.WriteSucceeded)
                writes++;
            NativeEventLog.Trace("memory.phase2.main_model_candidate", "model", "lilith",
                "main model proposal routed to candidate pipeline",
                patch.Action + ";" + patch.Text
                + ";state=" + candidateResult.State
                + ";decision=" + candidateResult.Decision
                + ";reason=" + candidateResult.Reason
                + ";write_succeeded=" + candidateResult.WriteSucceeded);
        }

        foreach (string text in result.AutonomousMemoryRemovals)
        {
            // Removals are not main-model writes; they stay the auxiliary pass's
            // work at the existing legacy boundary and are counted separately.
            _runtimeMemory.LegacySink.Capture("main_model_forget", text);
            NativeEventLog.Trace("memory.phase2.legacy_forget_audit", "model", "lilith",
                "main model forget captured as legacy audit only; not applied",
                text);
        }

        foreach (string text in result.AutonomousMemories)
        {
            candidates++;
            RuntimeProposalResult candidateResult = SubmitMainModelText(text);
            if (candidateResult.WriteSucceeded)
                writes++;
            NativeEventLog.Trace("memory.phase2.main_model_candidate", "model", "lilith",
                "main model remember routed to candidate pipeline",
                text + ";state=" + candidateResult.State
                + ";decision=" + candidateResult.Decision
                + ";reason=" + candidateResult.Reason
                + ";write_succeeded=" + candidateResult.WriteSucceeded);
        }

        result.MainModelCandidateCount = candidates;
        result.MainModelCandidateWrites = writes;
    }

    [HideFromIl2Cpp]
    private RuntimeProposalResult SubmitMainModelPatch(MemoryPatch patch)
    {
        return _runtimeMemory.Submit(new RuntimeProposal
        {
            Source = "main_model",
            ProposedText = patch.Text,
            RawText = patch.Text,
            RawAction = patch.Action,
            SourceQuote = "",
            EvidenceSource = "assistant",
            CategoryHint = patch.Category,
            Importance = patch.Importance,
            Strength = patch.Strength,
            PolicyStatus = "unconfigured",
            Hints = new[]
            {
                "possible_temporary",
                "assistant_derived"
            }
        });
    }

    [HideFromIl2Cpp]
    private RuntimeProposalResult SubmitMainModelText(string text)
    {
        return _runtimeMemory.Submit(new RuntimeProposal
        {
            Source = "main_model",
            ProposedText = text,
            RawText = text,
            RawAction = "save",
            SourceQuote = "",
            EvidenceSource = "assistant",
            CategoryHint = "event",
            PolicyStatus = "unconfigured",
            Hints = new[]
            {
                "possible_temporary",
                "assistant_derived"
            }
        });
    }

    [HideFromIl2Cpp]
    private void TryScheduleAutonomousMemoryExtraction(ApiResult result)
    {
        // The gate used to skip whenever the main model had proposed any
        // candidate, even if authority rejected all of them. Judge by what this
        // turn actually wrote (AutonomousExtractionPolicy keeps the original
        // switches and the removals boundary).
        bool hasSourceUserEvent = result.SourceUserEventId.Length > 0;
        bool looksMemoryWorthy = LooksMemoryWorthy(result.SourceUserText, result.DeliveredText);
        AutonomousExtractionDecision decision = AutonomousExtractionPolicy.Evaluate(
            _config.AutonomousMemoryEnabled,
            _config.MemoryUserWriteEnabled,
            hasSourceUserEvent,
            result.MainModelCandidateWrites,
            result.AutonomousMemoryRemovals.Count,
            _config.AutonomousMemoryExtractEveryTurn,
            looksMemoryWorthy);
        // One aggregate diagnostic per settled reply: candidates proposed,
        // candidates actually written, removals and the exact gate decision.
        // Counts and reasons only; no player or reply text enters this line.
        NativeEventLog.Trace("memory.phase2.aux_extraction_gate", "mod", "system",
            decision.ShouldSchedule
                ? "delivery-time auxiliary extraction scheduled"
                : "delivery-time auxiliary extraction skipped",
            "candidates=" + result.MainModelCandidateCount
            + ";writes=" + result.MainModelCandidateWrites
            + ";removals=" + result.AutonomousMemoryRemovals.Count
            + ";" + decision.Describe()
            + ";every_turn=" + _config.AutonomousMemoryExtractEveryTurn
            + ";worthy=" + looksMemoryWorthy
            + ";has_source_event=" + hasSourceUserEvent);
        if (!decision.ShouldSchedule)
            return;

        NaturalMemoryRequest? request = _memory.CaptureNaturalMemoryRequest(result.SourceUserEventId, result.SourceUserText);
        if (request == null) return;
        BridgeConfig requestConfig = _config;
        System.Threading.Interlocked.Increment(ref _sharedActivityAuxRequests);
        Task.Run(async () =>
        {
            try
            {
            string? raw = await _client.DecideNaturalMemoryAsync(requestConfig, request,
                System.Threading.CancellationToken.None).ConfigureAwait(false);
            NaturalMemoryDecision? operation = NaturalMemory.Parse(raw);
            _mainThread.Enqueue(() =>
            {
                NaturalMemoryResult applied = _memory.ApplyNaturalMemoryDecision(request,
                    _config.MemoryUserWriteEnabled ? operation : null, _runtimeMemory);
                if (applied.Applied && applied.Reason == "source_suppressed")
                    _sessionState.SuppressSources(_memory.IsExperienceSuppressed);
                NativeEventLog.Trace("memory.natural.settled", "mod", "system", "auxiliary natural memory decision settled",
                    "action=" + (operation?.Action ?? "none") + ";applied=" + applied.Applied + ";reason=" + applied.Reason);
            });
            }
            finally { System.Threading.Interlocked.Decrement(ref _sharedActivityAuxRequests); }
        });
    }
    /// <summary>
    /// Observation only (0.3.94). Records when the player asks her to confirm
    /// something he says he once said, and whether his own archived words support
    /// it. It never touches the request, the prompt or the reply — whether she
    /// confirmed or denied is judged later, offline, against the same turn.
    /// Rationale and the rules it respects: PRJL-PLAYER-CLAIM-OBSERVATION-20260919.md.
    /// </summary>
    private void ObservePlayerClaim(string text, string userEventId = "")
    {
        try
        {
            string claim = MemorySemantics.ExtractPlayerClaim(text);
            if (claim.Length == 0)
                return;
            (double best, int matches) = _memory.MeasurePlayerClaimSupport(claim, text);
            bool supported = matches > 0;
            NativeEventLog.Trace("player_claim.observed", "mod", "system",
                supported
                    ? "player memory claim is supported by his own words"
                    : "player memory claim has no supporting words",
                "verdict=" + (supported ? "supported" : "unsupported")
                    + ";best=" + best.ToString("0.00")
                    + ";matches=" + matches
                    + ";form=" + MemorySemantics.DescribePlayerClaimForm(text)
                    + ";claim_len=" + claim.Length
                    + ";claim=" + PrivacySafety.Redact(claim));
            // Observation phase (2026-09-20): the read-only bypass. Numbers and ids
            // only — the claim text stays out of the file — and the offline tool
            // compares this rule (H) with the frozen NLI pipeline (I) and OR.
            // Off unless claim_observation_enabled is set; failure is swallowed in
            // TryRecord so nothing here can reach the reply path.
            if (_config.ClaimObservationEnabled)
            {
                long startedAt = Environment.TickCount64;
                bool requiresCheck = matches == 0;
                ClaimObservation.TryRecord(
                    enabled: true,
                    root: DataPaths.Root(),
                    eventId: userEventId,
                    form: MemorySemantics.DescribePlayerClaimForm(text),
                    claimLength: claim.Length,
                    best: best,
                    matches: matches,
                    requiresCheck: requiresCheck,
                    elapsedMs: Environment.TickCount64 - startedAt);
            }
        }
        catch
        {
            // Observation must never affect the reply.
        }
    }

    /// <summary>
    /// The fixed 15-minute throttle was removed on 2026-09-19: it made reflection a
    /// schedule rather than a response, and it is what let the same evening be
    /// thought about again and again. What remains is a short floor that only stops
    /// back-to-back runs — whether she reflects at all is still decided by new
    /// material, being idle, and not being busy.
    /// </summary>
    private const float ReflectionAntiThrashSeconds = 60f;

    private float EffectiveReflectionGapSeconds() =>
        Math.Min(Math.Max(0f, _config.ReflectionIntervalSeconds), ReflectionAntiThrashSeconds);

    private void TryScheduleReflection()
    {
        TickReflectionContinuation();
        if (SharedActivityRunning || SharedActivityUiBusy) return;
        if (!_config.ReflectionEnabled || !_systemReady || !_reflectionWindow.InitializationSucceeded
            || string.IsNullOrWhiteSpace(_config.ApiKey) || string.IsNullOrWhiteSpace(_config.ReflectionModel))
            return;
        // Foreground preparation gets its short critical segment first; drafts,
        // network wait and ordinary display do not invalidate a source window.
        if (_reflectionInProgress || !ReflectionMayPrepare())
            return;

        bool testTrigger = _config.ReflectionTestTrigger && !_lastReflectionTestTrigger;
        if (!testTrigger && (Time.unscaledTime < _nextReflectionAt || Time.unscaledTime < _nextReflectionPreparationAt))
            return;
        if (NativeStateInspector.IsNativeQuietForMod())
            return;

        double idleSeconds = _config.ReflectionOnlyWhenIdle
            ? NativeStateInspector.TimeSinceLastInteractionSeconds
            : double.PositiveInfinity;
        ReflectionHistorySelection selection = _memory.SelectPendingReflectionHistory(_reflectionWindow.Snapshot);
        IReadOnlyList<MemoryTurn> pendingWindow = selection.History;
        int newDialogueLines = MemoryStore.CountReflectionDialogueLines(pendingWindow);
        // Choose the material after preserving the existing opportunity floor.
        // A smaller recent prefix may use a pause already earned by pending old
        // exchanges; it does not create another timer, request or concurrency slot.
        int schedulingDialogueLines = newDialogueLines;
        bool incompleteOpportunity = pendingWindow.Any(t => t.Role == "user" && !t.ReflectionExchangeComplete)
            || pendingWindow.FirstOrDefault()?.Role == "assistant";
        bool resumedOpportunity = _memory.HasResumedReflectionExchange(pendingWindow);
        int budgetOpportunityLines = selection.BudgetLimited ? selection.AvailableDialogueLines : 0;
        if (selection.Recent && newDialogueLines > 0)
        {
            var oldOpportunity = _memory.SelectPendingReflectionHistory(_reflectionWindow.InputId,
                _reflectionWindow.Snapshot.RecentConsumed);
            schedulingDialogueLines = Math.Max(schedulingDialogueLines,
                MemoryStore.CountReflectionDialogueLines(oldOpportunity.History));
            incompleteOpportunity |= oldOpportunity.History.Any(t => t.Role == "user" && !t.ReflectionExchangeComplete)
                || oldOpportunity.History.FirstOrDefault()?.Role == "assistant";
            resumedOpportunity |= _memory.HasResumedReflectionExchange(oldOpportunity.History);
            if (oldOpportunity.BudgetLimited) budgetOpportunityLines = Math.Max(budgetOpportunityLines, oldOpportunity.AvailableDialogueLines);
        }
        bool resumedPending = _memory.HasResumedReflectionExchange(pendingWindow);
        bool significantChange = _memory.MeasureMemoryActivity() != _reflectionWindow.FactActivity;
        // Scheduling no longer consumes novelty; do not copy and compare the
        // entire experience archive on every eligible frame for an unused value.
        const bool novelMaterial = false;
        bool requestedByModel = _reflectionWindow.ModelRequested;
        string trigger = RuntimeDirector.DescribeReflectionTrigger(
            _config, idleSeconds, significantChange, novelMaterial, requestedByModel);
        if (!testTrigger && !RuntimeDirector.ShouldReflect(
                _config,
                _systemReady,
                false, // Foreground preparation was checked above; HTTP wait is not a source invalidation.
                false,
                false, // A draft/input window does not own reflection's delivered-source window.
                _annotationInProgress,
                _noteSupplementInProgress,
                _reflectionInProgress,
                idleSeconds,
                schedulingDialogueLines,
                significantChange,
                novelMaterial,
                requestedByModel,
                resumedOpportunity,
                incompleteOpportunity,
                budgetOpportunityLines))
            return;

        string latestInput = pendingWindow.LastOrDefault()?.EventId ?? "";
        if (latestInput.Length == 0)
            return;
        if (!testTrigger && !selection.Recent && latestInput == _reflectionWindow.InputId)
            return;
        if (!QueueReflectionPreparation(selection))
            return;
        _lastReflectionTestTrigger = testTrigger || _lastReflectionTestTrigger;
        NativeEventLog.Trace("memory.reflection.start", "mod", "system",
            "Lilith started a quiet reflection",
            "entries=" + _memory.CountAutonomousMemoriesReadOnly()
            + ";test=" + testTrigger
            + ";trigger=" + trigger
            + ";new_turns=" + newDialogueLines
            + ";source_fragments=" + pendingWindow.Count
            + ";selection_lane=" + (selection.Recent ? "recent" : "backlog")
            + ";resumed_pending=" + resumedPending
            + ";budget_limited=" + selection.BudgetLimited
            + ";available_new_turns=" + selection.AvailableDialogueLines);
    }

    [HideFromIl2Cpp]
    private void OnReflectionResult(ReflectionResult? result, ReflectionRequestSnapshot snapshot)
    {
        // True completion/failure earns the existing bounded cooldown. UI
        // activity and deferred steps never reset an already earned chance.
        _nextReflectionAt = Time.unscaledTime + EffectiveReflectionGapSeconds();
        _reflectionInProgress = false;
        if (result == null)
        {
            bool retained = _reflectionWindow.EndPass(null);
            NativeEventLog.Trace("memory.reflection.empty", "mod", "system",
                "reflection returned no usable result", "window_retained=" + retained);
            return;
        }
        bool saved = _memory.ApplyReflectionNote(result, snapshot, out string reason,
            out ReflectionChainOutcome chains);
        // The window is consumed only by a pass that actually processed it; a
        // malformed proposal or a failed save keeps the material for later.
        bool windowRetained = _memory.CompleteReflectionWindow(_reflectionWindow, reason, chains,
            out string cursorReason);
        NativeEventLog.Trace("memory.reflection.done", "mod", "lilith",
            "reflection reviewed",
            "saved=" + saved + ";reason=" + reason + ";cursor_reason=" + cursorReason + ";window_retained=" + windowRetained);
        if (windowRetained)
        {
            NativeEventLog.Trace("memory.reflection.retained", "mod", "system",
                "reflection window kept for a later pass", "reason=" + reason);
        }
        // One pass can propose up to three artifacts. The aggregate reason cannot
        // tell "no lesson was offered" from "the lesson was refused", so the three
        // chains are reported separately. Nothing here decides anything.
        NativeEventLog.Trace("memory.reflection.chains", "mod", "lilith",
            "reflection per-chain outcome", chains.Describe());
    }

    private void TryConsolidateDream()
    {
        if (SharedActivityRunning || SharedActivityUiBusy) return;
        if (_dayTaskInProgress
            || !RuntimeDirector.ShouldConsolidateDream(
                _config,
                _systemReady,
                _requestPending,
                _queuedReply != null,
                _listening,
                _aiSpeechActive,
                _memoryRecallInProgress,
                _storyRecallInProgress,
                NativeStateInspector.TimeSinceLastInteractionSeconds)
            || Time.unscaledTime < _nextDreamAt
            )
            return;
        if (NativeStateInspector.TimeSinceLastInteractionSeconds
            < _config.MemoryDreamMinIdleSeconds)
        {
            return;
        }
        DayTaskSnapshot? snapshot = _memory.CaptureDayTask();
        if (snapshot == null)
            return;
        _nextDreamAt = Time.unscaledTime + Math.Max(
            60f,
            _config.MemoryDreamIntervalSeconds);
        _dayTaskInProgress = true;
        BridgeConfig requestConfig = _config;
        NativeEventLog.Trace("memory.day_task.start", "mod", "system",
            "Lilith started organising the day",
            "window=" + snapshot.Evidence.Count);
        Task.Run(async () =>
        {
            try
            {
                DayTaskResult? result = await _client.RunDayTaskAsync(
                    requestConfig,
                    snapshot.Source,
                    System.Threading.CancellationToken.None).ConfigureAwait(false);
                _mainThread.Enqueue(() => OnDayTaskResult(result, snapshot));
            }
            catch (Exception error)
            {
                _mainThread.Enqueue(() =>
                {
                    _dayTaskInProgress = false;
                    NativeEventLog.Trace("memory.day_task.error", "mod", "system",
                        error.GetType().Name + ": " + error.Message);
                });
            }
        });
    }

    [HideFromIl2Cpp]
    private void OnDayTaskResult(DayTaskResult? result, DayTaskSnapshot snapshot)
    {
        _dayTaskInProgress = false;
        if (result == null)
        {
            bool fallback = _memory.CreateDreamForDay(DateTimeOffset.Now.Date);
            NativeEventLog.Trace("memory.day_task.empty", "mod", "system",
                "day pass returned nothing", "local_fallback=" + fallback);
            return;
        }
        bool saved = _memory.ApplyDayTask(result, snapshot, out string reason);
        NativeEventLog.Trace("memory.day_task.done", "mod", "lilith",
            "day pass reviewed", "saved=" + saved + ";reason=" + reason);
    }

    private static bool LooksMemoryWorthy(string userText, string replyText)
    {
        string source = (userText ?? "") + "\n" + (replyText ?? "");
        return Regex.IsMatch(
            source,
            @"(?:搬家|生日|换工作|毕业|考试|结婚|家人|爸爸|妈妈|宠物|养猫|养狗|约定|以后|计划|目标|梦想|重要|害怕|失眠|住院|手术|吃药|离开|回来|下周|下个月|明年|纪念|喜欢|讨厌)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private void TickDesktopArrange()
    {
        if (_desktopArrangeInspectPending)
        {
            _desktopArrangeInspectPending = false;
            DesktopArrangeResult inspect = DesktopArrangeService.Inspect(_config);
            NativeEventLog.Trace(
                inspect.Ok ? "desktop_arrange.inspect" : "desktop_arrange.inspect_error",
                "mod", "system",
                inspect.Ok ? "desktop icon read-only inspection" : "desktop icon inspection failed",
                inspect.Error.Length > 0
                    ? inspect.Error
                    : "icons=" + inspect.Before?.IconCount
                    + ";dry_run=" + inspect.DryRun);
            return;
        }

        if (_desktopArrangeProposePending
            && Time.unscaledTime - _desktopArrangeProposalAt
                > _config.DesktopArrangeProposalExpirySeconds)
        {
            _desktopArrangeProposePending = false;
            NativeEventLog.Trace("desktop_arrange.expired", "mod", "system",
                "desktop arrange proposal expired without confirmation");
        }

        if (_desktopArrangeExecutePending && DesktopArrangeAllowed())
        {
            _desktopArrangeExecutePending = false;
            TryRunDesktopArrange();
        }

        if (_desktopArrangeUndoPending && DesktopArrangeAllowed())
        {
            _desktopArrangeUndoPending = false;
            TryUndoDesktopArrange();
        }
    }

    private bool DesktopArrangeAllowed()
    {
        return _config.DesktopArrangeEnabled
            && !NativeStateInspector.IsSleeping
            && !NativeStateInspector.IsNativeQuietForMod()
            && !_listening
            && !_requestPending
            && _queuedReply == null
            && !_desktopArrangeOperationPending;
    }

    private void TryRunDesktopArrange()
    {
        _desktopArrangeOperationPending = true;
        BridgeConfig requestConfig = _config;
        Task.Run(() =>
        {
            try
            {
                DesktopArrangeResult result = DesktopArrangeService.Arrange(requestConfig);
                _mainThread.Enqueue(() => OnDesktopArrangeResult(result, false));
            }
            catch (Exception error)
            {
                _mainThread.Enqueue(() =>
                {
                    _desktopArrangeOperationPending = false;
                    NativeEventLog.Trace("desktop_arrange.error", "mod", "system",
                        error.GetType().Name + ": " + error.Message);
                });
            }
        });
    }

    private void TryUndoDesktopArrange()
    {
        if (_lastDesktopArrangeSnapshot == null)
        {
            NativeEventLog.Trace("desktop_arrange.undo_skip", "mod", "system",
                "no desktop arrange undo snapshot available");
            return;
        }
        _desktopArrangeOperationPending = true;
        DesktopArrangeSnapshot snapshot = _lastDesktopArrangeSnapshot;
        Task.Run(() =>
        {
            try
            {
                bool ok = DesktopArrangeService.Undo(snapshot, out string error);
                _mainThread.Enqueue(() => OnDesktopArrangeResult(
                    ok ? new DesktopArrangeResult { Ok = true } : new DesktopArrangeResult { Error = error },
                    true));
            }
            catch (Exception exception)
            {
                _mainThread.Enqueue(() =>
                {
                    _desktopArrangeOperationPending = false;
                    NativeEventLog.Trace("desktop_arrange.undo_error", "mod", "system",
                        exception.GetType().Name + ": " + exception.Message);
                });
            }
        });
    }

    [HideFromIl2Cpp]
    private void OnDesktopArrangeResult(DesktopArrangeResult result, bool undo)
    {
        _desktopArrangeOperationPending = false;
        if (undo)
        {
            if (!result.Ok)
            {
                NativeEventLog.Trace("desktop_arrange.undo_error", "mod", "system",
                    result.Error);
                return;
            }
            _lastDesktopArrangeSnapshot = null;
            NativeEventLog.Trace("desktop_arrange.undo_done", "mod", "system",
                "desktop icon layout restored");
            return;
        }

        if (!result.Ok)
        {
            NativeEventLog.Trace("desktop_arrange.error", "mod", "system", result.Error);
            return;
        }
        if (result.DryRun)
        {
            NativeEventLog.Trace("desktop_arrange.dry_run", "mod", "system",
                "desktop arrange dry run completed",
                "icons=" + result.Before?.IconCount
                + ";method=" + result.Method
                + ";targets=" + result.After?.Positions.Count);
            return;
        }
        _lastDesktopArrangeSnapshot = result.Before;
        NativeEventLog.Trace("desktop_arrange.done", "mod", "lilith",
            "desktop icons arranged",
            "icons=" + result.Before?.IconCount
            + ";targets=" + result.After?.Positions.Count
            + ";method=" + result.Method
            + ";detail=" + result.Detail
            + ";undo_available=" + (_lastDesktopArrangeSnapshot != null));
    }

    private void DrainMain()
    {
        while (_mainThread.TryDequeue(out Action? action))
        {
            try
            {
                action();
            }
            catch (Exception error)
            {
                Plugin.PluginLog.LogWarning(error.GetType().Name + ": " + error.Message);
            }
        }
    }

    private static string ResolveNativeSpeaker(DialogueNode node)
    {
        string speaker = node.speaker ?? "";
        if (speaker.IndexOf("lilith", StringComparison.OrdinalIgnoreCase) >= 0
            || speaker.IndexOf("\u8389\u8389\u4e1d", StringComparison.Ordinal) >= 0)
            return "lilith";
        if (!string.IsNullOrWhiteSpace(node.playerLineInteraction)
            || (node.playerLineOptions != null && node.playerLineOptions.Count > 0))
            return "player";
        if (!string.IsNullOrWhiteSpace(speaker))
            return speaker;
        // An empty speaker used to mean "game", which dropped her own opening
        // lines from history: "又见面啦~想我了吗？" was captured but never archived
        // in three sessions on 2026-09-24/25. Every empty-speaker node in that
        // telemetry was her line (4/4), and the game routes narration elsewhere,
        // so an unnamed speaker here is Lilith. Named speakers keep their name and
        // stay out of her history unless they are her or the player.
        return "lilith";
    }

    private static bool IsSnackDialogueNode(DialogueNode node)
    {
        if (node == null)
            return false;
        if (node.id >= 861000 && node.id < 862000)
            return true;
        string text = node.text ?? "";
        return text.Contains("蛋糕", StringComparison.Ordinal)
            || text.Contains("草莓", StringComparison.Ordinal)
            || text.Contains("奶油", StringComparison.Ordinal)
            || text.Contains("甜点", StringComparison.Ordinal)
            || text.Contains("红茶", StringComparison.Ordinal)
            || text.Contains("点心", StringComparison.Ordinal)
            || text.Contains("甜", StringComparison.Ordinal);
    }

    private static bool IsInterestingAction(LilithActionType action)
    {
        return action is not (
            LilithActionType.None or
            LilithActionType.Idle or
            LilithActionType.Blink or
            LilithActionType.Walk or
            LilithActionType.WalkEnd or
            LilithActionType.Drag or
            LilithActionType.Intro or
            LilithActionType.StandUp);
    }

    private static string DescribeAction(LilithActionType action)
    {
        return action switch
        {
            LilithActionType.RubHead => "head rub",
            LilithActionType.RubFace => "face rub",
            LilithActionType.RubEyes => "rubbing eyes",
            LilithActionType.BesidePat => "patting beside the player",
            LilithActionType.Blush => "blushing",
            LilithActionType.ShyGiggle => "shy giggle",
            LilithActionType.Flirt => "flirting",
            LilithActionType.HugAsk => "asking for a hug",
            LilithActionType.Pout => "pouting",
            LilithActionType.Yawn => "yawning",
            LilithActionType.SmallYawn => "small yawn",
            LilithActionType.PerkYawn => "perky yawn",
            LilithActionType.Stretch => "stretching",
            LilithActionType.SneezeTiny => "sneezing",
            LilithActionType.Sleep => "sleeping",
            LilithActionType.PillowLie => "lying down",
            LilithActionType.Drowsy => "drowsy",
            LilithActionType.DrowsySlump => "drowsy slump",
            LilithActionType.HappyHop => "happy hop",
            LilithActionType.MagicCast => "magic",
            LilithActionType.CakeUp => "cake up",
            LilithActionType.Music => "music",
            LilithActionType.LookAround => "looking around",
            LilithActionType.TiltHead => "head tilt",
            LilithActionType.Turnhead => "turning head",
            LilithActionType.ScreenPeek => "peeking at the screen",
            LilithActionType.SecretHide => "secret hide",
            LilithActionType.Hide => "hiding",
            LilithActionType.Think => "thinking",
            LilithActionType.Confuse => "confused",
            LilithActionType.FakeCry => "fake crying",
            LilithActionType.FakeAngry => "fake angry",
            LilithActionType.Tsundere => "tsundere",
            LilithActionType.Tense => "tense",
            LilithActionType.Snap => "snapping fingers",
            LilithActionType.Arrange => "arranging",
            LilithActionType.Clap => "clapping",
            LilithActionType.FakeWronged => "feeling wronged",
            _ => action.ToString()
        };
    }
}
