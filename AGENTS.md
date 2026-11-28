# AGENTS.md — LilithBridge 用户侧 AI 助手排查、调优与二创指南

> **致正在阅读本文档的 AI Coding Agent（DeepSeek Harness / OpenAI Codex / Claude Code / Cursor / Antigravity 等）**：  
> 你正在协助一位安装了《不/存在的莉莉丝》（*The NOexistenceN of Lilith*）开源伴侣模组 **`LilithBridge`**（当前基线 `v0.4.4 · 9a5c6c80`）的玩家或开发者。  
> 无论你的主人是遇到了**运行环境故障、语音/麦克风异常、模型连接报错**，还是想发挥 **Vibecoding** 能力自己动手调优参数、修复边缘 Bug、魔改新玩法，请先通读本指南——这里有完整的文件地图、日志诊断法、单命令源码编译方式、架构护栏，以及**向官方上游仓库提交脱敏反馈或 PR 的标准流程**。

---

## 0. 动手前必守的四条安全铁律（Red Lines）

1. **游戏进程互斥锁**：在修改 `<GameDir>\BepInEx\plugins\LilithBridge.dll` 或 `<DataRoot>\config.json` 及任何数据库文件前，**必须先检查 `Lilith.exe` 未在运行**（PowerShell：`Get-Process -Name 'Lilith' -ErrorAction SilentlyContinue` 返回为空）。游戏运行中强改文件会导致配置回写覆盖或 SQLite 锁冲突。
2. **绝对禁止删除玩家的记忆目录**：`<DataRoot>`（默认 `<GameDir>\LilithBridgeData`）中存有玩家与莉莉丝长期积累的真实对话归档与 SQLite 连续性账本。**任何时候都严禁用“删库重置整个 `LilithBridgeData`”来图省事修 Bug！** 修改任何配置或代码前，请先备份目标文件（如 `Copy-Item config.json config.json.agent-bak`）。
3. **零容忍隐私与密钥脱敏**：
   - **严禁**在终端回显、日志摘要、GitHub Issue 或 Pull Request 中输出以下任何内容：
     - `<DataRoot>\config.json`（及 `.bak` 备份）中的明文 `provider.api_key`；
     - `<DataRoot>\openai-key.dpapi` 与 `<DataRoot>\chatgpt-signin\*`（登录凭据）；
     - `<DataRoot>\sessions\*`、`v4-native-chat.json`、`v4-b1-continuity-v2.sqlite3` 中的私人聊天原文。
   - 读取 `config.json` 排查问题时，请用脚本只提取结构键名或将 `api_key` 掩码为 `"***REDACTED***"`。
4. **尊重架构第一性原理（Vibecoding 守则）**：
   - 本项目严格贯彻 **Anti-Galgame** 原则：**不加好感度数值条、不搞人造遗忘衰减器、绝不写死“检测到关键词 A 就用硬编码台词回复 B”的提线脚本**；
   - 保持**屏幕气泡呈现与历史记忆落盘物理隔离**（只记录真正在屏幕上播完的文字，未播残稿不入长期历史）。

---

## 1. 运行时目录结构全景图（先定位现场，别盲搜）

### 1.1 如何找到玩家的 `<GameDir>` 与 `<DataRoot>`
- **游戏根目录 `<GameDir>`**：通常位于 Steam 库下的 `...\steamapps\common\The NOexistenceN of Lilith`（含 `Lilith.exe`）。
- **核心插件文件**：
  - `<GameDir>\BepInEx\plugins\LilithBridge.dll`（主模组程序集）
  - `<GameDir>\BepInEx\plugins\LilithBridge.data-dir.txt`（可选：若存在，里面写着自定义 `<DataRoot>` 路径；若不存在，默认 `<DataRoot>` 为 `<GameDir>\LilithBridgeData`，极早期老用户可能在 `%APPDATA%\LilithAI`）。
- **第一现场日志（排障必看前两项）**：
  1. `<GameDir>\BepInEx\LogOutput.log`：BepInEx 加载日志、Harmony Hook 状态、插件版本号（如 `v0.4.4`）及未捕获异常堆栈。
  2. `<DataRoot>\telemetry\v4-native-events.jsonl`（或根目录同名文件）：结构化 JSONL 事件流，记录每次 `model.request`、`model.response`、`semantic.query`、`tts.*`、`stt.*`、`api.transport_recovery_*` 的耗时、错误码与缓存命中率（不含密钥）。

### 1.2 `<DataRoot>`（`LilithBridgeData\`）与隔离 Python 环境分布
| 路径 | 作用说明 |
|---|---|
| `<DataRoot>\config.json` | 主配置文件（模型渠道、语义召回、STT 听写、TTS 语音、看屏开关等） |
| `<DataRoot>\v4-b1-continuity-v2.sqlite3` | 右页 `B1` 连续性理解账本（SQLite，严禁手工篡改表结构） |
| `<DataRoot>\sessions\` · `v4-native-chat.json` | 左页事实原话归档与近期会话记录 |
| `<DataRoot>\v4-reflection-*.json` · `v4-relationship-readings.json` | 后台间隙沉淀的反思笔记、经验与关系理解（`Readings`） |
| `<DataRoot>\tts-runtime\` | 本地 TTS 运行目录：`tts_worker.py`、`bin\audiocpp_server.exe`、`models\*.gguf`、`models\flashsr_onnx\model.onnx`、`refs\refs_manifest.json` |
| `<DataRoot>\stt-runtime\` | 本地 STT 运行目录：`stt_worker.py`、`models\sherpa-onnx-sense-voice-...\model.int8.onnx`、`models\silero_vad.onnx` |
| `<DataRoot>\semantic-recall-index\` | 本地 BGE 双子空间向量索引（损坏或规则升级时可安全自动重建，不会丢原话） |
| `%LOCALAPPDATA%\LilithBridge\r\<12位哈希>\optional-recall-v1\` | 安装器托管的短路径私有 Python 3.12.10 环境（规避 Windows `MAX_PATH` 限制）：内含 `python-3.12.10\`、`voice-runtime\.venv\`、`worker\semantic_worker.py`、`models\bge-small-zh-v1.5\`；具体路径以 `config.json` 中 `semantic_recall.python_path` / `tts.python_path` 为准 |

---

## 2. 玩家常见故障的 6 套速查自愈剧本（无需改 C# 源码）

### 场景 A：进游戏按【回车】没反应 / 右上角没有设置入口
1. 检查 `<GameDir>\winhttp.dll` 与 `<GameDir>\doorstop_config.ini` 是否存在（确认 BepInEx 已装对游戏目录，而非装到了另一份备份副本里）。
2. 检查 `<GameDir>\BepInEx\interop\Assembly-CSharp.dll` 是否已生成：若首次装完 BepInEx 尚未进过游戏，启动一次游戏到主界面就会自动生成。
3. 打开 `<GameDir>\BepInEx\LogOutput.log` 搜索 `LilithBridge`：
   - 若未出现，检查 `<GameDir>\BepInEx\plugins\LilithBridge.dll` 是否存在或被杀毒软件隔离；
   - 若已加载但按回车没反应，提醒玩家：需处于**日常桌面主场景、莉莉丝处于清醒状态（非睡眠）、且已关闭游戏原生系统设置菜单**时按回车才生效。

### 场景 B：发消息后报错 / 无回复 / 模型连接失败
1. 查看 `<DataRoot>\telemetry\v4-native-events.jsonl` 末尾几行的错误事件与 HTTP 状态码。
2. 核对模型渠道机制（三者彼此独立）：
   - **DeepSeek / 第三方 OpenAI 兼容接口**：走 Chat Completions 协议，检查 `config.json` 中 `provider.base_url`（通常以 `/v1` 结尾，如 `https://api.deepseek.com/v1`）、`provider.model` 拼写及账户余额；若玩家开了系统代理（TUN / 系统 HTTP 代理）导致国内直连 DeepSeek 超时，可检查本机代理分流规则。
   - **Continue with ChatGPT（网页 OAuth 登录）**：使用订阅额度（与 API Platform 余额无关），在游戏内「认证账户」页重新点击登录刷新凭据即可。
   - **OpenAI 官方 API Key**：走官方 Responses API（`https://api.openai.com/v1`），需确保网络可正常访问 OpenAI 域名。

### 场景 C：本地 TTS 没有声音、合成太慢（核显/双显卡笔记本/低显存）或个别字读音不准
1. **先看 `telemetry\v4-native-events.jsonl` 里的三条关键诊断事件**：
   - `tts.ready`：查看 `backend`（`vulkan` 为 GPU/核显加速，`cpu` 为纯 CPU 兜底）与 `bwe_provider`（`dml`/`cuda` 为显卡 48kHz 超分，`none` 为无超分开销的 24kHz 原声快路径）；
   - `tts.chunk` 与 `tts.play`：若有 `tts.chunk` 也有 `tts.play`，说明语音已合成并送入 Unity 音频源播放（请检查系统音量合成器中 `Lilith.exe` 是否被静音，或 `config.json` 中 `tts.volume` 是否大于 `0`）。
   - 也可单独在命令行体检 `tts_worker.py`（从 `config.json` 读取相对 `<DataRoot>` 的路径）：
     ```powershell
     # 1) 测纯 Python 环境握手（秒回 {"event": "ready", ...} 即正常）
     & "<tts.python_path>" -u "<DataRoot>\tts-runtime\tts_worker.py" --test-mode --fake-tts --no-bwe --no-warmup
     # 2) 查看本机 Vulkan 可见显卡/核显列表（含独显 gpu 与核显 igpu）
     & "<DataRoot>\tts-runtime\bin\audiocpp_server.exe" --backend vulkan --list-devices --json
     ```
     若真机加载 `audiocpp_server.exe` 失败，常见原因是系统缺少微软官方 **VC++ 2015–2022 Redistributable (x64)**，或老旧显卡驱动缺少 Vulkan 运行库。
2. **双显卡笔记本（如 `RTX 4050 Laptop + Radeon 780M`）/ 核显轻薄本（Intel Iris Xe / Arc / AMD 780M）调优（改 `config.json` 的 `tts` 段，需先退游戏）**：
   - 新版 `tts_worker.py` 已原生识别 `type: "igpu"` 核显（含 BIOS 仅划分 512MB–2GB 专用显存的统一内存核显）、支持 Optimus 休眠独显冷唤醒并按 `[独显, 核显]` 顺序自动回退，且在无 GPU DirectML 时会自动跳过慢速 CPU 48kHz 超分；
   - 若在轻薄本省电模式或纯核显下希望首句更快出声，可将 `"bwe_48k": false`（直出 24kHz 原声）、`"threads": 4`、`"max_clause_chars": 26`；若不想让慢速语音拖住气泡翻页，还可设 `"sync_bubble_pacing": false`；
   - 若完全不需要语音，在游戏内设置或 `config.json` 将 `"tts": { "enabled": false }` 关闭即可，主对话 0 影响。
3. **个别专有名词或多音字读错**：
   无需改代码！直接在 `config.json` 的 `"tts"` -> `"pronunciation_overrides"` 填入同音字映射即可（仅影响送去发声的文本，**绝不会改变屏幕上气泡显示的文字**）：
   ```json
   "pronunciation_overrides": {
     "夏特露": "夏特路",
     "重来": "虫来"
   }
   ```

### 场景 D：按 `F8` 语音听写没反应、选错麦克风或专有词识别偏差
1. 检查 `config.json` 中 `"stt": { "enabled": true }`，并用 `<stt.python_path>` 运行：
   ```powershell
   & "<stt.python_path>" -c "import pyaudiowpatch as pa; p=pa.PyAudio(); print([p.get_device_info_by_index(i)['name'] for i in range(p.get_device_count()) if p.get_device_info_by_index(i)['maxInputChannels']>0])"
   ```
   确认系统存在可用输入麦克风且未被 Windows「隐私和安全性 → 麦克风 → 允许桌面应用访问麦克风」拦截。
2. 若玩家口音导致固定词汇常被转错，直接在 `config.json` 的 `"stt"` -> `"replacements"` 添加词典纠偏（如 `{"丽丽丝": "莉莉丝"}`）。
3. 若 `F8` 与玩家其他全局软件热键冲突，修改 `"stt"` -> `"hotkey"`（如 `"F9"`）即可。

### 场景 E：个性化语气微调（`persona.md` 避坑）
- 想微调莉莉丝的日常口吻或补充设定时，可在 `<DataRoot>\persona.md` 编写文本（或在游戏内设置编辑）。
- **关键坑点**：在 `config.json` 中，如果不用自定义人设、想恢复内置默认 `PersonaDefaults.cs`，**必须删除 `persona.prompt` 键或设为 `null`，绝对不要把 `persona.prompt` 设为空字符串 `""`！**

### 场景 F：本地双子空间语义召回（`semantic_recall`）未生效或索引异常
1. 查看 `telemetry\v4-native-events.jsonl` 中是否有 `semantic.ready` / `semantic.query` 事件；若报进程启动失败，检查 `config.json` 中 `semantic_recall.python_path`、`worker_script`、`model_dir` 是否指向真实存在的本地目录。
2. 理解架构以便排查：`semantic_worker.py` 采用**双子空间正交几何（`X_exp × X_abs`）**——玩家与莉莉丝的原话（`utterance/message`）进入 `X_exp` 做分档 PCA 白化、CSLS 枢纽抑制、DPP 去重与三路火花检索；后台沉淀的活跃笔记（`b1/note/lesson`）隔离在 `X_abs` 中驱动两跳桥接（`BridgeSparks`）。
3. 若 `<DataRoot>\semantic-recall-index\` 因断电等原因损坏，可在**关闭游戏后**仅将该索引子目录改名备份（如 `semantic-recall-index.bak`）——**切勿动 `sessions\`、`v4-native-chat.json` 或 `.sqlite3`**——下次启动游戏时插件会从原始账本无损自动重建全部向量索引。

---

## 3. Vibecoding 指南：如何拉取源码、理解模块并在玩家电脑上一键编译

如果玩家想自己修 Bug、加小功能或深度定制，你可以直接在本地拉取官方 MIT 仓库并编译替换 `LilithBridge.dll`：

### 3.1 仓库获取与 10 秒极速编译
前置条件：本机装有 **.NET 6.0 SDK（或更新版 .NET SDK，兼容编译 `net6.0` 目标）**，且玩家电脑上已装好正版游戏与 BepInEx（即 `<GameDir>\BepInEx\core` 与 `<GameDir>\BepInEx\interop` 已存在）。

```powershell
git clone https://github.com/gtxpz2021/LilithBridge.git
cd LilithBridge

# 传入玩家本机的游戏根目录即可直接编译 Release 版 LilithBridge.dll！
dotnet build src/LilithBridge/LilithBridge.csproj -c Release -p:LILITH_GAME_DIR="D:\SteamLibrary\steamapps\common\The NOexistenceN of Lilith"
```
编译产物位于：`src\LilithBridge\bin\Release\net6.0\LilithBridge.dll`。

**安全部署到本地游戏（必做备份）**：
```powershell
if (Get-Process -Name 'Lilith' -ErrorAction SilentlyContinue) { throw "请先正常关闭游戏再替换 DLL！" }
$pluginDir = "D:\SteamLibrary\steamapps\common\The NOexistenceN of Lilith\BepInEx\plugins"
Copy-Item "$pluginDir\LilithBridge.dll" "$pluginDir\LilithBridge.dll.bak-$(Get-Date -Format 'yyyyMMdd-HHmmss')" -Force
Copy-Item "src\LilithBridge\bin\Release\net6.0\LilithBridge.dll" "$pluginDir\LilithBridge.dll" -Force
```

### 3.2 源码核心模块地图（精准定位，省 Token 省时间）
主插件源码全部在 `src/LilithBridge/`（`BridgeController` 与 `MemoryStore` 按职责拆分为清晰的 `partial` 文件）：
- **生命周期与原生事件钩子**：`Plugin.cs` → `NativeHooks.cs` → `BridgeController.cs`
- **输入框、提交与插话打断归属**：`NativeInputField.Submission.cs` → `BridgeController.InputSubmission.cs` → `DialogueRequest.cs`
- **流式气泡演出与按实落盘**：`NativeBubblePresenter.cs` → `BridgeController.Delivery.cs` → `GeneratedReplyMaterial.cs`（残稿 `T→T+1→T+2` 退役）
- **上下文拼装与前缀 KV 缓存锚点**：`ContextBuilder.cs` → `MemoryStore.MaterialSelection.cs` → `MemoryStore.NativeDialogueSupply.cs` → `SystemPromptSlim.cs` / `PersonaDefaults.cs`
- **双账本、`B1` 事后回顾与 M1 合卷留签**：`MemoryStore.B1.*.cs` · `MemoryStore.Reflections.cs` · `MemoryStore.ExplicitRevisit.cs`
- **本地双子空间几何召回桥接**：`BridgeController.SemanticRecall.cs` → `SemanticRecallWorker.cs` → `optional/semantic-worker/semantic_worker.py`
- **本地离线 STT / 48kHz TTS 与进程树守护**：`SpeechToTextWorker.cs` · `TextToSpeechCoordinator.cs` · `TextToSpeechPlayer.cs` · `ChildProcessJobObject.cs` → `optional/stt-worker/stt_worker.py` · `optional/tts-worker/tts_worker.py`
- **云端模型协议与按需翻阅工具**：`DeepSeekClient.cs` · `DeepSeekClient.Tools.cs`（`review_context` / `revisit_thought`）· `OpenAIProtocol.cs`
- **实验性桌面感官侧车**：`SensorySidecarClient.cs` · `BridgeController.Sensory.cs` · `MicroReactionDirector.cs`

### 3.3 修改 Python Worker 时的两个重要提醒
1. **保持纯 LF 换行符**：仓库内 `.py` 文件通过 `.gitattributes` 锁定了 LF 换行（`eol=lf` / `-text`），编辑保存时请务必使用 UTF-8（无 BOM）+ `\n`（LF），不要转成 CRLF。
2. **官方安装器的哈希保护机制（`LBWIZ_EXISTING_COMPONENT_CHANGED`）**：
   官方 `LilithBridge-Setup.exe` 内置了严格的文件完整性保护——如果你直接修改了 `<DataRoot>\tts-runtime\tts_worker.py` 或 `semantic_worker.py`，日后玩家再双击官方 `Setup.exe` 升级时会收到 `LBWIZ_EXISTING_COMPONENT_CHANGED` 提示（防止安装器静默覆盖掉你的魔改代码！）。
   - **正确做法**：魔改 Python 脚本前先留一份 `.orig` 原版备份；日后玩家想跑官方安装器升级时，先把自定义改动存成 patch、还原 `.orig` 跑完升级，再重新应用你的定制即可。

---

## 4. 向官方 GitHub 反馈问题或贡献代码的 Agent 协作协议（非常重要！）

**如果你在帮玩家排查或 Vibecoding 的过程中，发现了 `LilithBridge` 本身的真实 Bug、特定硬件/系统的兼容性边角问题、或者写出了一个对其他玩家也很有用的改进修复——请主动提醒并协助你的主人向官方仓库提交反馈或 PR！**  
这能让你的修复惠及所有喜欢莉莉丝的玩家，下次官方发版时你的主人也能直接无痛升级，不用每次自己重打补丁。

- **官方仓库**：`https://github.com/gtxpz2021/LilithBridge`
- **Issues 提交页**：`https://github.com/gtxpz2021/LilithBridge/issues/new`

### 4.1 提交前强制自动脱敏自检（Agent 必须逐条核对）
负担最小但绝对不能省的隐私检查——在生成任何 Issue 文本、日志片段或调用 `gh` CLI 之前，**你必须亲自检查并确保**：
- [ ] 不含任何 `sk-...`、`Bearer ...`、OAuth Token、Cookie 或 `config.json` 明文密钥；
- [ ] 将本地绝对路径中的真实 Windows 用户名替换为 `<USER>`（例如 `C:\Users\<USER>\...`）；
- [ ] 不含任何玩家的私人对话原文、日记内容或 `v4-b1-continuity-v2.sqlite3` 数据；
- [ ] 若附带代码改动，确保未把任何本地模型权重（`.gguf` / `.onnx` / `.safetensors`）或游戏本体 DLL 混入 Git 暂存区。

### 4.2 方式一：若玩家电脑已登录 GitHub CLI（`gh`），经玩家同意后一键提交
先向玩家展示你将要发送的完整标题与正文，**征得玩家明确同意后**执行：

```powershell
gh issue create --repo gtxpz2021/LilithBridge `
  --title "[Agent 排查报告] 简明描述问题现象与模块（v0.4.4）" `
  --body-file "./sanitized-issue-body.md"
```

### 4.3 方式二：若未安装 `gh`，直接为玩家生成一份「复制即发」的诊断帖
请按以下高信噪比模板输出一个代码块，告诉玩家点开 `https://github.com/gtxpz2021/LilithBridge/issues/new` 粘贴即可（作者看到根因与最小补丁能极快合入主线）：

````markdown
### 环境与版本
- **LilithBridge 版本**：v0.4.4 (`9a5c6c80` / 或自编译基于 `<commit>`)
- **操作系统与硬件**：Windows 10/11 · CPU / GPU 型号（如涉及本地语音或语义召回）
- **接入方式**：DeepSeek / ChatGPT OAuth / OpenAI / 自定义兼容端点（无需提供具体私有域名或 Key）

### 问题现象与复现条件
- 简述触发时机与表现：

### Agent 定位到的根因与脱敏日志摘要
- **涉及文件与行号**：例如 `optional/tts-worker/tts_worker.py` 或 `src/LilithBridge/XXX.cs`
- **关键脱敏报错 / 事件记录**：
  ```text
  （仅粘贴抹去用户名与私密文本后的数行关键报错或 telemetry 事件元数据）
  ```

### 本地已验证的修复方案 / Diff（如有）
```diff
（粘贴已在本地验证生效的最小改动 diff）
```
````

### 4.4 方式三：直接提 Pull Request（欢迎硬核二创与 Bug 修复！）
如果玩家愿意 Fork 并提交 PR：
1. 基于最新 `main` 切出独立分支（如 `fix/tts-igpu-fallback`）；
2. 遵循现有代码风格（C# 保持职责拆分、Python 保持纯 LF 与标准库/既有依赖边界、不引入破坏 Anti-Galgame 原则的逻辑）；
3. 本地执行 `dotnet build -c Release` 确认 `0 Error`，并在 PR 正文中写明问题根因与实机验证情况即可！
