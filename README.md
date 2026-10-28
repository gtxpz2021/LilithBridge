# LilithBridge

> To call up a demon, you must learn its name.

LilithBridge 是桌面伴侣游戏 **《不/存在的莉莉丝》（The NOexistenceN of Lilith）** 的非官方 MOD。它把游戏里的莉莉丝、语言模型和记忆系统连接起来，想让你们的日常聊天接得上过去：聊过的事可以在合适的时候被想起，之后的理解和变化也有共同经历作为依据。

**别买错游戏：** MOD 安装在《不/存在的莉莉丝》上，不是《不/存在的你，和我》。后者是另一款视觉小说，不是本 MOD 的安装目标，也无需为了安装 MOD 而另购它。

## 玩家从这里开始

1. **确认游戏和版本。** 正版入口是 [《不/存在的莉莉丝》Steam 商店](https://store.steampowered.com/app/4643090/?l=schinese)（App ID：4643090）。当前 MOD 预览仅接受 **Windows x64、已核验的游戏 1.0.4 及配套环境**。**Steam 1.1.0 和其他文件组合不支持；购买或下载游戏最新版，并不代表能安装这个预览。**
2. **准备前置。** 游戏、BepInEx 和 interop 需要自备；版本与取得入口见[依赖说明](docs/DEPENDENCIES.md)。
3. **下载 MOD。** 打开 [首次预览 preview-20261029](https://github.com/gtxpz2021/LilithBridge/releases/tag/preview-20261029)，下载附件 `LilithBridge-6a028692-win-x64-private-candidate-r1.zip`。不要下载 “Source code” 或仓库的 “Download ZIP” 来安装。
4. **解压并配置。** 退出游戏，完整解压安装包，运行 `Start-Setup.cmd` → 选择含 `Lilith.exe` 的游戏目录 → 检查改动 → 安装并打开配置 → 填写自己的 HTTPS API 地址、模型名和 Key，勾选配置确认后保存。[查看逐步说明与回退方法](docs/PLAYER-GUIDE.md)。

只使用远程聊天 API **不需要 Python 或本地模型**，可以先保持本地召回关闭。预览版的已知限制与验证范围见[技术附录](docs/TECHNICAL-NOTES.md)。

**不装游戏，只想看设计？** 直接读[架构入门](ARCHITECTURE.md)，不必购买游戏或配置 API。

## 聊起来会是什么样

下面是一段简短示意，不是原作台词或运行实录。

> 前几天，你说：“最近想去那家旧书店。”  
> 今天，你说：“下午终于空下来了。”  
> 莉莉丝：“那家旧书店还想去吗？还是今天只想歇一会儿？”

这里希望做到的，是她能接上之前的事，也给你留下改变主意的余地。如果你想聊别的，旧计划就不该占满眼前的对话。记忆是为了让来往更自然，不是让你一边聊天，一边替系统整理档案。

记得更多也不一定聊得更自然。项目希望她在需要时想起相关经历，而不是每次翻旧账、总结关系，或把你随口说的一句话永久记成偏好。

## 本地召回是可选项

本地语义召回用于按意思寻找旧经历，**不是聊天大模型**。启用它需要另备 Python 环境、固定版本的 BGE 模型和生产 worker；默认 API-only 路线不因此受阻。当前安装 ZIP 不含模型权重，取得与配置方法见[依赖与本地召回说明](docs/DEPENDENCIES.md)。

仓库不包含游戏、加载器、interop、Python、模型、wheel、用户配置或记忆。`optional-recall/probe-fixture/` 只用于合成自检，不能选择为游戏 worker。

## 想再深入一点

- [架构入门](ARCHITECTURE.md)：从一次聊天解释经历、记忆和实际交付。
- [技术附录](docs/TECHNICAL-NOTES.md)：实现入口、精确版本和验证范围。
- **源码入口：** 核心代码在 `src/LilithBridge/`，安装与配置器在 `tools/installer-source/`；可选环境工具在 `optional-recall/`，生产 worker 在 `optional/semantic-worker/`。源码不附带游戏依赖或构建产物。
- [许可与第三方内容](LICENSE-STATUS.md)：自有代码与可许可文档采用 MIT，第三方与原作权利分别保留。
- [《不/存在的你，和我》官方页面](https://store.steampowered.com/app/2873080/?l=schinese)：了解另一款作品；**不是这个 MOD 的安装入口，也不需要为了安装 MOD 而另购它。**
