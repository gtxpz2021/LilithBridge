# 第三方与原作材料通知

| 实际材料 | 来源与证据 | 权利/用途边界 |
|---|---|---|
| `setup/story/first-game.collection.jsonl`，1794674字节 | `Lilith_Original_Corpus_20260826.zip`内`origins/first_game_all.jsonl`；hash、2076条记录一致性及包装别名说明见`setup/story/SOURCE.json` | 第一作原作脚本文本整理；不属于项目MIT，既有再分发授权证据为not_established。仅作用户选择后有限片段本地检索，不是用户相处记录 |
| `PersonaDefaults.cs`及产品常量内四段原作示例、作品与角色引用 | 当前源码25–29行的原作标注及作品名 | 原作部分权利保留；不虚构逐句脚本映射或额外许可 |
| `setup/Mono.Cecil.dll`，0.11.4 | 完整MIT许可`LICENSES/Mono.Cecil.txt`；官方版本许可来源 `https://github.com/jbevain/cecil/blob/0.11.4/LICENSE.txt` | 按其MIT许可保留版权和通知 |
| 可选worker与锁文件说明 | `setup/optional/semantic-worker/NOTICE-REFERENCES.md`、`MODEL-ACQUISITION.md`；`setup/optional-recall/wheels.lock.json`等 | 普通cn19单EXE内嵌Python、25个小wheel及10个小模型文件；thin更新ZIP只内嵌Python；BGE大权重及9个大wheel不内嵌。各依赖原有许可独立，来源记录不替代许可 |

语料来源说明按原README改写为相对文件名，没有复制历史本机输出路径。源README对第一作角色/说话人的高低置信判断仍只是整理推断；全部未核标记保持，不将它包装成已核实原作事实、执行分支或当前玩家经历。

本候选没有复制来源ZIP其余第二作文件、旧人格提示词、提取脚本、游戏可执行文件/素材、生成interop，或真实配置、存档、私人会话、凭据与追踪。公开动作须待协调线程确认本项具体来源/许可状态；当前没有上传或发布。

## cn19 本次实际增加的第三方准备材料

- 普通EXE中 `setup/offline-cache/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.769+32fb12f.zip` 原样固定官方归档，34325441字节，SHA12f70b8d7489c0931de5dada591b0e2eb6dc9d16a17161fe15f4144912f8924f。BepInEx本身LGPL2.1-or-later；完整许可、上游32fb12f源码ZIP及来源收据在THIRD-PARTY-SOURCES/BepInEx。归档内Doorstop、CoreCLR、Interop/Cpp2IL等各自独立，不改称MIT；其全部通知/对应源码范围未独立闭合，准备候选未公开发布。thin更新ZIP不含该归档。
- Python3.12.10 NuGet原包含Python LICENSE与自带pip通知，普通EXE和thin更新ZIP均内嵌，归档SHA0eb85c2dfccccf1b17352de4c397f69194035b7d37149eacc16f1147d93de3b8，不归项目MIT。
- 普通EXE的25个小wheel逐项见INSTALL-NETWORK-INVENTORY.json，保留未改原wheel归档及其中通知；BGE的10个小配置/说明文件也逐项锁定，原ModelScope/HF同源。没有据此声称全部第三方许可均已单独核完。
- 专门源码ZIP不含上述第三方二进制、wheel或模型正文，仅另保留BepInEx上游源码及许可。原作语料仍只随安装候选，非MIT，授权状态不变。
