# 依赖与本地召回说明

先使用远程聊天 API，只需要匹配的游戏环境和自己的 API 服务；不需要 Python 或本地模型。只有决定启用本地语义召回时，才需要后半部分的可选环境。

## 游戏与安装器

当前候选用于 Windows x64、游戏 1.0.4 及已验证的配套文件组合。安装器依赖 .NET Framework 4.8/4.8.1 运行条件，并逐项核对 安装包 `setup/install-manifest.json` 中的 32 个文件（仓库对应 `tools/setup/install-manifest.json`）。仅仅显示同一个版本号还不够，实际文件摘要也必须匹配。

本包不含游戏、加载器或 interop。BepInEx 的取得入口是[官方构建页](https://builds.bepinex.dev/projects/bepinex_be)，对应档案名为 `BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.769+32fb12f.zip`；安装方法见[官方 Unity IL2CPP 指南](https://docs.bepinex.dev/master/articles/user_guide/installation/unity_il2cpp.html)。不要任意改用较新构建来代替当前组合。官方流程涉及的首次游戏启动由用户自行决定，本安装包不会执行。

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

模型为 BAAI/bge-small-zh-v1.5，固定 revision `7999e1d3359715c523056ef9478215996d62a620`，需要清单中的 11 个文件。模型权重不在包里，worker 也不会自动下载缺失文件。取得后保留子目录结构并核对清单。

`optional-recall/probe-fixture/` 是自检夹具，里面的同名 `semantic_worker.py` **不能选入游戏配置**。生产 worker 已经过独立离线验证，但这份插件的完整游戏接入仍待实测；安装器不会自动部署或启用它。

在配置器中保持召回关闭，准备好 Python、生产 worker 和模型，再选择路径并保存、重新读取检查、主动启用。worker 的索引放在产品自己的数据根内，不放进模型或 worker 目录；不要复制别人的索引、记忆或配置。

[返回安装说明](PLAYER-GUIDE.md) · [许可状态](../LICENSE-STATUS.md)
