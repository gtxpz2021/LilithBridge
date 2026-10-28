# 可选本地语义召回 worker

这里提供供 LilithBridge 调用的生产 worker。只使用远程聊天 API 时，不需要运行它；决定启用本地语义召回后，再准备自己的 Python 环境和模型。

## 文件与路径

- `semantic_worker.py` 是生产 worker，游戏配置选择这个 `.py` 文件。
- `model-manifest.json` 必须与 worker 相邻，用于核对固定模型文件。
- `verify_model_files.py` 只读检查模型文件，不下载内容。
- `Start-Worker.cmd` 用于手动诊断，不应填到游戏的 worker 路径里。

Python、模型、wheel、venv 和个人索引不在仓库中。环境工具在 `optional-recall/`，准备方法见[依赖说明](../../docs/DEPENDENCIES.md)；模型取得方法见[MODEL-ACQUISITION.md](MODEL-ACQUISITION.md)。

**不要把 `optional-recall/probe-fixture/` 选作游戏 worker。** 那个目录服务于合成协议自检；即使个别文件字节相同，它的模型清单和启动上下文也不是生产配置。

## 手动诊断

下面的路径只是占位示例，换成你自己已准备好的路径后再操作：

```powershell
$Python = 'C:\YourRuntime\.venv\Scripts\python.exe'
$WorkerDir = 'C:\YourTools\optional\semantic-worker'
$Model = 'C:\YourModels\bge-small-zh-v1.5'
$Index = 'C:\YourData\separate-worker-check-index'
& $Python -I -X utf8 -B "$WorkerDir\verify_model_files.py" --model $Model
& "$WorkerDir\Start-Worker.cmd" $Python $Model $Index
```

保留引号。模型检查不匹配时停止，不修改清单来凑通过。不要使用 `-O` 或 `-OO`；worker 的模型身份检查需要正常优化级别。诊断使用专用索引目录，不复制日用索引，也不要同时手动启动第二份日用 worker。

进程启动后等待 `event:ready`，再逐行输入 JSON，例如：

```json
{"request_id":"manual-status","op":"status"}
{"request_id":"manual-stop","op":"shutdown"}
```

这是标准输入输出上的 JSONL 协议，不是 HTTP 服务。游戏运行时由插件管理 worker 进程。生产索引必须位于产品自己的数据根内，详细调用约定见[INTEGRATION.md](INTEGRATION.md)。

worker 已有独立离线验证；这不等于当前插件的完整游戏接入已经验证。此源码导入没有重新运行模型或游戏。

自有 worker 与可许可文档采用根目录 [MIT License](../../LICENSE)，第三方通知引用见[NOTICE-REFERENCES.md](NOTICE-REFERENCES.md)。
