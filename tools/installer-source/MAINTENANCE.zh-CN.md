# 最小发版维护流程

安装、升级、回退和受保护配置提交仍使用同一套实现。本轮只集中构建输入，没有引入运行时可变下载配置或通用迁移引擎。

## 常规更新位置

- `release-profile.json`：安装器/Launcher版本、构建/标题、产品DLL SHA、manifest id/schema、固定Python与loader身份、国内镜像前缀和精确重定向host。只放公开的版本/来源元数据，不能放Key、认证或个人配置。
- `../setup/optional-recall/wheels.lock.json`：34个依赖的精确文件、版本、大小、SHA与原始URL。
- `../setup/model-manifest.json`：当前BGE的11项文件身份；worker与验证helper的固定契约必须同步。
- `../setup/loader-files.json`：原loader归档中允许落盘的文件/哈希，不把另取Unity基础库伪装成该ZIP成员。
- 安装/源码白名单、许可与更新说明随实际选入文件更新，不复制整个开发目录或资料根。

## 相同流程

1. 在新的隔离副本放入最终产品DLL及必要组件，记录实际三产物SHA；产品版本、BepInPlugin显示版本和启动日志须一致。构建产品使用合法取得的引用；不把游戏、生成interop或私有配置打包。
2. 更新上述profile/清单。新产品的reader能力需按源码变化另行审阅，再在PersistentStateCompatibility与ReflectionWorkCompatibility对应的精确分支绑定；脚本不会自动赋予新hash未完成Work恢复权限。
3. 在有Python3及原.NET Framework C#工具链的构建机运行 `Build-Installer.ps1`。它先调用 `prepare_release.py`：核真实DLL/worker/清单和helper固定pin，仅按字段同步构建常量及install-manifest，再编译Installer/Handoff。全过程不运行安装器或产品。
4. 沿已批准白名单准备thin/payload/source三个隔离目录，重建PUBLIC-FILES/PACKAGE-FILES/SOURCE-FILES及身份。运行 `python package_staged_release.py prepare --thin <thin目录> --payload <完整目录> --source <清洁源码目录> --out <新输出目录>`。它只取清单内文件，核对SHA后封装并给出build-plan.json。thin仍小于50MiB；完整payload不是在线更新ZIP，仍须满足Launcher自有128MiB未压缩限制。大模型/大wheel保持独立锁定下载，不能只改上限塞入更新通道。
5. 按build-plan路径运行 `Build-SingleFile.ps1 -Payload <完整payload.zip> -Destination <候选Setup.exe>`。同一profile会再核身份并同步版本；该脚本只编译、内嵌payload。最后 `python package_staged_release.py finalize --out <同一输出目录>`，得到只含最终EXE的测试ZIP、清洁源码ZIP、SHA256SUMS和身份；它同时将本次自动生成的payload常量写回清洁源码清单。仍标“候选未运行验收”。

构建输入与检查均是静态文件身份核对，不等于安装、升级、UI或网络验证。运行测试由使用者本人完成；未知项目/数据格式不可仅改profile绕过兼容门。

## 合并约束与已知限制

按hunk合入产品DeepSeek Key、向导服务提示和召回进度，不能以整文件覆盖Wizard.Form.cs。版本同步只处理明确字段，不还原业务hunk。profile本身不在运行时授予新来源/数据读取能力；Source模板仍限定现有PyPI/torch/BGE契约，换模型种类、组件数量或协议属于额外代码审阅范围。

普通用户无需安装构建机Python；安装器使用包内固定解释器。缺系统Framework/VC++的覆盖、Unity基础库分发/预置、GitHub及OpenAI/ChatGPT国内可达等现有未验边界不因标准化关闭。固定旧网络诊断探测包也不是实际产品安装兜底。许可状态按实际来源说明保留，不把第三方或原作材料写成本项目MIT。
