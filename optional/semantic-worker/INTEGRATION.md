# 安装线整合约定

最小 payload 是两个**相邻原字节文件**：

```text
optional/semantic-worker/semantic_worker.py
optional/semantic-worker/model-manifest.json
```

可复制本包内说明和模型核验小脚本，勿把任何旧环境目录整包复制。生产 worker SHA-256 为 `6ef8145e129c4399150367df4a6f41490349203c00eab898a0948eb6725d4036`，相邻模型清单 SHA-256 为 `c0f597c0cd4a19a54e08b3d589633b94154ea6f76df9c23365a9ff212eb07c17`。构建发行包时应核对相应文件身份。无需把测试 launcher 当生产代码；本包不含假模型 launcher。

只读核对的产品调用约定：配置项 `SemanticPythonPath`、`SemanticWorkerScript`、`SemanticModelPath`、`SemanticIndexDirectory` 由用户实际路径解析；产品把 worker 所在目录的 `model-manifest.json` 作为 manifest。启动参数是 `-u <worker.py> --model <local model> --manifest <adjacent manifest> --index <index>`，working directory 为 worker 所在目录；stdin/stdout 为 UTF-8 JSONL。无需也不应配置 CMD 为 worker。

Python 路径指向用户独立 venv 的 `Scripts/python.exe`；worker 路径指向本包真实 `.py`；模型是单独取得的 BGE 目录。产品的索引必须在**用户选定的产品数据根内**，例如相对 `semantic-index`；不要放入模型/worker目录，不复制现役 index 或把临时探针目录当生产 index。现有产品会拒绝数据根外索引。

模型固定为 BAAI/bge-small-zh-v1.5 revision `7999e1d3359715c523056ef9478215996d62a620`，需要 manifest 中 11 文件，共 96,405,966 字节，512 维 embedding。384/48 是 worker 的 token 窗口/重叠规则，不是 embedding 维度。模型核验应保留相对子目录 `1_Pooling/config.json`。

安装器静态检查只能确认解释器/worker/manifest 的存在和身份，不能自动宣称新用户依赖/模型已可用。先保持召回关闭，完成用户自备依赖和模型，再由用户在配置器选择路径、保存、重新读取检查并启用。仓库里的文件不会自动写入配置或启用召回。

独立 worker 验证不等于完整产品或游戏接入验证。项目自有 worker 及可许可文档采用根目录 MIT License；第三方许可、原作权利和最终公开发行准备另行处理。
