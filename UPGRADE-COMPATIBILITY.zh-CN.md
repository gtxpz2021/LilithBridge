# 安装、升级与回退兼容性说明（.104 → .105）

- 产品目标产物 `LilithBridge.dll` SHA-256：`4de072f7e01f0e820f1ae6c7069963165bf969d4e99f21b8e9d8638e54bcf492`（版本 `0.3.150.105-posture-reflection-cache.1`）。
- 本版安装器（`0.1.4.21-posture-reflection-cache.release.1`，`installer_schema: 15`）已将 `4de072f7e01f0e820f1ae6c7069963165bf969d4e99f21b8e9d8638e54bcf492` 显式绑定到 `PersistentStateCompatibility.KnownCurrentReader` 与 `ReflectionWorkCompatibility` 的 `SupportsCompletedReceipt` / `SupportsRevisitReceipt` / `SupportsFinalReceipt`，并在 `install-manifest.json` 的 `previous_plugin_hashes` 中保留 `61122db554cdc2f71ae52dabae49d4c9fb8bb09c5266e57583353681572bfaf5`（`.104-cn20`）及更早版本哈希。
- 数据与配置保留：
  1. `v4-b1-continuity-v2.sqlite3`（version 2/3/4）与 `sessions\generated-public-v1` 格式未变更。
  2. `config.json` 原有所有键、API Key、登录凭据与用户显式关闭项全部保留不改写；本版新增 `dialogue.prompt_cache_history_stride`（默认 `8`）与 `telemetry.verbose_supply_trace`（默认 `false`），旧配置文件缺失这两个键时自动使用默认值。
  3. 反思请求快照新增 `ShortAliases` 与提案 `Settles`/`SettleReason` 字段，因此未开放 `SupportsWork` 自动接管非空 `Work`。从 `.104` 升级前请正常退出游戏；若 `v4-reflection-window.json` 仍含未完成的 `Work`，安装器会保守提示并停止，请先让旧版本跑完该轮反思后再更新，切勿手动删日志绕过预检。
- 安装器自举交接（Schema 14 → 15）：因旧版 `.104-cn20` 安装器（`installer_schema: 14`）的兼容白名单不包含新 DLL 哈希，本包声明 `min_installer_schema: 15` 并在 `CANDIDATE.json` 提供 `handoff_protocol: 1` 与 `supported_config_schema: 1`，确保旧安装器通过在线更新获取 `LilithBridge-installer-0.3.150.105-cn21-4de072f7.zip` 时安全交接给 `.21` 新安装器完成安装。
- 程序回退边界：仅替换旧 DLL 不等于完整数据回退；如需回退到更早版本，请同时使用与旧版本匹配的 `LilithBridgeData` 备份或独立数据目录。
