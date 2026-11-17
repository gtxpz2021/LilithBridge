# LilithBridge .105 · 4de072f7

面向官方游戏 **1.2.1 · Windows x64**。这版的核心工作围绕一件事展开：**让记忆回到“完整的人与经历”，而不是“零碎的气泡和审讯式规则”。**

> [!IMPORTANT]
> **怎么下载、怎么更新（新手与老玩家看这里就够了）**
> - **下载这个文件**：[`LilithBridge-Setup-0.3.150.105-cn21-4de072f7.exe`](https://github.com/gtxpz2021/LilithBridge/releases/latest)（单文件一键安装／升级，**无需自己解压或合并资源**）。
> - **绝对保留存档**：直接覆盖更新会**完好保留**原有 `LilithBridgeData`（含旧版 `%APPDATA%\LilithAI`）、`config.json`、回忆/B1 数据库、DeepSeek Key、ChatGPT 登录状态与游戏原生存档，绝不重置已关闭的开关。
> - **唯一前提**：动手前请先**正常退出游戏**（若旧版正好挂起未结束反思，进游戏正常收尾退出后再升级即可）。

---

## 这一版带来了什么实实在在的变化

1. **记忆不再被切碎，回忆时还原成完整的一段话**
   - 以往长回复在游戏里分几十个气泡播出后，在回忆材料区往往也被拆成几十条零散片段，还可能和“近期对话”“语义召回”一式两份地重复端给模型。
   - 现在同一次回复的显示气泡会在供给阶段自动合并回整段，折叠掉已在别处完整出现过的同源内容，并滤掉已经完整说完的重复草稿提示——**给真正相关的新线索和跳跃灵感留出上下文空间**。
2. **把理解还给自然交流：支持修订、收起旧想法，少点“审计腔”**
   - 长期笔记和经验规律不再像挂在头顶的死规定：旧理解既能随新对话自然修订，也能在过时后被明确“放下／收起”（`settles`），不让模型一边和你聊天一边像监工一样审视每一句话的格式来源。
   - 后台反思更聚焦在“换位回顾与心理脉络”的整理，与外部客观事实保持清晰界线，不把未观察过的猜想随便写成过去既成事实。
3. **响应更稳、前缀命中更高，长对话不易越聊越重**
   - 依照前缀缓存友好的思路重新分层：把静态规则、固定工具结构（含 `revisit_thought` 按需重访）与平滑推进的近期对话稳定在前部，动态材料和当前输入放到尾部——配合新增的可选步进参数（`prompt_cache_history_stride`，默认 `8`），连续聊天时的缓存利用率更稳，默认事件日志也做了瘦身。
   - 解决了长会话与连续跨帧下容易拖慢主线程甚至卡死的问题，整体回复交付更跟手。

---

## 附件清单与各自用途

| 文件名 | 普通玩家需要下吗 | 说明 |
|---|---|---|
| `LilithBridge-Setup-0.3.150.105-cn21-4de072f7.exe` | **需要（就下它）** | 一体化单文件安装与无损更新程序（支持自动加载器、可选本地 BGE 召回与断点缓存复用） |
| `LilithBridge-installer-0.3.150.105-cn21-4de072f7.zip` | 无需手下 | 供安装器在线更新与受限通道交接使用的瘦身安装包（`< 50 MiB`） |
| `LilithBridge-source-0.3.150.105-cn21-4de072f7.zip` | 开发者用 | 本版冻结清洁源码快照（不含游戏本体、私人配置及随包第三方/原作语料） |
| `SHA256SUMS.txt` | 按需核对 | 上述文件的 SHA-256 校验码文本 |

<details>
<summary>展开查看：兼容性边界、产品身份与离线检验细节（开发者／排障参考）</summary>

- **版本与产物身份**：
  - 产品版本：`0.3.150.105-posture-reflection-cache.1`
  - `LilithBridge.dll` SHA-256：`4de072f7e01f0e820f1ae6c7069963165bf969d4e99f21b8e9d8638e54bcf492`
  - 安装器版本：`0.1.4.21-posture-reflection-cache.release.1`（`installer_schema: 15`，内置对 `61122db5...` 等历史版本的受控升级支持与自动交接声明 `supported_config_schema: 1`）
- **数据兼容**：
  - `v4-b1-continuity-v2.sqlite3`（version 2/3/4）与既有 `config.json` 保持向后兼容；新增可选配置 `dialogue.prompt_cache_history_stride`（默认 `8`）与 `telemetry.verbose_supply_trace`（默认 `false`）在旧配置文件缺失时自动取默认值。
  - `v4-reflection-window.json` 在 `Work == null` 空闲状态可无缝衔接 `.104`；涉及未完成反思 `Work` 时仍保持保守阻断边界（需由原版本收尾后再升级，详细规则见 [`UPGRADE-COMPATIBILITY.zh-CN.md`](UPGRADE-COMPATIBILITY.zh-CN.md)）。
- **本版检验范围**：
  - 针对重编 `.105` 产物离线通过了 `eval.dll`（134 项）、`FreezeCarryTests`（262 项）、`PerfBench`、`DedupVerify`、`test_optional_recall_fixture`（3 项），以及静态 PE/CLR 载荷、ZIP CRC、宿主静态接口契约（`HostCapabilities.CheckContract`）与兼容门核对；实机 1.2.1 桌面端对话与缓存表现于同逻辑构建上完成 5 轮验证。未对新编译的 `.exe` 安装向导 UI 重复执行实机覆盖安装演练。
- **许可提示**：自有源码与工具为 MIT 许可；随安装包附带的 `first-game.collection.jsonl` 语料与 BepInEx/Python/BGE 等第三方组件不归入 MIT（见 [`LICENSE-STATUS.md`](LICENSE-STATUS.md)）。

</details>

---

[回到首页](README.md) · [安装与使用指南](NORMAL-USE.zh-CN.md) · [架构入门](ARCHITECTURE.md) · [问题反馈](FEEDBACK.zh-CN.md)
