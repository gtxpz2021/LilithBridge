# 安装器交接 protocol 1

仅在已授权的官方插件更新包声明所需 `min_installer_schema` 或 `config_schema` 超过当前能力时进入。

发行包必须来自固定仓库公开 Release 的已核验资产；ZIP 大小与 SHA-256、全包 `PACKAGE-FILES.json` 的每项大小/摘要、受限路径和固定角色全部校验。`CANDIDATE.json` 必须声明 `handoff_protocol: 1`、足够的 `installer_schema` 与 `supported_config_schema`。远端 entry、URL、命令不参与执行选择。

包在本机下载缓存的版本目录解开。旧安装器生成含组件选择、目标与发行身份的会话及完整性绑定；Key、服务草稿、配置内容、记忆与命令行秘密不保存。自有辅助程序等待匹配 PID 与启动时间的旧进程退出，复查完整性后，以固定 `--resume-handoff <plan>` 启动新目录的 `setup/LilithBridge-Installer.exe`。不覆盖旧 EXE，不提权、不杀游戏、不创建服务或计划任务。

新安装器必须支持这个入口，并检查自身路径/摘要、所需 schema 与会话。没有丢失未保存服务/Key 草稿时继续已确认选择；有草稿时打开原目标并要求亲自重填。所有安装与配置仍重新检查真实状态，游戏运行时等待正常退出。

缺少协议、角色、完整性或能力时停止并保留旧安装。协议仅交接，不自动解释未知未来 schema 或执行配置迁移命令。未来版本若提高 schema，必须同时实现对应能力及此协议入口；不能只修改声明数字。

复用解包缓存、helper 启动前复查和新入口接受时，PACKAGE-FILES.json 必须与已验证 ZIP 中的原始清单绑定；验证角色使用 ZIP 中的记录，缓存不能自证。
