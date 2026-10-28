using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace LilithBridge;

// Transparent program status. No input controls, raycaster, selection or memory writes.
internal sealed class NativeRequestStatus
{
    private GameObject? _root;
    private TMP_Text? _label;
    public void Tick(Transform owner, TMP_Text? style, DialogueRequest? request, bool visible)
    {
        try { TickCore(owner, style, request, visible); }
        catch { Destroy(); } // Optional status must never interrupt request/input processing.
    }
    private void TickCore(Transform owner, TMP_Text? style, DialogueRequest? request, bool visible)
    {
        string text = visible && request?.IsCancelled == false ? request.WaitStatus.Text : "";
        if (text.Length > 0 && _label == null && style?.font != null)
        {
            _root = new GameObject("LilithProgramRequestStatus");
            _root.AddComponent<RectTransform>();
            _root.transform.SetParent(owner, false);
            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 9998;
            var scaler = _root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = .5f;
            var labelGo = new GameObject("Status");
            labelGo.AddComponent<RectTransform>();
            labelGo.transform.SetParent(_root.transform, false);
            _label = labelGo.AddComponent<TextMeshProUGUI>();
            _label.font = style.font; _label.fontSize = 16;
            _label.color = new Color(.82f, .85f, .91f, .8f);
            _label.richText = false; _label.raycastTarget = false;
            _label.enableWordWrapping = false;
            _label.alignment = TextAlignmentOptions.BottomLeft;
            var rect = _label.rectTransform;
            rect.anchorMin = rect.anchorMax = Vector2.zero; rect.pivot = Vector2.zero;
            rect.anchoredPosition = new Vector2(18, 14); rect.sizeDelta = new Vector2(330, 24);
        }
        if (_label != null) _label.text = text;
        if (_root != null) _root.SetActive(text.Length > 0);
    }
    public void Destroy()
    {
        try { if (_root != null) UnityEngine.Object.Destroy(_root); }
        catch { /* Best effort on scene/application shutdown. */ }
        _root = null; _label = null;
    }
}
