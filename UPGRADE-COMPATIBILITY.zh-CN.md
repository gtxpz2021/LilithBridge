# 安装、升级与回退兼容性说明（v0.4 / .106 → v0.4.1）

- 产品目标产物 `LilithBridge.dll` SHA-256：`310561f95e5b7fdb916e03db90157635214253fd148816d5db7e9b41b1a817ea`（版本 `0.4.1` / 发布标签 `v0.4.1`）。
- 本版安装器（`0.1.4.25-v0.4.1.release.2`，`installer_schema: 18`）已将 `310561f95e5b7fdb916e03db90157635214253fd148816d5db7e9b41b1a817ea` 显式绑定到 `PersistentStateCompatibility.KnownCurrentReader` 与 `ReflectionWorkCompatibility` 的 `SupportsCompletedReceipt` / `SupportsRevisitReceipt` / `SupportsFinalReceipt`，并在 `install-manifest.json` 的 `previous_plugin_hashes` 中保留 `v0.4`（`ffe08b05eddca0ae3ef7c4be619b5974a472a7bf7990abff5e9b4a8d93bfc8df`）、`.106`（`2576304bf3d9fc5c1ed4db241a58b9b5b97d49efea69b4bce8266b1612a8a6a3`）及更早版本哈希。
- 老用户平滑升级修复与数据兼容保证：
  1. **严守不改写玩家记忆铁律**：`v4-b1-continuity-v2.sqlite3`、`v4-reflection-*.json`、`v4-relationship-readings.json` 与 `sessions\generated-public-v1` 100% 向后兼容，更新插件绝不静默篡改或清空既有笔记与记忆。
  2. **本地语义召回无损热升**：本版更新了 `X_exp × X_abs` 双子空间几何与 `SemanticWireCodec` 版 [`semantic_worker.py`](optional/semantic-worker/semantic_worker.py)（SHA-256 `aa41f9ae6b60a553442aded87d054da026fc7f20cdc06d4b774e041726411f0c`）。已安装 `v0.4` 或 `.106` 召回环境的老用户运行本版安装器时，会自动识别既有 Python / BGE 环境并原地原子更新官方 `semantic_worker.py`（不再触发旧版文件变更阻断或额外空间拦截）；首次启动直接复用已有 `vectors.npy` 向量缓存并原地补齐 `kind / lifecycle` 元数据，无需重跑全量编码，IPC 保持向下兼容。
  3. **分卷语音包直装与 Worker 原地热更新**：支持自动识别同目录 `LilithBridge-VoicePack-v1.zip.001/.002` 分卷整合包并通过 `ConcatenatedReadStream` 免解压流式直装；对已部署语音环境的老用户，向导会原地更新 EOS 防跑飞修复版 [`tts_worker.py`](optional/tts-worker/tts_worker.py)（SHA-256 `43922a80eec1658bd1cd3eed6261f93763ca339f7c11801110a94a9c7f261ec4`）与 `stt_worker.py`，`config.json` 原有密钥、登录态与开关全部保留；未部署 `stt-runtime` / `tts-runtime` 时游戏内隐藏对应入口，零额外开销。
  4. **正式版在线检查与宿主音频探测修复**：修复了向导「检查项目更新」仅匹配 `prerelease: true` 的过滤逻辑，并在 `HostCapabilities.cs` 中显式补充 `UnityEngine.AudioModule.dll` 成员检查。
  5. **未结反思预检与安装器自举交接（Schema 17 → 18）**：升级前请先正常退出游戏；若 `v4-reflection-window.json` 含未完成 `Work`，安装器会保守提示并停止，待旧版本跑完该轮反思后再更新。本包声明 `min_installer_schema: 18` 并在 `CANDIDATE.json` 提供 `handoff_protocol: 1` 与 `supported_config_schema: 1`，支持旧安装器安全交接至 `.25` 新安装器。
- 程序回退边界：仅替换旧 DLL 不等于完整数据回退；如需回退到更早版本，请同时使用与旧版本匹配的 `LilithBridgeData` 备份或独立数据目录。
