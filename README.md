# LilithBridge

> To call up a demon, you must learn its name.

[![Version](https://img.shields.io/badge/Version-v0.4.4%20%C2%B7%209a5c6c80-79677f?style=flat-square)](RELEASE-NOTES.zh-CN.md)
[![Game Target](https://img.shields.io/badge/Game-1.2.1%20%C2%B7%20Windows%20x64-6e7781?style=flat-square)](NORMAL-USE.zh-CN.md)
[![Voice](https://img.shields.io/badge/Voice-SenseVoice%20%2B%2048kHz%20Qwen3--TTS-5c786b?style=flat-square)](NORMAL-USE.zh-CN.md)
[![License](https://img.shields.io/badge/License-MIT-3fb950?style=flat-square)](LICENSE)

**让今天的对话、停顿与声音，真正成为明天相处的一部分。**

LilithBridge 是桌面伴侣游戏[《不/存在的莉莉丝》](https://store.steampowered.com/app/4643090/?l=schinese)的非官方开源 MOD。它把游戏里的莉莉丝、云端大语言模型，以及一套从第一性原理重做的**本机连续性记忆架构 × 双子空间语义几何 × 离线 48kHz 双向语音系统**接在一起——不是往桌宠里硬塞一个套壳聊天框，而是让每一次真实说出口的交流、随时插嘴的打断、慢半拍的余韵补话、闲暇时的反复琢磨与跨天联想，都沉淀为真正连续的长期陪伴。

[**安装与使用指南**](NORMAL-USE.zh-CN.md) · [**3 步快速开始**](QUICK-START.md) · [**v0.4.4 更新说明**](RELEASE-NOTES.zh-CN.md) · [**架构入门**](ARCHITECTURE.md) · [**升级兼容边界**](UPGRADE-COMPATIBILITY.zh-CN.md) · [**AI 助手接手与二创指南**](AGENTS.md) · [**问题反馈**](FEEDBACK.zh-CN.md)

---

## 装上之后，桌面上的她会变成什么样？

- **随时打断，所见即所记**：回车开聊，原生气泡与姿态动作自然联动。你可以随时敲字或开口打断她没说完的气泡——程序只将**真正显示到屏幕上的文字**落盘为长期经历，绝不会让没说出口的后半句假装“我们聊过”。
- **没有好感度条，却真的会记事、会改口**：严格贯彻 **Anti-Galgame** 原则，不靠好感度进度条或静态标签把人定死。聊完歇口气时，她会在后台安静回看刚才的对话，沉淀自己的理解；被新经历说服后可以自然改口（`revises`）、按情境分支（`qualify`），或把不再合时宜的旧念头温和「合卷留签」（`settles`）。
- **日常清爽不串戏，聊投机了接住跨天火花**：开启本地轻量化语义召回后，通过**具象经历（`X_exp`）× 抽象默契（`X_abs`）双子空间正交几何**，早安问候绝不翻八百年前的长文旧账；可一旦戳中彼此默契或情绪心境，她能在字面零重合时冷不丁接上几天甚至几周前的弦外之音。
- **离线耳朵 + 48kHz 本地专属声带**：可选接入独立本地 **SenseVoice STT**（按 `F8` 或点按钮开口说话，先看再发）与 **Qwen3-TTS 1.7B + FlashSR 48kHz 超分** 本地语音合成，子句平滑合成、声画气泡精准锁步、无静音空拖且游戏退出零进程残留。
- **傻瓜一键式安装、免解压分卷语音包、无损升级**：向导自动识别游戏目录并默认配齐推荐组件；近 3GB 语音整合包（`.zip.001/.002`）与安装器放同目录即可内存流式**免解压直装**（连压缩软件都不用装）；老玩家覆盖升级 **100% 保留**既有记忆库、配置、API Key 与登录态。

---

## 为什么它不只是个“套壳聊天框”？

市面上并不缺把大模型接进桌宠或角色扮演的粗放套壳方案，但它们大多逃不开“三天变复读机、旧长文串戏、假好感度数值表演”的通病。LilithBridge 选择从底层重做整条工程链路：

| 常见套壳 / 粗放做法 | LilithBridge 的做法 |
|---|---|
| **好感度数值 + 人造遗忘曲线**：靠进度条、`+1` 提示或衰减计时器表演“像真人” | **不搞仿生学，做真实的思想连续性**：原话归档一字不改；主观理解独立存账、条条可溯源，靠新经历自然演化、修订、分支或合卷留签 |
| **程序替模型抢台词**：命中某个旧词就硬规则催问、用死板脚本代角色作答 | **绝不让程序替模型说话**：程序只负责把干净、有出处的线索找全并守好权限边界；这一轮提不提、怎么怼、用什么语气接，永远交给模型自主裁决 |
| **整锅 RAG 上下文乱炖**：无门槛向量 Top-K 把碎片气泡与几千字旧长文灌进提示词 | **双子空间几何 + 结构压倒修辞**：`X_exp / X_abs` 双子空间自适应白化、CSLS 去枢纽化、DPP 去冗余与两跳 `BridgeSparks`，叠加长文阻尼、显著度门控与前缀 KV 缓存分层（实测命中率 **50%+**） |
| **前台卡顿或硬塞整本历史**：每轮等总结阻塞对话，或者把未说完草稿当成既定事实 | **前台零阻塞直答，后台静默消化**：呈现与记忆物理隔离，反思与语义索引退居后台；需要追溯旧细节或原笔记时由模型按需调用 `review_context` / `revisit_thought` 主动翻阅 |

支撑这一切的四条核心工程支柱：

1. **呈现与记忆物理隔离**：气泡只是屏幕演出。[`NativeBubblePresenter.cs`](src/LilithBridge/NativeBubblePresenter.cs) 与 [`BridgeController.Delivery.cs`](src/LilithBridge/BridgeController.Delivery.cs) 按实际播出文字落盘，回读时无损拼回连贯段落；被打断残稿与过渡提示分别在 `T+1` 承接后干净退役；TTS 与气泡共用真实播放节奏门控。
2. **事实归档 × 主观演化双账本 + M1 合卷留签**：原始经历永久保真；`B1` 连续性判断、反思笔记、经验法则与关系理解（`Readings`）绑定来源出处。事后回顾反思按完整问答边界回看近期互动，对历史积累的过时旧笔记执行无损「合卷留签」（隔离常规闲聊噪声、保留显式溯源重访），不强塞无聊总结。
3. **双子空间宽召回（`X_exp × X_abs`）× 四重边界校验**：独立 [`semantic_worker.py`](optional/semantic-worker/semantic_worker.py) 进程将记忆拆分为具象经历（`X_exp`）与抽象默契（`X_abs`）双子空间，经自适应收缩白化、自适应 CSLS 校准、Gram-Schmidt 场景新颖度计算、DPP 去重与 `X_exp → X_abs → X_exp` 两跳跨桥火花把跨词面线索找全（几何计算 `P50 ~1.3ms`），再经来源权限、更正链、长文阻尼与显著度门控把关后交由模型决定。
4. **本地离线双向语音 × 前缀 KV 缓存工程纪律**：[`SpeechToTextWorker.cs`](src/LilithBridge/SpeechToTextWorker.cs) 与 [`TextToSpeechWorker.cs`](src/LilithBridge/TextToSpeechWorker.cs) 经私密 NDJSON 管道与 Windows Job Object（[`ChildProcessJobObject.cs`](src/LilithBridge/ChildProcessJobObject.cs)）严格托管生命周期；提示词采用“固定静态前段 + 步进锚点 + 动态尾槽”结构，兼容现代化推理引擎高前缀缓存复用。

想了解完整架构图、设计取舍与源码阅读地图，请移步 [**架构入门（ARCHITECTURE.md）**](ARCHITECTURE.md)。

---

## 3 步快速开始（首次一键安装与老玩家无损升级通用）

> [!IMPORTANT]
> 已经装过任何旧版的玩家，直接运行新版安装器覆盖即可无损升级，**绝不会清空或覆盖**你原有的 `LilithBridgeData`（含 `%APPDATA%\LilithAI`）、`config.json`、记忆数据库、API Key 与登录态。动手前请先**正常退出游戏**。

1. **获取安装包（与可选语音分卷）**：前往 [**Releases 发布页**](https://github.com/gtxpz2021/LilithBridge/releases/latest)（或同步分发的网盘懒人文件夹）下载 **`LilithBridge-Setup-*.exe`**（面向官方游戏 **1.2.1 · Windows x64**）。
   - 若需要离线语音对话与 48kHz 语音播报：将分卷包 **`LilithBridge-VoicePack-v1.zip.001`** 与 **`.002`** 与安装器放在**同一个文件夹**即可（**无需安装 WinRAR/7-Zip，也不用手动解压合并**）。
2. **双击一键安装 / 升级**：向导自动识别 `Lilith.exe` 并默认选好推荐组件（插件、加载器、本地向量召回与检测到的同目录语音包），确认后点击开始即可。已安装旧版召回组件的老用户会自动原地热更新官方脚本，无需重复下载 8 GiB 环境。
3. **游戏内启用**：启动游戏，点击右上角 **`⋯ → 模型与资料设置`**：
   - 配好模型（填入 DeepSeek / OpenAI API Key，或在 **「认证账户」** 点击 **`Continue with ChatGPT`** 浏览器登录免 Key 直连）；
   - 点一次顶部 **`推荐使用设置`** 补齐记忆与按需回看开关，回到桌面主界面按 **回车键（Enter）**（或按 `F8` 直接开口说话）即可开聊。

完整安装图示、分卷语音包说明与常见问题排查见 [**安装与使用指南**](NORMAL-USE.zh-CN.md)。

---

## 支持的模型与本地可扩展能力

| 模块 | 接入 / 运行方式 | 说明与边界 |
|---|---|---|
| **DeepSeek 官方 / 兼容接口** | `deepseek-flash`（支持看屏图文）、`deepseek-v4-pro` 及自定义模型 ID | 使用自备 API Key；主对话与后台反思可分别指定模型与思考档位；支持自定义兼容 Chat Completions HTTPS 端点 |
| **Sign in with ChatGPT** | 浏览器官方 OAuth 授权选用订阅可用模型（如 `gpt-6.1-terra` 等） | 在游戏内「认证账户」点击 `Continue with ChatGPT` 登录，直接使用账户订阅额度，**无需 API Key**；凭据以 Windows DPAPI 加密留存本机 |
| **OpenAI 官方 API** | `gpt-6.1-terra`、`gpt-6.1-sol`、`gpt-6-astra` 及自定义模型 ID | 通过官方 Responses API（`store=false`）接入，API Key 以 Windows DPAPI 本机加密保存 |
| **本地语义召回（可选）** | 独立 Python 进程运行 `BGE-small-zh-v1.5`（仅做双子空间几何检索，不生成回复） | 占用轻量 CPU 内存；不开启也可依赖近期上下文与词面检索正常聊天，随时可重开向导补装 |
| **本地离线 STT / TTS（可选）** | `SenseVoiceSmall INT8`（按麦转写）+ `Qwen3-TTS 1.7B + FlashSR 48kHz` | 音频流与合成全在本机完成，不上传录音、不强制依赖外部收费配音接口 |
| **外部感官侧车（实验性）** | 默认关闭的独立桌面感知宿主机与微反应节律（[`SensorySidecarClient.cs`](src/LilithBridge/SensorySidecarClient.cs)） | 仍处于实验阶段、离线静默降级，不影响常规聊天与稳定性 |

---

## 数据主权、工程边界与开源许可

- **记忆永远留在你的电脑上**：全部对话归档、`B1` SQLite 数据库、反思笔记、语义索引与离线语音模型均保存在本地 `LilithBridgeData/`。后台反思、看屏截图、第一作回忆引用、语音开关均可独立控制，升级绝不重置既有关闭项。
- **透明的远程与能力边界**：与所有云端大模型应用一样，每次对话生成或后台整理会向你所选择的模型服务商发送本轮实际装配的上下文。长期相处的细腻度仍取决于模型自身理解力与召回命中率——我们不做“永不遗忘、永不误解”的空头承诺，但坚持每一条理解**有源可溯、有错可改**。
- **开源许可范围**：本项目自有 C# 插件源码、Python worker 脚本、一体化安装工具及文档采用根目录 [**MIT License**](LICENSE)。安装包附带的第一作整理语料以及 BepInEx、Python、BGE、SenseVoice、Qwen3-TTS / FlashSR / audiocpp_server 等第三方组件与权重保留各自原有权利、不归入 MIT 范围，且所有大型模型权重与私人配置均**严禁提交至 Git 仓库**。详见 [**许可说明**](LICENSE-STATUS.md) 与 [**第三方声明**](THIRD-PARTY-NOTICES.md)。

---

## 文档与源码导航

- [**架构入门（ARCHITECTURE）**](ARCHITECTURE.md)：从第一性原理看一次对话如何演化为长期相处，含完整架构图与源码阅读地图。
- [**安装与使用（NORMAL-USE）**](NORMAL-USE.zh-CN.md)：首次一键安装、分卷语音包使用、模型配置、无损升级与排查表。
- [**本版更新说明（RELEASE-NOTES）**](RELEASE-NOTES.zh-CN.md)：`v0.4.4` 完整体验变化、全量姿态衣着动画真值表与睡眠唤醒修复、提示词去防御化减负、核显/双显笔记本语音兼容修复与发布产物校验。
- [**升级与回退边界（UPGRADE-COMPATIBILITY）**](UPGRADE-COMPATIBILITY.zh-CN.md)：老用户无损升级、原地热更脚本与安全回退说明。
- [**本地依赖说明（DEPENDENCIES）**](docs/DEPENDENCIES.md) · [**技术附录（TECHNICAL-NOTES）**](docs/TECHNICAL-NOTES.md) · [**AI 助手排障与二创指南（AGENTS）**](AGENTS.md) · [**反馈与脱敏指南（FEEDBACK）**](FEEDBACK.zh-CN.md) · [**GitHub Issues**](https://github.com/gtxpz2021/LilithBridge/issues)

核心插件源码位于 [`src/LilithBridge/`](src/LilithBridge/)，可选召回与语音 Worker 位于 [`optional/`](optional/)，一体化单文件安装器与分卷打包源码位于 [`tools/installer-source/`](tools/installer-source/)。
