# LilithBridge .104 / cn19 候选测试包

只需解压测试ZIP并运行其中的单个 `LilithBridge-Setup-0.3.150.104-cn19-c1.exe`；无需手工合并资源。先退出游戏，核对发现的游戏目录和组件，再按向导操作。不是已发布版本，本轮未运行安装/游戏/模拟或联网测试。

- 新安装预选DeepSeek，模型和隐藏Key输入立即可用；选择所需模型并填本人Key。已有配置保持原选择和连接；使用OpenAI/ChatGPT或暂不配置者可选择保留服务入口。
- 游戏内“模型与资料 → 对话模型”选择DeepSeek/模型，再到“密钥与隐私”粘贴Key并点“保存 Key”；可点“清除 Key”。不回显原Key，切页/关闭/切换服务会清空未保存输入。保存不试连，不改变服务地址。DeepSeek Key沿用旧格式明文存本机config.json，请勿分享使用后的配置；OpenAI/ChatGPT原认证机制保持。
- 已安装者也可在向导点“仅修改服务”，不重装插件、加载器或召回；仍需退出游戏并核对确认。没有代取或预填未知Key。
- 召回检查细分本地SHA、环境创建、离线依赖安装、运行库导入、模型核验与临时检索；Python步骤未退出时每10秒给等待时长。等待提示不等于成功或一定有进展，未承诺耗时改善；重复父进程Torch导入调整已合入，原校验保留。
- 内置固定Python、BepInEx及小依赖/模型文件；剩余9个大wheel和一个模型权重仍优先国内镜像并保留官方fallback。Unity基础库首启路径、系统运行库及部分国外服务可达性仍有未验边界，见网络清单；不保证所有网络可用。

产品 `0.3.150.104-cn-install.candidate.1`，安装器/Launcher `0.1.4.19-cn-integrated.candidate.1`，完整hash见BUILD-IDENTITY.txt/SHA256SUMS。组件版本和来源由release-profile及锁表维护；未更改数据/存档格式，未知未完成Work回退限制保持。许可和原作非MIT范围保留，未公开发布。

反馈请本人选择完整状态行、发生时间、复现步骤及相应日志段：实际资料根 `.setup-checks/local-recall-check.log`，产品专用环境 `environment-check.log`、runtime中的pip/verify日志；游戏BepInEx/LogOutput.log。不要公开Key、认证或整个资料根，不自动上传。真实UI/输入保存清除、升级/回退、首次初始化和网络下载均由用户本人测试。
