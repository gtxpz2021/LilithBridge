# 依赖与本地召回

先使用远程聊天 API，只需准备游戏加载环境和自己的聊天服务。**Python 与本地模型都是可选项。**

## 游戏与加载环境

安装目标是[《不/存在的莉莉丝》（The NOexistenceN of Lilith）](https://store.steampowered.com/app/4643090/?l=schinese)，Steam App ID 为 **4643090**，运行系统为 Windows x64。

安装器会检查游戏目录中的运行组件和所需功能，再决定能否安装。游戏版本号不是唯一依据；游戏更新或不同加载环境仍可能影响兼容性。

### 首次给原版游戏安装 MOD

1. 从[BepInEx 官方构建页](https://builds.bepinex.dev/projects/bepinex_be)准备 **BepInEx 6 的 Unity IL2CPP、Windows x64 构建**。推荐使用已验证的 **be.769+32fb12f**，文件名为 `BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.769+32fb12f.zip`；不要选成 Mono 或 x86 构建。
2. 按[官方 Unity IL2CPP 安装说明](https://docs.bepinex.dev/master/articles/user_guide/installation/unity_il2cpp.html)将加载器放入游戏目录。
3. 启动一次游戏，让加载器生成所需的 interop 文件；首次准备可能需要一些时间。
4. 正常退出游戏，再打开 LilithBridge 安装器检查并安装。

其他构建是否可用，仍需由安装器检查并在游戏中确认；并非构建越新越适合。

游戏、加载器和 interop 不在 MOD 安装包中；LilithBridge 安装器不会替你下载加载器。安装器本身需要 .NET Framework 4.8/4.8.1 运行环境。

### 已经有加载环境

**先运行 LilithBridge 安装器检查即可，不必重装已有依赖。** 如果检查提示缺少组件，按提示补齐；若现有环境不支持，先备份并查看对应发布说明，不要随意覆盖共享加载器或修改清单。

## 远程聊天 API

默认服务商是 **DeepSeek 官方**，默认模型是 **deepseek-flash**。初次配置只需填入自己的 Key，服务地址由预设填写。

模型列表也提供 **Pro（`deepseek-v4-pro`）**。选择前请查看服务商对该模型的费用和账户要求。

如需其他服务，使用自定义高级入口，准备：

- **HTTPS API 地址**：兼容 Chat Completions 的接口地址，而不是聊天网页地址。
- **模型名**：服务商文档中的模型标识。
- **API Key**：该服务账户的 Key；切换服务商时不能沿用另一家服务的 Key。

不同服务商和模型的接口行为可能有差异，仍需实际确认是否能正常配合本插件。配置器只保存设置，不发送测试请求。

API 可能按使用量收费；你的发言和本次用到的历史材料会发送到所选服务。使用前请查看价格、额度及数据处理说明。Key 和配置备份以明文保存在本机，请勿随包分享。

## 可选本地召回

本地语义召回用 `BAAI/bge-small-zh-v1.5` 按意思寻找旧经历，不生成聊天回复。它需要 Python、依赖环境、模型和包内的 worker 脚本。当前预览的游戏接入仍待实测；初次使用可以先跳过这一节。

### 1. 准备 Python 环境

可选环境工具位于 `optional-recall/`，入口是根目录的 `Optional-Recall.ps1`。工具面向普通 CPython 3.12 Windows x64，已用 3.12.10 检查；其他 Python 版本和发行方式尚未验证。仓库和安装包不包含 Python、依赖安装包或建好的环境。

先把下面两条示例路径换成自己的路径，预览准备命令：

```powershell
.\Optional-Recall.ps1 -PythonExe 'C:\YourPython\python.exe' -RuntimeRoot 'C:\YourRuntime\LilithBridge' -Action plan -PreviewCommand
```

这个命令仅显示计划。后续动作如下：

| 动作 | 用途 |
|---|---|
| `plan` | 查看准备计划 |
| `download` | 在明确给出下载预算后，下载依赖安装包 |
| `install` | 从已核验的离线安装包缓存建立环境 |
| `verify` | 检查依赖导入与脚本通信；模型运行需要另外检查 |

工具不会修改游戏配置或开启召回。无需修改全局 PATH 或系统脚本策略；如果系统不允许运行脚本，请保留策略并使用已允许的方式准备环境。

### 2. 准备模型与 worker

模型需要自行下载，安装器和 worker 不会自动下载。请按[模型取得说明](../optional/semantic-worker/MODEL-ACQUISITION.md)取得固定版本及清单中的文件，保留目录结构。

配置器需要选择三个路径：

| 配置项 | 选择什么 |
|---|---|
| Python | 自己准备的环境中的 `Scripts/python.exe` |
| worker | 安装包内的 `optional/semantic-worker/semantic_worker.py` |
| 模型 | `bge-small-zh-v1.5` 权重目录；其中应只包含模型清单要求的文件及子目录 |

保留 worker 旁边的 `model-manifest.json`。如果模型下载包有外层说明目录，请选择内层权重目录；说明和许可证留在外层，模型中的官方 `README.md` 保持原样。

只选择上表中的 worker 路径。`optional-recall/probe-fixture/` 用于工具自检，不用于游戏配置。

### 3. 保存并启用

1. 在配置器选择模型、Python 和 worker 路径。
2. 明确选择“关闭本地召回”，勾选配置确认，再保存。
3. 点击“重新读取”，然后“检查本地文件”。这一步检查文件与路径，不会运行模型。
4. 检查通过后，选择“启用本地召回（先检查文件）”，再次勾选确认并保存。

更换任一路径时，重复上述顺序。如果检查失败，保持召回关闭，按错误提示核对缺失或不匹配的文件。[worker 使用说明](../optional/semantic-worker/README.md)提供单独诊断方法。

运行时生成的索引保存在插件数据目录中。请使用自己的配置和数据，不要复制他人的记忆或索引。

[返回安装说明](PLAYER-GUIDE.md) · [许可与第三方内容](../LICENSE-STATUS.md)
