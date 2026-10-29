# LilithBridge

> To call up a demon, you must learn its name.

LilithBridge 是桌面伴侣游戏 **《不/存在的莉莉丝》（The NOexistenceN of Lilith）** 的非官方 MOD。它把游戏里的莉莉丝、语言模型和记忆系统连接起来，希望让今天的聊天接得上过去：聊过的小事可以被想起，改变的想法也有机会被重新理解。

## 第一次使用

**当前公开预览只适用于 Windows x64、游戏 1.0.4 的指定配套环境，不支持 Steam 1.1.0。** 如果你还没有匹配的环境，请先等候支持自己游戏版本的安装包，不要仅为这个预览购买游戏。它仍是测试预览，完整游戏启动与聊天尚未完成验证。

1. **确认游戏。** 安装目标是 [《不/存在的莉莉丝》](https://store.steampowered.com/app/4643090/?l=schinese)（Steam App ID：4643090）。游戏和匹配的 BepInEx 加载环境需要另行准备，见[依赖说明](docs/DEPENDENCIES.md)。
2. **下载安装包。** 在 [preview-20261029 发布页](https://github.com/gtxpz2021/LilithBridge/releases/tag/preview-20261029)下载 `LilithBridge-6a028692-win-x64-private-candidate-r1.zip`。请选择这个附件；“Source code”和“Download ZIP”是供开发者使用的源码。
3. **安装并配置。** 退出游戏，完整解压安装包，运行 `Start-Setup.cmd`。选择游戏目录、检查改动，再按提示安装，填写自己的 API 地址、模型名和 Key。[逐步安装与常见问题](docs/PLAYER-GUIDE.md)。

先用远程聊天 API 即可，**不需要 Python 或本地模型**。API 可能按用量收费；聊天内容和本次用到的历史材料会发送到你选择的服务。API Key 以明文保存在本机，请保管好配置和备份。

## 聊起来会是什么样

下面是说明设计的小例子，不是原作台词或运行实录。

> 前几天，你说：“最近想去那家旧书店。”  
> 今天，你说：“下午终于空下来了。”  
> 莉莉丝：“那家旧书店还想去吗？还是今天只想歇一会儿？”

项目希望她能接上之前的事，也给你留下改变主意的余地。你想聊别的，旧计划就不该占满眼前的对话；你随口说的一句话，也不必从此变成固定偏好。记忆是为了让来往更自然，而不是让你一边聊天，一边整理档案。

## 想再了解一点

- [架构入门](ARCHITECTURE.md)：从一次聊天解释经历、记忆和回复如何衔接。不安装游戏也能读。
- [本地召回](docs/DEPENDENCIES.md#可选本地召回)：用本地小模型按意思寻找旧经历。它是可选辅助，不负责生成聊天回复；Python 和模型需要另行准备。
- [技术附录](docs/TECHNICAL-NOTES.md)：支持范围、已知限制和源码入口。
- [许可与第三方内容](LICENSE-STATUS.md)：自有代码采用 MIT；游戏、角色素材及第三方组件保留各自权利。

核心源码在 `src/LilithBridge/`，安装与配置器源码在 `tools/installer-source/`。仓库不附带游戏依赖或可直接安装的二进制文件。
