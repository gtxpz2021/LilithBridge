# 源码与发行字节

产品源码在 `src/LilithBridge/`，安装器源码在 `tools/installer-source/`。自行构建须通过合法游戏安装与 BepInEx 取得引用依赖，不在仓库或安装包内分发游戏 DLL、生成的 interop、原作素材或模型权重。

产品冻结采用 .NET SDK 6.0.428，信息版本 0.3.150.64-rc.3，数值版 0.3.150.64；PublicBuild.props 保留确定性/路径映射。可用 LILITH_GAME_DIR 指向自身合法的游戏依赖目录。重新构建的字节可能随 SDK、引用和源文件换行等环境变化，应独立计算 SHA256；不能以源码提交代替实际二进制身份。本次发布直接使用已冻结验证的产品 DLL，没有因公开文档/包标识变动重编产品。

安装器使用 Windows .NET Framework 4.8 编译器与 Mono.Cecil 0.11.4。依赖库需按随附 MIT 许可自行取得放入 tools/setup/Mono.Cecil.dll，再运行 Build-Installer.ps1；源码仓库不提交二进制。运行 dotnet 时设置 DOTNET_GENERATE_ASPNET_CERTIFICATE=false，不需创建或信任开发证书。

公开安装包的 setup/payload/LilithBridge.dll、两份安装器和依赖清单均由 PACKAGE-FILES.json 标识。Start-Setup.cmd 从完整解压后的本地 payload 安装本版本；向导“项目更新”另按官方 release asset 的真实 URL、长度和 digest 核验下载。固定旧小包仍保留作历史下载诊断/无本地包的兼容回退入口，不冒充本次 RC3。不要仅复制安装器 EXE 后将其当作完整 RC3 安装包。

游戏/环境开关和模型默认值不因本次发行改变；测试的临时 ignore_disable_switch=true 不进入生产包。公开源码不包含内部 Git 历史、私密证据或测试观察插件。
