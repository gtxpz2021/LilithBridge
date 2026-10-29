# LilithBridge 技术附录

这里列出当前预览的支持范围、主要模块和排查入口。首次安装请先读[玩家指南](PLAYER-GUIDE.md)，了解设计则读[架构入门](../ARCHITECTURE.md)。

## 当前预览

本页对应 [preview-20261029](https://github.com/gtxpz2021/LilithBridge/releases/tag/preview-20261029)，插件标识为 `6a028692`，安装宿主是《不/存在的莉莉丝》（Steam App ID 4643090）。可运行的安装器和插件由 Release 附件提供。

| 项目 | 当前状态 |
|---|---|
| 安装环境 | Windows x64；游戏 1.0.4 与匹配依赖，环境标识 `game-1.0.4-be769-exact-r1` |
| Steam 1.1.0 及其他文件组合 | 当前安装包不支持 |
| 安装器 | 安装、配置保存、取消预览与回退已检查；配置检查使用测试内容 |
| 游戏运行 | 完整游戏启动与聊天尚未完成验证 |
| 远程 API | 不要求 Python；配置器保存设置，不发送测试请求 |
| 本地语义召回 | 附带 worker 脚本；Python、依赖环境和模型另行准备；worker 已有独立离线验证，游戏接入仍待实测 |
| 一起看 | 未启用 |

安装器按 `tools/setup/install-manifest.json` 检查目标游戏与依赖文件。若环境不匹配，请使用支持该环境的发布包，保留原有文件。

## 一次请求中的材料

检索先找出可能相关的记录；材料选择再检查来源、状态、关联上下文和剩余空间。选入的材料与当前用户发言分开，并保留说话人、来源和时间。转述按转述处理。

材料进入请求后，还会经历发送、模型生成、游戏显示和保存。排查时分别查看这些阶段：没有被选入，可能是相关性不足或空间不够；已经生成却没显示完整，则需要检查交付过程。检索分数用于挑选候选，材料使用权限由另外的规则检查。

不同模块的容量可能按字符或 token 计量，查看日志时注意单位。

## 更正、反思和原始记录

明确的更正关系会把原话与后来的澄清关联起来。例如“周六去海边”与“我更正刚才那句，周六去图书馆”，在关联建立后应作为有上下文的一组材料处理。自然语言中的更正仍需要识别与判断。

反思使用一份状态快照，模型提出整理或修订，程序再检查来源、权限、依赖与版本后保存。原始经历与历次理解分别保留。

| 字段 | 用途 |
|---|---|
| `SourceEventIds` | 可引用的事件来源 |
| `SourceDayIds` | 可引用的日来源 |
| `RevisionTargets` | 本次允许修订的目标版本 |
| `NoteHistory` | 供阅读的历史内容 |

记录状态描述的是处理进度：`active` 为尚未结算或被替代，`settled` 保留结算理由与时间，`superseded` 保留版本替代关系。角色如何看待这些内容，由模型结合具体对话判断。

## 回复显示与历史

模型原始生成、显示计划、已显示片段和实际交付文本分别记录。后续历史、经历和反思引用以实际交付情况为依据。有关中断行为的完整覆盖仍需运行验证。

## 实现入口

| 文件 | 主要职责 |
|---|---|
| `Plugin.cs`、`NativeHooks.cs`、`BridgeController*.cs` | 加载、游戏输入与界面适配、请求编排 |
| `ConfigStore.cs`、`DataPaths.cs`、`MemoryStore*.cs` | 配置、数据目录、经历与记忆存储 |
| `HistoryProjection.cs`、`ContextBuilder.cs` | 整理历史与请求材料、选择容量预算 |
| `MemoryStore.Experiences.cs`、`MemoryStore.SemanticRecall.cs` | 经历检索、关联上下文和可选语义召回 |
| `DialogueMaterialEnvelope.cs`、`UtteranceAttribution.cs` | 发言、历史材料与转述的来源归属 |
| `BridgeController.Delivery.cs` | 回复显示与实际交付记录 |
| `MemoryStore.Reflections.cs`、`MemoryStore.SourceCorrections.cs` | 反思、修订与明确更正关系 |
| `ReflectionProjectionObservation.cs` | 记录材料的选入与省略情况 |

以上文件位于 `src/LilithBridge/`，共同组成依赖游戏与加载环境的插件。安装与配置器另在 `tools/installer-source/`。

## 安装包文件核对

下列路径指 Release 安装包内的位置：

| 文件 | SHA-256 |
|---|---|
| `setup/payload/LilithBridge.dll` | `6a028692fcc06d14666c0d4af0433be3cb727be19a97ae23252d6796de3be369` |
| `setup/LilithBridge-Installer.exe` | `74fcebbc45e0cc01af672a9de773f022ff1b3fc8f690bbbfd65a1b284745b679` |
| `optional/semantic-worker/semantic_worker.py` | `6ef8145e129c4399150367df4a6f41490349203c00eab898a0948eb6725d4036` |
| `optional/semantic-worker/model-manifest.json` | `c0f597c0cd4a19a54e08b3d589633b94154ea6f76df9c23365a9ff212eb07c17` |

安装包根目录的 `Verify-Package.ps1` 可检查包内文件是否与清单一致。它用于文件完整性检查；支持范围以本页说明为准。无需为此修改系统脚本执行策略。

本地召回的文件与路径要求见[依赖说明](DEPENDENCIES.md#可选本地召回)，第三方内容范围见[许可说明](../LICENSE-STATUS.md)。

## 小词典

- **召回**：从已有记录中寻找这次可能用得上的内容。
- **语义召回**：按意思接近程度寻找内容。
- **投影**：把保存的记录整理为本次请求所需的形式。
- **预算**：本次可容纳的材料量。
- **反思**：模型依据经历提出的整理、理解或修订。
- **交付**：实际显示给用户的回复。

[回到首页](../README.md) · [架构入门](../ARCHITECTURE.md) · [安装说明](PLAYER-GUIDE.md)
