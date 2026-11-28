# 安装、升级与回退兼容性说明（v0.4.3 / v0.4.2 / v0.4.1 / v0.4 / .106 → v0.4.4）

- 产品目标产物 `LilithBridge.dll` SHA-256：`9a5c6c8056dcdfafe807824dbb77dcd233a1291ad968e26b213070d406cff674`（版本 `0.4.4` / 发布标签 `v0.4.4`）。
- 本版安装器（`0.1.4.28-v0.4.4.release.1`，`installer_schema: 18`）已将 `9a5c6c8056dcdfafe807824dbb77dcd233a1291ad968e26b213070d406cff674` 显式绑定到 `PersistentStateCompatibility.KnownCurrentReader` 与 `ReflectionWorkCompatibility` 的 `SupportsCompletedReceipt` / `SupportsRevisitReceipt` / `SupportsFinalReceipt`，并在 `install-manifest.json` 的 `previous_plugin_hashes` 中完整保留 `v0.4.3`（`29dc4d8ac8efc8337480ae409bb5ed66e39300decffb6c5e6e9be4ff221f8cdd`）、`v0.4.2`（`1be326698ff78dfd8a3574f76c87aa81a5f025ac78a0b0a8851a518dec72f6c6`）、`v0.4.1`（`310561f95e5b7fdb916e03db90157635214253fd148816d5db7e9b41b1a817ea`）、`v0.4`（`ffe08b05eddca0ae3ef7c4be619b5974a472a7bf7990abff5e9b4a8d93bfc8df`）、`.106`（`2576304bf3d9fc5c1ed4db241a58b9b5b97d49efea69b4bce8266b1612a8a6a3`）及更早版本哈希。
- 老用户平滑升级修复与数据兼容保证：
  1. **严守不改写玩家记忆铁律**：`v4-b1-continuity-v2.sqlite3`、`v4-reflection-*.json`、`v4-relationship-readings.json` 与 `sessions\generated-public-v1` 100% 向后兼容，更新插件绝不静默篡改或清空既有笔记与记忆。
  2. **已装 `v0.4.1+` 语音包免重下直升**：已安装本地语音环境的老玩家无需重下 2.9 GB `VoicePack` 分卷，直接运行单个 `LilithBridge-Setup-0.4.4-cn28-9a5c6c80.exe` 即可自动识别既有语音与召回环境，并原地原子热更核显/双显兼容版 [`tts_worker.py`](optional/tts-worker/tts_worker.py)（SHA-256 `7b0701defadd8a1b474dedccb8bda95fd50c35db0dfceab58d55d85d19af241f`）与 `0.4.4` 插件，`config.json` 原有密钥、登录态与开关全部保留。
  3. **分卷语音包直装兼容**：首次安装语音时，`LilithBridge-VoicePack-v1.zip.001/.002` 与 `v0.4.4` 安装器完全通用，向导在流式解压模型后会自动覆盖部署包内最新 `tts_worker.py` 与 `stt_worker.py`。
  4. **未结反思预检与安装器自举交接（Schema 18）**：升级前请先正常退出游戏；若 `v4-reflection-window.json` 含未完成 `Work`，安装器会保守提示并停止，待旧版本跑完该轮反思后再更新。
- 程序回退边界：仅替换旧 DLL 不等于完整数据回退；如需回退到更早版本，请同时使用与旧版本匹配的 `LilithBridgeData` 备份或独立数据目录。
