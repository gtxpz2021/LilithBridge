# 可选本地语义召回 worker

这个脚本供 LilithBridge 在本机运行语义召回。只使用远程聊天 API 时可以跳过；准备启用时，先按[依赖说明](../../docs/DEPENDENCIES.md#可选本地召回)准备 Python 环境和模型。

## 文件与路径

| 文件 | 用途 |
|---|---|
| `semantic_worker.py` | 游戏配置中的 worker 路径应选择这个文件 |
| `model-manifest.json` | 固定模型文件清单，须保留在 worker 旁边 |
| `verify_model_files.py` | 检查已有模型文件，不下载内容 |
| `Start-Worker.cmd` | 单独诊断时使用；游戏配置中仍选择 `.py` 文件 |

模型取得方法见 [MODEL-ACQUISITION.md](MODEL-ACQUISITION.md)。游戏配置使用本目录的 worker；`optional-recall/probe-fixture/` 仅供工具自检。

## 单独诊断

以下示例适用于已经准备好 Python 环境与模型的用户。先退出游戏，将路径替换成自己的实际路径。索引请使用一个单独的诊断目录，保留游戏正在使用的索引。

```powershell
$Python = 'C:\YourRuntime\.venv\Scripts\python.exe'
$WorkerDir = 'C:\YourTools\optional\semantic-worker'
$Model = 'C:\YourModels\bge-small-zh-v1.5'
$Index = 'C:\YourData\separate-worker-check-index'
& $Python -I -X utf8 -B "$WorkerDir\verify_model_files.py" --model $Model
```

模型检查通过后，再运行：

```powershell
& "$WorkerDir\Start-Worker.cmd" $Python $Model $Index
```

若提示缺失或不匹配，先重新核对下载文件及目录，不修改模型清单。保留命令中的引号和 Python 参数；`-O`、`-OO` 会关闭 worker 所需的检查，请勿使用。

启动后等待 `event:ready`，然后逐行输入 JSON：

```json
{"request_id":"manual-status","op":"status"}
{"request_id":"manual-stop","op":"shutdown"}
```

worker 使用标准输入输出上的 JSONL 协议。游戏运行时由插件管理这个进程，索引位于插件数据目录内。接口细节见 [INTEGRATION.md](INTEGRATION.md)。

worker 已有独立离线验证；当前预览的游戏接入仍待实测。自有脚本及可许可文档采用根目录 [MIT License](../../LICENSE)，第三方来源见 [NOTICE-REFERENCES.md](NOTICE-REFERENCES.md)。
