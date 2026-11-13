# cn19接续说明

已合入召回独立窗口修正：MODEL_LOAD提示涵盖核验/导入，最终文件核对进度90，避免到配置95时倒退。本轮仍未运行验证或测量耗时。以下为原静态定位。

# cn16/cn17 等待流程的静态定位

依据cn16/cn17源码，不读取正在安装的笔记本状态、不执行安装或模拟验证。用户仅反馈“召回环境和模型检查很久”，缺精确状态行、时间和本人选择的日志，不能据此认定下载、哈希、Torch导入或模型初始化中的哪一个是实际瓶颈。

| 环节 | 原代码实际工作 | 重复与反馈情况 | cn18处理 |
|---|---|---|---|
| 预览与开始前重核 | Wizard.Planning.Preview逐一FindResource，存在的资源完整SHA；现有模型11项Verify | RefreshPlan及开始前会各重核；可重复读619MB torch等，原来仅笼统状态 | 显示当前文件/序号和本地SHA阶段；保留每次边界检查，不引入跨操作信任缓存 |
| 运行环境/模型现有路径 | ValidateRuntimeFiles只核路径/worker-manifest；ModelValidator核11文件清单/SHA | 此处不等于实际Python导入验证；ModelValidator有同实例指纹缓存 | 模型逐项提示；原文件检查/缓存/最终指纹比较保持 |
| 私有Python | NuGet档案size/SHA，首次展开；复用解释器时逐成员核验 | 磁盘/文件数可能等待，不能当成网络 | 加明确本地解释器阶段，不跳校验 |
| wheel准备 | 缓存查找/完整SHA、必要复制与写后SHA；随后helper和pip仍核锁/hash | 大wheel可在独立边界重复读取；并非所有重复都可删除 | 显示本地缓存SHA及逐文件序号，保留锁与完整性要求 |
| 创建环境与pip | runtime_setup创建venv/ensurepip，pip --no-index --no-deps --require-hashes离线安装；pip check | pip子输出由subprocess.run捕获，通常到结束才写专用日志；pip-install默认timeout900s，pip-check60s | 先报阶段；顶层Python -u；.NET每10秒诚实等待心跳，不伪称pip内部逐包进度 |
| 运行库/合成协议 | verify_runtime导入12类库（含Torch/SentenceTransformer）并检查CPU/依赖版本/隔离与合成协议；timeout180s；内部worker检查90s | 这是一段本地CPU/磁盘工作，不是拉真实BGE权重；各子进程原来缺UI心跳 | 显示运行库与协议阶段；检查/超时均保留 |
| BGE准备 | 11项96,405,966B；新装95,827,648B权重可能仍下载 | 安装器、最终Python、生产worker分别核模型，属于不同执行边界 | 保留全部哈希；下载与本地SHA/模型加载文案分开 |
| 最终真实worker验证 | verify_local_recall先版本/11文件SHA，再150s子进程运行固定worker；worker再核模型、导入运行库、加载BGE并做临时合成upsert/commit/query/shutdown | stdout JSON原来收齐后解析，stderr原来捕获但未写出；父进程随后再import Torch做CPU断言 | stderr直达现有安装日志；明确加载/检索阶段和等待；CPU/CUDA断言移到同一worker子进程runpy返回后，复用已加载Torch，不在worker线程池初始化前执行 |
| 最终配置 | CheckLocal再次核路径/模型与stamp，SaveWithCommitBoundary核当前配置版本/许可并提交 | 仍须防并发变更；不应为快而删 | 仅加“核对本地文件与配置版本”提示；保存/资料根/认证逻辑不改 |

## 明确变化与保留

- RecallProgressText只接收固定LBPROGRESS阶段；不把任意子进程命令/报错直接转成进度。原完整输出仍进入自有日志。stderr转接不会修改worker stdout JSONL，返回码和5条回复断言保持。
- .NET RunPython的退出/取消策略未改：不主动杀可能仍写venv的进程。每10秒的“进程尚未结束”是等待事实，不是活性或成功证明。顶层-u避免已发出的阶段被Python输出缓冲滞留。
- 最终CPU-only与矩阵乘法11断言未删除，只从父进程迁到生产worker子进程正常退出之后。生产worker本体SHA仍6ef8145e129c4399150367df4a6f41490349203c00eab898a0948eb6725d4036；避免提前执行CPU运算影响其set_num_interop_threads初始化。
- 不改hash锁、模型内容、下载主机/TLS、pip no-index/hash、fixture身份、临时目录限制、依赖版本、主产品DLL及用户资料/认证。没有引入“以旧回执直接跳过验证”的捷径。
- cn17已知Unity基础库/系统运行库/可选GitHub/OpenAI网络及loader依赖通知缺口继续存在，本轮未扩研究或测试。

## 证据与未验

本轮只构建Installer/Handoff/Launcher，Python两脚本仅ast.parse语法解析，没有import、运行或执行任何验证入口。静态可证重复工作与无细分反馈；不能证明笔记本实际时间占比，也没有前后耗时对照。GUI显示、心跳线程/日志顺序、CPU检查迁移、首次/升级/重复/回退与真实模型加载均待用户本人验证。
