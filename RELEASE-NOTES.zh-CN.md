# LilithBridge v0.4.1 · `310561f9`

面向官方游戏 **1.2.1 · Windows x64**。  
在 `v0.4` 清理掉跨会话复活与旧残稿病灶的基础上，**`v0.4.1` 完成了一次从「记忆几何」「反思诚实度」「本地 48kHz 双向语音」到「真正傻瓜一键式安装」的全栈升级。**

> [!IMPORTANT]
> **怎么安装、怎么升级（首次安装与老玩家无损覆盖通用）**
> - **核心插件单文件**：从 [**Releases 发布页**](https://github.com/gtxpz2021/LilithBridge/releases/latest) 下载 **`LilithBridge-Setup-*.exe`**（或从网盘懒人整合文件夹整包获取）。
> - **想直接用上本地语音（SenseVoice 听写 + 48kHz 莉莉丝原声）**：把 **`LilithBridge-VoicePack-v1.zip.001`** 与 **`.002`** 跟 `Setup.exe` 放在**同一个文件夹里**直接运行安装器即可——向导会在内存里流式直装，**不用装压缩软件、不用手动解压合并、不占双倍磁盘空间**。
> - **绝对保留存档**：覆盖升级 **100% 保留**你原有的 `LilithBridgeData`（含 `%APPDATA%\LilithAI`）、`config.json`、记忆库、笔记、`B1` SQLite、DeepSeek Key、ChatGPT 登录态与游戏原生存档。动手前请先**正常退出游戏**。

---

## 一、这一版玩起来有什么明显变化？

### 1. 真正的本地耳朵 + 48kHz 专属声带，且再也不“卡泡吞音”
- **本地离线语音输入（STT）**：内置 `SenseVoiceSmall INT8` + `Silero VAD`。不想打字时点输入框旁的语音按钮或按 **`F8`** 即可直接说话，转写文字先留在输入框里让你看一眼，按回车才发出，录音流不留盘、不上传。
- **48kHz 本地莉莉丝原声（TTS）**：内置微调 `Qwen3-TTS 1.7B` 声学模型 + `FlashSR` 48kHz 频带超分。
- **彻底修掉气泡拖长与末尾吞帧跑飞**：为多气泡切分后的子句补全语音合成末尾软标点闭合（`normalize_clause_for_synthesis`）与 12Hz Codec 动态帧上限（`estimate_clause_max_tokens`），隔离「静音等待超时（`3.2s`）」与「真实播放保护（`22.0s`）」，不再因为切句逗号导致整段卡死或哑巴气泡长期不消失；并通过 **Windows Job Object** 彻底根除退出游戏后 `audiocpp_server.exe` 残留占用显存的问题。

### 2. 双子空间语义几何（`X_exp × X_abs`）：日常极清爽，聊深处自带火花
- **拆开“具象经历”与“抽象默契”两个空间**：以前单一全局白化会把日常问候的琐事和长期感悟混在一个桶里挤占空间。现在本地 BGE 进程在 **`X_exp`（具象）** 与 **`X_abs`（抽象）** 各自独立白化与去枢纽化（自适应收缩 `k` 与 CSLS），防止“万金油长句”霸榜。
- **两跳跨桥火花（`BridgeSparks`）与 `Readings` 护航**：支持 `X_exp → X_abs → X_exp` 跨空间联想，即使眼前没提到任何旧关键词，也能在心情或语境贴合时顺出几天甚至几周前的共鸣；同时为 `Readings` 关系理解提供专门的 `DomainRank` 保护。

### 3. 历史旧本子「合卷留签」（M1 无损归架），不再硬凑废话反思
- **早期旧笔记与教条不再日常刷屏**：对于此前版本积累的过时旧笔记/经验教条，采用温和无损的**「合卷留签」**机制收归书架——常规闲聊时零串戏、零压迫，只有你明确追问或她主动翻阅时才按书签查回原文。
- **去模板化反思提示**：去掉容易把模型训成“填表客服”的强迫总结提示词，让后台反思像真正空闲复盘一样，有真体会才记、没必要就安静跳过（`no_new_note` / `no_retention`）。

### 4. 更傻瓜的“非常一键式”安装体验与无损热升级
- **默认配齐推荐项，减少来回勾选**：自动识别 `Lilith.exe` 目录并默认勾选插件、加载器、本地向量召回与同目录下检测到的分卷语音包，不用再对着十几个术语逐项打勾。
- **老玩家平滑热升级**：向导自动识别并原地更新 `semantic_worker.py` 与 `tts_worker.py`，不再误报现有文件变更阻断或强索 `8 GiB` 重复空间。

---

## 二、发行文件说明

- **`LilithBridge-Setup-0.4.1-cn25-310561f9.exe`**：单文件一键安装／升级程序（普通聊天只下这一个即可）。
- **`LilithBridge-VoicePack-v1.zip.001` / `.002` + `VOICEPACK-SHA256SUMS.txt`**：可选本地双向语音分卷整合包（共约 2.9 GB，每卷 `< 1.85 GiB` 适配 GitHub 单文件限制与网盘分发；与 `Setup.exe` 同目录放一起即可免解压直装）。
- **`LilithBridge-installer-0.4.1-cn25-310561f9.zip`**：供旧版安装器在线检测与协议交接的更新包。
- **`LilithBridge-source-0.4.1-cn25-310561f9.zip`**：清洁 MIT 开源源码包（不含模型权重、私密配置及非 MIT 语料）。

详细安装与常见问题见 [安装与使用指南](NORMAL-USE.zh-CN.md)，升级兼容与热更新说明见 [升级与回退边界](UPGRADE-COMPATIBILITY.zh-CN.md)，遇到问题请附脱敏日志提交至 [问题反馈指南](FEEDBACK.zh-CN.md)。

---

## 三、技术更新明细与源码改动（开发者／进阶参考）

<details>
<summary>点击展开完整技术变更、核心文件与回归验证明细（相对 <code>v0.4 / ffe08b05</code>）</summary>

### 1. 本地离线语音系统（STT + TTS 48kHz）与稳定性加固
- **本地离线语音听写（STT）**：新增 [SpeechToTextWorker.cs](src/LilithBridge/SpeechToTextWorker.cs)、[SpeechToTextSession.cs](src/LilithBridge/SpeechToTextSession.cs)、[BridgeController.Stt.cs](src/LilithBridge/BridgeController.Stt.cs)、[ConfigStore.Stt.cs](src/LilithBridge/ConfigStore.Stt.cs)、[NativeInputField.Stt.cs](src/LilithBridge/NativeInputField.Stt.cs) 与 [stt_worker.py](optional/stt-worker/stt_worker.py)，基于 `sherpa-onnx` + `SenseVoiceSmall INT8` + `Silero VAD` 实现全内存热键语音转写与同音纠偏。
- **本地离线语音合成（TTS + 48kHz FlashSR BWE）**：新增 [TextToSpeechWorker.cs](src/LilithBridge/TextToSpeechWorker.cs)、[TextToSpeechCoordinator.cs](src/LilithBridge/TextToSpeechCoordinator.cs)、[TextToSpeechPlayer.cs](src/LilithBridge/TextToSpeechPlayer.cs)、[BridgeController.Tts.cs](src/LilithBridge/BridgeController.Tts.cs)、[ConfigStore.Tts.cs](src/LilithBridge/ConfigStore.Tts.cs) 与 [tts_worker.py](optional/tts-worker/tts_worker.py)（SHA-256 `43922a80eec1658bd1cd3eed6261f93763ca339f7c11801110a94a9c7f261ec4`）。
- **EOS 跑飞防护、气泡双节奏锁与进程树回收**：
  - 在 `tts_worker.py` 新增 `normalize_clause_for_synthesis`（仅在送入声学合成前闭合末尾 `，/、/：` 为 `。`）、`estimate_clause_max_tokens`（12Hz `42..360` 帧上界）、动态 HTTP 超时、`_recover_wedged_server()` 自动恢复与 `_WinJobGuard`；
  - 新增 [ChildProcessJobObject.cs](src/LilithBridge/ChildProcessJobObject.cs)（`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`），宿主异常或退出时由操作系统内核级连带回收 STT/TTS 及其子进程树；
  - 在 [TextToSpeechCoordinator.cs](src/LilithBridge/TextToSpeechCoordinator.cs) 与 [NativeBubblePresenter.cs](src/LilithBridge/NativeBubblePresenter.cs) 新增 `HasPlayableChunkForBubble`、接收无 `utterance_id` 全局退出事件、将首泡音频等待收敛至 `MaxTtsFirstBubbleWaitSeconds = 3.2f`，并将静音等待（`MaxSilentBubbleHoldSeconds = 3.2f`）与真实播音持泡（`MaxNativeBubbleHoldSeconds = 22.0f`）解耦。

### 2. 双子空间语义几何（`X_exp × X_abs`）、线编码硬化与 M1 合卷留签
- **双子空间正交几何与联想火花**：升级 [semantic_worker.py](optional/semantic-worker/semantic_worker.py)（SHA-256 `aa41f9ae6b60a553442aded87d054da026fc7f20cdc06d4b774e041726411f0c`）、[MemoryStore.SemanticRecall.cs](src/LilithBridge/MemoryStore.SemanticRecall.cs) 与 [SemanticRecallWorker.cs](src/LilithBridge/SemanticRecallWorker.cs)，实现 `X_exp` / `X_abs` 各自自适应收缩白化、自适应 CSLS 校准、Gram-Schmidt 场景锥新颖度、`X_exp -> X_abs -> X_exp` 两跳 `BridgeSparks`、`DomainRank` 守护以及全精度二进制 `SemanticWireCodec`。
- **M1 合卷留签与反思去教条**：更新 [MemoryStore.Reflections.cs](src/LilithBridge/MemoryStore.Reflections.cs) 等模块，引入无损历史书签隔离前缀 `EpochShelvedReasonPrefix`，并移除僵化规训式反思指令，保留高自由度反思判断。

### 3. 一体化向导、分卷 VoicePack 流式直装与平滑升级
- 新增 [VoicePackProvisioning.cs](tools/installer-source/src/VoicePackProvisioning.cs)（内置 `ConcatenatedReadStream` 直接跨越 `.zip.001/.002/...` 分卷在内存打开 `ZipArchive`，无需外部解压工具与二次落盘合并）与打包脚本 [Build-VoicePack.py](tools/installer-source/Build-VoicePack.py)（自动去重冗余基底模型与 `.bak` 备份，按 `<1850 MiB` 切分分卷并输出 `VOICEPACK-SHA256SUMS.txt`）。
- 升级 [Wizard.Form.cs](tools/installer-source/src/Wizard.Form.cs)、[Wizard.Core.cs](tools/installer-source/src/Wizard.Core.cs)、[Setup.Core.cs](tools/installer-source/src/Setup.Core.cs)、[PersistentStateCompatibility.cs](tools/installer-source/src/PersistentStateCompatibility.cs) 与 [ReflectionWorkCompatibility.cs](tools/installer-source/src/ReflectionWorkCompatibility.cs)：实现目录/可选组件智能默认勾选、`UpgradeKnownWorkerInPlace`（原地热更 `semantic_worker.py` 与 `tts_worker.py`）、官方正式版 Release 检查修复以及 `UnityEngine.AudioModule.dll` 宿主能力探测。

### 4. 回归测试与校验记录
- **产品 DLL**：`0.4.1`（SHA-256 `310561f95e5b7fdb916e03db90157635214253fd148816d5db7e9b41b1a817ea`）
- **验证门全绿**：
  - `Verify-All.ps1 -IncludeSemanticAB`：**7 / 7**（Release 0 错误、`eval.dll` 134 项、`FreezeCarryTests` 262 项、`DedupVerify`、`PerfBench`、`test_optional_recall_fixture`、`test_semantic_geometry` 13/13、`semantic_ab_golden` 全部硬指标通过：`recall@3 = 1.0`、`p95_geometry_ms = 1.825ms`、`--real-bge-probe` `p50_geometry_ms = 1.275ms`）；
  - `BugFixTests`：**167 / 167** 通过；
  - `test_tts_worker.py`：**16 / 16** 通过；`test_stt_worker.py`：**11 / 11** 通过；
  - 安装器 `.25`（`0.1.4.25-v0.4.1.release.2`，`installer_schema: 18`）：.NET 4.0 `csc.exe /langversion:5` 编译 0 警告 0 错误，分卷 ZIP 拼接安装、配置补丁与 `HostCapabilities` 静态契约冒烟通过，1.2.1 桌面端实机多轮语音对话、KV 缓存（~53%）、`BridgeSparks` 共激活与退出零残留均已核验。

</details>
