# LilithBridge v0.4 · `ffe08b05`

面向官方游戏 **1.2.1 · Windows x64**。  
如果说上一版 `.106` 是在把莉莉丝的说话语气捋顺，那么 **`v0.4`** 做的事很简单：**把藏在记忆和后台里的几个老大难隐性病灶一次清干净，让她从“只是按台账记事”，真正向“脑子在线、记得住你们相处脉络的长期桌宠伴侣”迈进一大步。**

> [!IMPORTANT]
> **怎么下载、怎么更新（首次安装与老玩家升级通用）**
> - **下载这一个文件就够了**：[`LilithBridge-Setup-0.4.0-cn23-ffe08b05.exe`](https://github.com/gtxpz2021/LilithBridge/releases/latest)（单文件一键安装／升级，**不用自己解压或合并资源**）。
> - **绝对保留存档**：直接覆盖升级会**完好保留**你原有的 `LilithBridgeData`（含旧版 `%APPDATA%\LilithAI`）、`config.json`、所有记忆与笔记、DeepSeek Key、ChatGPT 登录状态和游戏原生存档，绝不重置已关闭的开关。
> - **唯一前提**：动手前请先**正常退出游戏**（若旧版正好有后台反思还没跑完，进游戏聊一两句等它空闲后再正常退出即可）。

---

## 一、这一版玩起来有什么明显变化？（大白话版）

### 1. 以前的旧想法“活过来”了，而且聊到空隙时她会自己琢磨、自己改口
- **老存档里的旧想法恢复生效**：以前跨一天重开游戏后，很多她之前记下来的想法会被后台误判成无权读取、悄悄“封存”掉。现在这些旧想法**无需迁移存档就会自动复活**，聊到相关往事时她能自然顺着以前的念头往下接。
- **聊天停下来后，她会安静回看刚聊过的事**：以前只在当场回复时顺手记一下；现在在你们聊完歇口气的时候，她会完整回看刚发生的那几轮对话，把有意思的判断沉淀下来，或者在被新事说服后，顺手修正、补充自己以前的想法（纯打招呼的闲聊不会瞎折腾，也不会凭空编造你没说过的话）。

### 2. 终于不再“聊着日常突然串戏”：清掉了旧长文、陈年残稿和乱入的复读
- **闲聊不再被旧长文带偏**：以前只要聊到几个常用词，过去聊过的一两千字长篇大论就会反复霸占记忆名额，导致她说几句就跑偏回老话题。现在正常聊天清爽很多，只有你真的提起相关话题时才会翻出来。
- **不再莫名其妙翻出几天前没播完的残稿**：以前气泡被打断过一次，隔好几天还可能被当成“未展示草稿”翻出来。现在只有你**刚刚打断的上一轮**会提醒她“后半句你没看见”，再聊一轮播完就会干净退场，不再粘在后台拖着走。
- **不再满屏“共几片、已拼合”的机器杂音**：整理给她的参考材料变得更干净紧凑，也不会一边递给她想法、一边反复自我否定“你可以忽略/可以推翻”，聊天更有自己的主心骨。

### 3. 她更能想起“你们之间现在的相处分寸”
- **关系理解不再长期缺席**：以前她写下的那些关于“你们怎么相处、什么分寸彼此最舒服”的理解，平时闲聊几乎很少能浮出来（除非你直白地问“我们算什么关系”）。现在只要话题沾边、情绪对味，她会更自然地想起自己对你们这段关系的真实感受；而纯打招呼或聊晚饭吃什么时则保持克制，不会生硬乱套。

### 4. 本地记忆检索更聪明，还支持了可选的桌面感官联动
- **按意思找回忆更准、不重复**：本地可选语义召回升级了检索与去重方式，不容易把意思一模一样的几句旧话重复塞满，也更容易在合适的时候冒出有点意思的跨天联想。
- **新增可选感官侧车支持**：为希望接入桌面环境感知（如环境声响、专注时长、睡眠惊醒微反应等）的进阶玩家预留并打通了独立进程宿主接口与动作节律协同；平时不开也完全不影响正常聊天。

### 5. 后台反思和长连接更稳，减少莫名其妙的断连报错
- **修掉了闲置一分钟后再发请求偶发断连的老毛病**：以前聊着聊着停顿一两分钟，下一句或后台整理偶尔会因为上游连接超时直接报错；现在增加了连接保活与自动换新。
- **后台整理更稳不卡死**：强制要求后台整理严格按结构化格式返回；万一某次模型抽风连续返回异常格式，系统会自动安全释放任务，不再卡住后续反思，也不会丢任何聊天记录。

---

## 二、本版发布文件怎么选？

- **`LilithBridge-Setup-0.4.0-cn23-ffe08b05.exe`**：单文件一键安装／升级程序（**绝大多数玩家只下载这一个即可**）。
- **`LilithBridge-installer-0.4.0-cn23-ffe08b05.zip`**：精简在线更新包（供旧版安装器在线检查更新与自动交接使用）。
- **`LilithBridge-source-0.4.0-cn23-ffe08b05.zip`**：清洁 MIT 开源源码包（不含二进制、私密配置及非 MIT 语料）。
- **`SHA256SUMS.txt`**：以上全部文件的 SHA-256 校验值清单。

详细安装与常见问题见 [安装与使用指南](NORMAL-USE.zh-CN.md)，升级兼容与回退说明见 [升级与回退边界](UPGRADE-COMPATIBILITY.zh-CN.md)，遇到问题请附脱敏日志提交至 [问题反馈指南](FEEDBACK.zh-CN.md)。

---

## 三、技术更新明细与源码改动（开发者／进阶参考）

<details>
<summary>点击展开完整技术变更、源码入口与回归测试明细（相对 <code>.106 / 2576304b</code>）</summary>

### 1. 新增架构与核心机制
- **独立进程感官侧车宿主机（Sensory Sidecar Host）与微反应**：
  - 新增 [SensorySidecarClient.cs](src/LilithBridge/SensorySidecarClient.cs)、[BridgeController.Sensory.cs](src/LilithBridge/BridgeController.Sensory.cs) 与 [ConfigStore.Sensory.cs](src/LilithBridge/ConfigStore.Sensory.cs)，支持外置侧车进程、独立 KV 尾槽注入、释放窗口门控、`TickSensoryRuntime` 节流与线程 HWND 缓存；
  - 新增姿态安全静默动作解析器、BPM `TimeScale` 同步与微反应状态机节律（[MicroReactionDirector.cs](src/LilithBridge/MicroReactionDirector.cs)），以及睡眠惊醒小剧场与专注长跑静默笔记（`pending_focus_note` IPC，[BridgeController.BackgroundWrites.cs](src/LilithBridge/BridgeController.BackgroundWrites.cs)）。
- **几何语义检索升级（Local BGE Worker）**：
  - 升级 [semantic_worker.py](optional/semantic-worker/semantic_worker.py)（SHA-256 `684462f4735d21511b4188018f269de68bcf1de644967b727a0733d811d54979`）、[MemoryStore.SemanticRecall.cs](src/LilithBridge/MemoryStore.SemanticRecall.cs) 与 [BridgeController.SemanticRecall.cs](src/LilithBridge/BridgeController.SemanticRecall.cs)：引入特征白化几何度量、CSLS 校准、DPP 多样性去冗余、多维联想火花（Orthogonal / Wormhole / Bridge / Scene Sparks）及显著度门控（`SalienceGated`）；
  - 完成 H-1..H-5 检索硬化：显式检索无条件消费 RRF、有界 `doc_sims`/`doc_quantiles` 传输、单轮回填优先级修复、全负 CSLS 偏移保护、无空格方括号 `SceneText` 解析与 BGE 探针预热。
- **B1 事后回顾反思（Experience-Anchored Retrospective Development）**：
  - 重写 [MemoryStore.B1.Reconsideration.cs](src/LilithBridge/MemoryStore.B1.Reconsideration.cs)、[MemoryStore.B1.ReflectionFlow.cs](src/LilithBridge/MemoryStore.B1.ReflectionFlow.cs) 与 [DeepSeekClient.B1Reflection.cs](src/LilithBridge/DeepSeekClient.B1Reflection.cs)：按完整问答边界（最近 3 个 `user` 回合、上限 24 条 / 3600 字）切片，防止长多气泡回复挤掉 `user` 触发句；
  - 词法选标采用严格 `[0, 1]` 有界双口径：用户发言走带长文阻尼的 `UserLexScore`，多气泡窗口走几何均值 `OchiaiScore`（阈值 `0.18`、至少 4 命中），结合缓存语义与 `Conditions` 实现新形成、`revises` 演进与 `qualify` 分支并存；禁止后台生成 `reported`、严格 CAS 与源句屏蔽级联撤回，100% SQLite 向后兼容。
- **`Readings`（关系理解）语义索引与精准共激活**：
  - [MemoryStore.SemanticRecall.cs](src/LilithBridge/MemoryStore.SemanticRecall.cs) 与 [SemanticRecallWorker.cs](src/LilithBridge/SemanticRecallWorker.cs) 将 `_relationshipReadings` 纳入 `SemanticDomain.Thoughts` 增量索引；
  - [MemoryStore.MaterialSelection.cs](src/LilithBridge/MemoryStore.MaterialSelection.cs)、[ContextMaterialSelection.cs](src/LilithBridge/ContextMaterialSelection.cs) 与 [ContextBuilder.cs](src/LilithBridge/ContextBuilder.cs) 接通语义直中（`>= 0.57`，词面辅助 `>= 0.54`）与高置信笔记／原话锚点共激活，同时守住寒暄过滤、`SalienceGated`、`SemanticCandidateMatches` 哈希／版本校验与 KV Prefix Cache 边界。

### 2. 关键根因修复
- **B1 跨会话复活**：[MemoryStore.B1.cs](src/LilithBridge/MemoryStore.B1.cs)、[MemoryStore.MethodSources.cs](src/LilithBridge/MemoryStore.MethodSources.cs) 与 [MemoryStore.ReflectionReasonBasis.cs](src/LilithBridge/MemoryStore.ReflectionReasonBasis.cs) 新增 `B1EntryPermissionFootprint`，`Footprint` 中未被 `Sources` / `ObjectAnchors` / `Materials` 引用的环境原生台词不再触发跨会话 `source_permission_denied` 误杀，显式屏蔽与防篡改哈希仍严格级联撤回。
- **B1 发现降噪、共激活与无损折叠**：[MemoryStore.B1.Discovery.cs](src/LilithBridge/MemoryStore.B1.Discovery.cs)、[MemoryStore.B1.Projection.cs](src/LilithBridge/MemoryStore.B1.Projection.cs)、[B1ProjectionBudget.cs](src/LilithBridge/B1ProjectionBudget.cs) 与 [B1ReadableView.cs](src/LilithBridge/B1ReadableView.cs) 增加 BGE 显著度／离群门控、弱词与粘连词清洗、已发现原话共激活 B1 头节点、紧凑连续编号（`材料1..N` + 无损子标 `材料N.2`）、去掉 `共X片`/`已拼合` 噪声并修正 `"下文「近期对话」"` → `"「当前会话近期消息」"`。
- **主经历长文阻尼、草稿考古与临时表达退役**：
  - [MemoryStore.Experiences.cs](src/LilithBridge/MemoryStore.Experiences.cs)：对 `>36` 词且超查询 2 倍的长文加平滑长度阻尼，并对 1-hop 联想桥长度归一化；
  - [GeneratedReplyMaterial.cs](src/LilithBridge/GeneratedReplyMaterial.cs)：门控语义 `generated_reply` 检索至显式追问旧草稿场景，并将当前会话残稿收敛到紧邻上一轮（`T -> T+1 -> T+2` 退役）；
  - [DialogueRequest.cs](src/LilithBridge/DialogueRequest.cs)、[BridgeController.Work.cs](src/LilithBridge/BridgeController.Work.cs)、[NativeWorkPlayback.cs](src/LilithBridge/NativeWorkPlayback.cs) 与 [BridgeController.Delivery.cs](src/LilithBridge/BridgeController.Delivery.cs)：新增 `RetireDeliveredCarry`，在承接轮完整交付（`IsFullyDelivered == true`）后自动退役已处理的 `[本会话临时表达·尚未进入长期历史]`，同时保留 `CarryPermissionChain` 传递撤回语义。
- **主观标题瘦身、去重与泄露剥离**：[MemoryStore.Reflections.cs](src/LilithBridge/MemoryStore.Reflections.cs)、[ContextBuilder.cs](src/LilithBridge/ContextBuilder.cs) 与 [B1ReadableView.cs](src/LilithBridge/B1ReadableView.cs) 移除主观块自我降权后缀并合并同块连续重复标题行，同步扩展 [ReplySanitizer.cs](src/LilithBridge/ReplySanitizer.cs)；同步优化 KV Prefix 顺序、LiveTail 宿主校验与视觉回执瘦身。
- **反思协议加固与传输保活**：[DeepSeekClient.cs](src/LilithBridge/DeepSeekClient.cs)、[OpenAIProtocol.cs](src/LilithBridge/OpenAIProtocol.cs)、[BridgeController.ReflectionResponse.cs](src/LilithBridge/BridgeController.ReflectionResponse.cs) 与 [ReflectionRetryFacts.cs](src/LilithBridge/ReflectionRetryFacts.cs) 强制启用 `response_format: json_object`（含 OpenAI `text.format` 映射）与 3 次完整 2xx 协议拒收后释放卡死 `Work`；`DeepSeekClient` 切换显式 `SocketsHttpHandler`（`PooledConnectionIdleTimeout = 45s`、`KeepAlivePingDelay = 30s`、`KeepAlivePingTimeout = 15s`）消除空闲 `sending_headers` `IOException`。

### 3. 回归验证与打包校验
- **产品 DLL**：`0.4.0`（SHA-256 `ffe08b05eddca0ae3ef7c4be619b5974a472a7bf7990abff5e9b4a8d93bfc8df`）
- **验证门全绿**：`Verify-All.ps1` **7 / 7**（Release 0 错误、`eval.dll` 134 项、`FreezeCarryTests` 262 项、`DedupVerify`、`PerfBench`、`test_optional_recall_fixture` 3 项、静态清洁）+ `BugFixTests` **129 / 129**。
- **安装器**：`0.1.4.23-v0.4.release.1`（`installer_schema: 17`，显式绑定 `v0.4` 读契约并在 `previous_plugin_hashes` 保留 `.106` 及更早版本哈希，单文件嵌入载荷静态一致性校验通过）。

</details>
