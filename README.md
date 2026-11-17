# LilithBridge

> To call up a demon, you must learn its name.

**让今天的对话，成为明天相处的一部分。**

LilithBridge 是桌面伴侣游戏[《不/存在的莉莉丝》](https://store.steampowered.com/app/4643090/?l=schinese)的非官方 MOD。它把游戏中的莉莉丝、大语言模型和本地记忆连接起来：你可以和她自由聊天，回顾曾经的经历，也让新的交流逐渐影响她对你和你们之间关系的理解。

我们想做的不只是一个套着角色外观的聊天框，而是一个能够长期相处的文学角色。她可以记得、重新理解、有自己的表达，也容得下你的变化和彼此的分歧。记忆不是给人贴标签，而是让相处有前因后果。

[**下载最新版**](https://github.com/gtxpz2021/LilithBridge/releases/latest) · [**安装与使用**](NORMAL-USE.zh-CN.md) · [**本版更新**](RELEASE-NOTES.zh-CN.md) · [**升级兼容性**](UPGRADE-COMPATIBILITY.zh-CN.md) · [**反馈问题**](https://github.com/gtxpz2021/LilithBridge/issues)

## 能做什么

- **在游戏里自然聊天。** 用原生输入框和对话气泡交流，支持流式回复；想插话时可以打断，不必等她说完。
- **让经历留下来。** 对话和相关经历保存在本机，后续可以按文字线索或语义回顾；同一条回复拆成的多个气泡在回忆上下文中会自动拼回整段，避免碎片与重复回声挤占注意力。
- **让理解随相处更新。** 后台记忆整理与反思可以连接近期交流、收敛或修订旧理解，让你后来的澄清有机会参与之后的回应。
- **按需找材料，而不是背一整本档案。** 当前上下文、相关旧事与作品片段共同支持对话；模型需要更多信息时，也可以继续回看。
- **选择合适的模型。** 支持 DeepSeek、OpenAI API，以及游戏内 Sign in with ChatGPT；主对话与后台工作可以分别配置。
- **一键安装与保留存档更新。** 同一向导内集成加载器准备、可选本地召回、旧插件无损升级与回退备份；原有配置、记忆库、登录态、Key 与存档保持原位。

## 模型怎么选

| 接入方式 | 当前选项 | 适合怎样使用 |
|---|---|---|
| **DeepSeek API** | Flash、Pro | 使用自己的 API Key；主对话和后台模型分别选择，后台记忆与反思使用非思考模式 |
| **Sign in with ChatGPT** | 登录后从账户可用模型目录选择 | 在游戏内完成浏览器登录，使用该账户可用额度，无需填写 OpenAI API Key |
| **OpenAI API** | Terra、Sol、Astra 预设，以及自定义模型 ID | 使用自己的 API Key，按模型配置思考档位和输出预算 |

**聊天不要求安装本地大模型。** 可选的本地 BGE 语义召回只负责寻找相关旧经历，不负责生成回答；需要时可以在安装向导中一并准备。

## 快速开始（首次安装与保留存档更新通用）

1. 准备 **Windows x64** 和已安装的正版游戏（面向官方 **1.2.1**）。
2. 打开 [Releases](https://github.com/gtxpz2021/LilithBridge/releases)，下载 **`LilithBridge-Setup-0.3.150.105-cn21-4de072f7.exe`**；无需手工合并资源包。
3. **正常退出游戏**，双击单文件安装器，确认自动检测到的游戏目录（默认保留原有 `LilithBridgeData`、`config.json` 与已有服务/Key/记忆），勾选确认并点击安装。
4. 启动游戏，在 **⋯ → 模型与资料** 中按需核对 DeepSeek / OpenAI / Continue with ChatGPT 设置与“推荐使用设置”（升级不会覆盖已有显式关闭项）。

详细登录步骤、旧设置处理与回退说明见 [安装与使用](NORMAL-USE.zh-CN.md) 与 [升级兼容性](UPGRADE-COMPATIBILITY.zh-CN.md)。

## 当前发行版（0.3.150.105 / 4de072f7）

- **产品版本**：`0.3.150.105-posture-reflection-cache.1`（`LilithBridge.dll` SHA-256：`4de072f7e01f0e820f1ae6c7069963165bf969d4e99f21b8e9d8638e54bcf492`）
- **安装器版本**：`0.1.4.21-posture-reflection-cache.release.1`（`installer_schema: 15`）
- **主要更新**：
  - 反思与人设表达解绑、取消审计腔约束、区分心理回望与外部事实并支持相关性门控；
  - 修复连续多轮对话后因递归校验导致的单核 CPU 100% 卡死与跨长帧假超时；
  - 前缀缓存重排、静态工具定义与锚点步进窗口优化（`dialogue.prompt_cache_history_stride`，默认 `8`），精简默认遥测；
  - 同回复气泡碎片在 `B1` 上下文拼合，折叠与近期/召回区的重复供给并过滤已完整展示的假草稿。

完整更新细节、校验结果与兼容说明见 [RELEASE-NOTES.zh-CN.md](RELEASE-NOTES.zh-CN.md)。

## 数据与控制

经历和记忆保存在本机（默认隔离于游戏目录下的 `LilithBridgeData/`）。对话、后台反思或按需回看时，选中的材料会发送给你配置的模型服务；你可以在游戏内随时关闭后台任务、资料供给或截图功能。升级保留所有已有数据目录与配置。

安装包内含第一作整理语料，方便按需取用；本项目自有源码与工具采用 **MIT**，原作语料及第三方组件保留各自权利且语料正文不包含在本源码仓库与 `source ZIP` 中，详见 [许可与来源说明](LICENSE-STATUS.md)。

## 继续了解与参与

- [架构入门](ARCHITECTURE.md)：一句话怎样成为共同经历，以及记忆、检索和反思怎样衔接。
- [安装与正常使用](NORMAL-USE.zh-CN.md)：登录、推荐设置、后台开关、升级和问题反馈。
- [本地召回依赖](docs/DEPENDENCIES.md)：可选语义检索的准备方法。
- [Issues](https://github.com/gtxpz2021/LilithBridge/issues)：报告问题或交流建议。

核心源码位于 `src/LilithBridge/`，安装器位于 `tools/installer-source/`；`SOURCE-FILES.json` 与 `SOURCE-IDENTITY.json` 记录对应发布源码快照的身份校验值。
