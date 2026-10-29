# 依赖与本地召回

正常安装使用一体化向导，不需要手动运行环境脚本或填写 Python、worker、模型路径。[最短步骤](QUICK-START.md)与[向导说明](docs/INSTALLER-WIZARD.md)解释当前状态和下一步。

## 游戏与加载环境

游戏由用户通过合法渠道取得。加载器固定 BepInEx 6 be.769 Unity IL2CPP Windows x64，可由向导从[官方构建页](https://builds.bepinex.dev/projects/bepinex_be)下载。首次启动由游戏加载器生成所需运行文件，也可能获取 Unity 基础库；向导不会替用户启动游戏。已有不同加载器文件保留并停止，不强行覆盖。

## 远程聊天 API

默认 DeepSeek 官方与 deepseek-flash，Pro 可选。地址由预设填写，Key 由用户亲自输入。自定义高级入口要求 HTTPS、模型 ID 和该服务自己的 Key，需兼容本项目请求格式。保存不调用对话 API；实际使用费用与数据处理规则以服务商为准。

## 可选本地召回

勾选本地召回后，向导准备官方 NuGet Python 3.12.10、普通隔离 venv、34 个固定 CPU wheel 与 BGE-small-zh-v1.5 固定 revision 的 11 个文件。模型负责按意思找旧经历，不生成聊天回复。完整游戏内接入仍待实测。

Python 包来自 [CPython 官方 NuGet 发行](https://www.nuget.org/packages/python/3.12.10)，模型来自 [BAAI 固定 revision](https://huggingface.co/BAAI/bge-small-zh-v1.5/tree/7999e1d3359715c523056ef9478215996d62a620)。这是已验证基线，不代表最新 Python 安全补丁推荐。安装位置采用本机产品专用短目录，避免要求修改 Windows 路径长度策略。不会注册 Python 或改 PATH。

## 离线包

可在高级区域一次选择离线包根目录。官方 Python nupkg、BepInEx ZIP 和可选的官方 LilithBridge 安装 ZIP 放在根目录；34 个 wheel 放在 wheelhouse/；模型下载文件按向导缓存命名放在 model/。默认联网安装无需用户整理这些目录。已配置且健康的组件会复用。

## 工具与系统要求

向导使用 Windows .NET Framework 4.8，随附 Mono.Cecil 的许可见第三方说明。CPU 环境可能依赖系统已有的 VC 运行库；全新 Windows 的缺失情形尚未验收，失败时不自动改管理员或安全设置。

根目录 Optional-Recall.ps1 和 optional-recall/runtime_setup.py 保留作高级离线排障入口，不是正常安装的必经步骤。生产 worker 在 optional/semantic-worker/，probe-fixture 只用于环境测试，绝不能配置成游戏 worker。
