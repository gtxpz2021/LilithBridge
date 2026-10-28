# 通知与官方引用

本 worker 包没有包含任何第三方 wheel、解释器、模型或运行库。项目自有 worker 及可许可文档采用根目录 MIT License，署名为 2026 gtxpz2021；以下第三方引用与通知仍分别保留，项目许可不覆盖第三方或原作权利。

tokenizers 0.22.2 的已核 wheel：`tokenizers-0.22.2-cp39-abi3-win_amd64.whl`，SHA-256 `c9ea31edff2968b44a88f97d784c2f16dc0729b8b143ed004699ebca91f05c48`。METADATA 声明 `License :: OSI Approved :: Apache Software License`，Project-URL 的 Homepage 和 Source 均为官方仓库 [huggingface/tokenizers](https://github.com/huggingface/tokenizers)；[该版本的官方 PyPI 页面](https://pypi.org/project/tokenizers/0.22.2/)也显示 Apache Software License。

这个 wheel 的 License / License-Expression 字段为空，没有 License-File 声明，文件列表中未找到独立 LICENSE/NOTICE 成员。这里只给出已证实的官方引用，不猜精确 release tag 的许可文本或把缺口标成已闭合；若以后捆绑依赖，需要补相应版本/原包的许可与第三方通知映射。

其他依赖（包括 Torch、NumPy、SciPy、Pillow、setuptools 随附组件）也需要保留各自适用的许可与通知。用户自行安装依赖，不表示可以忽略这些要求。
