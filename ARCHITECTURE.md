# LilithBridge：从一句话到一段共同经历

LilithBridge 是桌面伴侣游戏[《不/存在的莉莉丝》（The NOexistenceN of Lilith）](https://store.steampowered.com/app/4643090/?l=schinese)的非官方 MOD。它连接游戏中的角色、语言模型与本地记忆，让今天的交流有机会接上过去。

这篇说明不要求你安装游戏。想先知道它在做什么，读“快速了解”即可；想看程序如何实现，再顺着后面的过程和源码导航读下去。[安装步骤另在玩家指南](docs/PLAYER-GUIDE.md)，不用先读懂这些原理。

## 快速了解

可以把 LilithBridge 看成三部分：

- **游戏是交流发生的地方。** 它提供莉莉丝、输入框、对话气泡、动作和当前场景。
- **模型负责理解和回应。** 它根据这次收到的发言与材料，生成回复；需要额外信息时，也可以在允许范围内请求工具。
- **程序负责衔接。** 它准备材料、安排请求、显示回复，并把实际发生的交流保存在本机，供以后使用。

连续性主要来自这些记录及其使用方式，而不是每次聊天都重新训练模型。每次请求能容纳的内容有限，程序要决定“这次带上哪些过去”，模型再判断“这些过去与眼前有什么关系”。

```text
你的发言 → 游戏输入 → 记录本次发言 → 准备本次材料 → 语言模型
                                      ↑                 ↓
                         相关历史、经历和旧理解       回复与按需工具
                                      ↑                 ↓
                              本地记录 ← 实际显示 ← 游戏气泡
                                  │
                                  └─ 有合适机会时反思 → 保存新的理解
```

日常聊天不需要每轮调用工具，也不需要每轮反思。本地语义召回同样是可选项：它帮助找旧事，不负责生成回复；不开启它，也不等于聊天没有近期历史或本地记录。

### 一个贯穿全文的小例子

前几天，你说：“周六想去旧书店。”今天，你说：“下午终于空下来了。”如果相关记录被找到并放进请求，莉莉丝就有机会问起那家书店；也可能判断你现在更想休息，不提旧计划。

接着，你说：“今天不想出门了。”好的连续性不是继续催你去书店，也不是立即认定你从此不喜欢书店，而是让新话与旧话都有各自的位置。

这个例子用来解释程序怎样工作，不是原作台词或运行实录。它也不是对每次聊天效果的保证：记录可能没找到，材料可能不完整，模型也可能理解错。

---

## 详细原理

下面展示的是各环节怎样衔接。实际一次对话只走当前配置启用、且这次需要的分支。

### 1. 游戏负责呈现，控制器负责把一次交流串起来

BepInEx 加载插件后，[`Plugin.cs`](src/LilithBridge/Plugin.cs) 注册与游戏的连接，并创建持续运行的 `BridgeController`。这里的**宿主**就是游戏：Unity/IL2CPP 提供运行环境，原作的对话、角色状态与界面提供交互位置。

[`NativeHooks.cs`](src/LilithBridge/NativeHooks.cs) 接入相关游戏事件；`NativeInputField` 接收输入；控制器协调游戏状态、请求和显示。这样，API 返回文字只是流程的一步，文字还需要在合适的游戏状态下真正呈现出来。

输入框里的草稿与正式发送也有区别。非空文本提交后，控制器才把它记成本次玩家发言，并交给新的请求处理。如果你在上一轮还没结束时再次发送，新的发言会接管对话，旧请求和未显示的旧回复需要取消或停止继续交付。

这部分主要看 [`BridgeController.InputSubmission.cs`](src/LilithBridge/BridgeController.InputSubmission.cs) 与 [`DialogueRequest.cs`](src/LilithBridge/DialogueRequest.cs)。后者保存一次请求自己的状态、取消信号和可用能力，避免较慢的旧结果回来后接管新的聊天。

### 2. 先分清“保存了什么”和“这次给模型看什么”

项目中几类数据承担不同职责：

| 数据 | 主要作用 | 在书店例子里 |
|---|---|---|
| 近期聊天历史 | 接住正在进行的上下文 | 你刚说有空，接着又说不想出门 |
| 经历记录 | 留下收到的发言、实际显示的回复及其来源 | 记录你此前提过周六去书店 |
| 留存与整理后的理解 | 保存有依据的记忆、反思或关系理解，并保留修订关系 | 曾经形成“这件事让你期待”的理解，之后可能需要调整 |
| 语义索引 | 帮助按意思查找记录 | 尝试把“下午有空”和先前的出行话题联系起来 |
| 本次请求材料 | 从可用记录中选出这一轮要交给模型的内容 | 只带上相关书店经历，而不是整份聊天档案 |

**本次材料是记录的一种读取结果，不是原始档案的替代品。** 例如，一次长回复可能分成多个气泡保存；按配置构建请求时，可以把确属同一回复的连续气泡合在一起，节省消息窗口，而不改写原来的记录。

相关入口是 [`NativeState.cs`](src/LilithBridge/NativeState.cs) 中的 `MemoryTurn`、历史读写，以及 [`HistoryProjection.cs`](src/LilithBridge/HistoryProjection.cs)。经历归档在 [`MemoryStore.Experiences.cs`](src/LilithBridge/MemoryStore.Experiences.cs)，数据目录由 [`DataPaths.cs`](src/LilithBridge/DataPaths.cs) 解析。

### 3. 准备材料：先找得到，再决定这次用不用

[`BridgeController.Submission.cs`](src/LilithBridge/BridgeController.Submission.cs) 组织请求，[`ContextBuilder.cs`](src/LilithBridge/ContextBuilder.cs) 组装当前状态、历史和记忆材料。材料可能包括当前角色状态、近期交流、相关旧经历、已经保存的理解，以及本次可执行的能力。

这些内容按配置选择，并非每次全部加入。`ContextBuilder.Build` 有不同的装配分支；原始经历补检索、语义补充和旧理解的日常投影集中在 `BuildCleanContext` 路径中，由相应开关控制。阅读某个检索函数时，也要看它有没有被当前装配路径调用。

#### 词面检索与语义召回是互补的

**词面检索**利用实际文字线索，例如关键词、完整引语、说话人、时间范围和明确的更正关系。你问“我之前说周六去哪儿”，这些线索有助于找到具体原话。

**语义召回**尝试寻找措辞不同、意思相近的内容。启用后，独立的 Python worker 使用本地 BGE 模型生成向量、维护索引并返回候选记录。它是检索辅助，不是另一个负责聊天的大模型。

词面检索更适合抓住准确名称和原话，但可能漏掉换一种说法的线索；语义召回更容易找到近义内容，也可能把“看起来相似、实际不相关”的经历找回来。因此，召回结果只是候选，还要经过后面的选择。

本地模型工作放在独立进程中；游戏侧传递记录快照并接回结果。结果会与本次查询、请求及数据目录关联，过期结果不会直接混进另一轮对话。召回不可用或未完成时，这条补充通路可能缺少材料，不能把“这次没找到”理解成“过去没有发生”。

#### 找到候选后，还要过四道判断

1. **来源是否可用。** 记录是否仍存在、允许在当前请求中使用；玩家原话、模型回复、原作对话与后来形成的理解保留各自归属。
2. **版本是否仍适用。** 来源或理解已经更正、撤回或被替代时，不能仅因为相似度高就继续作为当前依据。
3. **与这次问题是否相关。** 同一主题不代表同一个问题；旧计划也不一定是眼前想聊的事。
4. **材料是否放得下。** 历史窗口、检索条数和部分材料块都有容量限制。已建立关联的更正与必要上下文要一起考虑，不能只塞入容易误解的一半。

这里的“预算”主要指请求能容纳多少材料，并不直接等同于账单金额；字符数、消息数和模型 token 也不是同一种单位。

相关代码集中在 [`MemorySemantics.cs`](src/LilithBridge/MemorySemantics.cs)、[`MemoryStore.Experiences.cs`](src/LilithBridge/MemoryStore.Experiences.cs)、[`MemoryStore.SemanticRecall.cs`](src/LilithBridge/MemoryStore.SemanticRecall.cs) 与 [`MemoryStore.SupplyContract.cs`](src/LilithBridge/MemoryStore.SupplyContract.cs)。worker 的启动与请求归属见 [`BridgeController.SemanticRecall.cs`](src/LilithBridge/BridgeController.SemanticRecall.cs) 和 [`SemanticRecallWorker.cs`](src/LilithBridge/SemanticRecallWorker.cs)。

### 4. 模型看到的是整理后的材料，不是整个数据库

[`DeepSeekClient.cs`](src/LilithBridge/DeepSeekClient.cs) 把系统说明、当前发言、选中的历史与材料组成 API 请求。当前玩家说的话与程序提供的旧材料需要区分，转述也要保留为转述；具体承载方式由请求模式决定，相关处理在 [`DialogueMaterialEnvelope.cs`](src/LilithBridge/DialogueMaterialEnvelope.cs) 与 [`UtteranceAttribution.cs`](src/LilithBridge/UtteranceAttribution.cs)。

回到书店例子：程序可以找出“周六想去旧书店”，告诉模型这是谁在什么时候说过的话。但“你现在只是想歇一会儿，还是在暗示可以去书店”，仍由模型结合当前发言判断。程序没有一条固定规则规定：看到“有空”就必须提书店。

模型也可以在本次已开放的能力范围内请求工具，例如补充查看当前屏幕或回顾材料。工具能否开放取决于配置。程序检查可用性、参数和请求状态后执行，再把结果带回同一轮。**普通聊天可以直接回答，不需要先走完一串工具。** 工具可能增加等待和 API 调用，其作用是补充这次确实需要的信息。

发往远程服务的是这次请求实际选入的内容。记忆保存在本机，并不意味着聊天请求完全离线；相关发言和历史材料仍会交给所选 API 服务处理；启用并使用看屏能力时，请求还可能包含截图。

程序主动发起的交流也会经过请求与交付流程，但来源与玩家输入不同：它可以依据当前状态或合适的话题线索发起一次尝试，也可以保持安静。主动请求不会因此多出一句玩家实际说过的话；后续有新的玩家输入时，仍由新输入接管对话。调度入口可看 `RuntimeDirector.cs` 与控制器中的主动参与流程。

### 5. 回复生成后，要以实际显示为准写回经历

模型生成的文本先成为待显示的回复，再由游戏气泡呈现。项目分别记录原始生成、显示计划、已显示片段与最终交付结果。

假设模型准备说：

> “那家旧书店还想去吗？如果今天累了，我们也可以先不去。”

如果游戏只显示第一句就被打断，后续历史应记下第一句，而不能假定第二句也已经对你说过。否则，下次聊天就会建立在你从未看到的回复上。

[`NativeBubblePresenter.cs`](src/LilithBridge/NativeBubblePresenter.cs) 管显示，[`BridgeController.Delivery.cs`](src/LilithBridge/BridgeController.Delivery.cs) 在气泡显示时写入助手历史，并在结束或中断时结算实际交付部分。后续记忆提取和相关状态更新也使用这部分文本；网络成功返回与游戏完整显示是两个不同结果。

这一点也解释了为什么需要请求归属和取消处理：过期的回复即使生成得很好，也不能在新对话里无条件补播。至于模型是否答得合适、玩家是否真的读到了气泡，则仍是另一层体验问题。

### 6. 反思是有条件的后台整理，不是每轮必做总结

经历回答“发生过什么”；反思回答“根据这些经历，可以形成什么理解，原来的理解是否需要调整”。反思是另一次模型工作，会使用选定的经历窗口和允许参考的已有记录。

调度会考虑是否启用、新的交流、空闲机会、任务状态与间隔等条件。没有合适材料时，可以没有新记录；生成失败或保存失败，也不应被当作已经整理完。主要入口是控制器的 `TryScheduleReflection`、[`MemoryStore.ReflectionScheduling.cs`](src/LilithBridge/MemoryStore.ReflectionScheduling.cs) 与 [`BridgeController.ReflectionPreparation.cs`](src/LilithBridge/BridgeController.ReflectionPreparation.cs)。

模型可以提出三类结果：

- **反思笔记**：针对具体经历形成的理解。
- **经验性做法**：从已有笔记中整理出的一种做法，仍可修订，而不是给玩家增加硬规则。
- **关系理解**：对相处的当前解释，带有来源，不是把关系压成一个分数。

返回这些文字并不立即等于保存。程序会检查它引用的来源、可修订的目标和版本是否仍然有效，再处理写入。新的理解保留与旧版本的关系，原始经历不会因此被改写。具体保存逻辑在 [`MemoryStore.Reflections.cs`](src/LilithBridge/MemoryStore.Reflections.cs)。

在书店例子里，反思可能形成“你期待出门，但也会根据当天状态调整”的理解，也可能认为这次不值得单独留下一条笔记。项目不要求每次闲聊都上升为长期偏好或关系结论。

### 7. 保存的旧理解怎样再次参与对话

保存以后，还需要有实际读取它的地方。否则，记忆只是磁盘上的文字。

| 读取场景 | 怎样使用旧理解 |
|---|---|
| 日常聊天的旧理解投影 | 在相关装配路径和开关启用时，结合本次已提供的经历来源，挑选相关且仍适用的笔记或经验性做法 |
| 你明确追问“以前怎么想” | 按问题查找相关理解与版本，连同依据说明，而不是把旧想法直接说成现在的想法 |
| 后续反思 | 参考已有笔记、做法和关系理解，决定是否继续、修订或结束一条理解 |
| 角色视角与关系材料 | 在对应功能启用时，将选中的关系理解放入当前角色视角材料 |

`BuildReflectionRecallContext` 与 `BuildChatReflectionContext` 负责前两条主要读取路径；[`ContextMaterialSelection.cs`](src/LilithBridge/ContextMaterialSelection.cs)、[`MemoryStore.MaterialSelection.cs`](src/LilithBridge/MemoryStore.MaterialSelection.cs) 和 [`SelfPositionView.cs`](src/LilithBridge/SelfPositionView.cs) 处理部分角色视角材料的选择与呈现。

读取时仍要区分**内容相关**与**依据有效**。例如，某条旧理解依据了后来被明确更正的原话，就需要重新检查它是否仍适用。已建立的更正关系会保留原话与澄清；但“今天不想出门”并不天然等于否定“前几天想去书店”。任意自然语言变化都能被准确识别，仍不是程序可以保证的事。

这里的“反思”“理解”“角色视角”是记录和生成机制的名称，不证明系统具有人的意识。保存的理解、单次请求的思考模式、后台反思也是不同的东西；有一段记录，不代表能够读取模型当时全部的内部过程。

## 为什么游戏更新不必等于重写全部 MOD

游戏适配与聊天、记忆逻辑分在不同模块。安装器会检查游戏身份、加载环境、interop 是否对应当前游戏，以及插件依赖的类型和成员签名，主要实现见 [`HostCapabilities.cs`](tools/installer-source/src/HostCapabilities.cs)。

所以，游戏版本号变化本身不是判定不兼容的全部依据。如果所需接口仍然存在，插件代码可能无需修改；若接口、生成文件或运行行为发生变化，就需要处理具体差异。接口检查能发现一部分问题，但无法证明所有动作和时序都保持原样，也不会自动修补未知接口。

安装与升级的具体操作以[玩家指南](docs/PLAYER-GUIDE.md)和对应发布说明为准；它们与这里讲的记忆、对话原理是两回事。

## 当前范围与局限

- **主聊天链路已经实现。** 宿主接入、请求编排、回复显示、经历写回及相关记忆机制在当前源码中有对应模块；具体使用哪些分支取决于配置。
- **本地语义召回是可选项。** 已在 Steam 1.1.0 游戏内验证本地 BGE 召回，并确认召回材料进入实际对话请求；长期召回质量仍需继续观察，基本聊天不要求安装它。
- **“一起看”不作为当前默认聊天能力介绍。** 源码中有相关模块，新的共同阅读流程仍在独立候选中验证，不应由文件存在推断为公开包已经提供完整体验。
- **记忆可靠性有多个环节。** 漏检、错误关联、过期理解、材料容量不足、模型误读以及显示中断，都可能造成不同的问题。一次回复正常，不代表长期记忆已经得到全面验证。

当前运行范围集中记录在[技术附录](docs/TECHNICAL-NOTES.md)。

## 源码导航：从哪里读最省力

不必从头读完所有文件。可以先沿一次请求走，再挑自己关心的分支。

| 想理解什么 | 建议阅读入口 |
|---|---|
| 插件怎样进入游戏 | `Plugin.cs` → `NativeHooks.cs` → `BridgeController.cs` |
| 一句输入怎样变成请求 | `NativeInputField.Submission.cs` → `BridgeController.InputSubmission.cs` → `BridgeController.Submission.cs` |
| 本次带哪些材料 | `ContextBuilder.cs` → `HistoryProjection.cs`、`ContextMaterialSelection.cs` |
| 旧经历怎样被找到 | `MemoryStore.Experiences.cs`、`MemorySemantics*.cs` → `MemoryStore.SemanticRecall.cs` |
| 本地模型如何接入 | `BridgeController.SemanticRecall.cs` → `SemanticRecallWorker.cs` → `optional/semantic-worker/semantic_worker.py` |
| 来源、更正与版本 | `MemoryStore.SupplyContract.cs`、`MemoryStore.SourceCorrections.cs`、`MemoryStore.NativeDialogueSupply.cs` |
| API 与按需工具 | `DeepSeekClient.cs`、`DeepSeekClient.Tools.cs`、`DialogueMaterialEnvelope.cs` |
| 哪些回复算真正说过 | `NativeBubblePresenter.cs` → `BridgeController.Delivery.cs` |
| 反思怎样产生、保存、再次被读取 | `BridgeController.ReflectionPreparation.cs` → `MemoryStore.Reflections.cs` → `ContextBuilder.cs` |
| 数据在哪里、怎样保存 | `DataPaths.cs`、`NativeState.cs`、`MemoryStore.Experiences.cs`、`MemoryStore.B1.Storage.cs` |

表中 C# 文件位于 [`src/LilithBridge/`](src/LilithBridge/)。`BridgeController` 和 `MemoryStore` 分散在多个 `partial` 文件中，共同组成相同的类；文件名后缀帮助定位职责，并不表示它们是可以单独运行的程序。

[回到首页](README.md) · [安装与使用](docs/PLAYER-GUIDE.md) · [可选召回依赖](docs/DEPENDENCIES.md#可选本地召回)
