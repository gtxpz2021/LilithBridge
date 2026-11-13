using TMPro;
using UnityEngine;

namespace LilithBridge;

internal sealed partial class NativeOpenAISettings
{
    private RectTransform _deepseekPrivacy = null!;
    private TMP_InputField _deepseekKey = null!;
    private TMP_Text _deepseekKeyState = null!, _deepseekService = null!;
    private string _credentialBinding = "";

    private void BuildDeepSeekKeyPage()
    {
        _deepseekPrivacy = Box("DeepSeekPrivacy", _pages[2], Vector2.zero, new Vector2(700, 360));
        _building = _deepseekPrivacy;
        Note("DeepSeek / 当前兼容服务的 API Key", new Vector2(0, 145), new Vector2(666, 32), 20);
        _deepseekKey = Input("DeepSeekKey", new Vector2(-52, 95), new Vector2(560, 36), true,
            "粘贴当前服务的 API Key（隐藏，不回显）", 4096);
        Button("SaveDeepSeekKey", new Vector2(284, 95), new Vector2(98, 36), () => SaveDeepSeekKey(false)).text = "保存 Key";
        _deepseekKeyState = Note("", new Vector2(-45, 52), new Vector2(575, 30), 16);
        Button("ClearDeepSeekKey", new Vector2(284, 52), new Vector2(98, 30), () => SaveDeepSeekKey(true)).text = "清除 Key";
        _deepseekService = Note("", new Vector2(0, -14), new Vector2(666, 86), 17);
        Note("Key 沿用安装器格式，明文保存在本机 config.json；不会回显。\n"
            + "保存只更新密钥，后续请求生效，不会发送测试请求。\n"
            + "对话与后台请求按所选服务计费；请勿分享使用后的配置目录。\n"
            + "关闭面板、切换页签或服务会清空未保存的输入。",
            new Vector2(0, -123), new Vector2(666, 108), 16);
    }

    private void ClearKeyInputs()
    {
        if (_key != null) _key.text = "";
        if (_deepseekKey != null) _deepseekKey.text = "";
    }

    private void RefreshDeepSeekKeyPage(BridgeConfig config)
    {
        string binding = config.DataDirectory + "\n" + config.ModelProvider + "\n" + config.OpenAIAuthMode + "\n" + config.BaseUrl + "\n" + config.DeepSeekKeyOwner;
        if (_credentialBinding != binding) { ClearKeyInputs(); _credentialBinding = binding; }
        _deepseekPrivacy.gameObject.SetActive(!config.IsOpenAI);
        if (config.IsOpenAI) return;
        _deepseekKeyState.text = string.IsNullOrWhiteSpace(config.ApiKey)
            ? "尚未配置当前服务的 Key" : "当前服务的 Key 已设置 · 不回显";
        string host = Uri.TryCreate(config.BaseUrl, UriKind.Absolute, out var endpoint) ? endpoint.Authority : "地址无效";
        _deepseekService.text = "当前服务：" + host + "\n对话模型：" + config.Model
            + "\n可在“对话模型”页选择模型；保存 Key 不会更换服务地址。";
    }

    private void SaveDeepSeekKey(bool clear)
    {
        if (_config.IsOpenAI) { ClearKeyInputs(); return; }
        try
        {
            _config = ConfigStore.SetDeepSeekApiKey(_config, clear ? "" : _deepseekKey.text, clear);
            _status.text = clear ? "当前服务的 Key 已清除；后续请求需重新填写。"
                : "当前服务的 Key 已保存；后续请求生效，没有发送测试请求。";
        }
        catch { _status.text = "Key 未保存。请检查输入、服务地址或文件权限；配置变化时请重新打开设置。"; }
        finally { ClearKeyInputs(); Refresh(_config); }
    }
}
