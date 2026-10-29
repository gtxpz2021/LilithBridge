# LilithBridge 技术附录

## 当前运行范围

Windows x64 下，已在《不/存在的莉莉丝》Steam 1.1.0 测试环境中完成安装、配置、启动、聊天回复交付和本地语义召回接入。其他环境由安装器检查运行条件，游戏更新仍可能影响兼容性；在线下载、更新检查和全新电脑的验证限制见[当前验证范围](../CANDIDATE-VALIDATION.md)。

## 按问题继续阅读

- **想理解整个系统：** [架构说明](../ARCHITECTURE.md)，先快速了解，再按一次聊天逐层展开。
- **想定位代码：** [源码导航](../ARCHITECTURE.md#源码导航从哪里读最省力)。
- **准备安装或排查：** [玩家指南](PLAYER-GUIDE.md)。
- **准备加载器、API 或本地召回：** [依赖说明](DEPENDENCIES.md)。

原理与模块职责统一维护在架构说明，不在本页重复。

## 安装包与许可

可安装文件以 [Releases](https://github.com/gtxpz2021/LilithBridge/releases) 中实际提供的 Windows 安装 ZIP 附件为准。对应发布说明和包内清单用于核对安装包身份与内容；源码 ZIP 用于阅读和构建，不是安装包。

项目自有且有权许可的内容采用 MIT，第三方组件与原作权利分别保留，见[许可说明](../LICENSE-STATUS.md)。

[回到首页](../README.md) · [架构说明](../ARCHITECTURE.md)
