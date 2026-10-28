using System;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace LilithBridge;

/// <summary>
/// A small, in-game TMP input clone. It is not an OS window; it inherits the
/// game's own font, input path and IME handling where possible.
/// </summary>
internal sealed partial class NativeInputField
{
    private GameObject? _root;
    private TMP_InputField? _input;
    private Action<string>? _onSubmit;
    private Action? _onCancel;
    private bool _wantsFocus;
    private int _characterLimit = 1000;
    private bool _multiLine = true;
    private string _lastLayoutText = "";
    private Action<string>? _onModeSelected;
    private NativeOptionsMenu? _menu;
    private GameObject? _canvasRoot;
    private string? _modeError;
    private Action? _onOpenHistory;
    private Action? _onOpenSharedActivity;
    private Action? _onAuthorizeReview;
    private bool _reviewAuthorized;
    private TMP_Text? _failureNotice;
    private string? _pendingFailureDraft, _pendingFailureNotice;
    public void StageTransportFailure(string draft,bool preserveCurrentDraft)
        => StageReplyFailure(draft, preserveCurrentDraft, TransportFailurePresentation.Notice);
    public void StageReplyFailure(string draft, bool preserveCurrentDraft, string notice)
    {
        if(preserveCurrentDraft) { if(_failureNotice!=null)_failureNotice.text=notice;return; }
        _pendingFailureDraft=draft;_pendingFailureNotice=notice;
    }
    public TMP_Text? StyleText => _input?.textComponent;
    public void SuspendForHistory(bool suspended) { _menu?.Close(false); _wantsFocus=!suspended; if (_root != null) _root.SetActive(!suspended); }
    public bool UiConsumesKeyboard => _menu?.ConsumesKeyboard == true;
    public bool IsMenuOpen => _menu?.IsOpen == true;
    public void CloseMenu() => _menu?.Close();
    public bool ContainsPointer(Vector3 point) => IsOpen && (RectTransformUtility.RectangleContainsScreenPoint(_root!.GetComponent<RectTransform>(),point,null) || _menu?.ContainsPointer(point)==true
        || (_failureNotice!=null && _failureNotice.text.Length>0 && RectTransformUtility.RectangleContainsScreenPoint(_failureNotice.rectTransform,point,null)));

    public bool IsOpen => _root != null && _root.activeSelf;
    public string Text => _input?.text ?? "";

    public void EnsureFocus()
    {
        if (!_wantsFocus || _input == null || _root == null || !_root.activeSelf)
            return;
        if (!_input.isFocused)
            Activate();
    }

    public bool Show(
        Transform owner,
        BridgeConfig config,
        Action<string> onSubmit,
        Action onCancel,
        Action<string>? onModeSelected = null,
        Action? onOpenHistory = null,
        Action? onAuthorizeReview = null,
        Action? onOpenSharedActivity = null)
    {
        _characterLimit = config.InputCharacterLimit;
        _multiLine = config.InputMultiLine;
        EnsureCreated(owner);
        if (_root == null || _input == null)
            return false;
        _input!.text = _pendingFailureDraft ?? ""; _pendingFailureDraft=null;
        if(_failureNotice!=null)_failureNotice.text=_pendingFailureNotice ?? "";_pendingFailureNotice=null;
        _onModeSelected = onModeSelected;
        _onOpenHistory = onOpenHistory;
        _onOpenSharedActivity = onOpenSharedActivity;
        _onAuthorizeReview = onAuthorizeReview;
        _reviewAuthorized = false;
        _modeError = null;
        RefreshThinkingMode(config);
        _lastLayoutText = "";
        ApplyLayout();
        if (_input != null)
        {
            _input.characterLimit = _characterLimit;
            _input.lineType = _multiLine
                ? TMP_InputField.LineType.MultiLineSubmit
                : TMP_InputField.LineType.SingleLine;
            if (_input.textComponent != null)
                _input.textComponent.enableWordWrapping = _multiLine;
        }
        _onSubmit = onSubmit;
        _onCancel = onCancel;
        _root.SetActive(true);
        _wantsFocus = true;
        Activate();
        UpdateDynamicHeight();
        return true;
    }

    public void Hide()
    {
        if (_root == null)
            return;
        if (EventSystem.current != null)
            EventSystem.current.SetSelectedGameObject(null);
        _menu?.Close(false);
        _root.SetActive(false);
        _wantsFocus = false;
        Input.imeCompositionMode = IMECompositionMode.Auto;
    }

    public void Destroy()
    {
        if (_canvasRoot != null)
            UnityEngine.Object.Destroy(_canvasRoot);
        _root = null;
        _input = null;
        _menu = null; _canvasRoot = null;
        _failureNotice=null;_pendingFailureDraft=null;_pendingFailureNotice=null;
        _onModeSelected = null;
        _onAuthorizeReview = null;
    }

    public void RefreshLayout()
    {
        if (_input == null)
            return;
        string current = _input.text;
        if (current == _lastLayoutText)
            return;
        _lastLayoutText = current;
        UpdateDynamicHeight();
    }

    public bool CancelCurrent()
    {
        if (_input == null || _root == null || !_root.activeSelf)
            return false;
        Hide();
        Action? callback = _onCancel;
        _onSubmit = null;
        _onCancel = null;
        callback?.Invoke();
        return true;
    }

    private void EnsureCreated(Transform owner)
    {
        if (_input != null)
            return;
        try
        {
            TMP_InputField? source = FindSourceInputField();
            TMP_Text? sourceText = source != null ? source.textComponent : null;

            var canvasGo = new GameObject("LilithNativeInputCanvas");
            canvasGo.AddComponent<RectTransform>();
            var canvasRect = canvasGo.GetComponent<RectTransform>();
            canvasRect.anchorMin = Vector2.zero;
            canvasRect.anchorMax = Vector2.one;
            canvasRect.offsetMin = Vector2.zero;
            canvasRect.offsetMax = Vector2.zero;
            canvasGo.AddComponent<Canvas>();
            canvasGo.AddComponent<CanvasScaler>();
            canvasGo.AddComponent<GraphicRaycaster>();
            canvasGo.transform.SetParent(owner, false);
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 10000;
            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            var fieldGo = new GameObject("LilithNativeInput");
            fieldGo.AddComponent<RectTransform>();
            fieldGo.AddComponent<CanvasRenderer>();
            fieldGo.transform.SetParent(canvasGo.transform, false);
            var fieldRect = fieldGo.GetComponent<RectTransform>();
            fieldRect.anchorMin = new Vector2(0.5f, 0.04f);
            fieldRect.anchorMax = new Vector2(0.5f, 0.04f);
            fieldRect.pivot = new Vector2(0.5f, 0.5f);
            fieldRect.anchoredPosition = new Vector2(0f, 18f);
            fieldRect.sizeDelta = new Vector2(430f, 54f);

            var background = fieldGo.AddComponent<Image>();
            background.color = new Color(0.05f, 0.06f, 0.10f, 0.94f);

            var viewportGo = new GameObject("Viewport");
            viewportGo.AddComponent<RectTransform>();
            viewportGo.AddComponent<RectMask2D>();
            viewportGo.transform.SetParent(fieldGo.transform, false);
            var viewportRect = viewportGo.GetComponent<RectTransform>();
            viewportRect.anchorMin = new Vector2(0.05f, 0.1f);
            viewportRect.anchorMax = new Vector2(0.95f, 0.9f);
            viewportRect.offsetMin = Vector2.zero;
            viewportRect.offsetMax = Vector2.zero;

            var textGo = new GameObject("Text");
            textGo.AddComponent<RectTransform>();
            textGo.AddComponent<CanvasRenderer>();
            textGo.transform.SetParent(viewportGo.transform, false);
            var text = textGo.AddComponent<TextMeshProUGUI>();
            text.rectTransform.anchorMin = Vector2.zero;
            text.rectTransform.anchorMax = Vector2.one;
            text.rectTransform.offsetMin = Vector2.zero;
            text.rectTransform.offsetMax = Vector2.zero;
            text.raycastTarget = false;
            if (sourceText != null)
            {
                text.font = sourceText.font;
                text.fontStyle = sourceText.fontStyle;
                text.fontSize = sourceText.fontSize;
                text.color = sourceText.color;
                text.alignment = sourceText.alignment;
            }
            else
            {
                text.fontSize = 24f;
                if (text.font == null)
                    text.font = FindFont();
                text.color = Color.white;
                text.alignment = TextAlignmentOptions.Left;
            }
            text.enableWordWrapping = false;
            text.overflowMode = TextOverflowModes.Overflow;

            var placeholderGo = new GameObject("Placeholder");
            placeholderGo.AddComponent<RectTransform>();
            placeholderGo.AddComponent<CanvasRenderer>();
            placeholderGo.transform.SetParent(viewportGo.transform, false);
            var placeholder = placeholderGo.AddComponent<TextMeshProUGUI>();
            placeholder.rectTransform.anchorMin = Vector2.zero;
            placeholder.rectTransform.anchorMax = Vector2.one;
            placeholder.rectTransform.offsetMin = Vector2.zero;
            placeholder.rectTransform.offsetMax = Vector2.zero;
            placeholder.raycastTarget = false;
            placeholder.text = "说点什么…";
            placeholder.color = new Color(1f, 1f, 1f, 0.5f);
            if (sourceText != null)
            {
                placeholder.font = sourceText.font;
                placeholder.fontSize = sourceText.fontSize;
                placeholder.alignment = sourceText.alignment;
            }
            else
            {
                placeholder.fontSize = 24f;
                if (placeholder.font == null)
                    placeholder.font = FindFont();
                placeholder.alignment = TextAlignmentOptions.Left;
            }
            placeholder.enableWordWrapping = false;
            placeholder.overflowMode = TextOverflowModes.Overflow;

            var input = fieldGo.AddComponent<TMP_InputField>();
            input.textViewport = viewportRect;
            input.textComponent = text;
            input.placeholder = placeholder;
            input.targetGraphic = background;
            input.lineType = _multiLine
                ? TMP_InputField.LineType.MultiLineSubmit
                : TMP_InputField.LineType.SingleLine;
            input.contentType = TMP_InputField.ContentType.Standard;
            input.characterLimit = _characterLimit;
            input.readOnly = false;
            input.onFocusSelectAll = false;
            input.shouldHideMobileInput = true;
            input.onSubmit.RemoveAllListeners();
            input.onEndEdit.RemoveAllListeners();
            input.onValueChanged.RemoveAllListeners();
            input.onSelect.RemoveAllListeners();
            input.onDeselect.RemoveAllListeners();

            _root = fieldGo;
            _input = input;
            _canvasRoot = canvasGo;
            var failureGo=new GameObject("TransportFailureNotice");failureGo.AddComponent<RectTransform>();failureGo.transform.SetParent(fieldGo.transform,false);
            _failureNotice=failureGo.AddComponent<TextMeshProUGUI>();_failureNotice.font=text.font;_failureNotice.fontSize=16;_failureNotice.color=new Color(1,.72f,.60f,1);
            _failureNotice.richText=false;_failureNotice.raycastTarget=false;_failureNotice.alignment=TextAlignmentOptions.Center;
            var failureRect=_failureNotice.rectTransform;failureRect.anchorMin=failureRect.anchorMax=new Vector2(.5f,1);failureRect.pivot=new Vector2(.5f,0);failureRect.anchoredPosition=new Vector2(0,6);failureRect.sizeDelta=new Vector2(430,28);
            _menu = new NativeOptionsMenu(fieldGo.transform,canvasGo.transform,text,
                mode=>_onModeSelected?.Invoke(mode),()=>_onOpenHistory?.Invoke(),
                owns=>{_wantsFocus=!owns;if(owns)_input?.DeactivateInputField();else Activate();},
                ()=>AuthorizeReviewFromMenu(), () => _onOpenSharedActivity?.Invoke());
        }
        catch (Exception error)
        {
            Plugin.PluginLog.LogWarning("Native input creation failed: " + error.Message);
        }
    }

    public void RefreshThinkingMode(BridgeConfig config, string? error = null)
    {
        if (error != null) _modeError = error.Length == 0 ? null : error;
        _menu?.Refresh(config,_modeError);
        _menu?.RefreshReview(_reviewAuthorized);
    }

    private void AuthorizeReviewFromMenu()
    {
        _reviewAuthorized = true;
        _menu?.RefreshReview(true);
        _onAuthorizeReview?.Invoke();
    }

    public void RefreshReviewAuthorization(bool authorized)
    {
        _reviewAuthorized = authorized;
        _menu?.RefreshReview(authorized);
    }

    private void ApplyLayout()
    {
        UpdateDynamicHeight();
    }

    private void UpdateDynamicHeight()
    {
        if (_input == null || _root == null)
            return;
        float height = 54f;
        if (_multiLine && _input.textComponent != null)
        {
            _input.textComponent.ForceMeshUpdate();
            float preferred = _input.textComponent.preferredHeight;
            height = Mathf.Clamp(preferred + 16f, 54f, 200f);
        }
        RectTransform? rect = _root.GetComponent<RectTransform>();
        if (rect != null)
            rect.sizeDelta = new Vector2(430f, height);
        _menu?.RefreshLayout();
    }

    private void Activate()
    {
        if (_input == null)
            return;
        _input.ActivateInputField();
        if (EventSystem.current != null)
            EventSystem.current.SetSelectedGameObject(_input.gameObject);
        Input.imeCompositionMode = IMECompositionMode.On;
    }

    private static TMP_InputField? FindSourceInputField()
    {
        try
        {
            var fields = Resources.FindObjectsOfTypeAll<TMP_InputField>();
            if (fields.Length == 0)
                return null;
            foreach (TMP_InputField field in fields)
            {
                if (ParentPath(field.transform).Contains("/Naming/", StringComparison.OrdinalIgnoreCase))
                    return field;
            }
            foreach (TMP_InputField field in fields)
            {
                string path = ParentPath(field.transform);
                if (path.IndexOf("InputField", StringComparison.OrdinalIgnoreCase) >= 0
                    && path.IndexOf("Lilith", StringComparison.OrdinalIgnoreCase) >= 0)
                    return field;
            }
            return fields[0];
        }
        catch
        {
            return null;
        }
    }

    private static TMP_FontAsset? FindFont()
    {
        try
        {
            var texts = Resources.FindObjectsOfTypeAll<TMP_Text>();
            if (texts.Length == 0)
                return null;
            foreach (TMP_Text text in texts)
            {
                if (text.font != null)
                    return text.font;
            }
        }
        catch
        {
            // Fall through.
        }
        return TMP_Settings.defaultFontAsset;
    }

    private static string ParentPath(Transform transform)
    {
        var current = transform;
        string path = "";
        while (current != null)
        {
            path = "/" + current.name + path;
            current = current.parent;
        }
        return path;
    }
}
