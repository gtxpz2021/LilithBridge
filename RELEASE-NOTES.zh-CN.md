# LilithBridge v0.4.4 · `9a5c6c80`

面向官方游戏 **1.2.1 · Windows x64**。  
在 `v0.4.1` 双子空间几何与本地双向语音的基础上，**`v0.4.4` 重建了全量 93 条 `姿态 × 衣着 × 动作` Spine 动画真值矩阵、归还了睡/躺姿态与角色动作自主权、清理了动态提示词中的过度防御说教，同时完整包含核显轻薄本 & 双显卡混合本（Optimus）本地语音兼容与提速、`B1` 观念全生命周期演化、慢半拍余韵补话（`<afterbeat>`）及去“鱼八股”清扫。**

> [!IMPORTANT]
> **怎么安装、怎么升级（首次安装与老玩家无损覆盖通用）**
> - **已装 `v0.4.1+` 语音包的老玩家极速升级**：**无需重新下载 2.9 GB 的 `VoicePack` 分卷！** 只需下载约 55 MB 的单个 **`LilithBridge-Setup-0.4.4-cn28-9a5c6c80.exe`** 双击运行，向导会自动保留既有模型环境并原地热更新 `LilithBridge.dll` 与新版 `tts_worker.py`。
> - **首次安装本地语音（SenseVoice 听写 + 48kHz 莉莉丝原声）**：把通用分卷 **`LilithBridge-VoicePack-v1.zip.001`** 与 **`.002`** 跟 `Setup.exe` 放在**同一个文件夹里**直接运行即可——向导会在内存里跨分卷流式直装，**不用装压缩软件、不用手动解压合并、不占双倍磁盘空间**。
> - **绝对保留存档**：覆盖升级 **100% 保留**原有的 `LilithBridgeData`（含 `%APPDATA%\LilithAI`）、`config.json`、记忆库、笔记、`B1` SQLite、DeepSeek Key、ChatGPT 登录态与游戏原生存档。动手前请先**正常退出游戏**。

---

## 一、这一版重点修复与升级了什么？

### 1. 身体自主权彻底归还 × 全量 93 条 Spine 动画真值恢复 × 提示词去防御化减负
- **根除夜间睡衣 `Pajamas/Think` 刷屏与“躺下盖被 ↔ 掀被拉起”横跳**：
  - 修复等待输入/回复时未按衣着校验动画、以 `2Hz` 反复请求不存在的 `stand_pajamas_think` 的问题；睡衣站姿下自动锁存抑制，自然维持 `stand_pajamas_idle`，且后台静默主动检查期间**绝不强塞 `Think` 待机姿势**；
  - 移除点击原生选项（如夜间“叫醒她”切入裹被抱枕躺姿）、右键菜单与拖拽钩子中越界调用的 `WakeForInput()` 强行拉起逻辑，并在打字聊天时同步原生 `EmotionSystem.NotifyInteraction()`，消除连续聊天超过 5 分钟被原版误判冷落而突然躺倒的问题。
- **找回白天常服被误锁的 35+ 丰富神态，切断程序越界硬塞动作**：
  - 修正早期动画探针“睡衣表覆盖常服表”的历史缺陷，完整恢复白天常服（`Stand + Casual`）下 `blush`、`shy_giggle`、`pout`、`greet`、`lazy_wave`、`look_around`、`confuse`、`think`、`happy_sigh` 等 **47 个直出动作**，清除常服误播 `tsundere / drowsy_slump` 与站姿误播 `TiltHead / Turnhead` 的警告，并向模型如实公开卧姿/沙发切换动作（`lie_stand_up / sofa_stand_up / sofa_doubt`）；
  - 关闭关键词匹配强塞动作后门（不再因玩家提到“吃/歌/困”就强替模型塞入 `cake_up` 等动作），移除卧姿下动作不适用时强替成 `Flirt`（抛媚眼）的逻辑。
- **动态上下文去防御化减负**：移除动态区对顶部鲜活人设的二次口号稀释、非等待状态下的矛盾“暂缓主动开口”注入，以及对玩家日常发言的法庭取证式免责声明，让对话回归轻盈鲜活的少女本色。

### 2. 核显轻薄本 & 双显卡游戏本（Optimus）本地语音全面打通，告别“有字无声”
- **原生识别 Intel / AMD 核显（`igpu`）与双显卡级联加速**：修复 `audiocpp_server` 设备选择器此前跳过 `type == "igpu"`（如 Intel Iris Xe / Arc、AMD `Radeon 780M` 等统一内存核显）的问题；探测超时放宽至 `18s` 兼容休眠独显 PCIe `D3Cold` 冷唤醒，并按 **`[独显, 核显]`** 顺序自动择优与无感降级。
- **根除 `FlashSR` 隐式退回 CPU 的 `5.8s/句` 性能陷阱**：在 `auto` 模式下自动遍历多块 DXGI 适配器择优；若无可用 GPU DirectML/CUDA 加速，**自动切换为 `0ms` 额外开销的 24kHz 原声快路径**。
- **首泡出字更快，慢硬件超时“单泡脱困而不误杀整句”**：首泡最长等待缩短至 **`1.8s`**（含引号安全的长首句逗号快切）；静默保护放宽至 **`8.5s`** 且超时仅平滑解锁当前气泡翻页、**绝不腰斩后台流水线合成**；新增 `trim_tts_audio` 裁除 `<EOS>` 漏停长尾静音，并将模组 TTS 与原版重复点击短语音静音滑块解耦。

### 3. 慢半拍余韵（`<afterbeat>`）、躺姿畅聊、`B1` 观念闭环与去“鱼八股”清扫
- **慢半拍余韵补刀与躺姿畅聊**：主回复说完片刻后偶尔自然补半句后话（玩家先开口打字则静默收起、绝不入历史）；解除躺姿/坐沙发对主动搭话与留小纸条的误屏蔽；根除主动回合混入句首元协议选择独白。
- **`B1` 全生命周期 × 剧集切片 × 多泡问答补全**：支持事后回顾生发新 `B1`、保留多分支对照（`compare`/`qualify`）与温和归架（`settles`）；超 `1400` 字长历史按句子边界切出 `Episode Window`；语义命中多泡助手回复自动配对原提问。
- **“鱼八股”残留清扫**：补齐主动回合末尾 `[开口之前]` 锚点并同步净化后台 `note / lesson / relationship / consolidation / B1` 提示词，压制惯性复述词头、记账套话、句尾短否定尾缀与破折号升华回声链。

---

## 二、发行文件说明

- **`LilithBridge-Setup-0.4.4-cn28-9a5c6c80.exe`**：单文件一键安装／升级程序（纯文字聊天或已装过 `v0.4.1+` 语音包的老玩家**只下这一个即可**）。
- **`LilithBridge-VoicePack-v1.zip.001` / `.002` + `VOICEPACK-SHA256SUMS.txt`**：可选本地双向语音分卷整合包（共约 2.9 GB，与 `v0.4.1` 分卷完全通用；首次安装语音时与 `Setup.exe` 放同一文件夹即可免解压直装）。
- **`LilithBridge-installer-0.4.4-cn28-9a5c6c80.zip`**：供旧版安装器在线检测与协议交接的更新包。
- **`LilithBridge-source-0.4.4-cn28-9a5c6c80.zip`**：清洁 MIT 开源源码包（不含模型权重、私密配置及非 MIT 语料）。
- **`AGENTS.md`**：供玩家侧 AI 助手（DeepSeek / Codex / Claude Code / Cursor 等）直接读取的排障、调优与 Vibecoding 二创手册。

详细安装与常见问题见 [安装与使用指南](NORMAL-USE.zh-CN.md)，升级兼容与热更新说明见 [升级与回退边界](UPGRADE-COMPATIBILITY.zh-CN.md)，遇到问题请附脱敏日志提交至 [问题反馈指南](FEEDBACK.zh-CN.md)。

---

## 三、技术更新明细与回归校验记录（开发者／进阶参考）

<details>
<summary>点击展开完整技术变更、核心文件与回归验证明细（相对 <code>v0.4.1 / 310561f9</code>）</summary>

### 1. 全量 `Pose × Clothing × Action` Spine 矩阵、动作自主权收口与提示词减负（`v0.4.4`）
- **93 条真值矩阵、过渡动作公开与无日志预检**：基于 `global-metadata.dat` 重建 [NativeAnimationCatalog.cs](src/LilithBridge/NativeAnimationCatalog.cs) 并升级 [RuntimeActionCapability.cs](src/LilithBridge/RuntimeActionCapability.cs)（严格区分 `casual`/`pajamas` 并公开 `Transition` 姿态切换动作）、[RawAnimationProbe.cs](src/LilithBridge/RawAnimationProbe.cs) 与 [ActionPoseClothingResolverAudit.cs](src/LilithBridge/ActionPoseClothingResolverAudit.cs)；[ActionExecutor.cs](src/LilithBridge/ActionExecutor.cs) 与 [ActionExecutor.Waiting.cs](src/LilithBridge/ActionExecutor.Waiting.cs) 接入 `LilithAnimName.TryGetAnimationName` + `state.HasAnimation` 无日志预检、`ShouldSuppressAmbientWaiting` 锁存，并移除卧姿不兼容动作强替 `Flirt` 逻辑；[ActionDirector.cs](src/LilithBridge/ActionDirector.cs) 与 [ConfigStore.cs](src/LilithBridge/ConfigStore.cs) 关闭对真实模型回复的关键词动作强塞。
- **停止唤醒劫持、同步交互时钟与动态提示词减负**：[BridgeController.cs](src/LilithBridge/BridgeController.cs) 与 [BridgeController.InputSubmission.cs](src/LilithBridge/BridgeController.InputSubmission.cs) 移除选项点击、右键、触控与拖拽中的 `NativePoseGate.WakeForInput()`，后台 `_proactiveRequest` 不触发 `SetWaiting`，并在打开输入/发送消息时调用 [NativeStateInspector.cs](src/LilithBridge/NativeStateInspector.cs) `NotifyPlayerInteraction()`；收束 [PersonaStateEngine.cs](src/LilithBridge/PersonaStateEngine.cs)、[SessionState.cs](src/LilithBridge/SessionState.cs)、[ContextBuilder.cs](src/LilithBridge/ContextBuilder.cs) 与 [ParticipationPrompt.cs](src/LilithBridge/ParticipationPrompt.cs) 中的冗余防御文本。

### 2. 本地 TTS 混合显卡/核显兼容、非破坏性节奏门控与尾静音裁剪
- **Vulkan `igpu`、冷唤醒与 `FlashSrBwe` 择优**（[tts_worker.py](optional/tts-worker/tts_worker.py)，SHA-256 `7b0701defadd8a1b474dedccb8bda95fd50c35db0dfceab58d55d85d19af241f`）：`rank_vulkan_devices` 原生支持 `gpu` / `igpu` 级联回退，`probe_vulkan_devices` 超时提升至 `18.0s`；无快速 GPU ONNX 时自动切换 `0ms` 24kHz 原声快路径；新增 `trim_tts_audio` 与首泡软停顿快切。
- **非破坏性慢气泡脱困与独立音量路由**：更新 [TextToSpeechCoordinator.cs](src/LilithBridge/TextToSpeechCoordinator.cs)、[BridgeController.Tts.cs](src/LilithBridge/BridgeController.Tts.cs)、[NativeBubblePresenter.cs](src/LilithBridge/NativeBubblePresenter.cs) 与 [TextToSpeechPlayer.cs](src/LilithBridge/TextToSpeechPlayer.cs)。

### 3. 主动性、`<afterbeat>`、`B1` 全生命周期与去“鱼八股”加固
- 新增 [BridgeController.Afterbeat.cs](src/LilithBridge/BridgeController.Afterbeat.cs)、[MemoryStore.B1.ProactiveExposure.cs](src/LilithBridge/MemoryStore.B1.ProactiveExposure.cs)、[B1ProjectionBudget.cs](src/LilithBridge/B1ProjectionBudget.cs)，升级 [BridgeController.Participation.cs](src/LilithBridge/BridgeController.Participation.cs)、[ReplySanitizer.cs](src/LilithBridge/ReplySanitizer.cs)、[MemoryStore.B1.Reconsideration.cs](src/LilithBridge/MemoryStore.B1.Reconsideration.cs)、[MemoryStore.SemanticRecall.cs](src/LilithBridge/MemoryStore.SemanticRecall.cs) 与 [DeepSeekClient.cs](src/LilithBridge/DeepSeekClient.cs)。

### 4. 回归测试与校验记录
- **产品 DLL**：`0.4.4`（SHA-256 `9a5c6c8056dcdfafe807824dbb77dcd233a1291ad968e26b213070d406cff674`）
- **验证门全绿**：
  - `Verify-All.ps1 -IncludeSemanticAB`：**7 / 7**（Release 0 错误、`eval.dll` 134 项、`FreezeCarryTests` 262 项、`DedupVerify`、`PerfBench`、`test_optional_recall_fixture` 3/3、`test_semantic_geometry` 13/13、`semantic_ab_golden` `recall@3 = 1.0`、`--real-bge-probe` 全部通过）；
  - `BugFixTests`：**198 / 198** 通过（含 `Pose1..Pose4` 与 `Prompt1..Prompt2` 专项回归）；
  - `test_tts_worker.py`：**18 / 18** 通过；`test_stt_worker.py`：**11 / 11** 通过；
  - 安装器 `.28`（`0.1.4.28-v0.4.4.release.1`，`installer_schema: 18`）：支持从 `v0.4.3`（`29dc4d8a`）、`v0.4.2`（`1be32669`）、`v0.4.1`（`310561f9`）、`v0.4`（`ffe08b05`）及更早版本平滑热更 `LilithBridge.dll` 与 `tts_worker.py`。

</details>
