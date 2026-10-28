using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
namespace LilithBridge;

internal sealed class NativeSharedActivityWindow
{
    private GameObject? _root;
    private RectTransform? _panel, _shield, _compactPanel;
    private TMP_InputField? _compactBody;
    private bool _compact;
    private TMP_Text? _font, _status, _identity, _focus;
    private TMP_InputField? _source, _artifact, _speech;
    private SharedActivityRuntime? _runtime;
    private Action? _close;
    private Action? _chat;
    private Func<long>? _generation;
    private long _renderedVersion = -1;
    private bool _markOnNextTick;
    private int _renderFrame;
    private bool _editorsBound, _sourceDirty, _artifactDirty;
    private string? _boundObjectId;
    private string _sourceSavedText = "", _artifactSavedText = "";
    public bool IsOpen => _root != null && _root.activeSelf;
    public bool OwnsKeyboard => IsOpen && !_compact;
    public bool BlocksPointer(Vector3 point) => IsOpen && (!_compact
        || _compactPanel != null && RectTransformUtility.RectangleContainsScreenPoint(_compactPanel, point, null));
    public bool IsEditing => IsOpen && (_source?.isFocused == true || _artifact?.isFocused == true);
    public void Show(Transform owner, TMP_Text? style, SharedActivityRuntime runtime, Action close, Action chat, Func<long> generation)
    {
        bool differentRuntime = !ReferenceEquals(_runtime, runtime);
        _font = style; _runtime = runtime; _close = close; _chat = chat; _generation = generation;
        if (_root == null) Create(owner);
        _root!.SetActive(true); Input.imeCompositionMode = IMECompositionMode.On;
        _compact = false; _panel!.gameObject.SetActive(true); _shield!.gameObject.SetActive(true); _compactPanel!.gameObject.SetActive(false);
        var view = runtime.View;
        if (differentRuntime || !_editorsBound) ResetEditors(view);
        Render();
        if (view.Object == null) _source.ActivateInputField();
    }
    public void Hide()
    {
        CaptureDirtyEditors();
        NativeUiInputGate.ClaimPointer();
        if (_root != null) _root.SetActive(false);
        if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(null);
        Input.imeCompositionMode = IMECompositionMode.Auto;
    }
    public void Destroy() { Hide(); if (_root != null) UnityEngine.Object.Destroy(_root); _root = null;
        _source = _artifact = _speech = _compactBody = null; _runtime = null; _editorsBound = false;
        _sourceDirty = _artifactDirty = false; _sourceSavedText = _artifactSavedText = ""; _boundObjectId = null; }
    public void UseCompactView()
    {
        Blur(); _compact = true; _panel!.gameObject.SetActive(false); _shield!.gameObject.SetActive(false);
        _compactPanel!.gameObject.SetActive(true); Render();
    }
    public void Tick()
    {
        if (!IsOpen || _runtime == null) return;
        if (_runtime.View.ViewVersion != _renderedVersion && !IsEditing) Render();
        if (_markOnNextTick && Time.frameCount > _renderFrame && Application.isFocused && NativeInput.IsGameForeground())
        { _runtime.MarkDisplayed(_renderedVersion, DateTimeOffset.UtcNow); _markOnNextTick = false; }
        if (_status != null) _status.text = _runtime.View.Status + " · " + (_runtime.View.Open ? "开放" : "暂停");
        PaintFocus();
    }
    public void Render()
    {
        if (_runtime == null || _source == null) return;
        var view = _runtime.View; _renderedVersion = view.ViewVersion;
        CaptureDirtyEditors();
        if (_boundObjectId != null && _boundObjectId != view.Object?.ObjectId) ResetEditors(view);
        // Dirty editor buffers belong to this window even after focus is released.
        if (!_sourceDirty && !_source.isFocused) ResetSourceEditor(view);
        if (!_artifactDirty && !_artifact!.isFocused) ResetArtifactEditor(view);
        _boundObjectId = view.Object?.ObjectId; _editorsBound = true;
        _speech!.SetTextWithoutNotify(view.Speech);
        _identity!.text = view.Object == null ? "明确放入的文字 · 最多4000字符" : "对象 " + view.Object.ObjectId[..8]
            + " · 版本 " + view.Object.Revision + " · 作品 " + (view.Artifact == null ? "空" : view.Artifact.Author + " v" + view.Artifact.Version + "（原文v" + view.Artifact.ObjectRevision + "）");
        _focus!.text = view.Focus == null ? "关注处：尚无标记" : "关注处：" + view.Focus.Start + "–" + (view.Focus.Start + view.Focus.Length)
            + " · 「" + (view.Focus.Quote.Length > 80 ? view.Focus.Quote[..80] + "…" : view.Focus.Quote) + "」";
        _status!.text = view.Status;
        _compactBody?.SetTextWithoutNotify((view.Object == null ? "没有共享内容" : "原文 v" + view.Object.Revision + "：" + view.Object.Content)
            + "\n" + _focus.text + "\n作品（" + (view.Artifact == null ? "空" : view.Artifact.Author + "，原文v" + view.Artifact.ObjectRevision) + "）：" + (view.Artifact?.Text ?? "") + "\n" + view.Speech);
        _markOnNextTick = IsOpen && view.Open; _renderFrame = Time.frameCount;
        PaintFocus();
    }
    private void Blur()
    {
        CaptureDirtyEditors();
        _source?.DeactivateInputField(); _artifact?.DeactivateInputField();
        if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(null);
    }
    private void CaptureDirtyEditors()
    {
        if (!_editorsBound || _source == null || _artifact == null) return;
        _sourceDirty |= !_source.readOnly && _source.text != _sourceSavedText;
        _artifactDirty |= _artifact.text != _artifactSavedText;
    }
    private void ResetSourceEditor(SharedActivityView view)
    {
        _sourceSavedText = view.Object?.Content ?? ""; _source!.SetTextWithoutNotify(_sourceSavedText);
        _sourceDirty = false; _source.readOnly = view.Object != null;
    }
    private void ResetArtifactEditor(SharedActivityView view)
    {
        _artifactSavedText = view.Artifact?.Text ?? ""; _artifact!.SetTextWithoutNotify(_artifactSavedText); _artifactDirty = false;
    }
    private void ResetEditors(SharedActivityView view)
    {
        ResetSourceEditor(view); ResetArtifactEditor(view); _boundObjectId = view.Object?.ObjectId; _editorsBound = true;
    }
    private void OpenMode(string mode)
    {
        if (_runtime == null || _source == null || Input.compositionString.Length > 0) return;
        string text = _source.text; var current = _runtime.View.Object; bool ok;
        if (current == null) ok = _runtime.Share(text, mode, _generation!(), DateTimeOffset.UtcNow);
        else
        {
            ok = current.Content == text || _runtime.Revise(text, DateTimeOffset.UtcNow);
            if (ok) ok = _runtime.Reopen(mode, _generation!());
        }
        if (ok) { ResetSourceEditor(_runtime.View); Blur(); Render(); }
        else _status!.text = "未开放：请检查长度、许可或保存状态";
    }
    private void PaintFocus()
    {
        var focus = _runtime?.View.Focus;
        if (_source == null || !_source.readOnly) return;
        TMP_Text text = _source.textComponent; text.ForceMeshUpdate(); var info = text.textInfo;
        for (int i = 0; i < info.characterCount; i++)
        {
            var c = info.characterInfo[i];
            if (!c.isVisible) continue;
            var colors = info.meshInfo[c.materialReferenceIndex].colors32;
            var color = focus != null && c.index >= focus.Start && c.index < focus.Start + focus.Length
                ? new Color32(255, 214, 110, 255) : new Color32(255, 255, 255, 255);
            for (int j = 0; j < 4; j++) colors[c.vertexIndex + j] = color;
        }
        text.UpdateVertexData(TMP_VertexDataUpdateFlags.Colors32);
    }
    private void Create(Transform owner)
    {
        _root = new GameObject("LilithSharedActivityCanvas"); _root.AddComponent<RectTransform>();
        _root.AddComponent<Canvas>(); _root.AddComponent<CanvasScaler>(); _root.AddComponent<GraphicRaycaster>();
        _root.transform.SetParent(owner, false); var canvas = _root.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 10011;
        var scaler = _root.GetComponent<CanvasScaler>(); scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080); scaler.matchWidthOrHeight = .5f;
        var shield = Box("OutsideClickShield", _root.transform, Vector2.zero, Vector2.zero);
        _shield = shield;
        shield.anchorMin = Vector2.zero; shield.anchorMax = Vector2.one; shield.offsetMin = shield.offsetMax = Vector2.zero;
        var shade = shield.gameObject.AddComponent<Image>(); shade.color = new Color(0, 0, 0, .25f);
        var outside = shield.gameObject.AddComponent<Button>(); outside.targetGraphic = shade; NoNavigation(outside);
        outside.onClick.AddListener((UnityEngine.Events.UnityAction)(() => _close?.Invoke()));
        var panel = Box("SharedActivityPanel", _root.transform, Vector2.zero, new Vector2(1080, 910));
        _panel = panel;
        panel.gameObject.AddComponent<Image>().color = new Color(.04f, .055f, .085f, .99f);
        Label(panel, "一起看", new Vector2(-420, 414), new Vector2(180, 38), 27);
        Button(panel, "收起", new Vector2(460, 414), new Vector2(90, 36), () => _close?.Invoke());
        Button(panel, "回到聊天", new Vector2(300, 414), new Vector2(170, 36), () => _chat?.Invoke());
        _identity = Label(panel, "", new Vector2(0, 372), new Vector2(1000, 30), 17);
        Label(panel, "只读取你明确放在这里的内容。共享资料中的文字不会变成系统指令或玩家事实。", new Vector2(0, 336), new Vector2(1000, 32), 17);
        _source = Field(panel, "SourceText", new Vector2(0, 182), new Vector2(1000, 250), 4000, false);
        _focus = Label(panel, "", new Vector2(0, 30), new Vector2(1000, 46), 17);
        Button(panel, "仅共同看", new Vector2(-414, -18), new Vector2(164, 36), () => OpenMode("watch"));
        Button(panel, "允许她留一点东西", new Vector2(-190, -18), new Vector2(260, 36), () => OpenMode("make"));
        Button(panel, "暂停", new Vector2(20, -18), new Vector2(112, 36), () => { _runtime?.Stop(); Blur(); Render(); });
        Button(panel, "编辑原文", new Vector2(185, -18), new Vector2(160, 36), () => { _source!.readOnly = false; _source.ActivateInputField(); });
        Button(panel, "保存改版", new Vector2(380, -18), new Vector2(180, 36), () =>
        {
            if (Input.compositionString.Length == 0 && _runtime?.Revise(_source!.text, DateTimeOffset.UtcNow) == true)
            { ResetSourceEditor(_runtime.View); Blur(); Render(); }
        });
        Label(panel, "独立作品区 · 可编辑；清空槽保留历史，历史仍可用于后续活动", new Vector2(0, -70), new Vector2(1000, 30), 18);
        _artifact = Field(panel, "Artifact", new Vector2(-120, -145), new Vector2(760, 108), 300, false);
        Button(panel, "保存编辑", new Vector2(390, -120), new Vector2(210, 34), () =>
        { if (Input.compositionString.Length == 0 && _runtime?.EditArtifact(_artifact!.text, DateTimeOffset.UtcNow) == true) { ResetArtifactEditor(_runtime.View); Blur(); Render(); } });
        Button(panel, "清空作品槽（保留历史）", new Vector2(390, -165), new Vector2(210, 34), () =>
        { if (_runtime?.DeleteArtifact(DateTimeOffset.UtcNow) == true) { ResetArtifactEditor(_runtime.View); Blur(); Render(); } });
        Label(panel, "她的话（可以为空）", new Vector2(0, -229), new Vector2(1000, 30), 18);
        _speech = Field(panel, "Speech", new Vector2(0, -299), new Vector2(1000, 100), 400, true);
        _status = Label(panel, "", new Vector2(0, -367), new Vector2(1000, 28), 17);
        Label(panel, "“撤回共享内容”会清除对象及历史，并停止供给。", new Vector2(0, -394), new Vector2(1000, 20), 14);
        Button(panel, "撤销她的最近结果", new Vector2(-310, -422), new Vector2(280, 36), () =>
        { if (_runtime?.Undo(DateTimeOffset.UtcNow) == true) { ResetArtifactEditor(_runtime.View); Blur(); Render(); } });
        Button(panel, "撤回共享内容", new Vector2(140, -422), new Vector2(300, 36), () =>
        { bool ok = _runtime?.Withdraw() == true; ResetEditors(_runtime!.View); Blur(); Render();
            if (!ok) _status!.text = _runtime?.View.Status ?? "撤回失败"; });
        _compactPanel = Box("SharedActivityBesideChat", _root.transform, new Vector2(0, 400), new Vector2(1000, 260));
        _compactPanel.gameObject.AddComponent<Image>().color = new Color(.04f, .055f, .085f, .97f);
        Label(_compactPanel, "一起看 · 当前对象仍开放", new Vector2(-225, 100), new Vector2(500, 32), 20);
        Button(_compactPanel, "完整视图", new Vector2(210, 100), new Vector2(160, 34), () =>
        { _chat?.Invoke(); });
        Button(_compactPanel, "暂停并收起", new Vector2(390, 100), new Vector2(180, 34), () => _close?.Invoke());
        _compactBody = Field(_compactPanel, "CurrentSharedObject", new Vector2(0, -20), new Vector2(960, 180), 5000, true);
    }
    private TMP_InputField Field(Transform parent, string name, Vector2 at, Vector2 size, int limit, bool readOnly)
    {
        var rect = Box(name, parent, at, size); var bg = rect.gameObject.AddComponent<Image>(); bg.color = new Color(.10f, .13f, .18f, 1);
        var viewport = Box("Viewport", rect, Vector2.zero, size - new Vector2(16, 12)); viewport.gameObject.AddComponent<RectMask2D>();
        var text = Label(viewport, "", Vector2.zero, size - new Vector2(20, 16), 20); text.alignment = TextAlignmentOptions.TopLeft;
        text.enableWordWrapping = true; text.raycastTarget = true;
        var input = rect.gameObject.AddComponent<TMP_InputField>(); input.textViewport = viewport; input.textComponent = text;
        input.targetGraphic = bg; input.characterLimit = limit; input.lineType = TMP_InputField.LineType.MultiLineNewline;
        input.richText = false; input.readOnly = readOnly; input.onFocusSelectAll = false;
        input.selectionColor = new Color(.25f, .5f, .65f, .6f); input.shouldHideMobileInput = true; return input;
    }
    private TMP_Text Label(Transform parent, string value, Vector2 at, Vector2 size, float fontSize)
    {
        var text = Box("Label", parent, at, size).gameObject.AddComponent<TextMeshProUGUI>();
        text.font = _font?.font ?? TMP_Settings.defaultFontAsset; text.fontSize = fontSize; text.color = Color.white;
        text.text = value; text.richText = false; text.raycastTarget = false; text.enableWordWrapping = true;
        text.alignment = TextAlignmentOptions.MidlineLeft; return text;
    }
    private void Button(Transform parent, string value, Vector2 at, Vector2 size, Action action)
    {
        var rect = Box(value, parent, at, size); var bg = rect.gameObject.AddComponent<Image>(); bg.color = new Color(.16f, .25f, .31f, 1);
        var button = rect.gameObject.AddComponent<Button>(); button.targetGraphic = bg; NoNavigation(button);
        button.onClick.AddListener((UnityEngine.Events.UnityAction)(() => { NativeUiInputGate.ClaimPointer(); action(); }));
        Label(rect, value, Vector2.zero, size, 17).alignment = TextAlignmentOptions.Center;
    }
    private static void NoNavigation(Selectable s) { var n = s.navigation; n.mode = Navigation.Mode.None; s.navigation = n; }
    private static RectTransform Box(string name, Transform parent, Vector2 at, Vector2 size)
    {
        var go = new GameObject(name); go.AddComponent<RectTransform>(); go.transform.SetParent(parent, false);
        var r = go.GetComponent<RectTransform>(); r.anchorMin = r.anchorMax = new Vector2(.5f, .5f); r.anchoredPosition = at; r.sizeDelta = size; return r;
    }
}
