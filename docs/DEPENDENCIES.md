# 依赖与本地召回说明

先使用远程聊天 API，只需要匹配的游戏环境和自己的 API 服务；不需要 Python 或本地模型。只有决定启用本地语义召回时，才需要后半部分的可选环境。

## 游戏与安装器

本 MOD 的运行宿主是 **《不/存在的莉莉丝》（The NOexistenceN of Lilith）**。[Steam 官方商店](https://store.steampowered.com/app/4643090/?l=schinese)提供正版购买与下载入口，App ID 为 4643090。《不/存在的你，和我》是另一款作品，不是安装前置。

当前公开预览仅接受 Windows x64、**游戏 1.0.4 的已核验配套文件组合**。Steam 1.1.0 和其他不匹配版本不在安装支持范围内；静态对照不能当作适配或运行验证。商店下载的当前版本未必是所需组合，不能承诺购买后就能安装本预览。

| 需要准备 | 当前要求 |
|---|---|
| 游戏本体 | 自备合法的《不/存在的莉莉丝》，实际文件必须匹配 |
| BepInEx / interop | 自备下述固定组合，不随 MOD 包分发 |
| 安装器运行条件 | .NET Framework 4.8/4.8.1 |
| 远程聊天 API | 自己选择服务，准备地址、模型名和 Key |
| Python / BGE 模型 | **仅本地语义召回需要**，API-only 路线不需要 |

安装器逐项核对安装包 `setup/install-manifest.json` 中的 32 个文件（仓库对应 `tools/setup/install-manifest.json`）。仅仅显示同一个版本号还不够，实际文件摘要也必须匹配。

本包不含游戏、加载器或 interop。BepInEx 的取得入口是[官方构建页](https://builds.bepinex.dev/projects/bepinex_be)，对应档案名为 `BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.769+32fb12f.zip`；安装方法见[官方 Unity IL2CPP 指南](https://docs.bepinex.dev/master/articles/user_guide/installation/unity_il2cpp.html)。不要任意改用较新构建来代替当前组合。官方指南包含解压到游戏根目录、首次启动以生成所需文件等步骤；它说明加载器怎样准备，不保证生成结果一定符合本 MOD 的固定指纹。首次游戏启动由用户自行决定，本安装包不会执行。

所需组合包括 BepInEx 6.0.0-be.769、Il2CppInterop 1.5.1-ci.829 和 HarmonyX 2.10.2，仍以完整文件检查结果为准。官方入口不等于任何下载文件都已与本包指纹对应；缺少或不匹配时应先停下来核对。安装目标指纹在仓库 `tools/setup/install-manifest.json` 中；许可范围另见[许可说明](../LICENSE-STATUS.md)。

## 远程聊天 API

API 地址、模型名称和 Key 由你选择并在配置器主动保存。配置器不会联网替你试调用；实际使用前请自行了解该服务的费用与数据处理方式。Key 和备份是本机明文文件，不要随包分享。

## 可选的 Python 环境

`optional-recall/` 包含环境准备、依赖锁和自检工具，不包含 Python、wheel 或已建好的 venv。当前工具面向普通 CPython 3.12 Windows x64，已有验证使用 3.12.10；这不是对所有补丁版本或其他 Python 发行方式的保证。

根目录 `Optional-Recall.ps1` 是工具入口，需要明确传入自己的 `python.exe` 和新环境目录。默认动作 `plan` 用来查看计划；下载需要显式选择 `download` 并给出下载预算，`install` 从已核验的离线 wheel 缓存安装，`verify` 做导入与合成协议检查。工具不会改游戏配置或开启召回。

例如，下面只预览会使用的命令，不执行环境准备；请把示例路径换成自己的实际路径：

```powershell
.\Optional-Recall.ps1 -PythonExe 'C:\YourPython\python.exe' -RuntimeRoot 'C:\YourRuntime\LilithBridge' -Action plan -PreviewCommand
```

不需要为此修改全局 PATH 或脚本执行策略。若系统策略不允许运行脚本，应保留策略并使用已允许的操作方式，不要绕过它。准备环境时，按工具给出的明确计划逐步操作，不能把 `verify` 的合成协议检查当成真实 BGE 模型推理。

## 生产 worker 与模型

可以供游戏配置选择的生产文件在 `optional/semantic-worker/`：

- `semantic_worker.py` 是实际 worker；
- 同目录 `model-manifest.json` 是它使用的固定模型文件清单；
- [worker 使用说明](../optional/semantic-worker/README.md)说明手动诊断与路径约定；
- [模型取得说明](../optional/semantic-worker/MODEL-ACQUISITION.md)给出官方固定 revision 和所需文件。

模型为 BAAI/bge-small-zh-v1.5，固定 revision `7999e1d3359715c523056ef9478215996d62a620`，需要清单中的 11 个文件。当前 MOD 安装 ZIP 不含模型权重，worker 也不会自动下载缺失文件。现在可按上面的官方固定 revision 手动取得；不要把未发布的单独模型包当成已有下载项。

取得后保留子目录结构并核对清单。配置器选择的模型目录须严格对应清单中的 11 个文件，不能混入额外文件或目录。如果模型压缩包外层包含说明、许可证和清单，内层是 `bge-small-zh-v1.5/`，请选择**内层权重目录**，而不是整个解压目录。外层说明和许可证留在原位，不要复制进内层。

`optional-recall/probe-fixture/` 是自检夹具，里面的同名 `semantic_worker.py` **不能选入游戏配置**。生产 worker 已经过独立离线验证，但这份插件的完整游戏接入仍待实测；安装器不会自动部署或启用它。

准备好 Python、生产 worker 和模型后，在配置器选择路径，并明确选择“关闭本地召回”，勾选确认后保存；再重新读取、检查本地文件，选择启用，再次勾选确认并保存。worker 的索引放在产品自己的数据根内，不放进模型或 worker 目录；不要复制别人的索引、记忆或配置。

[返回安装说明](PLAYER-GUIDE.md) · [许可状态](../LICENSE-STATUS.md)
