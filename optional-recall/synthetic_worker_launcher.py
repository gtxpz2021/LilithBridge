"""TEST ONLY: real worker source, real numeric libraries, fake embedding model.

This does not prove model loading or semantic recall quality. Never use as a
configured production worker. It creates no network listener or external request.
"""
import hashlib
from pathlib import Path
import runpy
import sys
import types
import numpy as np


class SyntheticModel:
    def __init__(self, *args, **kwargs):
        self.tokenizer = self.tokenize

    @staticmethod
    def tokenize(text, add_special_tokens=False, return_offsets_mapping=False, **kwargs):
        if return_offsets_mapping:
            return {"offset_mapping": [(i, i + 1) for i in range(len(text))]}
        return {"input_ids": list(range(len(text) + (2 if add_special_tokens else 0)))}

    @staticmethod
    def encode(texts, **kwargs):
        values = []
        for text in texts:
            seed = hashlib.sha256(text.encode("utf-8")).digest()
            vector = np.frombuffer(seed * 16, dtype=np.uint8).astype(np.float32) - 127.5
            values.append(vector / np.linalg.norm(vector))
        return np.stack(values)


worker = Path(sys.argv.pop(1)).resolve()
module = types.ModuleType("sentence_transformers")
module.SentenceTransformer = SyntheticModel
sys.modules["sentence_transformers"] = module
sys.argv[0] = str(worker)
runpy.run_path(str(worker), run_name="__main__")
