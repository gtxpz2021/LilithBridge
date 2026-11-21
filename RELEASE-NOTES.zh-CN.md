# LilithBridge v0.4 · `ffe08b05`

面向官方游戏 **1.2.1 · Windows x64**。  
本版基于上一正式版 `.106`（`2576304b`），是一次面向**感官协同、几何语义检索、B1 思维连续性与后台可靠性**的全面大版本升级：既补齐了独立进程感官侧车宿主机与高维几何语义召回，也从第一性原理逐项清除了跨会话想法失效、长文词法黑洞、陈年草稿考古、空闲连接断链与关系理解长期零供给等隐性顽疾。

> [!IMPORTANT]
> **怎么下载、怎么更新（首次安装与老玩家升级通用）**
> - **下载这个文件**：[`LilithBridge-Setup-0.4.0-cn23-ffe08b05.exe`](https://github.com/gtxpz2021/LilithBridge/releases/latest)（单文件一键安装／升级，**无需自己解压或合并资源**）。
> - **绝对保留存档**：直接覆盖更新会**完好保留**原有 `LilithBridgeData`（含旧版 `%APPDATA%\LilithAI`）、`config.json`、回忆／B1 数据库、DeepSeek Key、ChatGPT 登录状态与游戏原生存档，绝不重置已关闭的开关。
> - **唯一前提**：动手前请先**正常退出游戏**（若旧版正好挂起未结束反思，进游戏聊一两句待空闲再正常退出后升级即可）。

---

## 一、新增能力（相对 `.106`）

### 1. 独立进程感官侧车宿主机（Sensory Sidecar Host）与非言语微反应
- **非阻塞宿主底座**：新增 [SensorySidecarClient.cs](src/LilithBridge/SensorySidecarClient.cs)、[BridgeController.Sensory.cs](src/LilithBridge/BridgeController.Sensory.cs) 与 [ConfigStore.Sensory.cs](src/LilithBridge/ConfigStore.Sensory.cs)，支持可选外部感官侧车进程接入，通过独立 KV 尾槽注入与释放窗口门控把环境拾音、桌面节奏等非言语线索自然融入对话，不破坏前段 Prompt Prefix Cache；同时对 `TickSensoryRuntime` 节流并缓存线程 HWND 查询降低前台开销。
- **姿态安全静默动作与节律协同**：引入姿态安全静默动作解析器（Pose-safe silent action resolver）、BPM `TimeScale` 节奏同步与状态机微反应节律控制（[MicroReactionDirector.cs](src/LilithBridge/MicroReactionDirector.cs)）。
- **睡眠惊醒小剧场与专注长跑静默笔记**：支持睡眠中被晃动鼠标等触发的惊醒微反应（Sleep Startle Theater）以及长时间专注后的静默笔记交接（`pending_focus_note` IPC，[BridgeController.BackgroundWrites.cs](src/LilithBridge/BridgeController.BackgroundWrites.cs)）。

### 2. 几何语义检索升级与多维联想火花（Local BGE Worker）
- **白化几何与多样化检索**：升级 [semantic_worker.py](optional/semantic-worker/semantic_worker.py)、[MemoryStore.SemanticRecall.cs](src/LilithBridge/MemoryStore.SemanticRecall.cs) 与 [BridgeController.SemanticRecall.cs](src/LilithBridge/BridgeController.SemanticRecall.cs)，引入特征白化几何度量、CSLS 校准与 DPP 多样性挑选（避免近义复读霸占召回名额），并支持正交／虫洞／桥接／场景联想火花（Orthogonal / Wormhole / Bridge / Scene Sparks）及显著度门控（`SalienceGated`）。
- **检索管线 H-1..H-5 硬化**：显式检索直接消费 RRF 融合排序而不再强依赖几何开关；有界化 `doc_sims` / `doc_quantiles` 传输体积并保留全库分位分母；修正单轮回填优先级与全负 CSLS 偏移防塌陷；`SceneText` 兼容无空格方括号标题；BGE 探针增加预热校准。

### 3. B1 事后回顾反思与思维演进（Experience-Anchored B1 Retrospective Development）
- **空闲间隙的自然沉淀与演进**：重写 [MemoryStore.B1.Reconsideration.cs](src/LilithBridge/MemoryStore.B1.Reconsideration.cs)、[MemoryStore.B1.ReflectionFlow.cs](src/LilithBridge/MemoryStore.B1.ReflectionFlow.cs) 与 [DeepSeekClient.B1Reflection.cs](src/LilithBridge/DeepSeekClient.B1Reflection.cs)，在对话空闲反思间隙按**完整问答边界**（最近 3 个 `user` 回合、上限 24 条 / 3600 字，不再被长多气泡回复挤掉玩家触发句）回看真实交流：
  - 支持从有实质内容的对话中自然形成新 B1 想法，或通过词面／条件／语义双通道关联旧想法头节点进行 `revises` 演进与 `qualify` 并存分支；
  - 词法选标采用严格落在 `[0, 1]` 的双口径评分——用户发言走带长文阻尼的 `UserLexScore`，包含助手气泡的大窗口走几何均值 `OchiaiScore`（阈值 `0.18` 且至少 4 命中），彻底杜绝长对话窗口里的偶然虚词误触发；
  - 严格禁止后台编造 `reported` 事实、强制 CAS 防并发覆盖、保持源句屏蔽级联撤回与 100% SQLite 向后兼容，对纯寒暄低信息窗口零 HTTP 开销跳过。

### 4. `Readings`（关系理解）语义索引与精准共激活
- 在 [MemoryStore.SemanticRecall.cs](src/LilithBridge/MemoryStore.SemanticRecall.cs) 与 [SemanticRecallWorker.cs](src/LilithBridge/SemanticRecallWorker.cs) 中将 `_relationshipReadings` 正式纳入本地 BGE `Thoughts` 域增量索引；
- 在 [MemoryStore.MaterialSelection.cs](src/LilithBridge/MemoryStore.MaterialSelection.cs)、[ContextMaterialSelection.cs](src/LilithBridge/ContextMaterialSelection.cs) 与 [ContextBuilder.cs](src/LilithBridge/ContextBuilder.cs) 接通语义直中（`>= 0.57`，词面辅助 `>= 0.54`）与高置信反思笔记／原话锚点共激活，打破此前仅靠显式关系问句和纯词面导致的关系理解长期零供给，同时严格拦截寒暄、无关日常提议、`SalienceGated` 与版本哈希不匹配，且渲染于 `[近期对话]` 之后不破坏 KV Prefix Cache。

---

## 二、关键问题修复与体验治理

### 1. 跨会话 B1 想法复活与上下文去噪
- **B1 跨会话历史想法复活**：[MemoryStore.B1.cs](src/LilithBridge/MemoryStore.B1.cs)、[MemoryStore.MethodSources.cs](src/LilithBridge/MemoryStore.MethodSources.cs) 与 [MemoryStore.ReflectionReasonBasis.cs](src/LilithBridge/MemoryStore.ReflectionReasonBasis.cs) 新增 `B1EntryPermissionFootprint`——`Footprint` 中未被 `Sources` / `ObjectAnchors` / `Materials` 实际引用的环境原生游戏问候，不再于跨会话回读时触发 `source_permission_denied` 误杀（旧存档中历史 B1 想法无需迁移数据库即可即时恢复可见），同时显式屏蔽、提及边界与防篡改哈希校验保持严格级联撤回。
- **B1 语义发现降噪与锚定共激活**：[MemoryStore.B1.Discovery.cs](src/LilithBridge/MemoryStore.B1.Discovery.cs) 与 [MemoryStore.B1.Projection.cs](src/LilithBridge/MemoryStore.B1.Projection.cs) 加入 BGE 显著度／离群门控、称呼／弱词／粘连词清洗，并在历史原话获准发现时自动共激活其已锚定的有效 B1 `Current` 头节点。
- **无损折叠与清晰编号**：[B1ProjectionBudget.cs](src/LilithBridge/B1ProjectionBudget.cs) 与 [B1ReadableView.cs](src/LilithBridge/B1ReadableView.cs) 改进无损折叠与预算分配，采用紧凑连续编号（`材料1..N` 及无损子标 `材料N.2`），去掉 `共X片`／`已拼合` 气泡统计噪声，修正假向下指针 `"下文「近期对话」"` → `"「当前会话近期消息」"`。

### 2. 主经历“词法黑洞”、草稿考古与临时表达粘滞修复
- **消除长文词法劫持**：[MemoryStore.Experiences.cs](src/LilithBridge/MemoryStore.Experiences.cs) 对超过 36 词且超过查询词数 2 倍的历史长文引入平滑长度阻尼，并对 1-hop 联想桥按文档长度归一化，阻止 1000–2000 字旧元评论长文靠 2–3 个偶发双字词反复劫持日常闲聊，同时不影响正常长度记录与显式精确查找。
- **根治陈年草稿考古**：[GeneratedReplyMaterial.cs](src/LilithBridge/GeneratedReplyMaterial.cs) 仅在玩家明确追问旧草稿／此前没说完的话时才放行语义 `generated_reply` 检索，并将当前会话残稿收敛为严格紧邻上一轮（`T` 轮被打断 → `T+1` 轮呈递 → `T+2` 轮自动退场）。
- **收敛临时表达粘滞 JSON**：[DialogueRequest.cs](src/LilithBridge/DialogueRequest.cs)、[BridgeController.Work.cs](src/LilithBridge/BridgeController.Work.cs)、[NativeWorkPlayback.cs](src/LilithBridge/NativeWorkPlayback.cs) 与 [BridgeController.Delivery.cs](src/LilithBridge/BridgeController.Delivery.cs) 新增 `RetireDeliveredCarry`，当承载了上一轮未播完草稿的新一轮回复**完整交付完毕**（`IsFullyDelivered == true`）后自动退役已处理的 `[本会话临时表达·尚未进入长期历史]`，避免原始 JSON 跨多轮粘滞，同时完整保留连续打断与 `CarryPermissionChain` 传递撤回语义。

### 3. 主观上下文呈现瘦身、缓存与视觉回执优化
- **摘除主观块自我降权后缀并去重标题**：[MemoryStore.Reflections.cs](src/LilithBridge/MemoryStore.Reflections.cs)、[ContextBuilder.cs](src/LilithBridge/ContextBuilder.cs) 与 [B1ReadableView.cs](src/LilithBridge/B1ReadableView.cs) 去掉对自身想法／法则／关系理解／B1 材料的冗余自我降权尾缀，合并同一块内连续多条想法的重复标题行，并同步扩展 [ReplySanitizer.cs](src/LilithBridge/ReplySanitizer.cs) 覆盖全部新旧内部标题剥离。
- **KV Prefix 稳定排序、LiveTail 校验与视觉瘦身**：优化 [ContextBuilder.cs](src/LilithBridge/ContextBuilder.cs) 与 [DeepSeekClient.cs](src/LilithBridge/DeepSeekClient.cs) 的静态／动态段边界与视觉回执体积，稳定提高多轮对话缓存命中率。

### 4. 后台反思协议加固与 HTTP 长连接保活
- **强制 JSON 对象契约与卡死释放**：[DeepSeekClient.cs](src/LilithBridge/DeepSeekClient.cs)、[OpenAIProtocol.cs](src/LilithBridge/OpenAIProtocol.cs)、[BridgeController.ReflectionResponse.cs](src/LilithBridge/BridgeController.ReflectionResponse.cs) 与 [ReflectionRetryFacts.cs](src/LilithBridge/ReflectionRetryFacts.cs) 为常规反思、B1 回顾、日整理与上下文相关性调用启用服务商级 `response_format: json_object`（含 OpenAI Responses `text.format` 映射）；若同一 `Work` 连续遭遇 3 次完整 2xx 协议拒收则安全释放卡死任务并完整保留历史与记忆资产（不完整传输失败仍可重试）。
- **消除空闲断链 `sending_headers` 报错**：[DeepSeekClient.cs](src/LilithBridge/DeepSeekClient.cs) 将默认 `HttpClient` 替换为显式 `SocketsHttpHandler`（`PooledConnectionIdleTimeout = 45s`、`KeepAlivePingDelay = 30s`、`KeepAlivePingTimeout = 15s`），根治上游约 60 秒空闲断连引发的后台反思／对话偶发 `IOException`。

---

## 三、验证与兼容性摘要

- **自动回归验证（针对最终 `v0.4` 同一份编译产物 `ffe08b05`）**：
  - `Verify-All.ps1` **7 / 7 全通过**：Release 零错误构建、`eval.dll`（134 项）、`FreezeCarryTests`（262 项）、`DedupVerify`、`PerfBench`、`test_optional_recall_fixture`（3 项）、静态清洁与 LF 检查。
  - `BugFixTests` **129 / 129 全通过**（新增覆盖 B1 噪音门控、Step 2 事后回顾反思、长文词法阻尼、草稿生命周期、跨会话 B1 复活、关系理解语义召回、主观标题去重与 `RetireDeliveredCarry` 传递撤回等全部回归场景）。
- **安装器与升级兼容**：安装器同步升级至 `0.1.4.23`（`installer_schema: 17`），显式绑定 `v0.4` 产品 DLL 哈希并在 `previous_plugin_hashes` 保留 `.106` 及更早版本哈希；`v4-b1-continuity-v2.sqlite3` 与 `config.json` 100% 向后兼容。详情见 [升级与回退边界](UPGRADE-COMPATIBILITY.zh-CN.md)。

---

## 四、本版发布资产说明

- **`LilithBridge-Setup-0.4.0-cn23-ffe08b05.exe`**：单文件一键安装／升级程序（**普通玩家下载这一个即可**）。
- **`LilithBridge-installer-0.4.0-cn23-ffe08b05.zip`**：精简在线更新包（供旧版安装器检查更新与自动交接使用）。
- **`LilithBridge-source-0.4.0-cn23-ffe08b05.zip`**：清洁 MIT 源码归档（不含二进制、私密配置及非 MIT 语料）。
- **`SHA256SUMS.txt`**：以上全部发布资产的 SHA-256 校验清单。

详细安装与常见问题见 [安装与使用指南](NORMAL-USE.zh-CN.md)，遇到问题请附脱敏日志提交至 [问题反馈指南](FEEDBACK.zh-CN.md)。
