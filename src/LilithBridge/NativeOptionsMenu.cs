using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
namespace LilithBridge;
internal sealed class NativeOptionsMenu
{
    private readonly GameObject _layer;
    private readonly RectTransform _field,_canvas,_panel,_toggle,_divider,_historyButton,_reviewButton,_sharedButton,_modelsButton;
    private readonly TMP_Text _font,_title,_status,_thinking,_reviewLabel;
    private readonly Image[] _modeImages=new Image[6];
    private readonly TMP_Text[] _modeTexts=new TMP_Text[6];
    private readonly Action<bool> _ownership;
    private readonly Action<string> _select;
    private readonly NativeOpenAISettings _models;
    private BridgeConfig _config=new();
    private string[] _efforts=Array.Empty<string>();
    private float _keysUntil;
    private bool _reviewAuthorized;
    public bool IsOpen=>_layer.activeSelf;
    public bool ConsumesKeyboard=>IsOpen||Time.unscaledTime<_keysUntil;
    public NativeOptionsMenu(Transform field,Transform canvas,TMP_Text font,Action<string> select,Action history,Action<bool> ownership,Action authorizeReview,Action sharedActivity,Action selection)
    {
        _field=field.GetComponent<RectTransform>();_canvas=canvas.GetComponent<RectTransform>();_font=font;_ownership=ownership;_select=select;
        _toggle=Box("OptionsToggle",field,new Vector2(241,0),new Vector2(38,36));
        MakeButton(_toggle,"⋯",()=>{if(IsOpen)Close();else Open();},26);
        var layer=Box("OptionsMenuLayer",canvas,Vector2.zero,Vector2.zero);_layer=layer.gameObject;
        layer.anchorMin=Vector2.zero;layer.anchorMax=Vector2.one;layer.offsetMin=layer.offsetMax=Vector2.zero;
        var shield=Box("OutsideClickShield",layer,Vector2.zero,Vector2.zero);
        shield.anchorMin=Vector2.zero;shield.anchorMax=Vector2.one;shield.offsetMin=shield.offsetMax=Vector2.zero;
        shield.gameObject.AddComponent<Image>().color=new Color(0,0,0,.01f);
        var outside=shield.gameObject.AddComponent<Button>();NoNavigation(outside);
        outside.onClick.AddListener((UnityEngine.Events.UnityAction)(()=>Close()));
        _panel=Box("OptionsPanel",layer,Vector2.zero,new Vector2(304,266));
        _panel.pivot=Vector2.zero;_panel.anchorMin=_panel.anchorMax=new Vector2(.5f,.04f);
        NativeMenuStyle.Paint(_panel.gameObject.AddComponent<Image>(),new Color(.055f,.075f,.10f,.99f));
        var shadow=_panel.gameObject.AddComponent<Shadow>();shadow.effectColor=new Color(0,0,0,.32f);shadow.effectDistance=new Vector2(0,-4);
        _title=Label(_panel,"对话选项",Vector2.zero,new Vector2(272,28),21);
        _status=Label(_panel,"",Vector2.zero,new Vector2(272,20),13);_status.color=Muted;
        _thinking=Label(_panel,"思考强度",Vector2.zero,new Vector2(272,22),14);_thinking.color=Muted;
        for(int i=0;i<_modeImages.Length;i++)
        {
            int index=i;var rect=Box("ThinkingSlot"+i,_panel,Vector2.zero,new Vector2(82,32));
            (_modeImages[i],_modeTexts[i])=MakeButton(rect,"",()=>SelectEffort(index),16);_modeTexts[i].alignment=TextAlignmentOptions.Center;
        }
        _divider=Box("Divider",_panel,Vector2.zero,new Vector2(272,1));_divider.gameObject.AddComponent<Image>().color=new Color(.23f,.30f,.35f,.7f);
        _historyButton=ActionButton("OpenHistory","历史记录",history);
        _reviewButton=Box("ReviewAuthorize",_panel,Vector2.zero,new Vector2(272,34));(_, _reviewLabel)=MakeButton(_reviewButton,"回看：允许本轮",authorizeReview,16);
        _sharedButton=ActionButton("OpenSharedActivity","一起看",sharedActivity);
        _models=new NativeOpenAISettings(layer,_font,CloseModels);
        _modelsButton=ActionButton("ModelSettings","模型与资料  ›",()=>{_panel.gameObject.SetActive(false);_models.Open();},false);
        _modelsButton.GetComponent<Image>().color=new Color(.10f,.23f,.28f,1);
        _layer.SetActive(false);Refresh(new BridgeConfig(),null);
    }
    private static Color Muted=>new(.64f,.74f,.80f,1);
    private RectTransform ActionButton(string name,string label,Action action,bool close=true)
    {
        var r=Box(name,_panel,Vector2.zero,new Vector2(272,34));MakeButton(r,label,()=>{if(close)Close(false);action();},16);return r;
    }
    private void SelectEffort(int index)
    {if(index>=_efforts.Length)return;_select(DialogueProviderOptions.BindEffort(_config,_efforts[index]));Close();}
    private void CloseModels(){_models.Close();Refresh(ConfigStore.Load(),null);_panel.gameObject.SetActive(true);}
    public void Refresh(BridgeConfig config,string? error)
    {
        _config=config;_models.Refresh(config);_efforts=DialogueProviderOptions.Efforts(config);string selected=DialogueProviderOptions.Effort(config);
        for(int i=0;i<_modeImages.Length;i++)
        {
            bool visible=i<_efforts.Length;_modeImages[i].gameObject.SetActive(visible);if(!visible){_modeImages[i].gameObject.name="ThinkingSlot"+i;continue;}
            string value=_efforts[i];_modeImages[i].gameObject.name="Thinking-"+value;_modeTexts[i].text=DialogueProviderOptions.Label(value);
            _modeImages[i].color=value==selected?new Color(.20f,.43f,.45f,1):new Color(.11f,.17f,.22f,1);_modeImages[i].GetComponent<Button>().interactable=true;
        }
        _status.text=error??DialogueProviderOptions.Provider(config)+" · "+(config.IsOpenAI?DialogueProviderOptions.Model(config):DeepSeekModels.Label(config.Model));
        _status.color=error==null?Muted:new Color(1,.71f,.53f,1);
        _reviewButton.gameObject.SetActive(ReviewContextModes.Normalize(config.ReviewContextMode)==ReviewContextModes.Request);
        _sharedButton.gameObject.SetActive(config.SharedActivityEnabled);RefreshReview(_reviewAuthorized);RefreshLayout();
    }
    public void RefreshReview(bool authorized){_reviewAuthorized=authorized;_reviewLabel.text=authorized?"回看：已允许本轮":"回看：允许本轮";}
    public void RefreshLayout()
    {
        int rows=Math.Max(1,(_efforts.Length+2)/3);var actions=new List<RectTransform>{_historyButton};
        if(_reviewButton.gameObject.activeSelf)actions.Add(_reviewButton);if(_sharedButton.gameObject.activeSelf)actions.Add(_sharedButton);actions.Add(_modelsButton);
        float height=188+(rows-1)*38+(actions.Count-1)*40;_panel.sizeDelta=new Vector2(304,height);float top=height/2-14;
        Place(_title.rectTransform,0,top-12);Place(_status.rectTransform,0,top-39);Place(_thinking.rectTransform,0,top-68);
        for(int i=0;i<_efforts.Length;i++){_modeImages[i].rectTransform.sizeDelta=new Vector2(_efforts.Length==1?272:82,32);Place(_modeImages[i].rectTransform,_efforts.Length==1?0:-90+i%3*90,top-96-i/3*38);_modeTexts[i].rectTransform.sizeDelta=new Vector2(_efforts.Length==1?244:54,32);}
        float divider=top-122-(rows-1)*38;Place(_divider,0,divider);for(int i=0;i<actions.Count;i++)Place(actions[i],0,divider-28-i*40);
        float scale=Math.Clamp((_canvas.rect.height-_field.sizeDelta.y-62)/height,.55f,1f);_panel.localScale=Vector3.one*scale;
        _panel.anchoredPosition=_canvas.rect.width>=1120?new Vector2(_field.sizeDelta.x/2+14,18):new Vector2(-304*scale/2,18+_field.sizeDelta.y/2+14);
    }
    private static void Place(RectTransform r,float x,float y)=>r.anchoredPosition=new Vector2(x,y);
    public bool ContainsPointer(Vector3 p)=>IsOpen||RectTransformUtility.RectangleContainsScreenPoint(_toggle,p,null);
    private void Open()
    {
        NativeUiInputGate.ClaimPointer();_keysUntil=Time.unscaledTime+.25f;Refresh(ConfigStore.Load(),null);_models.Close();_panel.gameObject.SetActive(true);_layer.SetActive(true);_ownership(true);
        if(EventSystem.current!=null)EventSystem.current.SetSelectedGameObject(null);
    }
    public void Close(bool restoreFocus=true)
    {if(!IsOpen)return;_models.Close();NativeUiInputGate.ClaimPointer();_keysUntil=Time.unscaledTime+.25f;_layer.SetActive(false);if(restoreFocus)_ownership(false);}
    public void Dispose(){_models.Dispose();Close(false);}
    private (Image,TMP_Text) MakeButton(RectTransform rect,string text,Action action,float size)
    {
        var image=rect.gameObject.AddComponent<Image>();NativeMenuStyle.Paint(image,new Color(.11f,.17f,.22f,1));
        var button=rect.gameObject.AddComponent<Button>();button.targetGraphic=image;NoNavigation(button);
        var colors=button.colors;colors.normalColor=Color.white;colors.highlightedColor=new Color(.80f,.94f,1,1);colors.pressedColor=new Color(.60f,.81f,.86f,1);colors.selectedColor=Color.white;colors.fadeDuration=.10f;button.colors=colors;
        button.onClick.AddListener((UnityEngine.Events.UnityAction)(()=>{NativeUiInputGate.ClaimPointer();action();}));
        return(image,Label(rect,text,Vector2.zero,rect.sizeDelta-new Vector2(28,0),size));
    }
    private TMP_Text Label(Transform parent,string value,Vector2 at,Vector2 size,float fontSize)
    {
        var t=Box("Label",parent,at,size).gameObject.AddComponent<TextMeshProUGUI>();t.font=_font.font;t.fontSize=fontSize;t.text=value;t.color=new Color(.90f,.94f,.96f,1);
        t.richText=false;t.raycastTarget=false;t.enableWordWrapping=false;t.overflowMode=TextOverflowModes.Ellipsis;t.alignment=TextAlignmentOptions.MidlineLeft;return t;
    }
    private static void NoNavigation(Selectable c){var n=c.navigation;n.mode=Navigation.Mode.None;c.navigation=n;}
    private static RectTransform Box(string name,Transform parent,Vector2 at,Vector2 size)
    {var go=new GameObject(name);go.AddComponent<RectTransform>();go.transform.SetParent(parent,false);var r=go.GetComponent<RectTransform>();r.anchorMin=r.anchorMax=new Vector2(.5f,.5f);r.anchoredPosition=at;r.sizeDelta=size;return r;}
}
