# 安装、升级与回退兼容性说明（.105 → .106）

- 产品目标产物 `LilithBridge.dll` SHA-256：`2576304bf3d9fc5c1ed4db241a58b9b5b97d49efea69b4bce8266b1612a8a6a3`（版本 `0.3.150.106-hybrid-live-tail.1`）。
- 本版安装器（`0.1.4.22-hybrid-live-tail.release.1`，`installer_schema: 16`）已将 `2576304bf3d9fc5c1ed4db241a58b9b5b97d49efea69b4bce8266b1612a8a6a3` 显式绑定到 `PersistentStateCompatibility.KnownCurrentReader` 与 `ReflectionWorkCompatibility` 的 `SupportsCompletedReceipt` / `SupportsRevisitReceipt` / `SupportsFinalReceipt`，补全了 `LastDialogueService` / `LastCurrentDialogueService` 序列化字段 `IsValid` 的兼容校验以消除旧版实机回执升级时的 `LBINSTALL_WORK_STATE_UNKNOWN` 误报，并在 `install-manifest.json` 的 `previous_plugin_hashes` 中保留 `.105`（`4de072f7e01f0e820f1ae6c7069963165bf969d4e99f21b8e9d8638e54bcf492`）、`.104-cn20`（`61122db554cdc2f71ae52dabae49d4c9fb8bb09c5266e57583353681572bfaf5`）及更早版本哈希。
- 数据与配置保留：
  1. `v4-b1-continuity-v2.sqlite3`（version 2/3/4）与 `sessions\generated-public-v1` 格式相对 `.105` 未变更。
  2. `config.json` 原有所有键、API Key、登录凭据与用户显式关闭项全部保留不改写；本版在设置 UI 开放 `auto` 思考档位切换，原有 `config_schema: 1` 保持向后兼容。
  3. 反思持久回执读取边界与 `.105` 一致；仍未开放 `SupportsWork` 自动接管非空 `Work`。从 `.105` 或更早版本升级前请先正常退出游戏；若 `v4-reflection-window.json` 仍含未完成的 `Work`，安装器会保守提示并停止，请先让旧版本跑完该轮反思后再更新，切勿手动删日志绕过预检。
- 安装器自举交接（Schema 15 → 16）：因旧版 `.105` 安装器（`installer_schema: 15`）的兼容白名单不包含本版新 DLL 哈希，本包声明 `min_installer_schema: 16` 并在 `CANDIDATE.json` 提供 `handoff_protocol: 1` 与 `supported_config_schema: 1`，确保旧安装器通过在线更新获取 `LilithBridge-installer-0.3.150.106-cn22-2576304b.zip` 时安全交接给 `.22` 新安装器完成安装。
- 程序回退边界：仅替换旧 DLL 不等于完整数据回退；如需回退到更早版本，请同时使用与旧版本匹配的 `LilithBridgeData` 备份或独立数据目录。
