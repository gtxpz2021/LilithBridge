using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
namespace LilithBridge;

// Local creator control. Stored text is display-only and never interpreted as markup or dialogue.
internal sealed class NativeB1SelectionWindow
{
    private GameObject? _root;
    private RectTransform? _panel, _viewport;
    private TMP_Text? _style, _body, _status;
    private TMP_InputField? _reason;
    private ScrollRect? _scroll;
    private Button? _previousFamily, _nextFamily, _previousVersion, _nextVersion;
    private Button? _previousPage, _nextPage, _refresh, _preview, _back, _apply, _query;
    private B1SelectionSession? _session;
    private Action? _close;
    private float _nextTick;
    private string _shownBody = "", _failure = "";
    public bool IsOpen => _root != null && _root.activeSelf;

    public void Show(Transform owner, TMP_Text? style, B1SelectionSession session, Action close)
    {
        _style = style; _session = session; _close = close;
        if (_root == null) Create(owner);
        _root!.SetActive(true);
        if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(null);
        Input.imeCompositionMode = IMECompositionMode.On;
        _failure = "";
        Run(() => session.Open());
        _nextTick = Time.unscaledTime + 1f;
        FitPanel();
    }
    public void Hide()
    {
        // No implicit Apply, Back or Query on dismissal, including an unknown result.
        NativeUiInputGate.ClaimPointer();
        try { _session?.Close(); }
        catch (Exception error) { Log(error); }
        if (_root != null) _root.SetActive(false);
        if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(null);
        Input.imeCompositionMode = IMECompositionMode.Auto;
    }
    public void Destroy()
    {
        Hide();
        if (_root != null) UnityEngine.Object.Destroy(_root);
        _root = null;
    }
    public void Tick()
    {
        if (!IsOpen || _session == null) return;
        FitPanel();
        if (Time.unscaledTime < _nextTick) return;
        _nextTick = Time.unscaledTime + 1f;
        // The session revalidates permissions and stale preview only; no automatic selection.
        Run(() => _session.Tick(), false);
    }
    private void Run(Action action, bool clearFailure = true)
    {
        try
        {
            if (clearFailure) _failure = "";
            action();
        }
        catch (Exception error)
        {
            // A thrown Apply must never look successful or be retried automatically.
            _failure = "操作未能确认完成。请查看状态；若结果待确认，请查询原操作，不要重复应用。";
            Log(error);
        }
        Render();
    }
    private static void Log(Exception error) => Plugin.PluginLog.LogWarning("B1 selection UI: " + error.GetType().Name);
    private void Render()
    {
        if (_session == null || _body == null) return;
        string body = _session.Body;
        if (_shownBody != body)
        {
            _shownBody = body; _body.text = body;
            _body.ForceMeshUpdate();
            _body.rectTransform.sizeDelta = new Vector2(766, Mathf.Max(360, _body.preferredHeight + 24));
            _scroll!.verticalNormalizedPosition = 1;
        }
        _status!.text = _failure.Length == 0 ? _session.Status : _failure + "\n" + _session.Status;
        if (_reason!.text != _session.Reason) _reason.SetTextWithoutNotify(_session.Reason);
        bool navigate = _session.CanNavigate;
        _previousFamily!.interactable = _nextFamily!.interactable = navigate;
        _previousVersion!.interactable = _nextVersion!.interactable = navigate;
        _refresh!.interactable = navigate;
        _previousPage!.interactable = _session.CanPreviousPage;
        _nextPage!.interactable = _session.CanNextPage;
        _preview!.interactable = _session.CanPreview;
        _apply!.interactable = _session.CanApply && _failure.Length == 0;
        _query!.interactable = _session.CanQuery;
        _back!.interactable = _session.IsPreview && !_session.CanQuery;
        _reason.readOnly = !_session.CanEditReason;
    }
    private void Create(Transform owner)
    {
        _root = new GameObject("LilithB1SelectionCanvas");
        _root.AddComponent<RectTransform>(); _root.AddComponent<Canvas>();
        _root.AddComponent<CanvasScaler>(); _root.AddComponent<GraphicRaycaster>();
        _root.transform.SetParent(owner, false);
        var canvas = _root.GetComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 10012;
        var scaler = _root.GetComponent<CanvasScaler>(); scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080); scaler.matchWidthOrHeight = .5f;
        var viewportRoot = NativeUiViewport.Create(canvas); _viewport = viewportRoot.GetComponent<RectTransform>();
        var shield = Box("ModalPointerShield", viewportRoot, Vector2.zero, Vector2.zero);
        shield.anchorMin = Vector2.zero; shield.anchorMax = Vector2.one;
        shield.offsetMin = shield.offsetMax = Vector2.zero;
        shield.gameObject.AddComponent<Image>().color = new Color(0, 0, 0, .25f);
        _panel = Box("B1SelectionPanel", viewportRoot, Vector2.zero, new Vector2(850, 780));
        NativeMenuStyle.Paint(_panel.gameObject.AddComponent<Image>(), new Color(.04f, .055f, .085f, .99f));
        Label(_panel, "已存判断版本", new Vector2(-235, 354), new Vector2(320, 36), 25);
        MakeButton(_panel, "复制操作号", new Vector2(155, 354), new Vector2(160, 36), () =>
        { if (_session!.OperationId.Length > 0) GUIUtility.systemCopyBuffer = _session.OperationId; });
        MakeButton(_panel, "返回聊天", new Vector2(326, 354), new Vector2(130, 36), () => _close?.Invoke());
        Label(_panel, "浏览和关闭不会应用；本次不发起请求，理由会随以后请求提供。", new Vector2(0, 313), new Vector2(790, 32), 16);
        _previousFamily = MakeButton(_panel, "上一组", new Vector2(-337, 268), new Vector2(110, 34), () => _session!.MoveFamily(-1));
        _nextFamily = MakeButton(_panel, "下一组", new Vector2(-215, 268), new Vector2(110, 34), () => _session!.MoveFamily(1));
        _previousVersion = MakeButton(_panel, "上一版本", new Vector2(-65, 268), new Vector2(132, 34), () => _session!.MoveVersion(-1));
        _nextVersion = MakeButton(_panel, "下一版本", new Vector2(82, 268), new Vector2(132, 34), () => _session!.MoveVersion(1));
        _refresh = MakeButton(_panel, "刷新列表", new Vector2(307, 268), new Vector2(170, 34), () => _session!.Refresh());
        var area = Box("SavedVersionScroll", _panel, new Vector2(0, 54), new Vector2(790, 374));
        area.gameObject.AddComponent<Image>().color = new Color(.06f, .075f, .11f, 1);
        var clip = Box("Viewport", area, new Vector2(-6, 0), new Vector2(770, 366));
        clip.gameObject.AddComponent<RectMask2D>();
        var bodyRect = Box("ReadOnlyContent", clip, Vector2.zero, new Vector2(766, 360));
        bodyRect.anchorMin = bodyRect.anchorMax = new Vector2(.5f, 1); bodyRect.pivot = new Vector2(.5f, 1);
        _body = bodyRect.gameObject.AddComponent<TextMeshProUGUI>(); Style(_body, 19);
        _body.overflowMode = TextOverflowModes.Overflow;
        _body.alignment = TextAlignmentOptions.TopLeft; _body.enableWordWrapping = true; _body.raycastTarget = true;
        _scroll = area.gameObject.AddComponent<ScrollRect>(); _scroll.viewport = clip; _scroll.content = bodyRect;
        _scroll.horizontal = false; _scroll.vertical = true; _scroll.movementType = ScrollRect.MovementType.Clamped; _scroll.scrollSensitivity = 35;
        var rail = Box("ScrollRail", area, new Vector2(386, 0), new Vector2(10, 360));
        rail.gameObject.AddComponent<Image>().color = new Color(.11f, .14f, .19f, 1);
        var handle = Box("Handle", rail, Vector2.zero, Vector2.zero);
        handle.anchorMin = Vector2.zero; handle.anchorMax = Vector2.one; handle.offsetMin = handle.offsetMax = Vector2.zero;
        var image = handle.gameObject.AddComponent<Image>(); image.color = new Color(.38f, .48f, .56f, 1);
        var bar = rail.gameObject.AddComponent<Scrollbar>(); bar.handleRect = handle; bar.targetGraphic = image;
        bar.direction = Scrollbar.Direction.BottomToTop; NoNavigation(bar); _scroll.verticalScrollbar = bar;
        _previousPage = MakeButton(_panel, "上一页", new Vector2(-332, -157), new Vector2(120, 32), () => _session!.PreviousPage());
        _nextPage = MakeButton(_panel, "下一页", new Vector2(-199, -157), new Vector2(120, 32), () => _session!.NextPage());
        Label(_panel, "选择理由（最多 500 字符，保留草稿）", new Vector2(138, -157), new Vector2(490, 30), 16);
        var reasonRect = Box("SelectionReason", _panel, new Vector2(0, -216), new Vector2(790, 70));
        var reasonBg = reasonRect.gameObject.AddComponent<Image>(); reasonBg.color = new Color(.10f, .13f, .18f, 1);
        var reasonViewport = Box("Viewport", reasonRect, Vector2.zero, new Vector2(770, 62));
        reasonViewport.gameObject.AddComponent<RectMask2D>();
        var reasonText = Label(reasonViewport, "", Vector2.zero, new Vector2(766, 60), 18);
        reasonText.alignment = TextAlignmentOptions.TopLeft; reasonText.raycastTarget = true;
        _reason = reasonRect.gameObject.AddComponent<TMP_InputField>();
        _reason.textViewport = reasonViewport; _reason.textComponent = reasonText; _reason.targetGraphic = reasonBg;
        _reason.characterLimit = 500; _reason.lineType = TMP_InputField.LineType.MultiLineNewline;
        _reason.richText = false; _reason.onFocusSelectAll = false; _reason.shouldHideMobileInput = true;
        _reason.onSubmit.RemoveAllListeners(); _reason.onEndEdit.RemoveAllListeners();
        _reason.onValueChanged.AddListener((UnityEngine.Events.UnityAction<string>)(value => Run(() => _session!.SetReason(value))));
        _preview = MakeButton(_panel, "预览选择", new Vector2(-315, -278), new Vector2(152, 36), () => _session!.Preview());
        _back = MakeButton(_panel, "返回修改", new Vector2(-145, -278), new Vector2(152, 36), () => _session!.Back());
        _apply = MakeButton(_panel, "明确应用", new Vector2(25, -278), new Vector2(152, 36), () => _session!.Apply());
        _query = MakeButton(_panel, "查询原操作结果", new Vector2(261, -278), new Vector2(260, 36), () => _session!.Query());
        _status = Label(_panel, "", new Vector2(0, -338), new Vector2(790, 76), 16);
    }
    private Button MakeButton(Transform parent, string value, Vector2 at, Vector2 size, Action action)
    {
        var rect = Box(value, parent, at, size); var image = rect.gameObject.AddComponent<Image>();
        NativeMenuStyle.Paint(image, new Color(.16f, .25f, .31f, 1));
        var button = rect.gameObject.AddComponent<Button>(); button.targetGraphic = image; NoNavigation(button);
        button.onClick.AddListener((UnityEngine.Events.UnityAction)(() =>
        {
            // Navigation.None does not suppress Button.OnSubmit. Require an actual pointer release,
            // inside this control, and reject Enter/IME even when a button retains EventSystem focus.
            if (!IsOpen || !button.interactable || !Input.GetMouseButtonUp(0)
                || !RectTransformUtility.RectangleContainsScreenPoint(rect, Input.mousePosition, null)
                || !string.IsNullOrEmpty(Input.compositionString)
                || NativeInput.IsKeyDown(KeyCode.Return) || NativeInput.IsKeyDown(KeyCode.KeypadEnter)) return;
            NativeUiInputGate.ClaimPointer();
            Run(action);
            if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(null);
        }));
        Label(rect, value, Vector2.zero, size, 17).alignment = TextAlignmentOptions.Center;
        return button;
    }
    private void FitPanel()
    {
        if (_panel == null || _viewport == null) return;
        _panel.localScale = Vector3.one * Mathf.Clamp(Mathf.Min((_viewport.rect.width - 32) / 850f,
            (_viewport.rect.height - 32) / 780f), .2f, 1f);
    }
    private void Style(TMP_Text text, float size)
    {
        text.font = _style?.font ?? TMP_Settings.defaultFontAsset; text.fontSize = size; text.color = Color.white;
        text.richText = false; text.raycastTarget = false; text.enableWordWrapping = true;
        text.alignment = TextAlignmentOptions.MidlineLeft;
    }
    private TMP_Text Label(Transform parent, string value, Vector2 at, Vector2 size, float fontSize)
    {
        var text = Box("Label", parent, at, size).gameObject.AddComponent<TextMeshProUGUI>();
        Style(text, fontSize); text.text = value; return text;
    }
    private static void NoNavigation(Selectable selectable)
    { var navigation = selectable.navigation; navigation.mode = Navigation.Mode.None; selectable.navigation = navigation; }
    private static RectTransform Box(string name, Transform parent, Vector2 at, Vector2 size)
    {
        var go = new GameObject(name); go.AddComponent<RectTransform>(); go.transform.SetParent(parent, false);
        var rect = go.GetComponent<RectTransform>(); rect.anchorMin = rect.anchorMax = new Vector2(.5f, .5f);
        rect.anchoredPosition = at; rect.sizeDelta = size; return rect;
    }
}
