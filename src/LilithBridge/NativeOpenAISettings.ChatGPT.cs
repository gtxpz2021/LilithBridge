using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace LilithBridge;

internal sealed partial class NativeOpenAISettings
{
    private TMP_Text _authMode = null!, _accountState = null!, _planPrivacy = null!, _backgroundDetails = null!;
    private string _apiBackgroundDetails = "";
    private GameObject[] _apiPrivacyWidgets = Array.Empty<GameObject>();
    private Task<string>? _authTask;
    private readonly ChatGPTSignInLifetime _signIn=new();
    private TMP_Text _signInLabel=null!;
    private Task<ChatGPTModel[]>? _planModelsTask;
    private bool _planModelBackground, _showPlanWelcome;
    private string _catalogBinding = "";
    private void CaptureApiPrivacyWidgets()
    {
        _apiPrivacyWidgets = _pages[2].GetComponentsInChildren<RectTransform>(true)
            .Where(r => r.parent == _pages[2] && r.gameObject.name != "RuntimeDataPermission").Select(r => r.gameObject).ToArray();
        _building = _pages[2];
        _planPrivacy = Note("当前可使用本应用独立授权的 ChatGPT 计划额度。\n角色输入、所选历史、记忆、工具结果和获准图片仍会发给 OpenAI。\n首次登录后可一次采用推荐设置；已有关闭项保留。\nAPI 密钥保留。认证账户页可登录、断开连接和 Manage usage。\n这不会读取你的 ChatGPT 对话或 Codex 登录凭据。", new Vector2(0, 40), new Vector2(666, 200), 17);
    }
    private void BuildChatGPTPage()
    {
        _building = _pages[5];
        Caption("OpenAI 认证", -330, 144);
        _authMode = Button("AuthModeSelect", new Vector2(113, 144), new Vector2(440, 38), () =>
            Choose("AuthModeSelect", new[] { "api_key", "chatgpt" }, _config.OpenAIAuthMode,
                v => Change("auth_mode", v), v => v == "chatgpt" ? "ChatGPT 计划额度（独立登录）" : "API 密钥（按 API 计费）"));
        _accountState = Note("", new Vector2(0, 97), new Vector2(666, 44), 16);
        _signInLabel=Button("ChatGPTSignIn", new Vector2(-169, 47), new Vector2(330, 40), () =>
        {
            if(_signIn.Pending){_signIn.Cancel();_status.text="正在取消本次浏览器登录。";return;}
            BeginChatGPTSignIn(false);
        });_signInLabel.text="Continue with ChatGPT";
        Button("ChatGPTAccounts", new Vector2(169, 47), new Vector2(330, 40), ChooseChatGPTAccount).text = "切换 / 添加账户";
        Button("ChatGPTModels", new Vector2(-169, -2), new Vector2(330, 38), () => BeginChatGPTModels(false)).text = "刷新 ChatGPT 模型目录";
        Button("ChatGPTDisconnect", new Vector2(169, -2), new Vector2(330, 38), BeginChatGPTSignOut).text = "断开 ChatGPT 连接";
        Button("ChatGPTManageUsage", new Vector2(0, -51), new Vector2(666, 38), ChatGPTAuth.OpenUsageFromUserAction).text = "Manage usage · 管理 ChatGPT 用量";
        Note("所选对话、记忆、反思与作品片段会发送给 OpenAI。\n首次登录后可一次采用推荐；已有关闭项保留。\n后台缺省开启；不默认启用图片或自动截图。\n支持可用 Plus 等账户；模型和额度以账户为准。\n目录可见不等于生成通过；不自动回退 API 计费。", new Vector2(0, -128), new Vector2(666, 100), 16);
        _apiBackgroundDetails = _backgroundDetails.text;
    }
    private bool ChatGPTBusy => Busy || _authTask != null || _planModelsTask != null || _signIn.Busy;
    private CancellationToken StartChatGPTNetwork(bool signIn)
    {
        _network?.Cancel(); _network?.Dispose();
        _network = new CancellationTokenSource(signIn ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(40));
        return _network.Token;
    }
    private void BeginChatGPTSignIn(bool addAccount)
    {
        if (ChatGPTBusy) return;
        string root = _config.DataDirectory;
        _status.text = "请在系统浏览器中选择账户、核对 LilithBridge 并决定是否允许计划用量；这里不会代你批准。";
        _signIn.TryStart(root,async (diagnostics,token) =>
        {
            using var auth = new ChatGPTAuth(root,diagnostics:diagnostics);
            var connection = await auth.SignInFromUserActionAsync(addAccount, token).ConfigureAwait(false);
            ConfigStore.Refresh();
            bool welcome=connection.Sharing&&ChatGPTCredentials.Read(root).Active?.WelcomeShown==false;
            return new SignInResult(true,connection.Sharing,welcome,connection.Sharing
                ?"ChatGPT 连接已保存；请求仍按当前选择的认证方式运行，请选账户模型并核对资料发送许可。"
                :"身份登录已完成，尚未获准计划用量；不会发送模型请求。");
        });
    }
    private void BeginChatGPTSignOut()
    {
        if (ChatGPTBusy) return;
        string root = _config.DataDirectory; var token = StartChatGPTNetwork(false);
        ConfigStore.RevokeChatGPTConnection(root);
        _status.text = "正在断开本应用的 ChatGPT 连接；API 密钥和其他应用的登录不变。";
        _authTask = Task.Run(async () =>
        {
            using var auth = new ChatGPTAuth(root);
            string notice = await auth.SignOutFromUserActionAsync(token).ConfigureAwait(false);
            ConfigStore.Refresh(); return notice;
        }, token);
    }
    private void ChooseChatGPTAccount()
    {
        if (ChatGPTBusy) return;
        try
        {
            var accounts = ChatGPTCredentials.Read(_config.DataDirectory);
            var names = accounts.Profiles.ToDictionary(p => p.ClientId, p => p.Email.Length > 0 ? p.Email : "已保存的账户");
            Choose("ChatGPTAccounts", new[] { "add" }.Concat(names.Keys).ToArray(), accounts.ActiveClient, value =>
            {
                if (value == "add") { BeginChatGPTSignIn(true); return; }
                using var locked = ChatGPTCredentials.Lock(_config.DataDirectory);
                var current = ChatGPTCredentials.Read(_config.DataDirectory);
                if (!current.Profiles.Any(p => p.ClientId == value)) throw new InvalidOperationException();
                current.ActiveClient = value; ChatGPTCredentials.Save(_config.DataDirectory, current);
                Refresh(ConfigStore.Refresh()); _status.text = "已切换本应用保存的 ChatGPT 账户。请刷新模型目录；在途旧账户请求已撤销。";
            }, value => value == "add" ? "添加其他账户 / 工作区" : names[value]);
        }
        catch { _status.text = "本应用的账户记录暂不可用，请稍后重试。"; }
    }
    private void BeginChatGPTModels(bool background)
    {
        if (ChatGPTBusy) return;
        if (_config.OpenAIAuthMode != "chatgpt") { _status.text = "请先在认证账户页选择 ChatGPT 计划额度。"; return; }
        var connection = ChatGPTCredentials.Describe(_config.DataDirectory);
        if (!connection.Sharing) { _status.text = "请先 Continue with ChatGPT，并在浏览器授权计划用量。"; return; }
        string root = _config.DataDirectory; _catalogBinding = connection.Binding; _planModelBackground = background;
        var token = StartChatGPTNetwork(false); _status.text = "正在获取此账户的 ChatGPT 模型目录，不发起生成。";
        _planModelsTask = Task.Run(async () => { using var auth = new ChatGPTAuth(root); return await auth.ModelsAsync(connection.Binding, token).ConfigureAwait(false); }, token);
    }
    private void RefreshChatGPTPage(BridgeConfig config)
    {
        _authMode.text = (config.OpenAIAuthMode == "chatgpt" ? "ChatGPT 计划额度" : "API 密钥（按 API 计费）") + "  ▾";
        var connection = ChatGPTCredentials.Describe(config.DataDirectory);
        _accountState.text = connection.Connected
            ? connection.Email + "\n" + (connection.Sharing ? "已连接 · 已允许 ChatGPT 计划用量" : "身份已连接 · 尚未允许计划用量")
            : "本应用尚未连接 ChatGPT；不会借用其他应用的登录状态。";
        _signInLabel.text=_signIn.Pending?"取消本次登录":"Continue with ChatGPT";
        if(_signIn.Pending)_accountState.text="浏览器登录仍在进行；关闭面板不会中断。\n如需结束，请点击“取消本次登录”。";
        if(_signIn.TryTakeResult(out var signInResult))
        {
            _status.text=signInResult!.Notice;_showPlanWelcome=signInResult.ShowWelcome;
            _config=ConfigStore.Load();
        }
        if(_showPlanWelcome&&config.UsesChatGPT){_showPlanWelcome=false;ShowPlanWelcome();}
        foreach (var widget in _apiPrivacyWidgets) widget.SetActive(!config.UsesChatGPT);
        _planPrivacy.gameObject.SetActive(config.UsesChatGPT);
        _customModel.interactable = !config.UsesChatGPT;
        if(_customModel.placeholder is TMP_Text hint)hint.text=config.UsesChatGPT?"点击上方模型，从当前账户目录选择":"自定义 API 模型 ID（可选）";
        _pages[0].Find("ApplyModel").GetComponent<Button>().interactable = !config.UsesChatGPT;
        _backgroundDetails.text = config.UsesChatGPT
            ? "此 ChatGPT 账户的后台工作缺省开启；明确关闭项保留。记忆提取、反思等会使用计划额度。\n可用推荐设置核对账户 Terra / low；缺少时请自行选择，不回退到 API。\n采用普通 HTTP/SSE 请求，不使用服务器 background 模式，也不发送输出上限参数。"
            : _apiBackgroundDetails;
        if (_authTask is { IsCompleted: true })
        {
            try { _status.text = _authTask.GetAwaiter().GetResult(); _config = ConfigStore.Load(); }
            catch (ChatGPTAuthException e) { _status.text = e.Message; }
            catch (OperationCanceledException) { _status.text = "ChatGPT 登录或连接操作已取消。"; }
            catch { _status.text = "ChatGPT 连接操作未完成，请重新尝试；原有 API 配置保留。"; }
            _authTask = null;
            if (_showPlanWelcome&&config.UsesChatGPT) { _showPlanWelcome = false; ShowPlanWelcome(); }
        }
        if (_planModelsTask is { IsCompleted: true })
        {
            try
            {
                var models = _planModelsTask.GetAwaiter().GetResult();
                if (connection.Binding == _catalogBinding && config.OpenAIAuthMode == "chatgpt")
                {
                    _status.text = models.Length == 0 ? "此账户未返回可选模型；未改动现有模型。" : ChatGPTProtocol.CatalogAvailabilityNotice(models,_planModelBackground?config.ChatGPTBackgroundModel:config.ChatGPTModel);
                    if (_normalUseSelection is { } selected)
                    {
                        _normalUseSelection = null;
                        ApplyNormalUse(selected, _catalogBinding, models.Select(m => m.Slug).ToArray());
                    }
                    else if (models.Length > 0)
                    {
                        bool background = _planModelBackground;
                        string selectedBinding = _catalogBinding;
                        string control = background ? "BackgroundModel" : (_pages[5].gameObject.activeSelf ? "ChatGPTModels" : "ModelSelect");
                        Choose(control, models.Select(m => m.Slug).ToArray(), background ? config.ChatGPTBackgroundModel : config.ChatGPTModel,
                            value =>
                            {
                                try { Refresh(ConfigStore.SetChatGPTModelFromCatalog(background ? "background.model" : "model", value, selectedBinding)); _status.text="账户模型已保存；后续请求生效。"; }
                                catch { _status.text="账户或认证方式已变化，请刷新模型目录；未改动原配置。"; }
                            }, value => models.First(m => m.Slug == value).DisplayName + " · " + value);
                    }
                }
            }
            catch (ChatGPTAuthException e) { _status.text = e.Message; }
            catch (OperationCanceledException) { _status.text = "模型目录请求已取消。"; }
            catch { _status.text = "未取得 ChatGPT 模型目录；未改动现有模型。"; }
            _planModelsTask = null; _normalUseSelection = null;
        }
    }
    private void ShowPlanWelcome() => ShowNormalUseSetup(true);
    private void CloseChatGPTTasks()
    {
        if (_authTask != null) Observe(_authTask); if (_planModelsTask != null) Observe(_planModelsTask);
        _authTask = null; _planModelsTask = null; _normalUseSelection = null;
    }
    private void CancelSettingsNetwork()
    {
        _signIn.SelectionChanged();
        _network?.Cancel();
        if (_catalogTask != null) Observe(_catalogTask); if (_testTask != null) Observe(_testTask);
        _catalogTask = null; _testTask = null; CloseChatGPTTasks();
    }
    internal void Dispose()
    {
        _signIn.Dispose();Close();
    }
}
