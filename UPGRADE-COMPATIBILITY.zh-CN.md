# 安装、升级与回退兼容性说明（v0.4 / .106 → v0.4.1）

- 产品目标产物 `LilithBridge.dll` SHA-256：`2a2a26e41bba3776384c710cfc68b6a4685133935912cb37bda17356c3f0f0b4`（版本 `0.4.1` / 发布标签 `v0.4.1`）。
- 本版安装器（`0.1.4.24-v0.4.1.release.1`，`installer_schema: 18`）已将 `2a2a26e41bba3776384c710cfc68b6a4685133935912cb37bda17356c3f0f0b4` 显式绑定到 `PersistentStateCompatibility.KnownCurrentReader` 与 `ReflectionWorkCompatibility` 的 `SupportsCompletedReceipt` / `SupportsRevisitReceipt` / `SupportsFinalReceipt`，并在 `install-manifest.json` 的 `previous_plugin_hashes` 中保留 `v0.4`（`ffe08b05eddca0ae3ef7c4be619b5974a472a7bf7990abff5e9b4a8d93bfc8df`）、`.106`（`2576304bf3d9fc5c1ed4db241a58b9b5b97d49efea69b4bce8266b1612a8a6a3`）及更早版本哈希。
- 老用户平滑升级修复与数据兼容保证：
  1. **严守不改写玩家记忆铁律**：`v4-b1-continuity-v2.sqlite3`、`v4-reflection-*.json`、`v4-relationship-readings.json` 与 `sessions\generated-public-v1` 100% 向后兼容，更新插件绝不静默篡改或清空既有笔记与记忆。
  2. **本地语义召回无损热升**：本版更新了双子空间几何 `semantic_worker.py`（SHA-256 `aa41f9ae6b60a553442aded87d054da026fc7f20cdc06d4b774e041726411f0c`）。已安装 `v0.4` 或 `.106` 召回环境的老用户运行本版安装器时，会自动识别既有 Python / BGE 环境并原地原子更新官方 `semantic_worker.py`（修复了旧版安装器更新 `semantic_worker.py` 时触发 `LBWIZ_EXISTING_COMPONENT_CHANGED` 及要求重留 8 GiB 空间的升级阻断）；首次启动直接复用已有 `vectors.npy` 向量缓存并原地补齐 `kind / lifecycle` 元数据，无需重跑全量编码；IPC 协议同时保持与 `v0.4` 的双向兼容。
  3. **配置与可选语音运行包隔离**：`config.json` 原有所有键、API Key、登录凭据与用户显式关闭项全部保留。本版内置本地离线语音输入（`stt.*`）与语音播报（`tts.*`）支持，默认关闭；未在 `LilithBridgeData` 部署对应 `stt-runtime` / `tts-runtime` 可选包时，游戏内菜单自动隐藏对应入口且保持零额外每帧开销，解压可选语音包后即刻激活。
  4. **正式版在线检查修复**：修复了向导“检查项目更新”此前仅匹配 `prerelease: true` 而跳过正式版 Release 的过滤逻辑。
  5. **未结反思预检与安装器自举交接（Schema 17 → 18）**：升级前请先正常退出游戏；若 `v4-reflection-window.json` 含未完成 `Work`，安装器会保守提示并停止，待旧版本跑完该轮反思后再更新。本包声明 `min_installer_schema: 18` 并在 `CANDIDATE.json` 提供 `handoff_protocol: 1` 与 `supported_config_schema: 1`，支持旧安装器安全交接至 `.24` 新安装器。
- 程序回退边界：仅替换旧 DLL 不等于完整数据回退；如需回退到更早版本，请同时使用与旧版本匹配的 `LilithBridgeData` 备份或独立数据目录。
