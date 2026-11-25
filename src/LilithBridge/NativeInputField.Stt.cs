using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace LilithBridge;

internal sealed partial class NativeInputField
{
    private RectTransform? _sttButtonRect;
    private Image? _sttButtonImage;
    private TMP_Text? _sttButtonLabel;
    private TMP_Text? _sttNotice;
    private Action? _onToggleStt;
    private SttButtonVisualState _sttVisualState = SttButtonVisualState.Hidden;
    private string _sttStatusText = "";
    private bool _dictationReadOnly;

    private bool HasVisibleSttNotice => _sttNotice != null && _sttNotice.gameObject.activeSelf && _sttNotice.text.Length > 0;

    internal bool ContainsSttPointer(Vector3 point) =>
        _sttButtonRect != null
        && _sttButtonRect.gameObject.activeSelf
        && RectTransformUtility.RectangleContainsScreenPoint(_sttButtonRect, point, null);

    private void CreateSttControls(GameObject fieldGo, TMP_Text styleSource)
    {
        var btnGo = new GameObject("SttToggle");
        btnGo.AddComponent<RectTransform>();
        btnGo.transform.SetParent(fieldGo.transform, false);
        var btnRect = btnGo.GetComponent<RectTransform>();
        btnRect.anchorMin = btnRect.anchorMax = new Vector2(0.5f, 0.5f);
        btnRect.anchoredPosition = new Vector2(-241f, 0f);
        btnRect.sizeDelta = new Vector2(38f, 36f);

        var img = btnGo.AddComponent<Image>();
        NativeMenuStyle.Paint(img, new Color(0.11f, 0.17f, 0.22f, 1f));
        var btn = btnGo.AddComponent<Button>();
        btn.targetGraphic = img;
        var nav = btn.navigation;
        nav.mode = Navigation.Mode.None;
        btn.navigation = nav;
        var colors = btn.colors;
        colors.normalColor = Color.white;
        colors.highlightedColor = new Color(0.84f, 0.95f, 1f, 1f);
        colors.pressedColor = new Color(0.65f, 0.84f, 0.88f, 1f);
        colors.selectedColor = Color.white;
        colors.fadeDuration = 0.08f;
        btn.colors = colors;
        btn.onClick.AddListener((UnityEngine.Events.UnityAction)(() =>
        {
            NativeUiInputGate.ClaimPointer();
            _onToggleStt?.Invoke();
        }));

        var labelGo = new GameObject("Label");
        labelGo.AddComponent<RectTransform>();
        labelGo.transform.SetParent(btnGo.transform, false);
        var label = labelGo.AddComponent<TextMeshProUGUI>();
        label.font = styleSource.font;
        label.fontSize = 18f;
        label.text = "语";
        label.color = new Color(0.90f, 0.94f, 0.96f, 1f);
        label.richText = false;
        label.raycastTarget = false;
        label.enableWordWrapping = false;
        label.alignment = TextAlignmentOptions.Center;
        var labelRect = label.rectTransform;
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = Vector2.zero;
        labelRect.offsetMax = Vector2.zero;

        var noticeGo = new GameObject("SttStatusNotice");
        noticeGo.AddComponent<RectTransform>();
        noticeGo.transform.SetParent(fieldGo.transform, false);
        var notice = noticeGo.AddComponent<TextMeshProUGUI>();
        notice.font = styleSource.font;
        notice.fontSize = 15f;
        notice.color = new Color(0.76f, 0.91f, 0.96f, 0.96f);
        notice.richText = false;
        notice.raycastTarget = false;
        notice.enableWordWrapping = false;
        notice.alignment = TextAlignmentOptions.Center;
        var noticeRect = notice.rectTransform;
        noticeRect.anchorMin = noticeRect.anchorMax = new Vector2(0.5f, 1f);
        noticeRect.pivot = new Vector2(0.5f, 0f);
        noticeRect.anchoredPosition = new Vector2(0f, 8f);
        noticeRect.sizeDelta = new Vector2(560f, 28f);

        _sttButtonRect = btnRect;
        _sttButtonImage = img;
        _sttButtonLabel = label;
        _sttNotice = notice;
        ApplySttVisual();
    }

    private void DestroySttControls()
    {
        _sttButtonRect = null;
        _sttButtonImage = null;
        _sttButtonLabel = null;
        _sttNotice = null;
        _onToggleStt = null;
        _sttVisualState = SttButtonVisualState.Hidden;
        _sttStatusText = "";
        _dictationReadOnly = false;
    }

    public void BindSttToggle(Action? onToggleStt)
    {
        _onToggleStt = onToggleStt;
    }

    public void RefreshSttVisual(SttButtonVisualState state, string statusText = "", float level = 0f)
    {
        _sttVisualState = state;
        _sttStatusText = statusText ?? "";
        ApplySttVisual(level);
    }

    public void SetDictationReadOnly(bool readOnly)
    {
        _dictationReadOnly = readOnly;
        if (_input != null)
            _input.readOnly = readOnly;
    }

    public void SetDictationText(string text, bool restoreEditableFocus)
    {
        if (_input == null)
            return;
        string value = text ?? "";
        if (restoreEditableFocus)
        {
            _dictationReadOnly = false;
            _input.readOnly = false;
        }
        _input.SetTextWithoutNotify(value);
        int end = value.Length;
        _input.caretPosition = end;
        _input.stringPosition = end;
        _input.selectionAnchorPosition = end;
        _input.selectionFocusPosition = end;
        _layoutDirty = true;
        RefreshLayout();
        if (restoreEditableFocus && IsOpen && !UiConsumesKeyboard)
            Activate();
    }

    private void ApplySttVisual(float level = 0f)
    {
        if (_sttButtonRect != null)
            _sttButtonRect.gameObject.SetActive(_sttVisualState != SttButtonVisualState.Hidden);

        if (_sttButtonImage != null && _sttButtonLabel != null)
        {
            switch (_sttVisualState)
            {
                case SttButtonVisualState.Recording:
                {
                    float pulse = Mathf.Clamp01(level) * 0.16f;
                    _sttButtonImage.color = new Color(0.68f + pulse, 0.20f + pulse * 0.35f, 0.24f + pulse * 0.35f, 1f);
                    _sttButtonLabel.text = "停";
                    _sttButtonLabel.color = Color.white;
                    break;
                }
                case SttButtonVisualState.Finalizing:
                    _sttButtonImage.color = new Color(0.16f, 0.38f, 0.44f, 1f);
                    _sttButtonLabel.text = "…";
                    _sttButtonLabel.color = new Color(0.92f, 0.97f, 1f, 1f);
                    break;
                case SttButtonVisualState.Initializing:
                    _sttButtonImage.color = new Color(0.11f, 0.17f, 0.22f, 0.78f);
                    _sttButtonLabel.text = "…";
                    _sttButtonLabel.color = new Color(0.68f, 0.78f, 0.84f, 1f);
                    break;
                case SttButtonVisualState.Unavailable:
                    _sttButtonImage.color = new Color(0.22f, 0.19f, 0.16f, 0.88f);
                    _sttButtonLabel.text = "语";
                    _sttButtonLabel.color = new Color(0.86f, 0.68f, 0.56f, 1f);
                    break;
                default:
                    _sttButtonImage.color = new Color(0.11f, 0.17f, 0.22f, 1f);
                    _sttButtonLabel.text = "语";
                    _sttButtonLabel.color = new Color(0.90f, 0.94f, 0.96f, 1f);
                    break;
            }
        }

        if (_sttNotice != null)
        {
            bool showNotice = _sttVisualState != SttButtonVisualState.Hidden && _sttStatusText.Length > 0;
            _sttNotice.text = showNotice ? _sttStatusText : "";
            _sttNotice.gameObject.SetActive(showNotice);
            if (showNotice)
            {
                bool hasFailure = _failureNotice != null && _failureNotice.text.Length > 0;
                _sttNotice.rectTransform.anchoredPosition = new Vector2(0f, hasFailure ? 62f : 8f);
                _sttNotice.color = _sttVisualState == SttButtonVisualState.Recording
                    ? new Color(1f, 0.82f, 0.78f, 0.98f)
                    : new Color(0.76f, 0.91f, 0.96f, 0.96f);
            }
        }
    }
}
