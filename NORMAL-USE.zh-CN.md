# LilithBridge .105 · 4de072f7

面向官方游戏 **1.2.1 / Windows x64**。本版在 `v0.3.150.104-61122db5` 的单文件安装、国内镜像与游戏内 DeepSeek Key 入口基础上，重点完成反思与人设表达收敛、多轮卡死修复、KV 前缀缓存优化，以及 B1 记忆碎片拼合与跨区去重。

## 这次改变

1. **人设姿态与反思解绑（Wave 1）**
   - 取消反思回写与人格提示中的强行审计腔和过度限制，明确区分第一人称心理回望与外部事实编造。
   - 支持 `lesson.settles` / `settle_reason` 与反思经验相关性门控；整理 `PersonaDefaults` 与精简系统提示词、会话状态表达及梦境前缀。
2. **多轮卡死修复与跨帧假超时消除（Wave 2）**
   - 引入作用域权限缓存（`SourcePermissionScope`）与请求级记忆化，消除 `TemporaryExpressionSession.Capture()` 在连续多轮对话后的指数递归计算，解决单核 CPU 100% 锁死问题。
   - 修复跨长帧导致的 `Time.unscaledTime` 3 秒假超时（`appearance_unconfirmed` 误判取消气泡）。
3. **KV 缓存前缀重排、性能与遥测瘦身（P1）**
   - 重排系统指令、静态工具定义与锚点步进历史窗口（新增可选配置 `dialogue.prompt_cache_history_stride`，默认 `8`），提升 DeepSeek/LLM 上下文前缀缓存命中率。
   - 新增请求内读取记忆化、异步语义提交与精简遥测开关（`telemetry.verbose_supply_trace`，默认 `false`）。
4. **同回复气泡拼合与跨区去重（P2）**
   - 将同一次回复拆成多个气泡展示的片段在 `B1` 材料区自动合并为整段（`[同次已显示回复组 ... 已拼合]`），避免同一句话被切碎后重复占用上下文。
   - 自动折叠已在 `[近期对话]` / `[关联召回的过往原话]` / `[语义与联想参考]` 出现的同源文本，过滤已 100% 完整播出的假草稿回复，并用自然语言格式整理 `[你的想法记录]`。

## 下载与一键安装 / 保留存档更新

- **全新安装或手动覆盖更新（推荐）**：下载并双击单个 **`LilithBridge-Setup-0.3.150.105-cn21-4de072f7.exe`**。先正常退出游戏，核对向导自动识别的游戏目录后直接点击安装即可。
- **保留原有存档与配置**：安装器仅替换程序自身拥有的 `BepInEx\plugins\LilithBridge.dll`（并保留可回退备份与事务日志），**绝不覆盖、重置或删除** `LilithBridgeData`（含 `%APPDATA%\LilithAI` 旧目录）、`config.json`、记忆库、`v4-b1-continuity-v2.sqlite3`、OpenAI/ChatGPT 登录态、DeepSeek Key 及游戏原生存档。已有 `false` / `off` 关闭项保持原样。
- **旧版安装器在线更新**：若使用旧版 `.104-cn20` 安装器的“检查项目更新”获取在线更新包 **`LilithBridge-installer-0.3.150.105-cn21-4de072f7.zip`**（小于 50 MiB），因本版包含兼容性门更新，旧安装器将通过交接协议（`handoff_protocol: 1`、`min_installer_schema: 15`）自动安全交接至 `.21` 新安装器继续完成更新（未保存的临时 Key 不会自动传递，已有本地 `config.json` 与 Key 保持原位）。
- 源码快照可使用随附 **`LilithBridge-source-0.3.150.105-cn21-4de072f7.zip`**；全部资产校验见 `SHA256SUMS.txt`。

## 验证与兼容边界

- 产品版本：`0.3.150.105-posture-reflection-cache.1`；安装器构建：`0.1.4.21-posture-reflection-cache.release.1`。
- `LilithBridge.dll` SHA-256：`4de072f7e01f0e820f1ae6c7069963165bf969d4e99f21b8e9d8638e54bcf492`。
- 兼容性说明：`v4-b1-continuity-v2.sqlite3`（version 2/3/4）与 `config.json` 保持向后兼容（新增的两个配置键缺省时自动按默认值 `8` 与 `false` 生效）。`v4-reflection-window.json` 在 `Work == null` 空闲状态下与 `.104` 完全兼容；若旧版尚有未完成反思 `Work`，请先运行旧版游戏待其自然结束并正常退出游戏后再升级。
- 已完成验证：针对本版重编 `0.3.150.105` `LilithBridge.dll` 与安装源码，离线通过 `eval`（134 项）、`FreezeCarryTests`（262 项）、`PerfBench` 性能基准、`DedupVerify` 去重校验、`test_optional_recall_fixture`（3 项），并在实机游戏（Windows x64 / 1.2.1）中验证过同源代码产物；此外安装器经过静态 PE/CLR 载荷、SHA-256、ZIP CRC 及宿主接口静态合约核对。未重复独立启动安装向导执行实机 UI 覆盖安装演练。

## 许可与反馈

- 自有源码、工具与文档采用 MIT 许可；随包 `first-game.collection.jsonl` 仅用于安装器分发且与第三方依赖均不属于项目 MIT（见 `LICENSE-STATUS.md`），公开源码仓库与源码包均不含原作语料正文、游戏本体、个人配置或任何凭据。
- 问题反馈：https://github.com/gtxpz2021/LilithBridge/issues 。请附版本身份、发生时间、最短复现步骤及自选脱敏日志（`BepInEx/LogOutput.log` 等）；切勿上传 API Key、认证文件或完整 `LilithBridgeData` 目录。
