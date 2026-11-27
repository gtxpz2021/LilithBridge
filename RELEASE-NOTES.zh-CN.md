# LilithBridge v0.4.2 · `1be32669`

面向官方游戏 **1.2.1 · Windows x64**。  
在 `v0.4.1` 双子空间几何与本地双向语音的基础上，**`v0.4.2` 集中解决了核显轻薄本与双显卡混合本（Optimus）本地语音“无声/慢半拍被掐断”的兼容性与性能痛点，补齐了 `B1` 观念全生命周期演化、慢半拍余韵补话（`<afterbeat>`）与躺姿畅聊节奏，并深度清理了主对话与后台反思残留的“鱼八股”套话。**

> [!IMPORTANT]
> **怎么安装、怎么升级（首次安装与老玩家无损覆盖通用）**
> - **已装 `v0.4.1` 语音包的老玩家极速升级**：**无需重新下载 2.9 GB 的 `VoicePack` 分卷！** 只需下载约 55 MB 的单个 **`LilithBridge-Setup-0.4.2-cn26-1be32669.exe`** 双击运行，向导会自动保留既有模型环境并原地热更新 `LilithBridge.dll` 与新版 `tts_worker.py`。
> - **首次安装本地语音（SenseVoice 听写 + 48kHz 莉莉丝原声）**：把 **`LilithBridge-VoicePack-v1.zip.001`** 与 **`.002`** 跟 `Setup.exe` 放在**同一个文件夹里**直接运行即可——向导会在内存里跨分卷流式直装，**不用装压缩软件、不用手动解压合并、不占双倍磁盘空间**。
> - **绝对保留存档**：覆盖升级 **100% 保留**原有的 `LilithBridgeData`（含 `%APPDATA%\LilithAI`）、`config.json`、记忆库、笔记、`B1` SQLite、DeepSeek Key、ChatGPT 登录态与游戏原生存档。动手前请先**正常退出游戏**。

---

## 一、这一版重点升级与修复了什么？

### 1. 核显轻薄本 & 双显卡游戏本（Optimus）本地语音全面打通，告别“有字无声”
- **原生识别 Intel / AMD 核显（`igpu`）与双显卡级联加速**：修复了 `audiocpp_server` 设备选择器此前仅识别 `type == "gpu"` 而跳过 `type == "igpu"`（如 Intel Iris Xe / Arc、AMD `Radeon 780M` 等 BIOS 划分 512MB–2GB 共享显存的统一内存核显）的问题；在 `RTX 4050 Laptop + Radeon 780M` 等混合架构本上，探测超时放宽至 `18s` 以兼容休眠独显 PCIe `D3Cold` 冷唤醒，并按 **`[独显, 核显]`** 顺序自动择优与无感降级。
- **根除 `FlashSR` 隐式退回 CPU 的 `5.8s/句` 性能陷阱**：在 `auto` 模式下自动遍历多块 DXGI 适配器择优；若设备无可用 GPU DirectML/CUDA 加速，**自动切换为 `0ms` 额外开销的 24kHz 原声快路径**，避免轻薄本每句被 CPU 超分白白拖慢近 6 秒。
- **首泡出字更快，慢硬件超时“单泡脱困而不误杀整句”**：
  - 首泡出字前最长等待由 `3.2s` 缩短至 **`1.8s`**，并对 `>=24` 字无早期句号的首泡启用引号安全的逗号短领句快切，首包出声提速约一倍；
  - 彻底移除旧版 `3.2s` 静默到期直接调用 `CancelTtsSpeech` 腰斩整段合成的逻辑——现放宽至 **`8.5s`**，且即便在极低功耗设备上超时，也只平滑解锁当前气泡翻页，**绝不掐断后台正在流水线合成的后续语音**；
  - 新增 `trim_tts_audio` 自动裁除 `<EOS>` 漏停产生的长尾静音（保留 `240ms` 自然呼吸尾音），修复瞬时静音帧提前出队丢包，并将模组 TTS 与原版游戏点击短语音静音滑块解耦（关掉原版重复短音效不再导致模组 TTS 静音）。

### 2. 打破“一问一答”客服感：慢半拍余韵（`<afterbeat>`）、躺姿畅聊与主动发话净空
- **慢半拍余韵补刀（Staged `<afterbeat>`）**：主回复说完并安静片刻后，莉莉丝偶尔会像突然想起什么似的再补半句俏皮话或叮嘱；若你在此期间先开口或打字，尚未说出口的后话会自动收起、绝不写进历史，且内置令牌与未回应退避门控确保克制不刷屏。
- **躺下与坐沙发不再误锁主动性**：解除 `NativeStateInspector.IsNativeQuietForMod()` 对休息姿态（`IsPoseBlocked`）的过度屏蔽——现在她趴着或坐沙发犯懒时也能自然主动搭话、递送余韵补话与留小纸条，同时绝不强行打断卧姿动画。
- **根除主动发话气泡混入内心选择独白**：为 DeepSeek 主动回合补齐隐藏思考通道、移除提示词中的编号菜单诱导措辞，并增加句首协议元决策括号纵深剥离（保留正常神态括号与玩家引用）。

### 3. `B1` 观念全生命周期演化 × 剧集窗口切片 × 多气泡问答成对召回
- **`B1` 完整闭环（新建 · 并存分支 · 修订 · `settles` 温和翻篇）**：事后回顾现在可以基于近期真实交流自然生发新疑问与新判断、在遇到反例时保留并存分支（`compare`/`qualify`），或将已聊透翻篇的旧念头温和归架（`settles`，退出日常自动注入与跨桥中继，仍保留精确重访历史）；主动发话对 `B1` 线索采用中性曝光轮换，绝不复读同一旧话头。
- **超长对话句子对齐切片与多泡问答补全**：超过 `1400` 字的历史长段落自动围绕命中锚点切出句子对齐的 `Episode Window`（避免整条被预算一刀切丢弃）；语义直中多气泡助手回复时自动回溯配对当时的玩家原提问。

### 4. “鱼八股”残留深度清扫
- 针对实机观察到的残余句式惯性，补齐主动回合末尾 `[开口之前]` 锚点，集中压制惯性**“唔，XX啊”复述词头开场**、**“记一笔/先收下/放进心里”记账套话**、**句尾短否定尾缀（“，不插嘴/不装懂”）**与**“是 A 还是 B？”二选一设问**；并在后台 `note / lesson / relationship / consolidation / B1` 提示词中统一清理破折号示范与升华金句诱导，切断“反思产出八股 → 反哺污染主对话”的回声链。

---

## 二、发行文件说明

- **`LilithBridge-Setup-0.4.2-cn26-1be32669.exe`**：单文件一键安装／升级程序（纯文字聊天或已装过 `v0.4.1` 语音包的老玩家**只下这一个即可**）。
- **`LilithBridge-VoicePack-v1.zip.001` / `.002` + `VOICEPACK-SHA256SUMS.txt`**：可选本地双向语音分卷整合包（共约 2.9 GB，与 `v0.4.1` 分卷完全通用；首次安装语音时与 `Setup.exe` 放同一文件夹即可免解压直装）。
- **`LilithBridge-installer-0.4.2-cn26-1be32669.zip`**：供旧版安装器在线检测与协议交接的更新包。
- **`LilithBridge-source-0.4.2-cn26-1be32669.zip`**：清洁 MIT 开源源码包（不含模型权重、私密配置及非 MIT 语料）。
- **`AGENTS.md`**：供玩家侧 AI 助手（DeepSeek / Codex / Claude Code / Cursor 等）直接读取的排障、调优与 Vibecoding 二创手册。

详细安装与常见问题见 [安装与使用指南](NORMAL-USE.zh-CN.md)，升级兼容与热更新说明见 [升级与回退边界](UPGRADE-COMPATIBILITY.zh-CN.md)，遇到问题请附脱敏日志提交至 [问题反馈指南](FEEDBACK.zh-CN.md)。

---

## 三、技术更新明细与回归校验记录（开发者／进阶参考）

<details>
<summary>点击展开完整技术变更、核心文件与回归验证明细（相对 <code>v0.4.1 / 310561f9</code>）</summary>

### 1. 本地 TTS 混合显卡/核显兼容、非破坏性节奏门控与尾静音裁剪
- **Vulkan `igpu` 与冷启动唤醒**（[tts_worker.py](optional/tts-worker/tts_worker.py)，SHA-256 `7b0701defadd8a1b474dedccb8bda95fd50c35db0dfceab58d55d85d19af241f`）：新增 `rank_vulkan_devices` 支持 `gpu` / `igpu` 并按独显优先、核显次之级联尝试启动；`probe_vulkan_devices` 超时提升至 `18.0s`（含冷唤醒重试），[TextToSpeechWorker.cs](src/LilithBridge/TextToSpeechWorker.cs) 初始化等待同步提升至 `125s`。
- **`FlashSrBwe` 多适配器探测与免 CPU 阻塞**：`auto` 模式下遍历候选 DXGI 适配器并实测推理耗时，无快速 GPU ONNX Provider 时自动降级为 `0ms` 24kHz 原生输出；新增 `trim_tts_audio`（20ms 帧 RMS 自适应门限 + `240ms` 呼吸尾音保留）与首泡引号外软停顿领句切分。
- **非破坏性慢气泡脱困与独立音量路由**：在 [TextToSpeechCoordinator.cs](src/LilithBridge/TextToSpeechCoordinator.cs)、[BridgeController.Tts.cs](src/LilithBridge/BridgeController.Tts.cs)、[NativeBubblePresenter.cs](src/LilithBridge/NativeBubblePresenter.cs) 与 [TextToSpeechPlayer.cs](src/LilithBridge/TextToSpeechPlayer.cs) 中将 `MaxTtsFirstBubbleWaitSeconds` 降至 `1.8f`、新增 `AdvanceMinPlayableBubble` 与 `UnstickSilentTtsBubble` 替代整句 `CancelTtsSpeech`、修复静音帧提前出队并新增 `tts.play` 遥测。

### 2. 主动性、`<afterbeat>`、`B1` 全生命周期与去“鱼八股”加固
- **慢半拍余韵与主动节奏**：新增 [BridgeController.Afterbeat.cs](src/LilithBridge/BridgeController.Afterbeat.cs)、[MemoryStore.B1.ProactiveExposure.cs](src/LilithBridge/MemoryStore.B1.ProactiveExposure.cs)，重构 [BridgeController.Participation.cs](src/LilithBridge/BridgeController.Participation.cs)、[ParticipationPrompt.cs](src/LilithBridge/ParticipationPrompt.cs)、[NativeStateInspector.cs](src/LilithBridge/NativeStateInspector.cs) 与 [ReplySanitizer.cs](src/LilithBridge/ReplySanitizer.cs)（`StripLeadingMetaDecisionParenthetical`）。
- **`B1` 生命周期、剧集切片与问答配对**：升级 [MemoryStore.B1.Reconsideration.cs](src/LilithBridge/MemoryStore.B1.Reconsideration.cs)、[MemoryStore.B1.Projection.cs](src/LilithBridge/MemoryStore.B1.Projection.cs)、[B1ProjectionBudget.cs](src/LilithBridge/B1ProjectionBudget.cs)（`episode_window_utf16`）与 [MemoryStore.SemanticRecall.cs](src/LilithBridge/MemoryStore.SemanticRecall.cs) 多泡 `LogicalDialogueUnit` 反向溯源补全。
- **文风与反思回声治理**：更新 [PersonaDefaults.cs](src/LilithBridge/PersonaDefaults.cs)、[DeepSeekClient.cs](src/LilithBridge/DeepSeekClient.cs)（`ReplyRecencyAnchor` 覆盖主动回合、清理 `BuildReflectionPrompt` 与 `BuildDayTaskPrompt` 破折号/记账套话）及 [BridgeController.Submission.cs](src/LilithBridge/BridgeController.Submission.cs)。

### 3. 回归测试与校验记录
- **产品 DLL**：`0.4.2`（SHA-256 `1be326698ff78dfd8a3574f76c87aa81a5f025ac78a0b0a8851a518dec72f6c6`）
- **验证门全绿**：
  - `Verify-All.ps1 -IncludeSemanticAB`：**7 / 7**（Release 0 错误、`eval.dll` 134 项、`FreezeCarryTests` 262 项、`DedupVerify`、`PerfBench`、`test_optional_recall_fixture` 3/3、`test_semantic_geometry` 13/13、`semantic_ab_golden` `recall@3 = 1.0`、`--real-bge-probe` 全部通过）；
  - `BugFixTests`：**192 / 192** 通过；
  - `test_tts_worker.py`：**18 / 18** 通过；`test_stt_worker.py`：**11 / 11** 通过；
  - 安装器 `.26`（`0.1.4.26-v0.4.2.release.1`，`installer_schema: 18`）：支持从 `v0.4.1`（`310561f9`）、`v0.4`（`ffe08b05`）及更早版本平滑热更 `LilithBridge.dll` 与 `tts_worker.py`。

</details>
