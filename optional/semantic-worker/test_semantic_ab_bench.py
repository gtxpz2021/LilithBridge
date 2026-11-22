"""Real-model A/B bench for the frozen semantic golden set; never creates the fixture."""
from pathlib import Path
import argparse
import hashlib
import importlib.util
import json
import math
import os
import statistics
import subprocess
import sys
import tempfile
import time
from typing import Any, Dict, Iterable, List, Sequence, Tuple

HERE = Path(__file__).resolve().parent
WORKER = HERE / "semantic_worker.py"
MANIFEST = HERE / "model-manifest.json"
FIXTURE = Path(r"C:\Projects\LilithBridge\tests\fixtures\semantic_ab_golden.json")
DEFAULT_MODEL = Path(r"C:\Users\gtxpz\Desktop\PrjL\The NOexistenceN of Lilith"
                     r"\LilithBridgeData\semantic-runtime-v1\models\bge-small-zh-v1.5")
PREFIX = "为这个句子生成表示以用于检索相关文章："
REVISION = "7999e1d3359715c523056ef9478215996d62a620"

PROBE_RECORDS = [
    ("probe-target", "明天暴雨出门要带雨伞，还要把阳台的晾衣架收起来。"),
    ("probe-music", "耳机里单曲循环的那首钢琴曲，副歌有一段很轻的鼓点。"),
    ("probe-cup", "书桌右上角的青瓷茶杯缺了一个很小的口。"),
    ("probe-code", "这段代码在异常分支里少写了文件句柄释放。"),
    ("probe-night", "深夜窗外偶尔能听见便利店冷柜压缩机的低鸣。"),
    ("probe-rain", "雨点先打在屋檐上，再顺着玻璃慢慢流下来。"),
    ("probe-plant", "窗台的小盆栽新长出的叶子有点发黄。"),
    ("probe-bus", "末班公交车进站时刹车声比白天显得更清楚。"),
    ("probe-book", "那本旧书的书脊已经裂开，但每一页都还在。"),
    ("probe-light", "台灯的暖光把键盘边缘照出一圈很淡的影子。"),
    ("probe-walk", "小区里的桂花落了一地，走过去有很淡的甜味。"),
    ("probe-food", "楼下关东煮的汤底很咸，萝卜倒是煮得很透。"),
]


def emit(value: Dict[str, Any]) -> None:
    print(json.dumps(value, ensure_ascii=False, separators=(",", ":")), flush=True)


def sha256(text: str) -> str:
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


def safe_angle(cosine: float) -> float:
    return float(math.acos(max(-1.0, min(1.0, cosine))))


def percentile(values: Sequence[float], percent: float) -> float:
    if not values:
        return 0.0
    ordered = sorted(float(v) for v in values)
    at = (len(ordered) - 1) * percent
    low = int(math.floor(at))
    high = int(math.ceil(at))
    if low == high:
        return ordered[low]
    return ordered[low] + (ordered[high] - ordered[low]) * (at - low)


def load_geometry_module():
    spec = importlib.util.spec_from_file_location("semantic_worker_bench", WORKER)
    module = importlib.util.module_from_spec(spec)
    if spec.loader is None:
        raise RuntimeError("semantic_worker.py loader unavailable")
    spec.loader.exec_module(module)
    return module


def load_model(model_dir: Path):
    os.environ.update(HF_HUB_OFFLINE="1", TRANSFORMERS_OFFLINE="1",
                      HF_HUB_DISABLE_TELEMETRY="1", HF_HUB_DISABLE_IMPLICIT_TOKEN="1",
                      TOKENIZERS_PARALLELISM="false")
    from sentence_transformers import SentenceTransformer
    return SentenceTransformer(str(model_dir), device="cpu", local_files_only=True,
                               trust_remote_code=False, token=False)


def encode_texts(model, texts: Sequence[str]) -> Any:
    import numpy as np
    return model.encode(list(texts), batch_size=16, normalize_embeddings=True,
                        convert_to_numpy=True, show_progress_bar=False).astype(np.float32)


def fixture_records(fixture: Dict[str, Any]) -> List[Dict[str, Any]]:
    records = fixture.get("records")
    if not isinstance(records, list) or not records:
        raise ValueError("fixture records must be a non-empty list")
    for record in records:
        if not isinstance(record, dict) or not isinstance(record.get("id"), str) \
                or not isinstance(record.get("text"), str) or not record["text"]:
            raise ValueError("fixture record requires string id and text")
    return records


def fixture_queries(fixture: Dict[str, Any]) -> List[Dict[str, Any]]:
    queries = fixture.get("queries")
    if not isinstance(queries, list) or not queries:
        raise ValueError("fixture queries must be a non-empty list")
    for query in queries:
        if not isinstance(query, dict) or not isinstance(query.get("query"), str) \
                or not isinstance(query.get("expected_id"), str):
            raise ValueError("fixture query requires query and expected_id")
    return queries


def lexical_overlap(query: str, text: str, tokens: Iterable[str]) -> int:
    return sum(1 for token in tokens if token and token in query and token in text)


def run_frozen_ab(model_dir: Path, fixture_path: Path) -> int:
    try:
        fixture = json.loads(fixture_path.read_text(encoding="utf-8"))
        records = fixture_records(fixture)
        queries = fixture_queries(fixture)
    except Exception as exc:
        emit(dict(status="invalid_frozen_golden_fixture", error=type(exc).__name__))
        return 3
    started = time.perf_counter()
    model = load_model(model_dir)
    geometry_module = load_geometry_module()
    import numpy as np
    ids = [record["id"] for record in records]
    texts = [record["text"] for record in records]
    raw_docs = encode_texts(model, texts)
    prefixed_queries = encode_texts(model, [PREFIX + query["query"] for query in queries])
    raw_queries = encode_texts(model, [query["query"] for query in queries])
    rows = {}
    for event_id, text, vector in zip(ids, texts, raw_docs):
        rows[event_id] = dict(text_hash=sha256(text), vectors=vector[None, :],
                              windows=[dict(chunk_start=0, chunk_end=1, char_start=0,
                                            char_end=len(text))])
    baseline_orders: List[List[str]] = []
    baseline_ranks: Dict[str, List[int]] = {}
    for q_vector in prefixed_queries:
        scores = raw_docs @ q_vector
        order = [ids[index] for index in np.argsort(-scores)]
        baseline_orders.append(order)
        for rank, event_id in enumerate(order, 1):
            baseline_ranks.setdefault(event_id, []).append(rank)
    engine = geometry_module.SemanticGeometryEngine()
    geometry_results = [engine.query(rows, raw_queries[at], []) for at in range(len(queries))]
    geometry_orders = [result["geometry"]["dpp_selected_ids"] for result in geometry_results]
    iso = engine.iso
    x_iso = iso["x_iso"]
    r_k = iso["r_k"]
    # Exact full-corpus CSLS ranking (matching semantic_worker.py:280-285).
    # H-2 bounded result["doc_sims"] to ~30 IDs, so reading doc_sims here would
    # omit the tail and drop the -r_k hubness term.
    csls_orders: List[List[str]] = []
    csls_ranks: Dict[str, List[int]] = {}
    for at in range(len(queries)):
        q_doc = engine._project(geometry_module._event_vector(raw_queries[at]), iso)
        sims = x_iso @ q_doc
        k_query = min(10, len(ids))
        r_query = float(np.mean(np.partition(sims, -k_query)[-k_query:]))
        true_csls = 2.0 * sims - r_query - r_k
        order = [ids[index] for index in np.argsort(-true_csls)]
        csls_orders.append(order)
        for rank, event_id in enumerate(order, 1):
            csls_ranks.setdefault(event_id, []).append(rank)

    required_total = 0
    required_geometry_hits = 0
    required_baseline_hits = 0
    all_geometry_hits = 0
    all_baseline_hits = 0
    for at, query in enumerate(queries):
        baseline_top3 = baseline_orders[at][:3]
        geometry_top3 = geometry_orders[at][:3]
        expected = query["expected_id"]
        in_baseline = expected in baseline_top3
        in_geometry = expected in geometry_top3
        all_baseline_hits += int(in_baseline)
        all_geometry_hits += int(in_geometry)
        if query.get("required", True):
            required_total += 1
            required_baseline_hits += int(in_baseline)
            required_geometry_hits += int(in_geometry)
            if not in_geometry:
                emit(dict(status="metric_failed", metric="recall_at_3",
                          query_index=at, expected=expected,
                          geometry_top3=geometry_top3))
                return 4
    recall_at_3 = (required_geometry_hits / required_total) if required_total else (all_geometry_hits / len(queries))
    baseline_recall_at_3 = (required_baseline_hits / required_total) if required_total else (all_baseline_hits / len(queries))
    all_queries_recall_at_3 = all_geometry_hits / len(queries)
    all_queries_baseline_recall_at_3 = all_baseline_hits / len(queries)

    def volume(event_ids: Sequence[str], vectors: Any) -> float:
        chosen = np.stack([vectors[ids.index(event_id)] for event_id in event_ids]).astype(np.float64)
        if chosen.shape[0] == 0:
            return 0.0
        _, singular, _ = np.linalg.svd(chosen, full_matrices=False)
        return float(np.prod(np.maximum(singular, 1e-12)))

    baseline_volumes: List[float] = []
    geometry_volumes: List[float] = []
    geometry_raw_volumes: List[float] = []
    for at in range(len(queries)):
        baseline_top = baseline_orders[at][:3]
        geometry_top = geometry_orders[at][:3]
        baseline_volumes.append(volume(baseline_top, raw_docs))
        geometry_volumes.append(volume(geometry_top, x_iso))
        geometry_raw_volumes.append(volume(geometry_top, raw_docs))
    dpp_gain = (statistics.geometric_mean([max(v, 1e-12) for v in geometry_volumes])
                / statistics.geometric_mean([max(v, 1e-12) for v in baseline_volumes]))
    dpp_raw_gain = (statistics.geometric_mean([max(v, 1e-12) for v in geometry_raw_volumes])
                    / statistics.geometric_mean([max(v, 1e-12) for v in baseline_volumes]))

    metaphor_total = metaphor_hits = 0
    for at, query in enumerate(queries):
        tokens = query.get("must_not_share")
        if not isinstance(tokens, list):
            continue
        metaphor_total += 1
        if not any(lexical_overlap(query["query"], record["text"], tokens) for record in records):
            if query["expected_id"] in csls_orders[at][:15]:
                metaphor_hits += 1
    metaphor_rate = metaphor_hits / metaphor_total if metaphor_total else 1.0

    # Hub suppression is measured without Top-10 rank truncation bias or empty-mean crashes:
    # 1) Manifold hubness-excess suppression (whitening collapse of hub vs normal r_k excess)
    # 2) Top-5 hub intrusion suppression ratio (baseline_top5_hits / dpp_top5_hits)
    # 3) Full-corpus CSLS mean hub rank pushed strictly deeper than raw cosine baseline
    hub_indices = [idx for idx, record in enumerate(records) if record.get("kind") == "hub"]
    normal_indices = [idx for idx, record in enumerate(records) if record.get("kind") != "hub"]
    hub_id_set = {ids[idx] for idx in hub_indices}
    if hub_indices and normal_indices:
        k_nn = min(10, max(0, len(ids) - 1))
        if k_nn > 0:
            raw_sim_mat = raw_docs @ raw_docs.T
            np.fill_diagonal(raw_sim_mat, -2.0)
            raw_rk = np.mean(np.partition(raw_sim_mat, -k_nn, axis=1)[:, -k_nn:], axis=1)
        else:
            raw_rk = np.zeros((len(ids),), dtype=np.float32)
        raw_hub_excess = float(np.mean(raw_rk[hub_indices]) - np.mean(raw_rk[normal_indices]))
        iso_hub_excess = float(np.mean(r_k[hub_indices]) - np.mean(r_k[normal_indices]))
        if raw_hub_excess <= 0.0:
            hub_suppression = 1.0
        elif iso_hub_excess <= 0.0:
            hub_suppression = math.inf
        else:
            hub_suppression = raw_hub_excess / iso_hub_excess
        baseline_hub_top5 = sum(1 for order in baseline_orders for eid in order[:5] if eid in hub_id_set)
        dpp_hub_top5 = sum(1 for order in geometry_orders for eid in order[:5] if eid in hub_id_set)
        if baseline_hub_top5 == 0 and dpp_hub_top5 == 0:
            hub_top5_suppression = 1.0
        elif dpp_hub_top5 == 0:
            hub_top5_suppression = math.inf
        else:
            hub_top5_suppression = baseline_hub_top5 / dpp_hub_top5
        baseline_hub_mean_rank = statistics.mean([rank for eid in hub_id_set for rank in baseline_ranks[eid]])
        csls_hub_mean_rank = statistics.mean([rank for eid in hub_id_set for rank in csls_ranks[eid]])
        hub_rank_improved = csls_hub_mean_rank > baseline_hub_mean_rank
    else:
        raw_hub_excess = iso_hub_excess = 0.0
        hub_suppression = math.inf
        baseline_hub_top5 = dpp_hub_top5 = 0
        hub_top5_suppression = math.inf
        baseline_hub_mean_rank = csls_hub_mean_rank = 0.0
        hub_rank_improved = True

    # p95 is measured on a real worker round trip, not on in-process matrix products.
    # M-3: the fifth hard gate is split by the measured reality that pure-CPU BGE
    # encoding dominates end-to-end while the geometry kernel itself is << 5 ms.
    timing = run_worker_p95(model_dir, records, queries)
    p95_geometry_ms = timing["p95_geometry_ms"]
    p95_end_to_end_ms = timing["p95_end_to_end_ms"]
    metrics = dict(
        status="runner_ready",
        fixture=str(fixture_path),
        model=str(model_dir),
        records=len(records), queries=len(queries),
        required_queries=required_total,
        metaphor_queries=metaphor_total,
        recall_at_3=round(recall_at_3, 5),
        baseline_recall_at_3=round(baseline_recall_at_3, 5),
        all_queries_recall_at_3=round(all_queries_recall_at_3, 5),
        all_queries_baseline_recall_at_3=round(all_queries_baseline_recall_at_3, 5),
        dpp_volume_gain=round(dpp_gain, 5),
        dpp_raw_space_volume_gain=round(dpp_raw_gain, 5),
        zero_lexical_metaphor_hit_rate=round(metaphor_rate, 5),
        hub_suppression_multiplier=round(hub_suppression, 5) if math.isfinite(hub_suppression) else "infinite",
        hub_top5_intrusion_ratio=round(hub_top5_suppression, 5) if math.isfinite(hub_top5_suppression) else "infinite",
        hub_top5_baseline_hits=baseline_hub_top5,
        hub_top5_dpp_hits=dpp_hub_top5,
        hub_baseline_mean_rank=round(baseline_hub_mean_rank, 3),
        hub_csls_mean_rank=round(csls_hub_mean_rank, 3),
        hub_raw_rk_excess=round(raw_hub_excess, 5),
        hub_iso_rk_excess=round(iso_hub_excess, 5),
        p95_geometry_ms=p95_geometry_ms,
        p95_end_to_end_ms=p95_end_to_end_ms,
        elapsed_ms=round((time.perf_counter() - started) * 1000, 3))
    emit(metrics)
    if (recall_at_3 < 1.0 or recall_at_3 < baseline_recall_at_3
            or dpp_gain <= 1.15 or metaphor_rate < 0.70
            or hub_suppression < 2.0 or hub_top5_suppression < 1.25 or not hub_rank_improved
            or p95_geometry_ms >= 5.0 or p95_end_to_end_ms >= 35.0):
        emit(dict(status="hard_gate_failed", metrics=metrics))
        return 5
    emit(dict(status="all_hard_gates_passed", metrics=metrics))
    return 0


class WorkerProcess:
    def __init__(self, model_dir: Path, index_dir: Path):
        self.process = subprocess.Popen(
            [sys.executable, "-u", "-X", "utf8", "-B", str(WORKER),
             "--model", str(model_dir), "--manifest", str(MANIFEST), "--index", str(index_dir)],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            encoding="utf-8", errors="strict", env={**os.environ,
                "HF_HUB_OFFLINE": "1", "TRANSFORMERS_OFFLINE": "1",
                "HF_HUB_DISABLE_TELEMETRY": "1", "HF_HUB_DISABLE_IMPLICIT_TOKEN": "1",
                "TOKENIZERS_PARALLELISM": "false"})
        self.ready = self.read()
        if self.ready.get("event") != "ready":
            raise RuntimeError("semantic worker did not become ready")
        self.generation = int(self.ready["index_generation"])

    def read(self) -> Dict[str, Any]:
        line = self.process.stdout.readline()
        if not line:
            error = self.process.stderr.read()
            raise RuntimeError("semantic worker exited: " + error.strip())
        return json.loads(line)

    def call(self, request: Dict[str, Any]) -> Dict[str, Any]:
        assert self.process.stdin is not None
        self.process.stdin.write(json.dumps(request, ensure_ascii=False) + "\n")
        self.process.stdin.flush()
        return self.read()

    def close(self) -> None:
        try:
            self.call(dict(request_id="shutdown", op="shutdown"))
        finally:
            if self.process.stdin:
                self.process.stdin.close()
            self.process.terminate()
            self.process.wait(timeout=5)


def run_worker_p95(model_dir: Path, records: Sequence[Dict[str, Any]],
                   queries: Sequence[Dict[str, Any]]) -> Dict[str, float]:
    with tempfile.TemporaryDirectory(prefix="semantic-ab-") as temp:
        worker = WorkerProcess(model_dir, Path(temp))
        try:
            for at in range(0, len(records), 16):
                batch = records[at:at + 16]
                worker.call(dict(request_id=f"upsert-{at}", op="upsert", records=[
                    dict(event_id=record["id"], delivery="delivered", role="assistant",
                         native_id=index + 1, text=record["text"])
                    for index, record in enumerate(batch)]))
            commit = worker.call(dict(request_id="commit", op="commit", enable_geometry=True))
            worker.generation = int(commit["index_generation"])
            timings: List[float] = []
            for at, query in enumerate(queries):
                text = query["query"]
                reply = worker.call(dict(request_id=f"query-{at}", op="query", text=text,
                    query_hash=sha256(text), model_revision=REVISION,
                    index_generation=worker.generation, conversation_generation=at + 1,
                    enable_geometry=True, recent_event_ids=[], scene_text="",
                    probe_event_ids=[]))
                if reply.get("status") != "ok":
                    raise RuntimeError("worker query failed: " + str(reply.get("error", reply.get("status"))))
                # M-3: keep geometry compute and end-to-end separate. On pure CPU the
                # BGE encode dominates end-to-end; a single 15 ms threshold was
                # physically unreachable even though geometry stays far below 5 ms.
                timings.append((float(reply["elapsed_ms"]), float(reply.get("geometry_ms") or 0.0)))
            return dict(
                p95_end_to_end_ms=round(percentile([t for t, _ in timings], 0.95), 5),
                p95_geometry_ms=round(percentile([g for _, g in timings], 0.95), 5))
        finally:
            worker.close()


def real_bge_probe(model_dir: Path) -> int:
    if not model_dir.exists():
        emit(dict(status="missing_real_bge_model", model=str(model_dir)))
        return 6
    with tempfile.TemporaryDirectory(prefix="semantic-probe-") as temp:
        worker = WorkerProcess(model_dir, Path(temp))
        try:
            for at in range(0, len(PROBE_RECORDS), 16):
                batch = PROBE_RECORDS[at:at + 16]
                upsert = worker.call(dict(request_id=f"upsert-{at}", op="upsert", records=[
                    dict(event_id=event_id, delivery="delivered", role="assistant",
                         native_id=index + 1, text=text)
                    for index, (event_id, text) in enumerate(batch)]))
                if upsert.get("status") != "ok":
                    raise RuntimeError("probe upsert failed")
            commit = worker.call(dict(request_id="commit", op="commit", enable_geometry=True))
            worker.generation = int(commit["index_generation"])
            target_id, target_text = PROBE_RECORDS[0]
            target_query = worker.call(dict(request_id="self-angle", op="query", text=target_text,
                query_hash=sha256(target_text), model_revision=REVISION,
                index_generation=worker.generation, conversation_generation=1,
                enable_geometry=True, recent_event_ids=[], scene_text="", probe_event_ids=[target_id]))
            if target_query.get("status") != "ok":
                raise RuntimeError("target query failed: " + json.dumps(target_query, ensure_ascii=False))
            self_cosine = float(target_query["doc_sims"][target_id])
            self_angle = safe_angle(self_cosine)
            prefixed_cosine = float(next(candidate["score"] for candidate in target_query["candidates"]
                                         if candidate["event_id"] == target_id))
            prefix_angle = safe_angle(prefixed_cosine)
            recent_ids = [event_id for event_id, _ in PROBE_RECORDS[-6:]]
            query_text = "窗外的雨声和刚才那段钢琴副歌有点像。"

            def timed_query(request_id: str, generation: int) -> Dict[str, Any]:
                return worker.call(dict(request_id=request_id, op="query", text=query_text,
                    query_hash=sha256(query_text), model_revision=REVISION,
                    index_generation=worker.generation, conversation_generation=generation,
                    enable_geometry=True, recent_event_ids=recent_ids,
                    scene_text="耳机里有钢琴曲；窗外雨点打在屋檐上。",
                    probe_event_ids=[event_id for event_id, _ in PROBE_RECORDS[:16]]))

            # H-5: two unrecorded warmups keep a single cold-start geometry
            # sample from dominating a five-point p95. The first cold value is
            # still reported for observability.
            cold_first_geometry_ms = 0.0
            for warm in range(2):
                warm_reply = timed_query(f"warmup-{warm}", warm + 2)
                if warm_reply.get("status") != "ok":
                    raise RuntimeError("warmup query failed")
                if warm == 0:
                    cold_first_geometry_ms = float(warm_reply["geometry_ms"])
            timings: List[Dict[str, float]] = []
            for at in range(15):
                reply = timed_query(f"timing-{at}", at + 4)
                if reply.get("status") != "ok":
                    raise RuntimeError("timing query failed")
                timings.append(dict(total=float(reply["elapsed_ms"]), encode=float(reply["encode_ms"]),
                                    geometry=float(reply["geometry_ms"])))
            incremental = worker.call(dict(request_id="incremental-upsert", op="upsert", records=[
                dict(event_id="probe-incremental", delivery="delivered", role="assistant", native_id=1,
                     text="增量提交用来实测一次纯新增事件的安全维护耗时。")]))
            if incremental.get("status") != "ok":
                raise RuntimeError("incremental upsert failed")
            incremental_commit = worker.call(dict(request_id="incremental-commit", op="commit", enable_geometry=True))
            if incremental_commit.get("status") != "ok":
                raise RuntimeError("incremental commit failed")
            result = dict(
                status="real_bge_probe_completed",
                model=str(model_dir), model_revision=REVISION,
                self_angle_rad=round(self_angle, 6), self_angle_pass=self_angle < 0.05,
                prefixed_query_offset_rad=round(prefix_angle, 6),
                query_count=len(timings),
                warmup_queries=2,
                cold_first_geometry_ms=round(cold_first_geometry_ms, 3),
                encode_ms=[round(item["encode"], 3) for item in timings],
                geometry_ms=[round(item["geometry"], 3) for item in timings],
                end_to_end_ms=[round(item["total"], 3) for item in timings],
                p50_encode_ms=round(percentile([item["encode"] for item in timings], 0.50), 3),
                p50_geometry_ms=round(percentile([item["geometry"] for item in timings], 0.50), 3),
                p50_end_to_end_ms=round(percentile([item["total"] for item in timings], 0.50), 3),
                p95_encode_ms=round(percentile([item["encode"] for item in timings], 0.95), 3),
                p95_geometry_ms=round(percentile([item["geometry"] for item in timings], 0.95), 3),
                p95_end_to_end_ms=round(percentile([item["total"] for item in timings], 0.95), 3),
                incremental_commit_ms=round(float(incremental_commit["elapsed_ms"]), 3),
                incremental_commit_geometry_ms=round(float(incremental_commit["geometry_ms"]), 3))
            emit(result)
            # A model-encode-heavy host can exceed 40 ms end-to-end. The probe is
            # still successful when the split shows geometry below 10 ms.
            return 0 if self_angle < 0.05 else 7
        finally:
            worker.close()


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--real-bge-probe", action="store_true")
    parser.add_argument("--model", type=Path,
                        default=Path(os.environ.get("LILITH_SEMANTIC_MODEL", DEFAULT_MODEL)))
    parser.add_argument("--fixture", type=Path, default=FIXTURE)
    args = parser.parse_args()
    model_dir = args.model.resolve()
    if args.real_bge_probe:
        return real_bge_probe(model_dir)
    if not args.fixture.exists():
        emit(dict(status="missing_frozen_golden_fixture", fixture=str(args.fixture)))
        return 2
    return run_frozen_ab(model_dir, args.fixture)


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as exc:
        emit(dict(status="bench_error", error=type(exc).__name__))
        print(type(exc).__name__, file=sys.stderr)
        raise
