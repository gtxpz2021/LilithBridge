using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace LilithBridge;

internal sealed partial class NativeOpenAISettings
{
    private readonly RectTransform _panel,_canvas;
    private readonly TMP_Text _font,_status,_provider,_model,_effort,_budget,_timeout,_backgroundModel,_backgroundEffort,_backgroundBudget,_backgroundEnabled,_keyState,_consent,_storyStatus,_storyConsent,_storyAuto,_temporaryExpressions;
    private readonly TMP_InputField _key,_customModel,_storyPath;
    private readonly TMP_Text _modelCaption,_budgetCaption,_dialogueNote,_deepseekBackground;
    private readonly TMP_Text _deepseekReflection,_deepseekMemory;
    private readonly GameObject[] _openAIBackgroundWidgets;
    private readonly RectTransform[] _pages=new RectTransform[6];
    private readonly Image[] _tabs=new Image[6];
    private Transform? _building;
    private GameObject? _choices;
    private BridgeConfig _config=new();
    private Task<string[]>? _catalogTask;
    private Task<string>? _testTask;
    private CancellationTokenSource? _network;
    private bool Busy=>_catalogTask!=null||_testTask!=null;
    private string[]? _available;
    private DateTimeOffset _storyStatusAt=DateTimeOffset.MinValue;
    internal NativeOpenAISettings(Transform parent,TMP_Text font,Action close)
    {
        _font=font;_canvas=parent.GetComponent<RectTransform>();
        _panel=Box("OpenAISettings",parent,Vector2.zero,new Vector2(760,680));
        NativeMenuStyle.Paint(_panel.gameObject.AddComponent<Image>(),new Color(.045f,.065f,.09f,.995f));
        Text("模型与资料设置",new Vector2(-220,294),new Vector2(260,36),26,_panel);
        var sub=Text("后续请求生效 · 对话与后台分开设置",new Vector2(-140,259),new Vector2(420,28),16,_panel);sub.color=Muted;
        Button("NormalUseSetup",new Vector2(258,257),new Vector2(210,32),()=>ShowNormalUseSetup(),_panel).text="推荐使用设置";
        Button("CloseSettings",new Vector2(329,292),new Vector2(42,36),close,_panel).text="×";
        string[] names={"对话模型","后台与费用","密钥与隐私","作品资料","对话过程","认证账户"};
        for(int i=0;i<_pages.Length;i++)
        {
            int tab=i;
            var label=Button("Tab"+i,new Vector2(-290+i*116,205),new Vector2(110,40),()=>ShowTab(tab),_panel);label.text=names[i];
            label.fontSize=16;
            _tabs[i]=label.transform.parent.GetComponent<Image>();
            _pages[i]=Box("Page"+i,_panel,Vector2.zero,new Vector2(700,360));
        }
        _building=_pages[0];
        Caption("提供方",-330,144);
        _provider=Button("ProviderSelect",new Vector2(113,144),new Vector2(440,38),()=>Choose("ProviderSelect",new[]{"deepseek","openai"},_config.ModelProvider,ConfirmProviderChange,v=>v=="openai"?"OpenAI":"DeepSeek"));
        _modelCaption=Caption("API 模型",-330,94);
        _model=Button("ModelSelect",new Vector2(113,94),new Vector2(440,38),()=>ModelChoices(false));
        Caption("思考强度",-330,44);
        _effort=Button("EffortSelect",new Vector2(113,44),new Vector2(440,38),EffortChoices);
        _budgetCaption=Caption("输出预算",-330,-6);
        _budget=Button("BudgetSelect",new Vector2(113,-6),new Vector2(440,38),BudgetChoices);
        Caption("等待上限",-330,-56);
        _timeout=Button("TimeoutSelect",new Vector2(113,-56),new Vector2(440,38),TimeoutChoices);
        _customModel=Input("CustomModel",new Vector2(-51,-110),new Vector2(562,34),false,"自定义 API 模型 ID（可选）",128);
        Button("ApplyModel",new Vector2(284,-110),new Vector2(98,34),()=>ChangeDialogue("model",_customModel.text.Trim(),_config.ModelProvider)).text="应用 ID";
        _dialogueNote=Note("",new Vector2(0,-158),new Vector2(666,46),16);

        _building=_pages[1];
        Caption("后台任务",-330,144);
        _backgroundEnabled=Button("BackgroundEnabled",new Vector2(113,144),new Vector2(440,38),()=>Choose("BackgroundEnabled",new[]{"true","false"},_config.OpenAIBackgroundEnabled?"true":"false",v=>Change("background.enabled",v),v=>v=="true"?"开启":"关闭"));
        Caption("后台模型",-330,94);
        _backgroundModel=Button("BackgroundModel",new Vector2(113,94),new Vector2(440,38),()=>ModelChoices(true));
        Caption("反思强度",-330,44);
        _backgroundEffort=Button("BackgroundEffort",new Vector2(113,44),new Vector2(440,38),()=>Choose("BackgroundEffort",OpenAIProtocol.Efforts(_config.OpenAIBackgroundModel),_config.OpenAIBackgroundEffort,v=>Change("background.reasoning_effort",v),EffortLabel));
        Caption("后台预算",-330,-6);
        _backgroundBudget=Button("BackgroundBudget",new Vector2(113,-6),new Vector2(440,38),()=>Choose("BackgroundBudget",new[]{"1024","4096","8192","16384"},_config.OpenAIBackgroundMaxTokens.ToString(),v=>Change("background.max_output_tokens",v),v=>v+" token（含思考）"));
        _backgroundDetails=Note("默认 Terra / low，独立于对话模型。选择 Astra 不会再连带升级后台。\nTerra 的逐轮记忆提取固定使用 none / 1024；其他后台任务使用本页设置。\n后台等待上限默认 120 秒。失败后依次冷却 2、10、30 分钟，不自动加预算。\n关闭后台后会保留待处理材料；再次开启也不会把失败算成已经完成。",new Vector2(0,-103),new Vector2(666,126),17);

        _openAIBackgroundWidgets=_pages[1].GetComponentsInChildren<RectTransform>(true)
            .Where(t=>t.parent==_pages[1]).Select(t=>t.gameObject).ToArray();
        _deepseekReflection=Button("DeepSeekReflectionModel",new Vector2(0,133),new Vector2(666,40),()=>DeepSeekTaskChoices("reflection"));
        _deepseekMemory=Button("DeepSeekMemoryModel",new Vector2(0,80),new Vector2(666,40),()=>DeepSeekTaskChoices("memory"));
        _deepseekBackground=Note("",new Vector2(0,-46),new Vector2(666,180),17);

        _building=_pages[2];
        _key=Input("OpenAIKey",new Vector2(-52,139),new Vector2(560,36),true,"粘贴 OpenAI API key（隐藏，不回显）",512);
        Button("SaveKey",new Vector2(284,139),new Vector2(98,36),SaveKey).text="加密保存";
        _keyState=Text("",new Vector2(-45,101),new Vector2(575,28),16);_keyState.color=Muted;
        Button("DeleteKey",new Vector2(284,101),new Vector2(98,30),DeleteKey).text="删除密钥";
        Button("AccountModels",new Vector2(-170,53),new Vector2(325,38),FetchModels).text="核对账户模型 · 不生成";
        Button("CheapProbe",new Vector2(170,53),new Vector2(325,38),TestConnection).text="所选模型极短试连 · 付费";
        Note("启用后，角色提示、输入、选中的对话/记忆/反思及获准回看的旧思考、笔记和工具结果会发送至 OpenAI（api.openai.com）。允许看图时还包括图片/截图。API 按量计费，思考也消耗输出预算。\n试连仅发送“Reply only OK.”，最多 32 输出 token，不发送相处资料。请求使用 store=false，这不等于零数据保留。",new Vector2(0,-35),new Vector2(666,116),16);
        _consent=Button("RuntimeDataPermission",new Vector2(0,-124),new Vector2(666,40),()=>Change("allow_runtime_data",_config.OpenAIDataConsent?"false":"true"));
        Note("密钥仅以 Windows 当前用户加密文件保存。关闭面板或切换页签会清空输入框。",new Vector2(0,-167),new Vector2(666,30),15);

        CaptureApiPrivacyWidgets();
        _building=_pages[3];
        Note("私有作品资料 · 本地检索，有限供给",new Vector2(-120,143),new Vector2(420,32),18);
        Button("SelectLocalFirstGame",new Vector2(234,143),new Vector2(198,32),SelectLocalFirstGame).text="选择本地第一作库";
        _storyPath=Input("StoryPath",new Vector2(-52,97),new Vector2(560,36),false,"本地 .jsonl：短资料或第一作原文库",512);
        Button("ApplyStoryPath",new Vector2(284,97),new Vector2(98,36),()=>StoryChange("file",_storyPath.text)).text="保存路径";
        _storyStatus=Text("",new Vector2(0,57),new Vector2(666,30),15);_storyStatus.color=Muted;
        Note("选中的第一作片段提供给当前模型（OpenAI / DeepSeek，按其计费）；检索在本地完成，每次供给相关片段，可继续扩查。\n资料保留来源、说话人和分支记录，部分归属尚未核实；本库不包含本机玩家的路线选择。",new Vector2(0,-19),new Vector2(666,110),15);
        _storyConsent=Button("StoryDataPermission",new Vector2(0,-102),new Vector2(666,40),()=>StoryChange("allow_model_data",_config.StoryDataConsent?"false":"true"));
        _storyAuto=Button("StoryAutoRecall",new Vector2(0,-151),new Vector2(666,36),()=>StoryChange("auto_recall_enabled",_config.PreviousStoryAutoRecallEnabled?"false":"true"));

        _building=_pages[4];
        Note("检索过程中的临时表达",new Vector2(0,140),new Vector2(666,36),22);
        _temporaryExpressions=Button("TemporaryExpressions",new Vector2(0,88),new Vector2(666,44),ToggleTemporaryExpressions);
        Note("默认关闭。开启后，模型调用工具时可以先显示临时气泡，再继续处理。\n气泡出现后按整泡保留；临时表达不会直接并入长期历史。\n沿用现有资料、图像与模型设置，不修改这些设置；可能增加上下文用量。\n有可用工具且模型输出相应内容时才会显示，并非每轮都有。\n开关从下一轮起生效，当前回复继续完成。",new Vector2(0,-26),new Vector2(666,164),17);

        BuildChatGPTPage();
        _building=null;
        var divider=Box("Divider",_panel,new Vector2(0,-207),new Vector2(694,1));divider.gameObject.AddComponent<Image>().color=new Color(.17f,.23f,.28f,1);
        _status=Text("",new Vector2(0,-249),new Vector2(690,60),16,_panel);_status.enableWordWrapping=true;_status.color=Muted;
        Button("Back",new Vector2(0,-306),new Vector2(210,36),close,_panel).text="返回";
        ShowTab(0);_panel.gameObject.SetActive(false);
    }
    private static Color Muted=>new(.65f,.75f,.82f,1);
    private static string EffortLabel(string value)=>value switch{"none"=>"关闭 · none","low"=>"低 · low","medium"=>"中 · medium","high"=>"高 · high","xhigh"=>"很高 · xhigh","max"=>"最大 · max",_=>"API 默认"};
    private static string ModelLabel(string value)=>value switch{"gpt-6-astra"=>"GPT-6 Astra","gpt-6.1-sol"=>"GPT-6.1 Sol","gpt-6.1-terra"=>"GPT-6.1 Terra",_=>value};
    private void ModelChoices(bool background)
    {
        if(_config.UsesChatGPT){BeginChatGPTModels(background);return;}
        if(background)
        {
            Choose("BackgroundModel",OpenAIProtocol.Presets.Concat(new[]{_config.OpenAIBackgroundModel}).Distinct().ToArray(),
                _config.OpenAIBackgroundModel,v=>Change("background.model",v),ModelLabel);
            return;
        }
        string provider=_config.ModelProvider;string auth=_config.OpenAIAuthMode;
        Choose("ModelSelect",DialogueProviderOptions.Models(_config),provider=="openai"?DialogueProviderOptions.Model(_config):DeepSeekModels.SelectionId(_config.Model),
            v=>ChangeDialogue("model",v,provider,auth),v=>provider=="openai"?ModelLabel(v):DeepSeekModels.Label(v));
    }
    private void EffortChoices()
    {
        string provider=_config.ModelProvider;string auth=_config.OpenAIAuthMode;
        Choose("EffortSelect",DialogueProviderOptions.Efforts(_config),DialogueProviderOptions.Effort(_config),
            v=>ChangeDialogue("reasoning_effort",v,provider,auth),v=>DialogueProviderOptions.Label(v)+" · "+v);
    }
    private void BudgetChoices()
    {
        if(_config.UsesChatGPT)return;
        if(!_config.IsOpenAI && _config.DialogueAutoThinkingEnabled)return;
        string provider=_config.ModelProvider;string auth=_config.OpenAIAuthMode;string current=DialogueProviderOptions.Budget(_config).ToString();
        string[] choices=_config.IsOpenAI?new[]{"4096","8192","16384","32768"}
            :DialogueGeneration.Normalize(_config.DialogueThinkingMode)=="off"?new[]{"256","512","800","1024","2048"}:new[]{"1024","2048","4096","8192"};
        Choose("BudgetSelect",choices.Concat(new[]{current}).Distinct().ToArray(),current,
            v=>ChangeDialogue("max_output_tokens",v,provider,auth),v=>v+" token");
    }
    private void TimeoutChoices()
    {
        string provider=_config.ModelProvider;string auth=_config.OpenAIAuthMode;string current=DialogueProviderOptions.Timeout(_config).ToString();
        string[] choices=_config.IsOpenAI?new[]{"120","240","360","600"}:new[]{"30","60","90","120","180"};
        Choose("TimeoutSelect",choices.Concat(new[]{current}).Distinct().ToArray(),current,
            v=>ChangeDialogue("timeout_seconds",v,provider,auth),v=>v+" 秒");
    }
    private void ChangeDialogue(string name,string value,string provider,string? auth=null)
    {
        try{_config=ConfigStore.SetDialogueProviderOption(provider,name,value,auth);_customModel.text="";
            _status.text="已保存到 "+DialogueProviderOptions.Provider(_config)+"；后续请求生效。";Refresh(_config);}
        catch{_status.text="未保存。提供方可能已变化，请重新选择；原配置保留。";Refresh(ConfigStore.Load());}
    }
    private void ShowTab(int index)
    {
        CloseChoices();if(_key!=null)_key.text="";
        for(int i=0;i<_pages.Length;i++){_pages[i].gameObject.SetActive(i==index);_tabs[i].color=i==index?new Color(.16f,.36f,.42f,1):new Color(.09f,.14f,.19f,1);}
    }
    private void ToggleTemporaryExpressions()
    {
        try
        {
            Refresh(ConfigStore.SetTemporaryExpressions(! _config.ExperimentalTemporaryExpressions));
            _status.text="已保存；下一轮生效。当前回复不会因此中断。";
        }
        catch { _status.text="保存失败，原设置未更改；请稍后重试。"; }
    }
    private void Choose(string control,string[] values,string selected,Action<string> choose,Func<string,string> label)
    {
        CloseChoices();
        _choices=Box("ChoiceDrawer",_panel,Vector2.zero,_panel.sizeDelta).gameObject;
        var shield=_choices.AddComponent<Image>();shield.color=new Color(0,0,0,.18f);
        var outside=_choices.AddComponent<Button>();outside.targetGraphic=shield;outside.onClick.AddListener((UnityEngine.Events.UnityAction)CloseChoices);
        float contentHeight=values.Length*42+12;
        float height=Math.Min(contentHeight,330);
        float top=Math.Min(170,Math.Max(-100,FindControl(control).anchoredPosition.y-22));
        float center=Math.Max(-194+height/2,top-height/2);
        var list=Box("Choices",_choices.transform,new Vector2(113,center),new Vector2(440,height));list.gameObject.AddComponent<Image>().color=new Color(.08f,.13f,.18f,1);
        var viewport=Box("ChoiceViewport",list,Vector2.zero,new Vector2(440,height-4));viewport.gameObject.AddComponent<RectMask2D>();
        var content=Box("ChoiceContent",viewport,Vector2.zero,new Vector2(440,contentHeight));content.anchorMin=content.anchorMax=new Vector2(.5f,1);content.pivot=new Vector2(.5f,1);
        var scroll=list.gameObject.AddComponent<ScrollRect>();scroll.viewport=viewport;scroll.content=content;scroll.horizontal=false;scroll.vertical=contentHeight>height;
        scroll.movementType=ScrollRect.MovementType.Clamped;scroll.scrollSensitivity=28;
        for(int i=0;i<values.Length;i++)
        {
            string value=values[i];
            var item=Button("Choice-"+value,new Vector2(0,contentHeight/2-27-i*42),new Vector2(426,38),()=>{CloseChoices();choose(value);},content);
            item.text=(value==selected?"✓  ":"    ")+label(value);
            item.alignment=TextAlignmentOptions.MidlineLeft;
            if(value==selected)item.transform.parent.GetComponent<Image>().color=new Color(.16f,.36f,.42f,1);
        }
    }
    private RectTransform FindControl(string name)
    {foreach(var page in _pages){var child=page.Find(name);if(child!=null)return child.GetComponent<RectTransform>();}throw new InvalidOperationException("Unknown setting control");}
    private void CloseChoices(){if(_choices!=null)UnityEngine.Object.Destroy(_choices);_choices=null;}
    internal void Open()
    {
        _storyStatusAt=DateTimeOffset.MinValue;_config=ConfigStore.Load();_key.text="";_customModel.text="";_storyPath.text=_config.PreviousStoryFile;_status.text="可用“推荐使用设置”一次核对资料发送与后台；已有关闭项保留。";
        _panel.gameObject.SetActive(true);ShowTab(0);Refresh(_config);
    }
    internal void Close()
    {
        _signIn.ViewClosed();
        CloseChoices();_key.text="";_customModel.text="";_network?.Cancel();_network?.Dispose();_network=null;
        if(_catalogTask!=null)Observe(_catalogTask);if(_testTask!=null)Observe(_testTask);_catalogTask=null;_testTask=null;CloseChatGPTTasks();_panel.gameObject.SetActive(false);
    }
    private static void Observe(Task task)=>_=task.ContinueWith(t=>{_=t.Exception;},TaskContinuationOptions.OnlyOnFaulted);
    internal void Refresh(BridgeConfig config)
    {
        _signIn.CheckRoot(config.DataDirectory);
        if(!_panel.gameObject.activeSelf)return;_config=config;
        _panel.localScale=Vector3.one*Math.Min(1f,Math.Min((_canvas.rect.height-32)/680f,(_canvas.rect.width-24)/760f));
        _provider.text=(config.IsOpenAI?"OpenAI":"DeepSeek")+"  ▾";
        RefreshChatGPTPage(config);
        _modelCaption.text=config.UsesChatGPT?"账户模型":DialogueProviderOptions.Provider(config)+" 模型";
        _model.text=(config.IsOpenAI?ModelLabel(config.OpenAIModel):DeepSeekModels.Label(config.Model))+"  ▾";
        _effort.text=DialogueProviderOptions.Label(DialogueProviderOptions.Effort(config))+" · "+DialogueProviderOptions.Effort(config)+"  ▾";
        _budgetCaption.text=config.IsOpenAI?"输出预算":DialogueGeneration.Normalize(config.DialogueThinkingMode)=="off"?"回复上限":"思考输出上限";
        _budget.text=!config.IsOpenAI&&config.DialogueAutoThinkingEnabled?"自动模式 · 沿用原回复 / 思考预算":DialogueProviderOptions.Budget(config)+" token  ▾";
        _budget.transform.parent.GetComponent<Button>().interactable=config.IsOpenAI||!config.DialogueAutoThinkingEnabled;
        if(config.UsesChatGPT){_budgetCaption.text="输出上限";_budget.text="由 ChatGPT 计划决定";_budget.transform.parent.GetComponent<Button>().interactable=false;}
        _timeout.text=DialogueProviderOptions.Timeout(config)+" 秒  ▾";
        _dialogueNote.text=config.IsOpenAI?"本页只调整 OpenAI 对话；其他提供方与后台配置独立保留。\n未知模型使用 API 默认强度；等待时可按 Esc 取消。"
            :"本页只调整 DeepSeek 对话；OpenAI 设置保持不变。\nFlash 支持图像，Pro 仅文本；思考模式沿用原有预算。";
        if(config.UsesChatGPT)_dialogueNote.text="Using ChatGPT plan · 认证账户页可管理用量。\n模型来自此账户目录；不发送 API 输出上限参数。";
        foreach(var widget in _openAIBackgroundWidgets)widget.SetActive(config.IsOpenAI);
        _deepseekBackground.gameObject.SetActive(!config.IsOpenAI);
        _deepseekReflection.transform.parent.gameObject.SetActive(!config.IsOpenAI);
        _deepseekMemory.transform.parent.gameObject.SetActive(!config.IsOpenAI);
        _deepseekReflection.text="反思模型："+DeepSeekModels.Label(config.ReflectionModel)+"  ▾";
        _deepseekMemory.text="记忆提取模型："+DeepSeekModels.Label(config.AuxModel)+"  ▾";
        _deepseekBackground.text="DeepSeek 后台模型独立于对话模型，选择只对后续请求生效。\nFlash 支持图像，Pro 仅文本；账户权限与费用以提供方为准。\n旧 Flash / Vision ExP 配置仍可读取；不会自动改写。\nOpenAI 后台设置独立保留，切换提供方后可编辑。";
        _backgroundEnabled.text=(config.OpenAIBackgroundEnabled?"开启":"关闭")+"  ▾";_backgroundModel.text=ModelLabel(config.OpenAIBackgroundModel)+"  ▾";
        _backgroundEffort.text=EffortLabel(config.OpenAIBackgroundEffort)+"  ▾";_backgroundBudget.text=config.UsesChatGPT?"由 ChatGPT 计划决定":config.OpenAIBackgroundMaxTokens+" token（含思考）  ▾";
        _backgroundBudget.transform.parent.GetComponent<Button>().interactable=!config.UsesChatGPT;
        if(DateTimeOffset.UtcNow-_storyStatusAt>TimeSpan.FromSeconds(1)){_storyStatus.text=PreviousStoryArchive.Status(config);_storyStatusAt=DateTimeOffset.UtcNow;}
        _storyConsent.text=config.StoryDataConsent?"已允许作品片段供给 · 点击停用":"我已了解外发范围 · 启用作品资料";
        _temporaryExpressions.text=config.ExperimentalTemporaryExpressions?"临时表达：开启 · 点击关闭":"临时表达：关闭（默认）· 点击开启";
        _storyAuto.text=config.PreviousStoryAutoRecallEnabled?"当前作品问题自动查找：开 · 点击改为模型按需请求":"仅模型按需请求 · 点击开启当前问题自动查找";
        _keyState.text=OpenAICredentialStore.Exists(config.DataDirectory)?"密钥已加密保存 · 不回显":"尚未配置 OpenAI 密钥";
        _consent.text=config.OpenAIDataConsent?"已允许发送相处资料 · 点击停用":"我已了解费用与数据流 · 允许发送相处资料";
        if(_catalogTask is {IsCompleted:true}){try{_available=_catalogTask.GetAwaiter().GetResult();_status.text=config.OpenAIModel+(_available.Contains(config.OpenAIModel)?" 在账户模型列表中。":" 未在账户模型列表中，请核对权限。");}catch(Exception e){ShowNetworkError(e);}_catalogTask=null;}
        if(_testTask is {IsCompleted:true}){try{_status.text=_testTask.GetAwaiter().GetResult();}catch(Exception e){ShowNetworkError(e);}_testTask=null;}
    }
    private void ShowNetworkError(Exception e)=>_status.text=e is InvalidOperationException?e.Message:e is OpenAIProtocolException p?p.Message:e is OperationCanceledException?"请求超时或已取消，没有自动重试。":"请求失败，请检查网络、密钥与账户权限。";
    private void Change(string name,string value)
    {try{if(name is "provider" or "auth_mode"){CancelSettingsNetwork();_customModel.text="";_key.text="";}_config=ConfigStore.SetOpenAIOption(name,value);_status.text="已保存。后续请求使用新设置；在途请求保持原设置。";Refresh(_config);}catch{_status.text="未保存，请检查模型 ID、参数或文件权限。";}}
    private void StoryChange(string name,string value)
    {try{_config=ConfigStore.SetStoryOption(name,value);if(name=="file")_storyPath.text=_config.PreviousStoryFile;_storyStatusAt=DateTimeOffset.MinValue;_status.text=name=="file"?"路径已保存。请核对上方状态，再单独启用资料供给；不会自动授权。":"已保存。后续请求使用新设置；在途请求保持原设置。";Refresh(_config);}catch{_status.text="未保存：请核对本地 JSONL 格式、路径及文件权限。";}}
    private void SelectLocalFirstGame()
    {
        string path=Path.Combine(_config.DataDirectory,"private-story","first-game.collection.jsonl");
        var preview=new BridgeConfig {DataDirectory=_config.DataDirectory,PreviousStoryFile=path};
        if(!PreviousStoryArchive.IsAvailable(preview)){_status.text="本地第一作库不可读，请检查资料安装；当前选择和许可未改动。";return;}
        StoryChange("file",path);
    }
    private void SaveKey(){try{OpenAICredentialStore.Save(_config.DataDirectory,_key.text);_config=ConfigStore.Refresh();_status.text="密钥已加密保存，可核对账户模型或进行极短试连。";}catch{_status.text="密钥未保存，请检查输入或 Windows 加密权限。";}finally{_key.text="";Refresh(_config);}}
    private void DeleteKey(){try{OpenAICredentialStore.Delete(_config.DataDirectory);ConfigStore.SetOpenAIOption("allow_runtime_data","false");_config=ConfigStore.Refresh();_status.text="密钥已删除，相处资料发送已停用。";Refresh(_config);}catch{_status.text="删除未完成，请检查文件权限。";}}
    private void StartNetwork(){_network?.Dispose();_network=new CancellationTokenSource(TimeSpan.FromSeconds(35));_status.text="正在请求 OpenAI，不发送相处资料…";}
    private void FetchModels(){if(_config.UsesChatGPT){BeginChatGPTModels(false);return;}if(Busy)return;StartNetwork();_catalogTask=OpenAISettings.FetchModelsAsync(_config.DataDirectory,_network!.Token);}
    private void TestConnection(){if(_config.UsesChatGPT){BeginChatGPTModels(false);return;}if(!_config.IsOpenAI){_status.text="请先选择 OpenAI 提供方及模型；此按钮测试 OpenAI API。";return;}if(Busy)return;StartNetwork();_testTask=OpenAISettings.TestSelectedConnectionAsync(_config,_network!.Token);}
    private void DeepSeekTaskChoices(string task)
    {
        string control=task=="reflection"?"DeepSeekReflectionModel":"DeepSeekMemoryModel";
        string selected=task=="reflection"?_config.ReflectionModel:_config.AuxModel;
        Choose(control,DeepSeekModels.Choices(_config),DeepSeekModels.SelectionId(selected),value=>
        {
            try{Refresh(ConfigStore.SetDeepSeekTaskModel(task,value));_status.text="DeepSeek 后台模型已保存；后续任务生效。";}
            catch{_status.text="未保存；提供方或配置可能已变化，原设置保留。";}
        },DeepSeekModels.Label);
    }
    private TMP_Text Caption(string text,float x,float y){var t=Text(text,new Vector2(x+71,y),new Vector2(142,30),17);t.color=Muted;return t;}
    private TMP_Text Note(string text,Vector2 at,Vector2 size,float font){var t=Text(text,at,size,font);t.enableWordWrapping=true;t.color=Muted;t.alignment=TextAlignmentOptions.MidlineLeft;return t;}
    private TMP_InputField Input(string name,Vector2 at,Vector2 size,bool password,string placeholder,int limit)
    {
        var field=Box(name,_building??_panel,at,size);var image=field.gameObject.AddComponent<Image>();image.color=new Color(.08f,.12f,.17f,1);
        var view=Box("Viewport",field,Vector2.zero,size-new Vector2(18,4));view.gameObject.AddComponent<RectMask2D>();
        var text=Text("",Vector2.zero,view.sizeDelta,17,view);var hint=Text(placeholder,Vector2.zero,view.sizeDelta,15,view);hint.color=Muted;
        var input=field.gameObject.AddComponent<TMP_InputField>();input.textViewport=view;input.textComponent=text;input.placeholder=hint;input.targetGraphic=image;
        input.lineType=TMP_InputField.LineType.SingleLine;input.contentType=password?TMP_InputField.ContentType.Password:TMP_InputField.ContentType.Standard;input.characterLimit=limit;input.onFocusSelectAll=false;input.richText=false;return input;
    }
    private TMP_Text Button(string name,Vector2 at,Vector2 size,Action action,Transform? parent=null)
    {
        var rect=Box(name,parent??_building??_panel,at,size);var image=rect.gameObject.AddComponent<Image>();NativeMenuStyle.Paint(image,new Color(.10f,.19f,.25f,1));
        var button=rect.gameObject.AddComponent<Button>();button.targetGraphic=image;var nav=button.navigation;nav.mode=Navigation.Mode.None;button.navigation=nav;
        button.onClick.AddListener((UnityEngine.Events.UnityAction)(()=>{NativeUiInputGate.ClaimPointer();action();}));
        var text=Text("",Vector2.zero,size-new Vector2(24,0),18,rect);text.alignment=TextAlignmentOptions.Center;return text;
    }
    private TMP_Text Text(string value,Vector2 at,Vector2 size,float fontSize,Transform? parent=null)
    {
        var rect=Box("Label",parent??_building??_panel,at,size);var text=rect.gameObject.AddComponent<TextMeshProUGUI>();text.font=_font.font;text.fontSize=fontSize;text.fontStyle=FontStyles.Normal;
        text.text=value;text.richText=false;text.raycastTarget=false;text.enableWordWrapping=false;text.overflowMode=TextOverflowModes.Ellipsis;text.color=new Color(.90f,.94f,.96f,1);text.alignment=TextAlignmentOptions.MidlineLeft;return text;
    }
    private static RectTransform Box(string name,Transform parent,Vector2 at,Vector2 size)
    {var go=new GameObject(name);go.AddComponent<RectTransform>();go.transform.SetParent(parent,false);var r=go.GetComponent<RectTransform>();r.anchorMin=r.anchorMax=new Vector2(.5f,.5f);r.anchoredPosition=at;r.sizeDelta=size;return r;}
}
