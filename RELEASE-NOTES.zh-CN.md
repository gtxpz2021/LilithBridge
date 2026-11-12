# LilithBridge 0.3.150.103：让对话、记忆与相处连起来

LilithBridge 是《不/存在的莉莉丝》的非官方 MOD：在游戏中与莉莉丝自由交流，让过去的经历可以被找回，让新的对话有机会改变旧的理解。

本版是从公开 RC3/fix2 到现在的累计更新，面向官方游戏 **1.2.1**。重点是让连续交流更完整、回看与播放更顺畅，并把模型接入和安装流程整理到一起。

## 从 RC3 到现在

### 更完整的上下文与记忆

- 改善对话材料的连续组织与供给，减少零散片段和重复材料。
- 分别保留完整生成内容、实际显示内容，以及被打断后尚未显示的部分，让后续回顾能分清“已经说过”和“原本想说”。
- 完善近期反思与积压工作的接续，保留原始经历及后来形成、修订的理解。
- 修复“公开片段接续未能保存”的时序问题。

### 更轻的回看与播放链路

- 减少回看来源、生成快照的重复加载与检查。
- 普通回答被播放系统接纳后，不再在播放期间反复执行完整来源检查；明确打断和会话切换仍正常生效。
- 包含输入框、历史布局及官方新版游戏接口的适配改进。

### 更清楚的模型选择

- **DeepSeek：Flash / Pro**，替换旧的 Vision ExP 选择；主对话与记忆、反思模型分别配置，后台请求使用非思考模式。
- **Sign in with ChatGPT：** 在游戏内发起浏览器登录，从账户目录选择可用模型，使用该账户额度。
- **OpenAI API：** 保留 Terra、Sol、Astra 预设及自定义模型 ID；试连使用当前选择的主模型。
- 新配置默认开启普通记忆与后台反思，推荐设置补齐必要选项，同时保留升级前已经明确关闭的功能。截图默认关闭。

### 更容易安装和升级

- 安装器 **0.1.4.13** 统一新装、旧插件升级、加载环境准备与可选本地召回。
- 保留已有设置、登录和资料；随包提供第一作作品语料的自动配置，无需手填 JSON 或文件路径。
- 程序升级提供备份与回退入口。

## 下载哪个文件

- **玩游戏：** 下载 `LilithBridge-installer-0.3.150.103-e4c865ca-normal-use-prep5.zip`。
- **看源码：** 下载专门的 `LilithBridge-source-0.3.150.103-e4c865ca-normal-use-prep5.zip`，或查看[当前 main](https://github.com/gtxpz2021/LilithBridge/tree/main)。
- **核对下载：** 使用附件中的 `SHA256SUMS.txt`。

本版 Release 的历史标签仍指向旧提交，GitHub 自动生成的 “Source code (zip/tar.gz)” 不是本版源码；请使用上述专门源码附件。发行文件本身不变。

## 怎么开始

1. 正常退出游戏，完整解压安装包。
2. 运行 `setup/LilithBridge-Installer.exe`，选择游戏目录并按向导安装。Steam 用户可用“管理 → 浏览本地文件”找到目录。
3. 启动游戏，在 **⋯ → 模型与资料** 中选择提供方并配置 Key，或选择 **ChatGPT 计划额度 → Continue with ChatGPT** 登录。
4. 点击 **推荐使用设置**，确认资料使用和后台选项，然后按回车聊天。

完整步骤与关闭入口见[安装与正常使用](https://github.com/gtxpz2021/LilithBridge/blob/main/NORMAL-USE.zh-CN.md)。

## 使用时需要知道

- ChatGPT 的模型和额度取决于登录账户的实际权限；API Key 通路使用独立的 API 计费。
- 记忆保存在本机，选入对话或后台工作的材料会发送到所选服务。游戏内可以调整资料和后台开关。
- 随包第一作语料为 2,076 条、18 个脚本的整理资料，不属于项目 MIT；来源及许可状态见包内 `setup/story/SOURCE.json` 和仓库[许可说明](https://github.com/gtxpz2021/LilithBridge/blob/main/LICENSE-STATUS.md)。

## 反馈问题

请在 [Issues](https://github.com/gtxpz2021/LilithBridge/issues) 写明版本、发生时间和最短复现步骤。日志位置和整理方法见[使用指南](https://github.com/gtxpz2021/LilithBridge/blob/main/NORMAL-USE.zh-CN.md#反馈问题)。

<details>
<summary>构建身份与验证记录</summary>

本版 DLL SHA256：`e4c865ca691559e8b9fe784c88cc7c491f825e32f8cf051b0496f027319152ff`；完整信息见 `BUILD-IDENTITY.txt`。

安装包已在官方 1.2.1 隔离副本完成升级、重复安装与回退检查，并核对新配置默认值和已有关闭项的保留。真实账户生成、Steam 安装及 Unity 调度的组合体验仍欢迎继续反馈；不把离线检查当作所有实机情形都已验证。

</details>
