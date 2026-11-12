# 构建

产品299个根文件逐字节对应冻结e4c865ca源码，本任务未重编产品；使用global.json指定SDK和合法取得的本体/BepInEx引用。排除NuGet迁移标记、bin/obj/interop。安装器基于prep4最小改动，仅增加精确e4 reader绑定及提示/新本地库推荐；schema12协议保持，AssemblyVersion .13。取得Mono.Cecil0.11.4到tools/setup后运行tools/installer-source/Build-Installer.ps1。任何重编须重新绑定hash；本源码包不含二进制依赖，不宣称独立可复现产品构建。原作语料正文仅安装ZIP交付，非项目MIT，见来源和许可通知。
