# LilithBridge .106 · 2576304b

面向官方游戏 **1.2.1 · Windows x64**。  
本版基于 `.105`（`4de072f7`）聚焦解决一个非常具体的顽疾：**把“为什么提示词改轻快了，大模型还是像在读一份两万字档案后写评论稿”的根因找出来并从协议结构上改顺**，同时补齐每句自动思考分流与原版剧情台词归属隔离。

> [!IMPORTANT]
> **怎么下载、怎么更新（新手与老玩家看这里就够了）**
> - **下载这个文件**：[`LilithBridge-Setup-0.3.150.106-cn22-2576304b.exe`](https://github.com/gtxpz2021/LilithBridge/releases/latest)（单文件一键安装／升级，**无需自己解压或合并资源**）。
> - **绝对保留存档**：直接覆盖更新会**完好保留**原有 `LilithBridgeData`（含旧版 `%APPDATA%\LilithAI`）、`config.json`、回忆／B1 数据库、DeepSeek Key、ChatGPT 登录状态与游戏原生存档，绝不重置已关闭的开关。
> - **唯一前提**：动手前请先**正常退出游戏**（若旧版正好挂起未结束反思，进游戏聊一两句待空闲再正常退出后升级即可）。

---

## 一、这次解决了什么关键问题

### 1. 混合真实对话尾轮（修复“读档案写审稿腔”的结构性根因）
- **此前的问题**：在开启材料隔离外壳（`SourceEnvelope`）时，系统把将近五十轮近期对话全部拍扁塞进同一条 `role="user"` 长说明里，整个请求里没有任何一条 `role="assistant"` 消息。模型因此一直处在“阅读超长说明的写作助手”状态，即使把人设改得再轻快，也容易反复冒出“唔，这句飘过来……不是……是……我不催”之类的旁白和说教尾缀。
- **`.106` 的做法**：
  - [DeepSeekClient.SourceBodies.cs](src/LilithBridge/DeepSeekClient.SourceBodies.cs) 与 [MemoryStore.NativeDialogueSupply.cs](src/LilithBridge/MemoryStore.NativeDialogueSupply.cs) 引入锚定式尾轮拆分（`SplitHybridTail` / `_liveTailAnchorEventId`）：把最近 `4–14` 轮（目标 `6` 轮、按缓存步进锚定）干净的普通日常来回从档案文本中移出，恢复成真实的 `user` / `assistant` 交替消息紧跟在材料后方，且不与上方 `[近期对话]` 重复。
  - [DialogueMaterialEnvelope.cs](src/LilithBridge/DialogueMaterialEnvelope.cs) 插入固定静态提示 `[近期对话续接]`，守住 DeepSeek KV Prefix Cache；遇到游戏原生剧情台词（`Origin="game"`）、转述文本、缺损片段或特殊流时自动收敛拆分边界，保障归属合同完整。
  - [B1ReadableView.cs](src/LilithBridge/B1ReadableView.cs) 放行短句重复场景（例如玩家连续说“好的”），避免误判 `readable_source_changed`。

### 2. 人设口吻、近尾锚点与反思去套路化
- **贴合少女直觉的默认声线**：重写 [PersonaDefaults.cs](src/LilithBridge/PersonaDefaults.cs) 与 [SystemPromptSlim.cs](src/LilithBridge/SystemPromptSlim.cs)，去掉偏老成点评与强说教语法的规则措辞，也不硬塞具体道具名或破折号例句；保留前作记忆底色、轻快跳脱带点狡黠与真心话断续软肋的张力。
- **集中收尾锚点 `[开口之前]`**：在系统提示最末附带精简静态收口，协同 [ReplySanitizer.cs](src/LilithBridge/ReplySanitizer.cs) 抑制破折号泛滥、无端“不是…是…”对称造句与自我解释动机，同时不额外注入动态字数损耗缓存。
- **反思笔记本不再产出固定套路剧本**：在 [DeepSeekClient.cs](src/LilithBridge/DeepSeekClient.cs) 的后台反思引导中禁止“他 X 时别急着……先……再……”式应对剧本与“晾他半句/交底”审稿语态，保留全部既有引用序号（`e#` / `n#` / `l#`）与修订/放下机制（`revises` / `settles`）。

### 3. 每句思考深度自适应路由（`auto` 档位 / `auto_v2`）与选项公开
- [DialogueRequest.cs](src/LilithBridge/DialogueRequest.cs)、[DialogueProviderOptions.cs](src/LilithBridge/DialogueProviderOptions.cs) 与 [NativeOpenAISettings.cs](src/LilithBridge/NativeOpenAISettings.cs) 开放 DeepSeek `auto` 思考选项并启用 `auto_v2` 分流：
  - 日常短闲聊、问候与确认走 `off`（配合输出上限 `256–1024` tokens）；
  - 细腻情绪与近期回忆线索走 `low`（限制在 `1024–3072` tokens）；
  - 存在与哲学探讨、看屏/视觉请求、复杂长问句自动切到 `high`。
  - 如偏好固定 `high` 或 `off` 思考模式，可在设置界面随时手动切换保存。

### 4. 游戏原生台词归属隔离与异步读策略防串扰
- **原生台词不再与自由对话混称**：在上下文与 `B1ReadableView` 材料区，原版游戏剧情台词与选项统一明确标注为 `莉莉丝（原生台词）` / `玩家（原生选项）`，并在切回自由聊天后清理过期提示，避免模型把原版剧情选项或旧台词当成刚刚的桌面自由发言。
- **异步读写隔离**：在提交请求抓取 `ExecutionContext` 前及时释放本轮组合作用域，并对跨异步嵌套 `B1` / `CarryChain` 检查增加不同查询隔离，防止 `recall_source_changed` 误触发。

### 5. 修复安装器实机回执预检误报（`.104 / .105` 遗留问题）
- 修复 [ReflectionWorkCompatibility.cs](tools/installer-source/src/ReflectionWorkCompatibility.cs) 中 `LastDialogueService` / `LastCurrentDialogueService` 校验白名单遗漏序列化布尔字段 `IsValid` 导致实机升级预检抛出 `LBINSTALL_WORK_STATE_UNKNOWN` 的问题，并在离线复制的 `.NET Framework 4.8` 实机数据副本上验证通过。

---

## 二、目前仍未解决的局限（实话实说）

1. **跨话题长距 RAG 召回仍待进一步聚焦精简**  
   目前较老的词法/语义召回碎片仍会占去一部分材料窗口，跨话题大幅跳跃时依然会让 KV 前缀缓存命中有一定下滑；后续版本计划继续做“主线索强锚 + 少量联想火花位”与折叠后逻辑单元计费优化。
2. **`auto_v2` 思考路由目前仅看当轮单句线索**  
   遇到表面极短的反话或承接上轮情绪的短句时，单轮关键词判断仍有局限；后续准备引入结合最近 2–3 轮语境的前置轻量分流。
3. **桌面主动性与受限交互工作区仍在后续主线**  
   本版优先把“像她本人自然接话、记住前因后果、运行稳当”巩固下来；更丰富的桌面共居主动性仍在后续规划中。
4. **验证边界说明**  
   针对 `.106` 重编产物已离线通过 `eval.dll`（134 项）、`FreezeCarryTests`（262 项）、`PerfBench`、`DedupVerify`、`test_optional_recall_fixture`（3 项）、`BugFixTests`（51 项含原生归属、`CarryChain`、`auto_v2`、混合尾轮与缓存锚点回归）及源码洁净检查；同核心逻辑版本已于实机 **1.2.1 / Windows x64** 连续完成 15 轮自由聊天验证；安装器 `.exe` 与内嵌载荷已通过编译、SHA-256、静态清单及 `.NET Framework 4.8` 下对实机数据副本的 `PersistentStateCompatibility` 兼容预检，**未在本机自动点按 GUI 安装向导完成实装覆盖点击演练**。

---

## 三、附件清单与各自用途

| 文件名 | 普通玩家需要下吗 | 说明 |
|---|---|---|
| `LilithBridge-Setup-0.3.150.106-cn22-2576304b.exe` | **需要（就下它）** | 一体化单文件安装与无损更新程序（支持自动加载器、可选本地 BGE 召回与断点缓存复用） |
| `LilithBridge-installer-0.3.150.106-cn22-2576304b.zip` | 无需手下 | 供安装器在线更新与受限通道交接使用的瘦身安装包（`< 50 MiB`） |
| `LilithBridge-source-0.3.150.106-cn22-2576304b.zip` | 开发者用 | 本版冻结清洁源码快照（不含游戏本体、私人配置及随包第三方/原作语料） |
| `SHA256SUMS.txt` | 按需核对 | 上述文件的 SHA-256 校验码文本 |

<details>
<summary>展开查看：兼容性边界、产品身份与校验细节（开发者／排障参考）</summary>

- **版本与产物身份**：
  - 产品版本：`0.3.150.106-hybrid-live-tail.1`
  - `LilithBridge.dll` SHA-256：`2576304bf3d9fc5c1ed4db241a58b9b5b97d49efea69b4bce8266b1612a8a6a3`
  - 安装器版本：`0.1.4.22-hybrid-live-tail.release.1`（`installer_schema: 16`，内置对 `.105` `4de072f7...` 及更早历史哈希的无损升级识别与 `handoff_protocol: 1` 交接支持）
- **持久化兼容**：
  - `v4-b1-continuity-v2.sqlite3`（version 2/3/4）、`sessions/generated-public-v1`、`config.json`（`config_schema: 1`）与 `v4-reflection-window.json` 已完成回执格式均与 `.105` 保持一致（`Work != null` 时仍保守要求先回旧版正常结束反思后再升级，详见 [`UPGRADE-COMPATIBILITY.zh-CN.md`](UPGRADE-COMPATIBILITY.zh-CN.md)）。
- **许可提示**：自有源码与工具为 MIT 许可；随安装包附带的 `first-game.collection.jsonl` 语料与 BepInEx/Python/BGE 等第三方组件不归入 MIT（见 [`LICENSE-STATUS.md`](LICENSE-STATUS.md)）。

</details>

---

[回到首页](README.md) · [安装与使用指南](NORMAL-USE.zh-CN.md) · [架构入门](ARCHITECTURE.md) · [问题反馈](FEEDBACK.zh-CN.md)
