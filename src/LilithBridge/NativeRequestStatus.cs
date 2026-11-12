using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace LilithBridge;

// Transparent program status. No input controls, raycaster, selection or memory writes.
internal sealed class NativeRequestStatus
{
    private GameObject? _root;
    private TMP_Text? _label;
    public void Tick(Transform owner, TMP_Text? style, DialogueRequest? request, bool visible, NativeInputField? editingInput = null)
    {
        try { TickCore(owner, style, request, visible, editingInput); }
        catch { Destroy(); } // Optional status must never interrupt request/input processing.
    }
    private void TickCore(Transform owner, TMP_Text? style, DialogueRequest? request, bool visible, NativeInputField? editingInput = null)
    {
        var inputAnchor = editingInput?.RequestStatusAnchor;
        bool editing = inputAnchor != null;
        string text = visible && request?.IsCancelled == false ? request.WaitStatus.DisplayTextFor(editing) : "";
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
            labelGo.transform.SetParent(NativeUiViewport.Create(canvas), false);
            _label = labelGo.AddComponent<TextMeshProUGUI>();
            _label.font = style.font; _label.fontSize = 18;
            _label.color = new Color(.82f, .85f, .91f, .8f);
            _label.richText = false; _label.raycastTarget = false;
            _label.enableWordWrapping = false;
            _label.alignment = TextAlignmentOptions.Center;
            var rect = _label.rectTransform;
            rect.anchorMin = rect.anchorMax = new Vector2(.5f,0); rect.pivot = new Vector2(.5f,0);
            rect.anchoredPosition = new Vector2(0,88); rect.sizeDelta = new Vector2(760,58);
        }
        if (_label != null)
        {
            _label.text = text;
            var rect = _label.rectTransform;
            rect.anchorMin = rect.anchorMax = editing ? inputAnchor!.anchorMax : new Vector2(.5f, 0);
            rect.anchoredPosition = editing
                ? inputAnchor!.anchoredPosition + new Vector2(0, (1f - inputAnchor.pivot.y) * inputAnchor.rect.height + editingInput!.RequestStatusTopPadding)
                : new Vector2(0, 88);
        }
        if (_root != null) _root.SetActive(text.Length > 0);
    }
    public void Destroy()
    {
        try { if (_root != null) UnityEngine.Object.Destroy(_root); }
        catch { /* Best effort on scene/application shutdown. */ }
        _root = null; _label = null;
    }
}
