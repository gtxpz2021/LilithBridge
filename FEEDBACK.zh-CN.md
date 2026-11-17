# 问题反馈与日志脱敏指南

遇到无法排查的问题时，欢迎提交到 [GitHub Issues](https://github.com/gtxpz2021/LilithBridge/issues)。

## 提交前请带上这几项信息

1. **版本身份**：在 Release 页面下载的 EXE/DLL 版本（例如 `0.3.150.105 · 4de072f7`），可通过 `BepInEx\LogOutput.log` 开头确认。
2. **发生时间与最短复现步骤**：例如“刚升级后首次启动”“连续聊天几轮后无回复”“勾选本地召回时某一步下载卡住”。
3. **按需挑选的脱敏日志**（请先打开看一眼，确认无私密信息再传）：
   - 运行与插件加载日志：游戏目录下的 `BepInEx\LogOutput.log`
   - 对话／事件遥测（仅需问题时段对应片段）：`LilithBridgeData\telemetry\v4-native-events.jsonl`
   - 安装或本地召回异常：`LilithBridgeData\.setup-checks\local-recall-check.log` 或运行目录的 `environment-check.log`

> [!CAUTION]
> **以下文件绝对不要上传或公开：**
> - `config.json`（及 `.bak` 配置备份，**内含明文 API Key**）
> - `LilithBridgeData\chatgpt-signin\` 目录（包含登录凭据）
> - 完整的 `LilithBridgeData\` 记忆目录或私人聊天记录原文
