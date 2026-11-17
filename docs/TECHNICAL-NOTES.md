# LilithBridge 技术附录

## 当前运行范围

Windows x64 下，面向官方 **1.2.1** 的完整对话、流式交付、KV 缓存分层、气泡拼合去重与反思更新链路已在实机桌面测试环境及离线套件中完成验证；可选本地 BGE 语义召回在实机与离线测试夹具上均保持兼容接入。游戏更新后是否兼容，由安装器通过 `HostCapabilities` 静态检查所需游戏类型、成员签名与加载器状态，实际手感与长期表现仍取决于具体游玩环境与所选模型服务；检验边界见[验证范围说明](../CANDIDATE-VALIDATION.md)。

## 按问题继续阅读

- **想从第一性原理了解系统设计：** [架构入门](../ARCHITECTURE.md)
- **想直接定位源码文件：** [架构入门（文末源码导航）](../ARCHITECTURE.md)
- **想装包、配模型或排查故障：** [安装与使用指南](../NORMAL-USE.zh-CN.md)
- **想看加载器、API 或本地 BGE 召回细节：** [依赖说明](DEPENDENCIES.md)

架构原理与模块职责统一维护在 `ARCHITECTURE.md`，不在本页重复展开。

## 安装包与许可

可安装的 Windows 发行程序以 [Releases](https://github.com/gtxpz2021/LilithBridge/releases/latest) 中的单文件 `LilithBridge-Setup-*.exe`（及供自动更新用的 `LilithBridge-installer-*.zip`）为准；源码 ZIP 仅用于阅读与自行构建，不可直接安装。

本项目自有源码与工具采用 MIT，第三方组件与原作权利分别保留，详见 [许可说明](../LICENSE-STATUS.md)。

[回到首页](../README.md) · [架构入门](../ARCHITECTURE.md) · [安装与使用指南](../NORMAL-USE.zh-CN.md)
