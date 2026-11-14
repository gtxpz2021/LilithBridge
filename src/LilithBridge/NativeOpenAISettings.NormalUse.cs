using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace LilithBridge;
internal sealed partial class NativeOpenAISettings
{
    private BridgeConfig? _normalUseSelection;
    private void MarkPlanWelcome()
    {
        if (!_config.UsesChatGPT) return;
        try
        {
            using var locked = ChatGPTCredentials.Lock(_config.DataDirectory);
            var accounts = ChatGPTCredentials.Read(_config.DataDirectory);
            if (accounts.Active is { } p) { p.WelcomeShown = true; ChatGPTCredentials.Save(_config.DataDirectory, accounts); }
        }
        catch { _status.text = "首次使用提示状态未保存；原有连接保留。"; }
    }
    private RectTransform SetupCard(string name, string title, string note)
    {
        CloseChoices();
        _choices = Box(name, _panel, Vector2.zero, _panel.sizeDelta).gameObject;
        _choices.AddComponent<Image>().color = new Color(0, 0, 0, .7f);
        var card = Box("SetupCard", _choices.transform, Vector2.zero, new Vector2(680, 410));
        NativeMenuStyle.Paint(card.gameObject.AddComponent<Image>(), new Color(.07f, .13f, .18f, 1));
        Text(title, new Vector2(0, 166), new Vector2(620, 38), 23, card);
        var text = Text(note, new Vector2(0, 46), new Vector2(620, 172), 17, card);
        text.enableWordWrapping = true; text.alignment = TextAlignmentOptions.MidlineLeft;
        return card;
    }
    private void ShowNormalUseSetup(bool welcome = false)
    {
        var selected = _config;
        string provider = DialogueProviderOptions.Provider(selected);
        var card = SetupCard("NormalUseChoice", welcome ? "You’re using your ChatGPT plan" : "推荐使用设置 · " + provider,
            "所选对话、历史、记忆、反思及按需检索的作品片段会发给 " + provider + "。\n"
            + "使用所选认证额度，API 按量计费；不会整库上传。\n"
            + "补齐理解保存和按需回看；已有关闭项、模型与预算保留。\n"
            + "后台工作缺省开启；ChatGPT 优先 Terra / low，须账户目录可用。\n"
            + "不改变图片、自动截图或主动性设置。\n"
            + "可在密钥与隐私、后台与费用、作品资料页关闭许可。\n"
            + (NormalUseSetup.BackgroundEnabled(selected) ? "当前普通后台工作：开启。" : "后台有关闭项；下方明确开启按钮包含逐轮提取。"));
        Button("UseRecommended", new Vector2(0, -71), new Vector2(430, 40), () =>
        {
            if (welcome) MarkPlanWelcome();
            CloseChoices();
            if (selected.UsesChatGPT)
            {
                if (ChatGPTBusy) { _status.text = "请等待当前账户操作完成后再采用推荐；原配置保留。"; return; }
                _normalUseSelection = selected;
                BeginChatGPTModels(false);
                if (_planModelsTask == null) _normalUseSelection = null;
            }
            else ApplyNormalUse(selected);
        }, card).text = "知情并采用推荐 · 保留已有关闭项";
        bool enable = !NormalUseSetup.BackgroundEnabled(selected);
        Button("ToggleNormalBackground", new Vector2(0, -121), new Vector2(430, 38), () =>
        {
            if (welcome) MarkPlanWelcome();
            CloseChoices(); ApplyNormalUse(selected, background: enable);
        }, card).text = enable ? "明确开启普通记忆提取与反思工作" : "关闭普通记忆提取与反思工作";
        Button("KeepNormalUse", new Vector2(0, -169), new Vector2(250, 36), () =>
        { if (welcome) MarkPlanWelcome(); CloseChoices(); }, card).text = "保留设置 / 返回";
    }
    private void ApplyNormalUse(BridgeConfig selected, string? binding = null, string[]? catalog = null, bool? background = null)
    {
        try { var result = ConfigStore.SetNormalUse(selected, binding, catalog, background); Refresh(result.Config); _status.text = result.Notice; }
        catch (Exception e) { _status.text = e is InvalidOperationException ? e.Message : "未更改推荐设置，请核对配置；原选择保留。"; }
    }
    private void ConfirmProviderChange(string provider)
    {
        if (provider == _config.ModelProvider) return;
        string label = provider == "openai" ? "OpenAI" : "DeepSeek";
        var card = SetupCard("ProviderDataChoice", "切换到 " + label,
            "切换后，获准的对话、历史、记忆、反思及作品片段将发送给 " + label + "。\n"
            + "会沿用该提供方已保存的认证、资料许可和后台选择；API 按量计费。\n"
            + "请确认这是你要使用的资料接收方。不会复制凭据或整库上传。\n"
            + "现有关闭项和模型预算保留；可在各设置页关闭许可。");
        Button("ConfirmProvider", new Vector2(0, -71), new Vector2(360, 40), () =>
        { CloseChoices(); Change("provider", provider); }, card).text = "确认资料接收方并切换";
        Button("CancelProvider", new Vector2(0, -135), new Vector2(250, 38), CloseChoices, card).text = "取消";
    }
}
