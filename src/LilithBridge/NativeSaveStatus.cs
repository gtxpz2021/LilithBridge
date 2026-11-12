using TMPro;
using UnityEngine;
using UnityEngine.UI;
namespace LilithBridge;

// Persistent failure, brief recovery. No controls, input ownership or raycaster.
internal sealed class NativeSaveStatus
{
    private readonly SaveFailurePresentation _presentation = new();
    private GameObject? _root;
    private TMP_Text? _label;
    private RectTransform? _panel;
    private string? _lastText;
    private float _layoutWidth = float.NaN, _layoutFontSize = float.NaN;
    private TMP_FontAsset? _layoutFont;
    private float _nextFontProbe;
    public void Tick(Transform owner, TMP_Text? style, MemoryStore memory, bool visible)
    {
        try
        {
            string text = _presentation.Update(memory.PersonaSaveFailure, memory.LastSceneSaveFailure, Time.unscaledTime);
            if (visible && text.Length > 0 && _label == null && Time.unscaledTime >= _nextFontProbe)
            {
                _nextFontProbe = Time.unscaledTime + 1f;
                TMP_FontAsset? font = style?.font;
                if (font == null)
                    foreach (TMP_Text candidate in Resources.FindObjectsOfTypeAll<TMP_Text>())
                        if (candidate.font != null) { font = candidate.font; break; }
                font ??= TMP_Settings.defaultFontAsset;
                if (font != null) Create(owner, font);
            }
            if (_label != null)
            {
                bool textChanged = !string.Equals(_lastText, text, System.StringComparison.Ordinal);
                if (textChanged) { _label.text = text; _lastText = text; }
                // Empty status has no layout work. A later nonempty text
                // change still measures its current width and font metrics.
                if (text.Length > 0)
                {
                    float width = _label.rectTransform.rect.width;
                    var font = _label.font;
                    float fontSize = _label.fontSize;
                    if (textChanged || width != _layoutWidth || font != _layoutFont || fontSize != _layoutFontSize)
                    {
                        _panel!.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical,
                            System.Math.Max(60f, _label.preferredHeight + 16f));
                        _layoutWidth = width; _layoutFont = font; _layoutFontSize = fontSize;
                    }
                }
            }
            if (_root != null)
            {
                bool active = visible && text.Length > 0;
                if (_root.activeSelf != active) _root.SetActive(active);
            }
        }
        catch { Destroy(); } // Optional status must not break gameplay/input.
    }
    private void Create(Transform owner, TMP_FontAsset font)
    {
        _root = new GameObject("LilithProgramSaveStatus");
        _root.AddComponent<RectTransform>(); _root.transform.SetParent(owner, false);
        var canvas = _root.AddComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 9999;
        var scaler = _root.AddComponent<CanvasScaler>(); scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080); scaler.matchWidthOrHeight = .5f;
        var go = new GameObject("SaveStatus"); go.AddComponent<RectTransform>(); go.transform.SetParent(NativeUiViewport.Create(canvas), false);
        _panel = go.GetComponent<RectTransform>();
        var rect = _panel; rect.anchorMin = new Vector2(0,1); rect.anchorMax = new Vector2(1,1);
        rect.pivot = new Vector2(.5f,1); rect.offsetMin = new Vector2(16,-150); rect.offsetMax = new Vector2(-16,-16);
        var background = go.AddComponent<Image>(); background.color = new Color(.06f,.07f,.10f,.94f); background.raycastTarget = false;
        var labelGo = new GameObject("Status"); labelGo.AddComponent<RectTransform>(); labelGo.transform.SetParent(go.transform,false);
        _label = labelGo.AddComponent<TextMeshProUGUI>(); _label.font = font; _label.fontSize = 18;
        _lastText = null; _layoutWidth = _layoutFontSize = float.NaN; _layoutFont = null;
        _label.color = new Color(1,.81f,.62f,1); _label.richText = false; _label.raycastTarget = false;
        _label.enableWordWrapping = true; _label.alignment = TextAlignmentOptions.TopLeft;
        var labelRect = _label.rectTransform; labelRect.anchorMin = Vector2.zero; labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = new Vector2(12,8); labelRect.offsetMax = new Vector2(-12,-8);
    }
    public void Destroy()
    {
        try { if (_root != null) UnityEngine.Object.Destroy(_root); } catch { }
        _root = null; _label = null; _panel = null;
        _lastText = null; _layoutWidth = _layoutFontSize = float.NaN; _layoutFont = null;
    }
}
