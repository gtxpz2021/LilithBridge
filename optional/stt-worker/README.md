# 可选本地语音输入（STT）Worker

本目录提供 LilithBridge 的离线本地语音转文字子进程，基于 `sherpa-onnx` + 官方 `SenseVoiceSmall INT8`（可选搭配 `Silero VAD ONNX`）。

## 设计原则与隐私约束

- **音频不落盘、不上传**：麦克风 PCM 采样全程仅驻留 Python 内存，在停止、取消或退出时立即清空释放。
- **按需开麦**：仅在玩家主动点击「语」或按热键（默认 `F8`，且仅在游戏/桌宠获焦时生效）开始录音后才打开默认 WASAPI 麦克风；结束或取消瞬间立即关闭硬件录音流。
- **先看再发、绝不自动直发**：录音期间识别内容实时呈现在游戏输入框中；再次点击「停」或按热键结束后自动解锁输入框供手动修改，按 `Enter` 才正式发送。
- **自然断句与语气保留**：短迟疑停顿不会提前强行切句打句号；保留「嗯」「啊」「嘿」等语气词，不做 LLM 改口；内置「莉莉丝 / LilithBridge」高精同音纠偏，并支持通过 `config.json` 的 `stt.replacements` 添加个人常用词表。
- **与 `sensory-sidecar` 彻底解耦**：采用独立 `stdin/stdout NDJSON` 私有子进程，无需开启 HTTP 端口，也不依赖侧面感官模块。

## 文件说明

| 文件 | 用途 |
|---|---|
| `stt_worker.py` | 插件启动的离线 JSONL STT 子进程 |
| `model-manifest.json` | 固定版本 SenseVoice INT8 模型 SHA-256 清单 |
| `verify_model_files.py` | 仅校验本地模型完整性，不联网 |
| `download_model.py` | 由用户手动执行、带断点续传与 SHA-256 校验的模型下载脚本 |
| `test_stt_worker.py` | 单元测试与真实 SenseVoice 端到端 IPC 测试 |

## 手动准备模型与自检

Python 虚拟环境需安装：`numpy`、`sherpa-onnx`、`pyaudiowpatch`（可选 `onnxruntime` 用于 Silero VAD）。

```powershell
# 1. 下载并校验 SenseVoiceSmall INT8（约 155 MB）
& $Python -u optional\stt-worker\download_model.py --dest "LilithBridgeData\stt-runtime\models"

# 2. 只读校验模型哈希
& $Python -u optional\stt-worker\verify_model_files.py --model "LilithBridgeData\stt-runtime\models\sherpa-onnx-sense-voice-zh-en-ja-ko-yue-int8-2024-07-17"

# 3. 运行单元测试与真实模型 IPC 测试
& $Python -B -m unittest discover -s optional\stt-worker -p "test_stt_worker.py" -v
```
