# 安装与首次使用网络清点

默认按中国用户无代理网络考虑，不要求代理；以下为源码/包静态事实，非本候选国内可达测试。

| 阶段 | 当前实际来源与复用 | 国内优先/内置措施 | 校验与未验 |
|---|---|---|---|
| 安装入口/自有插件/Mono.Cecil/向导脚本/语料 | 单EXE内嵌，自动释放到自有缓存 | 已内置；无需GitHub获取初始产品 | payload固定SHA、每个释放文件核合；产品e4与语料6f43不变；未运行 |
| Python3.12.10，14,515,433B | 固定NuGet URL，已内置 | 正常流程不需NuGet下载；缓存校验复用 | 原size/SHA；随包LICENSE保留 |
| BepInEx Unity.IL2CPP win-x64 be.769+32fb12f，34,325,441B | 原官方builds.bepinex.dev；归档原样 | 已内置；不更换loader版本、不猜国内镜像 | 整包SHA12f70…及227条安装文件SHA；保护已有不同文件并停止；无运行验收 |
| CoreCLR/.NET6加载器运行文件、Doorstop、Interop/Cpp2IL/Harmony等 | 同一BepInEx归档内的dotnet/BepInEx/core与原生文件 | 随loader带入，不另访NuGet | 仍用loader文件白名单；license/第三方通知范围见下 |
| 34个CPU wheel，共712,733,417B | 33个来自files.pythonhosted.org；torch来自download-r2.pytorch.org | 25个≤1MiB合计5,776,607B内置；其余PyPI文件先清华，torch先SJTU | 精确文件逐项size/SHA；手动损坏目录不能压过有效文件；既有镜像证据不冒称cn19实测 |
| pip/ensurepip | Python归档自带pip25.0.1；依赖安装no-index/no-deps/require-hashes | 无额外pip联网解析 | Python标准venv路径；未执行 |
| BGE 11项，共96,405,966B | 固定HF7999e1d…，ModelScope8399f11…内容相同 | 10小文件578,318B内置；95,827,648B权重先ModelScope，失败HF | 精确size/SHA；实际worker local_files_only、trust_remote_code=false，不在首次召回偷偷拉其他模型 |
| Unity基础库：首次IL2CPP生成 | BepInEx源码默认unity.bepinex.dev/libraries/{VERSION}.zip；同名本地ZIP优先 | **未闭合**：现无已核国内镜像或授权/锁定资源；本候选不夹带旧Unity/interop | be.769原路径不经安装器sha/host策略；1.2.1隔离样本为2021.3.45f2c1，笔记本版本和可达未验 |
| 系统.NET Framework4.8 | OS现有前提，不自动下载 | **未闭合**：缺失时当前EXE不能先自救安装Framework | 编译目标4.8；新旧Windows覆盖未验，不等同loader内dotnet |
| 系统VC++/UCRT | Python自带vcruntime140/140_1；其他原生依赖可依赖OS | **未闭合**：不引入未锁定vc_redist/静默提权；Microsoft官方可另行提供 | 仅读包目录，不声称干净系统torch必可加载；未执行native import |
| 项目更新（可选） | api.github.com与GitHub release资产 | 初装用内嵌；未部署国内更新站，保留官方 |50MiB/metadata digest/固定仓库校验；新增先明确CheckUpdate才允许切源，拒绝旧r5隐式兜底 |
| DeepSeek生成/后台 | api.deepseek.com；已有自定义endpoint由本人选择 | 国内官方原服务，不改用户现有provider或许可 | 属真实服务/账户调用，不能镜像缓存；本轮未调用 |
| ChatGPT登录/目录/生成；OpenAI API | auth.openai.com discovery/token/JWKS、api.openai.com/v1/models与responses；chatgpt.com usage | 保持官方认证，不转交镜像/代理，不保证中国直连 | 本人授权、账户/地区/额度/模型权限仍必要；本轮无真实调用 |
| Steam/游戏内容 | 用户已取得的官方游戏与Steam自身更新 | 安装器不下载或代替游戏本体，不改Steam入口 | 不纳游戏素材/生成interop；未运行 |
| 开发/维护旧CLI | runtime_setup.py的download动作仍官方URL，Optional-Recall等非普通EXE向导入口 | 普通向导先由.NET下载器镜像获取，再调用离线install；不推荐用户绕向导拼命令 | 未改为另一套下载器；不把源码维护脚本当默认用户安装链 |

## BepInEx适配和许可证据

针对x64 IL2CPP，不是BepInEx5 Mono。HostCapabilities核真实游戏元数据和插件所需接口，首启生成后检查hash关联；不会只凭版本串宣称兼容。既有1.2.1隔离记录属于旧候选，cn19未运行安装/首启/升级。

来源：[be.769固定归档](https://builds.bepinex.dev/projects/bepinex_be/769/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.769%2B32fb12f.zip)，本机仅复用此前已取得且hash一致的原始归档，233个ZIP条目，原包没有LICENSE/NOTICE成员。SHA是既有官方HTTPS字节实算，不冒称发行人签名。

[32fb12f LICENSE](https://raw.githubusercontent.com/BepInEx/BepInEx/32fb12f/LICENSE)明确LGPL2.1或以后版本，非本项目MIT。完整LICENSE和246,010B原样上游源码ZIP（SHA04924d095fb792b7ea8dcb35c2306336c6ac6c11207344e8cbbcad565cbcf34a）随候选保留；依赖含Doorstop/HarmonyX/MonoMod/Cecil/Il2CppInterop/Cpp2IL/CoreCLR，各有独立许可，原归档缺的所有第三方通知/对应源码未逐项闭合，不能将只附BepInEx源码说成完成整个归档许可审查。公开动作未执行。

Unity首启来源证据：[同一提交InteropManager](https://raw.githubusercontent.com/BepInEx/BepInEx/32fb12f/Runtimes/Unity/BepInEx.Unity.IL2CPP/Il2CppInteropManager.cs)及[官方Unity库站](https://unity.bepinex.dev/)。原loader优先复用同名ZIP，随后直接下载/解压；未在其中引入本安装器的锁定哈希。此处需要先取得准确版本/来源/许可后再由安装器自动预置，不应要求新用户手工拼路径，也不能先宣称完成。

VC++后续若需捆绑，必须锁定具体版本/签名/bytes/SHA并保留Microsoft适用条款，[官方说明](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist?view=msvc-170)；不是可随意从系统目录复制DLL。此轮未下载、运行或安装VC++。
