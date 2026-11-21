# 安装、升级与回退兼容性说明（.106 → v0.4）

- 产品目标产物 `LilithBridge.dll` SHA-256：`ffe08b05eddca0ae3ef7c4be619b5974a472a7bf7990abff5e9b4a8d93bfc8df`（版本 `0.4.0` / 发布标签 `v0.4`）。
- 本版安装器（`0.1.4.23-v0.4.release.1`，`installer_schema: 17`）已将 `ffe08b05eddca0ae3ef7c4be619b5974a472a7bf7990abff5e9b4a8d93bfc8df` 显式绑定到 `PersistentStateCompatibility.KnownCurrentReader` 与 `ReflectionWorkCompatibility` 的 `SupportsCompletedReceipt` / `SupportsRevisitReceipt` / `SupportsFinalReceipt`，并在 `install-manifest.json` 的 `previous_plugin_hashes` 中保留 `.106`（`2576304bf3d9fc5c1ed4db241a58b9b5b97d49efea69b4bce8266b1612a8a6a3`）、`.105`（`4de072f7e01f0e820f1ae6c7069963165bf969d4e99f21b8e9d8638e54bcf492`）及更早版本哈希。
- 数据与配置保留：
  1. `v4-b1-continuity-v2.sqlite3`（version 2/3/4）与 `sessions\generated-public-v1` 格式相对 `.106` 保持 100% 向后兼容；旧存档中因环境原生问候被跨会话拦截的历史 B1 想法将在读时自动恢复，无需迁移或改写数据库文件。
  2. `config.json` 原有所有键、API Key、登录凭据与用户显式关闭项全部保留不改写；本版新增的可选感官侧车（`sensory.*`）与几何语义召回开关缺失时均使用安全默认值，`config_schema: 1` 保持向后兼容。
  3. 若已启用本地语义召回，本版包含更新后的 `semantic_worker.py`（SHA-256 `684462f4735d21511b4188018f269de68bcf1de644967b727a0733d811d54979`），首次启动时会自动增量补充关系理解（`reading`）等向量条目，不丢失原有索引。
  4. 反思持久回执读取边界与 `.106` 一致；仍未开放 `SupportsWork` 自动接管非空 `Work`。从 `.106` 或更早版本升级前请先正常退出游戏；若 `v4-reflection-window.json` 仍含未完成的 `Work`，安装器会保守提示并停止，请先让旧版本跑完该轮反思后再更新，切勿手动删日志绕过预检。
- 安装器自举交接（Schema 16 → 17）：因旧版 `.106` 安装器（`installer_schema: 16`）的兼容白名单不包含本版新 DLL 哈希，本包声明 `min_installer_schema: 17` 并在 `CANDIDATE.json` 提供 `handoff_protocol: 1` 与 `supported_config_schema: 1`，确保旧安装器通过在线更新获取 `LilithBridge-installer-0.4.0-cn23-ffe08b05.zip` 时安全交接给 `.23` 新安装器完成安装。
- 程序回退边界：仅替换旧 DLL 不等于完整数据回退；如需回退到更早版本，请同时使用与旧版本匹配的 `LilithBridgeData` 备份或独立数据目录。
