using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
namespace LilithBridge;

internal sealed class NativeOptionsMenu
{
    private readonly GameObject _layer;
    private readonly RectTransform _field, _canvas, _panel, _toggle;
    private readonly TMP_Text _font, _status;
    private readonly RectTransform _reviewButton, _sharedButton;
    private readonly TMP_Text _reviewLabel;
    private bool _reviewAuthorized;
    private readonly Image[] _modeImages=new Image[3];
    private readonly TMP_Text[] _modeTexts=new TMP_Text[3];
    private readonly Action<bool> _ownership;
    private float _keysUntil;
    private static readonly string[] Modes={"off","low","high"};
    private static readonly string[] Labels={"Off","Low","High"};
    public bool IsOpen => _layer.activeSelf;
    public bool ConsumesKeyboard => IsOpen || Time.unscaledTime<_keysUntil;
    public NativeOptionsMenu(Transform field,Transform canvas,TMP_Text font,Action<string> select,Action history,Action<bool> ownership,Action authorizeReview, Action sharedActivity)
    {
        _field=field.GetComponent<RectTransform>();_canvas=canvas.GetComponent<RectTransform>();_font=font;_ownership=ownership;
        _toggle=Box("OptionsToggle",field,new Vector2(241,0),new Vector2(38,36));
        _toggle.anchorMin=_toggle.anchorMax=new Vector2(.5f,.5f);
        MakeButton(_toggle,"⋯",()=>{if(IsOpen)Close();else Open();},26);
        var layer=Box("OptionsMenuLayer",canvas,Vector2.zero,Vector2.zero);_layer=layer.gameObject;
        layer.anchorMin=Vector2.zero;layer.anchorMax=Vector2.one;layer.offsetMin=Vector2.zero;layer.offsetMax=Vector2.zero;
        // The outside click closes this modal layer and is consumed here.
        var backdrop=Box("OutsideClickShield",layer,Vector2.zero,Vector2.zero);
        backdrop.anchorMin=Vector2.zero;backdrop.anchorMax=Vector2.one;backdrop.offsetMin=Vector2.zero;backdrop.offsetMax=Vector2.zero;
        var bg=backdrop.gameObject.AddComponent<Image>();bg.color=new Color(0,0,0,.01f);
        var outside=backdrop.gameObject.AddComponent<Button>();outside.targetGraphic=bg;NoNavigation(outside);
        outside.onClick.AddListener((UnityEngine.Events.UnityAction)(()=>Close()));
        _panel=Box("OptionsPanel",layer,Vector2.zero,new Vector2(224,290));_panel.pivot=Vector2.zero;
        _panel.anchorMin=_panel.anchorMax=new Vector2(.5f,.04f);
        _panel.gameObject.AddComponent<Image>().color=new Color(.055f,.075f,.11f,.99f);
        _status=Label(_panel,"",new Vector2(0,76),new Vector2(192,24),16);_status.alignment=TextAlignmentOptions.MidlineLeft;
        var title=Label(_panel,"思考",new Vector2(0,47),new Vector2(192,22),14);title.color=new Color(.64f,.72f,.78f,1);title.alignment=TextAlignmentOptions.MidlineLeft;
        for(int i=0;i<Modes.Length;i++)
        {
            string mode=Modes[i];var rect=Box("Thinking-"+mode,_panel,new Vector2(-64+i*64,16),new Vector2(58,32));
            (_modeImages[i],_modeTexts[i])=MakeButton(rect,Labels[i],()=>{select(mode);Close();},17);
        }
        var line=Box("Divider",_panel,new Vector2(0,-14),new Vector2(192,1));line.gameObject.AddComponent<Image>().color=new Color(.18f,.23f,.29f,1);
        var historyButton=Box("OpenHistory",_panel,new Vector2(0,-46),new Vector2(192,36));
        MakeButton(historyButton,"历史记录",()=>{Close(false);history();},18);
        var reviewButton=Box("ReviewAuthorize",_panel,new Vector2(0,-84),new Vector2(192,32));
        _reviewButton=reviewButton;
        (_,_reviewLabel)=MakeButton(reviewButton,"回看：本轮允许",()=>authorizeReview(),16);
        var sharedButton=Box("OpenSharedActivity",_panel,new Vector2(0,-126),new Vector2(192,32));
        _sharedButton=sharedButton;
        MakeButton(sharedButton,"一起看",()=>{Close(false);sharedActivity();},18);
        _layer.SetActive(false);RefreshLayout();
    }
    public void Refresh(BridgeConfig config,string? error)
    {
        _sharedButton.gameObject.SetActive(config.SharedActivityEnabled);
        string current=DialogueGeneration.Normalize(config.DialogueThinkingMode);
        for(int i=0;i<Modes.Length;i++)
        {
            bool selected=Modes[i]==current;
            _modeImages[i].color=selected?new Color(.22f,.42f,.52f,1):new Color(.12f,.16f,.21f,1);
            _modeTexts[i].text=(selected?"• ":"")+Labels[i];
        }
        _status.text=error ?? "当前 "+(current=="off"?"Off":current=="low"?"Low":"High");
        _status.color=error==null?new Color(.78f,.88f,.91f,1):new Color(1,.70f,.55f,1);
        string reviewMode=ReviewContextModes.Normalize(config.ReviewContextMode);
        _reviewButton.gameObject.SetActive(reviewMode==ReviewContextModes.Request);
        if(reviewMode==ReviewContextModes.Request)
            _reviewLabel.text=_reviewAuthorized?"回看：已允许本轮":"回看：本轮允许";
    }
    public void RefreshReview(bool authorized)
    {
        _reviewAuthorized=authorized;
        _reviewLabel.text=authorized?"回看：已允许本轮":"回看：本轮允许";
    }
    public void RefreshLayout()
    {
        // Beside the input at normal widths; above it if horizontal room is tight.
        float width=_canvas.rect.width;
        _panel.anchoredPosition=width>=940?new Vector2(236,42):new Vector2(-9,18+_field.sizeDelta.y/2+12);
    }
    public bool ContainsPointer(Vector3 position)=>IsOpen || RectTransformUtility.RectangleContainsScreenPoint(_toggle,position,null);
    private void Open()
    {
        NativeUiInputGate.ClaimPointer();_keysUntil=Time.unscaledTime+.25f;RefreshLayout();_layer.SetActive(true);_ownership(true);
        if(EventSystem.current!=null)EventSystem.current.SetSelectedGameObject(null);
    }
    public void Close(bool restoreFocus=true)
    {
        if(!IsOpen)return;
        NativeUiInputGate.ClaimPointer();_keysUntil=Time.unscaledTime+.25f;_layer.SetActive(false);
        if(restoreFocus)_ownership(false);
    }
    private (Image,TMP_Text) MakeButton(RectTransform rect,string text,Action action,float size)
    {
        var image=rect.gameObject.AddComponent<Image>();image.color=new Color(.13f,.19f,.25f,1);
        var button=rect.gameObject.AddComponent<Button>();button.targetGraphic=image;NoNavigation(button);
        var colors=button.colors;colors.normalColor=Color.white;colors.highlightedColor=new Color(.83f,.92f,1,1);colors.pressedColor=new Color(.63f,.80f,.89f,1);colors.selectedColor=Color.white;colors.fadeDuration=.08f;button.colors=colors;
        button.onClick.AddListener((UnityEngine.Events.UnityAction)(()=>{NativeUiInputGate.ClaimPointer();action();}));
        return(image,Label(rect,text,Vector2.zero,rect.sizeDelta,size));
    }
    private TMP_Text Label(Transform parent,string value,Vector2 at,Vector2 size,float fontSize)
    {
        var t=Box("Label",parent,at,size).gameObject.AddComponent<TextMeshProUGUI>();t.font=_font.font;t.fontSize=fontSize;t.text=value;
        t.color=Color.white;t.richText=false;t.raycastTarget=false;t.enableWordWrapping=false;t.alignment=TextAlignmentOptions.Center;return t;
    }
    private static void NoNavigation(Selectable control){var nav=control.navigation;nav.mode=Navigation.Mode.None;control.navigation=nav;}
    private static RectTransform Box(string name,Transform parent,Vector2 at,Vector2 size)
    {
        var go=new GameObject(name);go.AddComponent<RectTransform>();go.transform.SetParent(parent,false);
        var r=go.GetComponent<RectTransform>();r.anchorMin=r.anchorMax=new Vector2(.5f,.5f);r.anchoredPosition=at;r.sizeDelta=size;return r;
    }
}
