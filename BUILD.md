# 源码与发行字节

产品源码在 src/LilithBridge/，安装器在 tools/installer-source/。自行构建须取得合法游戏/BepInEx 引用，仓库和安装包不分发游戏 DLL、interop、素材或模型。

产品采用 .NET SDK 6.0.428，信息版本 0.3.150.66-rc.3.fix.2，数值版 0.3.150.66；本次沿用已验证的冻结产品 DLL，SHA256 为 08cef63c6315d9564f59f8d97b510c4671e4eb5ed7e71ca3e21fde76f8755bd7，不因安装器守卫或公开文档重编产品。LILITH_GAME_DIR 指向合法依赖；重编字节须自行核对，不能只凭版本号或源码提交认定相同。

安装器/交接程序 0.1.3.0，能力 schema=3，使用 Windows .NET Framework 4.8 编译器与 Mono.Cecil 0.11.4。按许可取得 Mono.Cecil.dll 放入 tools/setup，执行 Build-Installer.ps1。运行 dotnet 时设置 DOTNET_GENERATE_ASPNET_CERTIFICATE=false。

冻结 worker 为 LF；冻结 requirements 与模型清单保留各自原字节，.gitattributes 禁止转换。不要对这些输入使用 whitespace=fix。已存在工作树应用补丁后须恢复冻结字节；新检出和发行包均包含正确字节。根目录与 setup 两份召回脚本应一致。

本标签安装 ZIP 的 PACKAGE-FILES、PACKAGE-IDENTITY 与安装清单标识实际字节。CANDIDATE.json 为既有安全安装器交接协议提供 schema 能力，不扩展 Work 哈希能力表。安装器更新会要求支持 schema=3 的完整包。默认加载器/功能和模型选择不变；控制台默认关闭，文件日志保留。
