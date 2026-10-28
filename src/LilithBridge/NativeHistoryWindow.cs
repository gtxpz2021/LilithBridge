using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

namespace LilithBridge;

internal sealed class NativeHistoryWindow
{
    private GameObject? _root;
    private TMP_Text? _body, _status, _notice;
    private TMP_Text? _toast;
    private TMP_InputField? _bodyInput;
    private RectTransform? _clip;
    private HistoryPageText? _page;
    private readonly HistoryCopyGesture _copyGesture = new();
    private float _toastUntil;
    private TMP_InputField? _search;
    private ScrollRect? _scroll;
    private DialogueHistoryView? _view;
    private Action? _close;
    private TMP_Text? _style;
    public bool IsOpen => _root != null && _root.activeSelf;
    public void Show(Transform owner, TMP_Text? style, DialogueHistorySnapshot snapshot, Action close)
    {
        _style = style; _close = close;
        if (_root == null) Create(owner);
        _view = new DialogueHistoryView(snapshot);
        _search!.text = ""; _root!.SetActive(true); Render();
        _search.ActivateInputField();
    }
    public void Hide()
    {
        NativeUiInputGate.ClaimPointer();
        _copyGesture.Reset(); _page = null;
        if (_root != null) _root.SetActive(false);
        if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(null);
        _view = null;
    }
    public void Destroy() { if (_root != null) UnityEngine.Object.Destroy(_root); _root = null; _view = null; }
    private void Search() { _view?.Search(_search?.text); Render(); }
    private void Render()
    {
        if (_view == null || _body == null) return;
        _copyGesture.Reset(); _page = _view.BuildPage();
        _bodyInput!.SetTextWithoutNotify(_page.Text);
        _bodyInput.selectionStringAnchorPosition = 0; _bodyInput.selectionStringFocusPosition = 0;
        if (_toast != null) _toast.text = "";
        _status!.text = _view.Status;
        _notice!.text = _view.ReadFailed ? "记录读取不完整" : "";
        _body.ForceMeshUpdate();
        _body.rectTransform.sizeDelta = new Vector2(690f, Mathf.Max(410f, _body.preferredHeight + 24f));
        _scroll!.verticalNormalizedPosition = 1f;
    }
    public void Tick()
    {
        if (!IsOpen) return;
        if (_toast != null && Time.unscaledTime >= _toastUntil) _toast.text = "";
        if (!Application.isFocused && !NativeInput.IsGameForeground()) { _copyGesture.Reset(); return; }
        Vector3 pointer = Input.mousePosition;
        DialogueHistoryEntry? entry = EntryAt(pointer);
        if (Input.GetMouseButtonDown(0)) _copyGesture.Down(entry,pointer.x,pointer.y,Time.unscaledTime);
        if (Input.GetMouseButton(0)) _copyGesture.Move(pointer.x,pointer.y);
        if (Input.GetMouseButtonUp(0))
        {
            string? whole = _copyGesture.Up(entry,pointer.x,pointer.y,Time.unscaledTime);
            if (whole != null)
            {
                Copy(whole,"已复制");
            }
        }
        // Read-only TMP supplies selection offsets; copying requires Ctrl+C.
        if (_bodyInput?.isFocused == true && Input.GetKeyDown(KeyCode.C)
            && (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl))
            && _bodyInput.selectionStringAnchorPosition != _bodyInput.selectionStringFocusPosition)
        {
            string? selected=HistoryCopyGesture.Selection(_page!,_bodyInput.selectionStringAnchorPosition,_bodyInput.selectionStringFocusPosition,true);
            if(selected!=null)Copy(selected,"已复制");
        }
    }
    private DialogueHistoryEntry? EntryAt(Vector3 pointer)
    {
        if (_body == null || _clip == null || _page == null || !RectTransformUtility.RectangleContainsScreenPoint(_clip,pointer,null)) return null;
        int hit = TMP_TextUtilities.FindIntersectingCharacter(_body,pointer,null,true);
        return hit < 0 || hit >= _body.textInfo.characterCount ? null : _page.MessageAt(_body.textInfo.characterInfo[hit].index);
    }
    private void Toast(string value) { if (_toast != null) _toast.text=value; _toastUntil=Time.unscaledTime+1.4f; }
    private void Copy(string value,string notice)
    {
        try { GUIUtility.systemCopyBuffer=value;Toast(notice); }
        catch { Toast("复制失败，请重试"); }
    }
    private void Create(Transform owner)
    {
        _root = new GameObject("LilithDialogueHistoryCanvas");
        _root.AddComponent<RectTransform>(); _root.AddComponent<Canvas>();
        _root.AddComponent<CanvasScaler>(); _root.AddComponent<GraphicRaycaster>();
        _root.transform.SetParent(owner, false);
        var canvas = _root.GetComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 10010;
        var scaler = _root.GetComponent<CanvasScaler>(); scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f,1080f); scaler.matchWidthOrHeight = .5f;
        var shield = Box("ModalPointerShield",_root.transform,Vector2.zero,Vector2.zero);
        shield.anchorMin=Vector2.zero;shield.anchorMax=Vector2.one;shield.offsetMin=Vector2.zero;shield.offsetMax=Vector2.zero;
        shield.gameObject.AddComponent<Image>().color=new Color(0,0,0,.20f);
        var panel = Box("HistoryPanel", _root.transform, new Vector2(0,0), new Vector2(760,690));
        panel.gameObject.AddComponent<CanvasRenderer>(); panel.gameObject.AddComponent<Image>().color = new Color(.04f,.05f,.08f,.98f);
        Label(panel,"历史",new Vector2(-245,308),new Vector2(200,34),25);
        Button(panel,"关闭",new Vector2(325,308),new Vector2(80,34),()=>_close?.Invoke());
        _notice = Label(panel,"",new Vector2(0,252),new Vector2(706,68),14); _notice.enableWordWrapping = true;
        var searchRect = Box("Search",panel,new Vector2(-78,196),new Vector2(546,36));
        var searchBg = searchRect.gameObject.AddComponent<Image>(); searchBg.color = new Color(.13f,.15f,.20f,1);
        var viewport = Box("SearchViewport",searchRect,Vector2.zero,new Vector2(524,32));
        viewport.gameObject.AddComponent<RectMask2D>();
        var text = Label(viewport,"",Vector2.zero,new Vector2(520,32),20);
        var placeholder = Label(viewport,"关键词",Vector2.zero,new Vector2(520,32),18);
        placeholder.color = new Color(1,1,1,.5f);
        _search = searchRect.gameObject.AddComponent<TMP_InputField>();
        _search.textViewport = viewport; _search.textComponent = text; _search.placeholder = placeholder;
        _search.targetGraphic = searchBg; _search.lineType = TMP_InputField.LineType.SingleLine;
        _search.characterLimit = 100; _search.onFocusSelectAll = false;
        _search.onSubmit.AddListener((UnityEngine.Events.UnityAction<string>)(_=>Search()));
        Button(panel,"查找",new Vector2(245,196),new Vector2(76,36),Search);
        Button(panel,"全部",new Vector2(329,196),new Vector2(76,36),()=>{_search.text="";Search();});
        var scrollArea = Box("HistoryScroll",panel,new Vector2(0,-38),new Vector2(710,420));
        scrollArea.gameObject.AddComponent<Image>().color = new Color(.06f,.075f,.11f,1);
        var clip = Box("Viewport",scrollArea,new Vector2(-5,0),new Vector2(694,414)); clip.gameObject.AddComponent<RectMask2D>();
        _clip = clip;
        var selection = Box("ReadOnlySelection",clip,Vector2.zero,new Vector2(690,414));
        var bodyRect = Box("Content",selection,Vector2.zero,new Vector2(680,410));
        bodyRect.anchorMin = new Vector2(.5f,1); bodyRect.anchorMax = new Vector2(.5f,1); bodyRect.pivot = new Vector2(.5f,1); bodyRect.anchoredPosition = Vector2.zero;
        _body = bodyRect.gameObject.AddComponent<TextMeshProUGUI>(); Style(_body,20); _body.enableWordWrapping = true;
        _body.alignment = TextAlignmentOptions.TopLeft; _body.richText = false;
        _body.raycastTarget = true;
        _bodyInput = selection.gameObject.AddComponent<TMP_InputField>();
        _bodyInput.textViewport=clip;_bodyInput.textComponent=_body;_bodyInput.targetGraphic=_body;
        _bodyInput.readOnly=true;_bodyInput.lineType=TMP_InputField.LineType.MultiLineNewline;
        _bodyInput.onFocusSelectAll=false;_bodyInput.richText=false;_bodyInput.shouldHideMobileInput=true;
        _bodyInput.selectionColor=new Color(.24f,.50f,.65f,.55f);
        _bodyInput.onSubmit.RemoveAllListeners();_bodyInput.onEndEdit.RemoveAllListeners();
        _scroll = scrollArea.gameObject.AddComponent<ScrollRect>(); _scroll.viewport = clip; _scroll.content = bodyRect;
        _scroll.horizontal = false; _scroll.vertical = true; _scroll.movementType = ScrollRect.MovementType.Clamped; _scroll.scrollSensitivity = 35;
        var rail=Box("ScrollRail",scrollArea,new Vector2(347,0),new Vector2(10,408));
        rail.gameObject.AddComponent<Image>().color=new Color(.11f,.14f,.19f,1);
        var handle=Box("Handle",rail,Vector2.zero,new Vector2(10,80));var handleImage=handle.gameObject.AddComponent<Image>();handleImage.color=new Color(.38f,.48f,.56f,1);
        var bar=rail.gameObject.AddComponent<Scrollbar>();bar.handleRect=handle;bar.targetGraphic=handleImage;bar.direction=Scrollbar.Direction.BottomToTop;
        var nav=bar.navigation;nav.mode=Navigation.Mode.None;bar.navigation=nav;_scroll.verticalScrollbar=bar;
        Button(panel,"更早",new Vector2(-295,-282),new Vector2(110,36),()=>{_view?.Older();Render();});
        Button(panel,"更新",new Vector2(295,-282),new Vector2(110,36),()=>{_view?.Newer();Render();});
        _status = Label(panel,"",new Vector2(0,-315),new Vector2(710,48),16);
        _toast=Label(panel,"",new Vector2(0,-256),new Vector2(690,16),14);_toast.color=new Color(.56f,.85f,.84f,1);_toast.alignment=TextAlignmentOptions.Center;
    }
    private static RectTransform Box(string name, Transform parent, Vector2 position, Vector2 size)
    {
        var go = new GameObject(name); go.AddComponent<RectTransform>(); go.transform.SetParent(parent,false);
        var rect=go.GetComponent<RectTransform>(); rect.anchorMin=new Vector2(.5f,.5f);rect.anchorMax=new Vector2(.5f,.5f);
        rect.anchoredPosition=position;rect.sizeDelta=size;return rect;
    }
    private void Style(TMP_Text text,float size)
    {
        text.font = _style?.font ?? TMP_Settings.defaultFontAsset; text.fontSize=size;text.color=Color.white;
        text.richText=false;text.raycastTarget=false;text.alignment=TextAlignmentOptions.MidlineLeft;
    }
    private TMP_Text Label(Transform parent,string value,Vector2 position,Vector2 size,float fontSize)
    {
        var rect=Box("Label",parent,position,size);var text=rect.gameObject.AddComponent<TextMeshProUGUI>();Style(text,fontSize);
        text.text=value;return text;
    }
    private void Button(Transform parent,string value,Vector2 position,Vector2 size,Action action)
    {
        var rect=Box(value,parent,position,size);var image=rect.gameObject.AddComponent<Image>();image.color=new Color(.16f,.25f,.31f,1);
        var button=rect.gameObject.AddComponent<Button>();button.targetGraphic=image;
        var navigation=button.navigation;navigation.mode=Navigation.Mode.None;button.navigation=navigation;
        button.onClick.AddListener((UnityEngine.Events.UnityAction)(()=>action()));
        var label=Label(rect,value,Vector2.zero,size,18);label.alignment=TextAlignmentOptions.Center;
    }
}
