# 模型单独取得：固定官方 BAAI revision

仓库和安装包都不含模型权重。用户需要自行从 [BAAI 的官方 Hugging Face 仓库固定 revision](https://huggingface.co/BAAI/bge-small-zh-v1.5/tree/7999e1d3359715c523056ef9478215996d62a620) 取得 `BAAI/bge-small-zh-v1.5`，不能只写 main/latest。

只取本包 `model-manifest.json` 中的 11 个路径，并保持相对结构；逐项字节数和 SHA-256 已在清单列明，总量 96,405,966 字节，约 91.94 MiB。不要默认 clone/download 整个仓库：同 revision 另有约 95.8 MB 的 `pytorch_model.bin`，本清单用 `model.safetensors`，不需要再取该重复权重。

固定单文件官方来源的格式为 `https://huggingface.co/BAAI/bge-small-zh-v1.5/resolve/7999e1d3359715c523056ef9478215996d62a620/<manifest中的相对路径>`。这是来源说明，不是自动下载脚本；用户实际下载前应确认总量、磁盘与路径。不要运行网页给出的在线 from_pretrained 示例替代固定本地路径，也不要允许 worker 自动 fallback 下载。

下载完可用 README 中的 `verify_model_files.py --model <明确目录>` 只读核验，不匹配或缺失就停止；不要修改 manifest 的 hash 来凑通过。该检查不会读取配置/记忆，不会下载。

官方固定版本页面显示模型卡许可标签 MIT；这只是来源事实，完整模型许可文本/固定 revision 来源映射仍保留为发布材料审阅项，不由本任务推导最终法律结论，也不把模型许可套给 Python 依赖。
