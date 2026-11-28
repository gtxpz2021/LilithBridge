# LilithBridge v0.4.5 · `7f44b518`

面向官方游戏 **1.2.1 · Windows x64**。  
在 `v0.4.4` 全量 93 条 Spine 姿态矩阵与本地双向语音基础上，**`v0.4.5` 重点修复了本地 TTS `single_voice_anchor` 绕过 5 情绪路由与原生动作音效叠音、彻底砍掉约 70%–80% 后台回看与主动开口 token 浪费、改为按需展开 `[native_memo]` 避免陈年随手记每轮进上下文，并补齐玩家原生选项入历史与 `B1` 说话人归属防错位闭环。**

> [!IMPORTANT]
> **怎么安装、怎么升级（首次安装与老玩家无损覆盖通用）**
> - **已装 `v0.4.1+` 语音包的老玩家极速升级**：**无需重新下载 2.9 GB 的 `VoicePack` 分卷！** 只需下载约 55 MB 的单个 **`LilithBridge-Setup-0.4.5-cn29-7f44b518.exe`** 双击运行，向导会自动保留既有模型环境并原地热更新 `LilithBridge.dll` 与新版 `tts_worker.py`。
> - **首次安装本地语音（SenseVoice 听写 + 48kHz 莉莉丝原声）**：把通用分卷 **`LilithBridge-VoicePack-v1.zip.001`** 与 **`.002`** 跟 `Setup.exe` 放在**同一个文件夹里**直接运行即可——向导会在内存里跨分卷流式直装，**不用装压缩软件、不用手动解压合并、不占双倍磁盘空间**。
> - **绝对保留存档**：覆盖升级 **100% 保留**原有的 `LilithBridgeData`（含 `%APPDATA%\LilithAI`）、`config.json`、记忆库、笔记、`B1` SQLite、DeepSeek Key、ChatGPT 登录态与游戏原生存档。动手前请先**正常退出游戏**。

---

## 一、这一版重点修复与升级了什么？

### 1. 本地 TTS 5 情绪语音路由全面恢复 × 语音回合静音原生动作小动静
- **修复 `single_voice_anchor` 绕过情绪路由（告别 20/20 句全锁 `gentle_warm`）**：
  - 新装与默认配置将 `tts.single_voice_anchor` 改为 `false`，默认开启完整 5 情绪路由与全量 5 情绪预热（`voice_prompt_cache_slots=8` 足够常驻无互踢）；
  - 补齐 `_EMOTION_TO_MOOD` 对 `ActionCatalog.NormalizeEmotion` 全系列 `emoji_*` 标签（`emoji_smile_1..4` / `emoji_angry_1` / `emoji_sad_1` / `emoji_surprise_1` / `emoji_sleepy_1` / `emoji_wronged_1` / `emoji_daze_1` / `emoji_calm_1`）的映射；
  - 兼顾旧配置老用户：即便已有 `config.json` 仍写着 `single_voice_anchor: true`，带显式 `<emotion:...>` 或 `mood_hint` 的回复也会走 `select_mood()` 切换情绪音色，仅在无显式情绪时稳住默认音色。
- **语音输出轮次屏蔽游戏原生动作音效与旧版 `<voice:...>` 标签**：
  - `ActionExecutor.PlayActionVoice` 与 `PlayVoice`（含 `se:` / 普通音效分支）在本地 TTS 活跃时统一静默，不再与 48kHz 语音叠音；启用本地 TTS 时 `RuntimeProtocol` 同步隐藏旧版 `<voice:数字ID>` 说明。

### 2. 后台 token 消费大幅瘦身 × `[native_memo]` 按需展开（省额度、保隐私）
- **`[native_memo]` 从“每轮 851 字全量裸奔”改为按需展开**：
  - 普通闲聊仅注入约 95 字单行统计摘要，不再把数月前的 `player_quick` 随手记和陈年过期提醒塞进每一轮对话；
  - 仅在玩家聊起记事本/提醒/待办、存在 1 小时内/当日到期提醒、最近 15 分钟内刚新建随手记或专属批注任务时展开正文（保留 `meta.memo_always_inject_full` 开关供按需全开）。
- **后台回看（`reflection` + `B1`）降温与瘦身（预计降耗 75%–80%）**：
  - 默认对话回看节奏由 `60s / 60s / ≥2 行` 调优至 `180s 服务间隔 / 90s 沉淀期 / ≥4 行（2 轮）`（贴合 6 轮单窗上限，既告别 60 秒高频刷屏又不积压待办）；取消 `B1` 结束后立即绕过冷却导致 1.6 秒紧贴双打的行为；
  - 普通回看对旧 `previous_notes` / `settled_notes` 伴随历史原话设合理上限（每条旧笔记至多附最近 2 条、总计上限 12 条伴随原话，`NoteHistoryByteLimit` 收至 `4096`），不再每次重复重传几十条数月前的原话。
- **主动开口（`proactive`）瘦身 ~70% + 防切回抢跑**：
  - 主动开口历史预算收紧至最近 12 轮（此前 41 轮 verbose JSON 占单次请求的 87%），且不扰动主对话 KV 缓存锚点；
  - 切回窗口触发 `player_returned` 增加 **10 秒稳定期**（窗口有效期延至 35 秒），避免刚切回准备按回车/说话就被主动开口抢跑白烧。

### 3. 原生互动三处闭环 × `B1` 说话人归属与虚词误连修复 × 八股收口
- **玩家原生选项完整入历史**：修复 `ButtonClickedPatch` 对 `SetButtonContents` 动态按钮（如睡醒“叫醒她/算了”）的漏捕，并在 `OnNativeOptionClick` 中正式调用 `AddSpokenLine("player", ...)` 与 `RegisterCurrentNativeSource`，消除“只有莉莉丝原生回复、缺失玩家所选选项”造成的说话人错位；平铺 `messages` 出站层同步补齐 `[原生台词]` / `[玩家原生选项]` 标识。
- **双滚动窗口抗噪音淘汰**：`SessionState.ShortTermEventLimit` 由 `16 → 32` 且连续切屏 `AppForeground` 原地合并，不再顶掉触摸/拖拽/选项；长挂机时同会话早期未回应原生闲聊上限保留最近 8 条（当场原生段全保留，全量记录仍在归档可召回）。
- **`B1` 防角色错位与假演化链**：修复 `B1ReflectionMaterial` 说话人归属（兼顾 `Origin=="user"` 与 `;role=user;`，输出明确中文 `speaker_label: "玩家"/"莉莉丝"` 并加强契约提醒）；`B1ConcreteDiscoveryToken` 过滤“等等/今晚/不过/其实”等高频口语虚词，杜绝仅凭单字词面把无关旧 `B1` 焊进演化链；收紧 `[开口之前]` 锚点对“不X只是Y/稳/记账”套话的约束。

---

## 二、发行文件说明

- **`LilithBridge-Setup-0.4.5-cn29-7f44b518.exe`**：单文件一键安装／升级程序（纯文字聊天或已装过 `v0.4.1+` 语音包的老玩家**只下这一个即可**）。
- **`LilithBridge-VoicePack-v1.zip.001` / `.002` + `VOICEPACK-SHA256SUMS.txt`**：可选本地双向语音分卷整合包（共约 2.9 GB，与 `v0.4.1` 分卷完全通用；首次安装语音时与 `Setup.exe` 放同一文件夹即可免解压直装）。
- **`LilithBridge-installer-0.4.5-cn29-7f44b518.zip`**：供旧版安装器在线检测与协议交接的更新包。
- **`LilithBridge-source-0.4.5-cn29-7f44b518.zip`**：清洁 MIT 开源源码包（不含模型权重、私密配置及非 MIT 语料）。
- **`AGENTS.md`**：供玩家侧 AI 助手直接读取的排障、调优与 Vibecoding 二创手册。

详细安装与常见问题见 [安装与使用指南](NORMAL-USE.zh-CN.md)，升级兼容与热更新说明见 [升级与回退边界](UPGRADE-COMPATIBILITY.zh-CN.md)，遇到问题请附脱敏日志提交至 [问题反馈指南](FEEDBACK.zh-CN.md)。

---

## 三、技术更新明细与回归校验记录（开发者／进阶参考）

<details>
<summary>点击展开完整技术变更、核心文件与回归验证明细（相对 <code>v0.4.4 / 9a5c6c80</code>）</summary>

### 1. 本地 TTS 5 情绪路由修复与原生音效防叠音
- [tts_worker.py](optional/tts-worker/tts_worker.py)（SHA-256 `ad563bd04e2d25de151f85d951ba200144f38ea3d63c1afe7d7b21e978092e7c`，同步至 `tools/setup/optional/tts-worker/tts_worker.py`）、[ConfigStore.Tts.cs](src/LilithBridge/ConfigStore.Tts.cs)、[config.example.json](config.example.json)、[Setup.Core.cs](tools/installer-source/src/Setup.Core.cs)、[test_tts_worker.py](optional/tts-worker/test_tts_worker.py)。
- [ActionExecutor.cs](src/LilithBridge/ActionExecutor.cs)（`PlayActionVoice` 与 `PlayVoice` 顶部统一响应 `SuppressesNativeReplyVoice`）与 [RuntimeProtocol.cs](src/LilithBridge/RuntimeProtocol.cs)（启用 TTS 时隐藏 `<voice:数字ID>` 说明）。

### 2. `[native_memo]` 按需展开、回看降温瘦身、主动开口瘦身与原生三闭环
- [NativeMemoContext.cs](src/LilithBridge/NativeMemoContext.cs)、[ContextBuilder.cs](src/LilithBridge/ContextBuilder.cs)、[ConfigStore.cs](src/LilithBridge/ConfigStore.cs)。
- [ReflectionDialogueOpportunity.cs](src/LilithBridge/ReflectionDialogueOpportunity.cs)、[BridgeController.B1Reflection.cs](src/LilithBridge/BridgeController.B1Reflection.cs)、[MemoryStore.Reflections.cs](src/LilithBridge/MemoryStore.Reflections.cs)、[MemoryStore.B1.ReflectionFlow.cs](src/LilithBridge/MemoryStore.B1.ReflectionFlow.cs)、[MemoryStore.B1.Discovery.cs](src/LilithBridge/MemoryStore.B1.Discovery.cs)、[MemoryStore.B1.Reconsideration.cs](src/LilithBridge/MemoryStore.B1.Reconsideration.cs)。
- [NativeHooks.cs](src/LilithBridge/NativeHooks.cs)、[BridgeController.cs](src/LilithBridge/BridgeController.cs)、[BridgeController.Submission.cs](src/LilithBridge/BridgeController.Submission.cs)、[DeepSeekClient.cs](src/LilithBridge/DeepSeekClient.cs)、[SessionState.cs](src/LilithBridge/SessionState.cs)、[MemoryStore.NativeDialogueSupply.cs](src/LilithBridge/MemoryStore.NativeDialogueSupply.cs)。

### 3. 回归测试与校验记录
- **产品 DLL**：`0.4.5`（SHA-256 `7f44b518339f2449909092b3696deba67f34a0fc1bb2361b25328e541a52c090`）
- **验证门全绿**：
  - `Verify-All.ps1 -IncludeSemanticAB`：**7 / 7**（Release 0 错误、`eval.dll` 134 项、`FreezeCarryTests` 262 项、`DedupVerify`、`PerfBench`、`test_optional_recall_fixture` 3/3、`test_semantic_geometry` 13/13、`semantic_ab_golden` `recall@3 = 1.0`、`--real-bge-probe` 全部通过）；
  - `BugFixTests`：**204 / 204** 通过（含 `Opt1..Opt6` 专项回归）；
  - `test_tts_worker.py`：**18 / 18** 通过；`test_stt_worker.py`：**11 / 11** 通过；
  - 安装器 `.29`（`0.1.4.29-v0.4.5.release.1`，`installer_schema: 18`）：支持从 `v0.4.4`（`9a5c6c80`）、`v0.4.1`（`310561f9`）及更早版本平滑热更 `LilithBridge.dll` 与 `tts_worker.py`。

</details>
