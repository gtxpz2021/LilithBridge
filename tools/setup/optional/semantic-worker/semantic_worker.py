"""Private offline JSONL worker with optional unified manifold geometry."""
from pathlib import Path
from typing import List, Tuple
import argparse
import gc
import hashlib
import json
import os
import socket
import sys
import time

import numpy as np

MODEL_REVISION = '7999e1d3359715c523056ef9478215996d62a620'
QUERY_PREFIX = '为这个句子生成表示以用于检索相关文章：'


def _norm_rows(mat: np.ndarray) -> np.ndarray:
    norms = np.linalg.norm(mat, axis=-1, keepdims=True)
    return (mat / np.maximum(norms, 1e-8)).astype(np.float32)


def _event_vector(vectors: np.ndarray) -> np.ndarray:
    """Every window contributes; no first-window-only long-memory shortcut."""
    array = np.asarray(vectors)
    if array.ndim == 1:
        array = array[None, :]
    if array.ndim != 2 or array.shape[0] == 0:
        raise ValueError('event requires at least one embedding window')
    return _norm_rows(np.mean(array, axis=0, keepdims=True))[0]


def _whiten_tier_d(n_rows: int) -> int:
    if n_rows < 64:
        return 0
    if n_rows < 200:
        return 1
    return 3


def _build_iso_state_chunked(raw_vecs: np.ndarray, wire_ids: List[str], k_nn: int = 10,
                             chunk_size: int = 512) -> dict:
    """Chunked cold build for X_iso, whitening, neighbor densities and identity map."""
    n = int(raw_vecs.shape[0])
    if len(wire_ids) != n or len(set(wire_ids)) != n:
        raise ValueError('geometry row identity mismatch')
    dimension = raw_vecs.shape[1] if raw_vecs.ndim == 2 else 512
    d = _whiten_tier_d(n)
    id_to_idx = {eid: i for i, eid in enumerate(wire_ids)}
    if n == 0:
        return dict(d=0, mu=np.zeros((dimension,), np.float32),
                    pcs=np.zeros((dimension, 0), np.float32), x_iso=raw_vecs.copy(),
                    top_k_sims=np.zeros((0, 0), np.float32), r_k=np.zeros((0,), np.float32),
                    id_to_idx=id_to_idx, since_rebuild=0)
    raw = _norm_rows(raw_vecs)
    if d == 0:
        mu = np.zeros((dimension,), np.float32)
        pcs = np.zeros((dimension, 0), np.float32)
        x_iso = raw
    else:
        mu = np.mean(raw, axis=0).astype(np.float32)
        centered = raw - mu
        sample = centered if n <= 512 else centered[np.linspace(0, n - 1, 512, dtype=int)]
        _, _, vt = np.linalg.svd(sample, full_matrices=False)
        pcs = vt[:min(d, vt.shape[0])].T.astype(np.float32)
        x_iso = _norm_rows(centered - (centered @ pcs) @ pcs.T)
    k = min(k_nn, max(0, n - 1))
    top_k_sims = np.full((n, max(1, k)), -2.0, dtype=np.float32)
    r_k = np.zeros((n,), dtype=np.float32)
    if k > 0:
        for start in range(0, n, chunk_size):
            end = min(n, start + chunk_size)
            block = x_iso[start:end] @ x_iso.T
            for local_i, global_i in enumerate(range(start, end)):
                block[local_i, global_i] = -2.0
            part = np.partition(block, -k, axis=1)[:, -k:]
            top_k_sims[start:end, :k] = part
            r_k[start:end] = np.mean(part, axis=1)
    return dict(d=d, mu=mu, pcs=pcs, x_iso=x_iso, top_k_sims=top_k_sims, r_k=r_k,
                id_to_idx=id_to_idx, since_rebuild=0)


def _incremental_append_row(iso: dict, eid: str, v_raw: np.ndarray, k_nn: int = 10) -> bool:
    """Exact O(N) append; False mandates a rebuild, never a duplicate append."""
    n_old = iso['x_iso'].shape[0]
    if (eid in iso['id_to_idx'] or _whiten_tier_d(n_old + 1) != iso['d']
            or iso['since_rebuild'] >= 40):
        return False
    centered = np.asarray(v_raw, dtype=np.float32) - iso['mu']
    pcs = iso['pcs']
    if pcs.shape[1] > 0:
        centered = centered - (centered @ pcs) @ pcs.T
    v_iso = _norm_rows(centered[None, :])[0]
    iso['id_to_idx'][eid] = n_old
    if n_old == 0:
        iso['x_iso'] = v_iso[None, :]
        iso['top_k_sims'] = np.full((1, 1), -2.0, dtype=np.float32)
        iso['r_k'] = np.zeros((1,), dtype=np.float32)
        iso['since_rebuild'] += 1
        return True
    x_old = iso['x_iso']
    sims_to_old = x_old @ v_iso
    k_target = min(k_nn, n_old)
    old_topk = iso['top_k_sims']
    if old_topk.shape[1] < k_target:
        pad = np.full((n_old, k_target - old_topk.shape[1]), -2.0, dtype=np.float32)
        old_topk = np.hstack([old_topk, pad])
    min_col_idx = np.argmin(old_topk[:, :k_target], axis=1)
    min_vals = old_topk[np.arange(n_old), min_col_idx]
    improve = sims_to_old > min_vals
    old_topk[improve, min_col_idx[improve]] = sims_to_old[improve]
    iso['r_k'] = np.mean(old_topk[:, :k_target], axis=1).astype(np.float32)
    new_topk = np.partition(sims_to_old, -k_target)[-k_target:].astype(np.float32)
    new_row_pad = np.full((1, old_topk.shape[1]), -2.0, dtype=np.float32)
    new_row_pad[0, :k_target] = new_topk
    iso['top_k_sims'] = np.vstack([old_topk, new_row_pad])
    iso['x_iso'] = np.vstack([x_old, v_iso[None, :]])
    iso['r_k'] = np.append(iso['r_k'], np.float32(np.mean(new_topk)))
    iso['since_rebuild'] += 1
    return True


def _salience_z_bg(scores: np.ndarray, top_exclude: int = 20) -> Tuple[float, float, float]:
    """Use the non-top bulk, avoiding the Top-20 self-cancellation paradox."""
    if scores.size == 0:
        return 0.0, 0.0, 1.0
    ordered = np.sort(scores)[::-1]
    top = float(ordered[0])
    bg = ordered[top_exclude:] if ordered.size > max(40, top_exclude * 2) else ordered
    mu_bg = float(np.mean(bg))
    std_bg = max(float(np.std(bg)), 0.05)
    return (top - mu_bg) / std_bg, mu_bg, std_bg


def _find_wormhole_sparks(x_iso_hist: np.ndarray, hist_meta: List[dict], q_doc_iso: np.ndarray,
                          q_anchor_id: str, u_prior_vecs: List[np.ndarray], u_prior_ids: List[str],
                          max_hits: int = 2) -> List[dict]:
    """anchor_a is always this query; anchor_b is the least-aligned prior clean turn."""
    if not u_prior_vecs or x_iso_hist.shape[0] < 10:
        return []
    mat_prior = np.stack(u_prior_vecs, axis=0)
    prior_sims = mat_prior @ q_doc_iso
    j_min = int(np.argmin(prior_sims))
    if float(prior_sims[j_min]) >= 0.25:
        return []
    u_b = mat_prior[j_min]
    sim_a = x_iso_hist @ q_doc_iso
    sim_b = x_iso_hist @ u_b
    scores = np.minimum(sim_a, sim_b)
    z_top, mu_bg, std_bg = _salience_z_bg(scores, top_exclude=10)
    if z_top < 2.4:
        return []
    out = []
    for idx in np.argsort(-scores)[:max_hits]:
        score = float(scores[idx])
        z_i = (score - mu_bg) / std_bg
        if z_i >= 2.2:
            out.append({**hist_meta[int(idx)], 'anchor_a': q_anchor_id,
                        'anchor_b': u_prior_ids[j_min], 'wormhole_score': round(score, 4),
                        'z_score': round(z_i, 3)})
    return out


def _greedy_dpp(x_iso: np.ndarray, csls_scores: np.ndarray, wire_ids: List[str],
                top_n: int = 48, max_select: int = 10) -> Tuple[List[str], dict]:
    """Greedy Gram-Schmidt log-volume selection using only whitened CSLS quality."""
    if not wire_ids:
        return [], {}
    count = min(top_n, len(wire_ids))
    pool = np.argsort(-csls_scores)[:count]
    positive = np.clip(csls_scores[pool].astype(np.float64), 0.0, None)
    peak = float(np.max(positive)) if positive.size else 0.0
    quality = positive / peak if peak > 1e-12 else np.ones_like(positive)
    residual = x_iso[pool].astype(np.float64)
    selected = []
    orth_sins = {}
    available = np.ones(count, dtype=bool)
    for _ in range(min(max_select, count)):
        squared = np.sum(residual * residual, axis=1)
        gain = np.log(np.maximum(squared, 1e-12)) + np.log(np.maximum(quality, 1e-3))
        gain[~available] = -np.inf
        choice = int(np.argmax(gain))
        if not np.isfinite(gain[choice]):
            break
        available[choice] = False
        selected.append(wire_ids[int(pool[choice])])
        sine = float(np.sqrt(max(squared[choice], 0.0)))
        orth_sins[wire_ids[int(pool[choice])]] = round(sine, 5)
        direction = residual[choice]
        norm = np.linalg.norm(direction)
        if norm <= 1e-8:
            continue
        direction = direction / norm
        residual -= np.outer(residual @ direction, direction)
        residual[choice] = 0.0
    return selected, orth_sins


class SemanticGeometryEngine:
    """In-memory derived geometry only; vectors.npy/index.json remain unchanged."""

    def __init__(self):
        self.iso = None
        self.dirty = True

    @staticmethod
    def _raw(rows: dict, event_id: str) -> np.ndarray:
        return _event_vector(rows[event_id]['vectors'])

    def ensure(self, rows: dict) -> dict:
        if self.iso is None or self.dirty:
            ids = list(rows.keys())
            raw = (np.stack([self._raw(rows, eid) for eid in ids]).astype(np.float32)
                   if ids else np.zeros((0, 512), dtype=np.float32))
            self.iso = _build_iso_state_chunked(raw, ids)
            self.dirty = False
        return self.iso

    def after_upsert(self, rows: dict, changed_ids: List[str], overwritten: bool) -> None:
        if not changed_ids:
            return
        if self.iso is None or self.dirty or overwritten:
            self.dirty = True
            return
        for event_id in changed_ids:
            if not _incremental_append_row(self.iso, event_id, self._raw(rows, event_id)):
                self.dirty = True
                return

    def after_prune(self, removed: int) -> None:
        if removed:
            self.dirty = True

    @staticmethod
    def _project(v_raw: np.ndarray, iso: dict) -> np.ndarray:
        centered = np.asarray(v_raw, dtype=np.float32) - iso['mu']
        pcs = iso['pcs']
        if pcs.shape[1] > 0:
            centered = centered - (centered @ pcs) @ pcs.T
        return _norm_rows(centered[None, :])[0]

    @staticmethod
    def _recent_basis(vectors: List[np.ndarray]) -> np.ndarray:
        if len(vectors) < 3:
            return np.zeros((vectors[0].shape[0] if vectors else 512, 0), dtype=np.float32)
        mat = np.stack(vectors, axis=1)
        u, r = np.linalg.qr(mat, mode='reduced')
        keep = np.abs(np.diag(r)) > 1e-4
        return u[:, keep].astype(np.float32)

    @staticmethod
    def _residual(vector: np.ndarray, basis: np.ndarray) -> np.ndarray:
        return vector - basis @ (basis.T @ vector) if basis.shape[1] else vector.copy()

    @staticmethod
    def _metadata(rows: dict, idx: int, ids: List[str], score: float) -> dict:
        event_id = ids[int(idx)]
        row = rows[event_id]
        windows = row['windows']
        return dict(event_id=event_id, text_hash=row['text_hash'], score=round(float(score), 5),
                    chunk_start=windows[0]['chunk_start'], chunk_end=windows[-1]['chunk_end'],
                    char_start=windows[0].get('char_start', 0), char_end=windows[-1].get('char_end', 0))

    def query(self, rows: dict, q_doc_raw: np.ndarray, recent_event_ids: List[str],
              scene_raw: np.ndarray = None, probe_event_ids: List[str] = None,
              query_event_id: str = '', explicit_lookup: bool = False) -> dict:
        iso = self.ensure(rows)
        ids = list(rows.keys())
        x_iso = iso['x_iso']
        q_doc = self._project(_event_vector(np.asarray(q_doc_raw, dtype=np.float32)), iso)
        sims = x_iso @ q_doc if ids else np.zeros((0,), dtype=np.float32)
        if ids:
            k_query = min(10, len(ids))
            r_query = float(np.mean(np.partition(sims, -k_query)[-k_query:]))
            csls = 2.0 * sims - r_query - iso['r_k']
        else:
            csls = sims.copy()
        recent_ids = [eid for eid in recent_event_ids[-6:] if eid in iso['id_to_idx']]
        recent_vecs = [x_iso[iso['id_to_idx'][eid]] for eid in recent_ids]
        basis = self._recent_basis(recent_vecs)
        residual = self._residual(q_doc, basis)
        eta = float(np.linalg.norm(residual)) if basis.shape[1] else 0.0
        q_perp = _norm_rows(residual[None, :])[0] if eta >= 0.28 else None
        arc_length = displacement = jump_ratio = 0.0
        regime = 'steady_drift'
        if len(recent_vecs) >= 2:
            steps = np.arccos(np.clip(np.sum(np.stack(recent_vecs[:-1]) * np.stack(recent_vecs[1:]), axis=1), -1, 1))
            arc_length = float(np.sum(steps))
            displacement = float(np.arccos(np.clip(np.dot(recent_vecs[0], recent_vecs[-1]), -1, 1)))
            previous = steps[:-1]
            jump_ratio = float(steps[-1] / max(float(np.median(previous)) if previous.size else 0.0, 0.12))
            tortuosity = arc_length / max(displacement, 0.08)
            if len(recent_vecs) >= 4 and jump_ratio >= 2.1 and steps[-1] >= 0.85:
                regime = 'topic_pivot'
                basis = self._recent_basis([recent_vecs[-1]])
                residual = self._residual(q_doc, basis)
                eta = float(np.linalg.norm(residual))
                q_perp = _norm_rows(residual[None, :])[0] if eta >= 0.28 else None
            elif len(recent_vecs) >= 4 and tortuosity >= 2.1 and displacement <= 0.48 and arc_length >= 0.85:
                regime = 'orbiting_loop'
            elif arc_length > 0.0 and displacement > 0.0:
                regime = 'linear_deepening'
        else:
            tortuosity = 0.0
        recent_set = set(recent_ids)
        target_indices = [i for i, eid in enumerate(ids)
                          if eid not in recent_set and eid != query_event_id
                          and not eid.startswith('derived/generated_reply/')]
        target = np.asarray(target_indices, dtype=int)
        orthogonal = []
        if q_perp is not None and target.size >= 4:
            target_x = x_iso[target]
            if basis.shape[1]:
                recent_alignment = np.max(np.maximum(0.0, target_x @ basis), axis=1)
            else:
                recent_alignment = np.zeros((target.size,), dtype=np.float32)
            scores = (target_x @ q_perp) * (1.0 - 0.65 * recent_alignment)
            safe = sims[target] >= float(np.quantile(sims[target], 0.75))
            z_top, mu, std = _salience_z_bg(scores)
            if z_top >= 2.4:
                for local in np.argsort(-scores)[:3]:
                    z_i = (float(scores[local]) - mu) / std
                    if safe[local] and z_i >= 2.2:
                        idx = int(target[local])
                        orthogonal.append({**self._metadata(rows, idx, ids, csls[idx]),
                                           'orth_score': round(float(scores[local]), 4),
                                           'z_score': round(z_i, 3)})
        wormhole = []
        if not explicit_lookup and target.size:
            hist_meta = [self._metadata(rows, int(i), ids, csls[int(i)]) for i in target]
            wormhole = _find_wormhole_sparks(x_iso[target], hist_meta, q_doc,
                                             query_event_id or 'current_query', recent_vecs, recent_ids)
        bridges = []
        bridge_base = (_norm_rows(np.sum(np.stack(recent_vecs), axis=0, keepdims=True))[0]
                       if regime == 'orbiting_loop' and recent_vecs else q_doc)
        abstract_indices = [i for i, eid in enumerate(ids)
                            if eid.startswith(('derived/b1/', 'derived/note/', 'derived/lesson/'))]
        if not explicit_lookup and abstract_indices and target.size:
            abstract = np.asarray(abstract_indices, dtype=int)
            abstract_scores = x_iso[abstract] @ bridge_base
            z_top, _, _ = _salience_z_bg(abstract_scores, top_exclude=10)
            if z_top >= 1.8:
                for local in np.argsort(-abstract_scores)[:2]:
                    bridge_idx = int(abstract[local])
                    via_vec = x_iso[bridge_idx]
                    via_residual = self._residual(via_vec, basis)
                    if np.linalg.norm(via_residual) < 0.12:
                        continue
                    via_perp = _norm_rows(via_residual[None, :])[0]
                    target_scores = x_iso[target] @ via_perp
                    z_bridge, mu, std = _salience_z_bg(target_scores)
                    if z_bridge < 2.0:
                        continue
                    direct_cut = float(np.quantile(sims[target], 0.75))
                    for target_local in np.argsort(-target_scores)[:3]:
                        idx = int(target[target_local])
                        eid = ids[idx]
                        if eid.startswith('derived/') or sims[idx] > direct_cut:
                            continue
                        z_i = (float(target_scores[target_local]) - mu) / std
                        if z_i < 1.8:
                            continue
                        bridges.append({**self._metadata(rows, idx, ids, csls[idx]),
                                        'bridge_via': ids[bridge_idx],
                                        # M-2: carry the middle node's own text hash so C# can
                                        # validate it against the active local b1/note/lesson
                                        # record without requiring it in the q_query top-K.
                                        'bridge_via_hash': rows[ids[bridge_idx]]['text_hash'],
                                        'bridge_score': round(float(target_scores[target_local]), 4),
                                        'z_score': round(z_i, 3)})
                        if len(bridges) >= 3:
                            break
                    if len(bridges) >= 3:
                        break
        scene_sparks = []
        if (not explicit_lookup and scene_raw is not None and q_perp is not None
                and basis.shape[1] and target.size):
            scene_doc = self._project(scene_raw, iso)
            scene_residual = self._residual(scene_doc, basis)
            if np.linalg.norm(scene_residual) >= 0.12:
                scene_perp = _norm_rows(scene_residual[None, :])[0]
                angle = 25.0 * np.pi / 180.0
                cone = _norm_rows((q_perp * np.cos(angle) + scene_perp * np.sin(angle))[None, :])[0]
                cone_scores = x_iso[target] @ cone
                z_top, mu, std = _salience_z_bg(cone_scores)
                if z_top >= 2.4:
                    for local in np.argsort(-cone_scores)[:2]:
                        z_i = (float(cone_scores[local]) - mu) / std
                        if z_i >= 2.2:
                            idx = int(target[local])
                            scene_sparks.append({**self._metadata(rows, idx, ids, csls[idx]),
                                                 'cone_score': round(float(cone_scores[local]), 4),
                                                 'z_score': round(z_i, 3)})
        salience_z, _, _ = _salience_z_bg(csls)
        salience_gated = (not explicit_lookup and salience_z < 2.2 and not orthogonal
                          and not wormhole and not bridges and not scene_sparks)
        dpp_ids, orth_sins = ([] , {}) if explicit_lookup else _greedy_dpp(x_iso, csls, ids)
        doc_sims = {}
        doc_quantiles = {}
        if ids:
            requested = list(dict.fromkeys((probe_event_ids or [])[:16] + ids))
            ordered = np.sort(sims)
            for eid in requested:
                if eid not in iso['id_to_idx']:
                    continue
                value = float(sims[iso['id_to_idx'][eid]])
                doc_sims[eid] = round(value, 6)
                doc_quantiles[eid] = round(float(np.searchsorted(ordered, value, side='right') / len(ids)), 6)
        return dict(salience_gated=salience_gated, salience_z=round(float(salience_z), 5),
                    geometry=dict(novelty_eta=round(eta, 5), tortuosity=round(float(tortuosity), 5),
                                  net_displacement_rad=round(displacement, 5),
                                  arc_length_rad=round(arc_length, 5), step_jump_ratio=round(jump_ratio, 5),
                                  whitening_d=iso['d'], regime=regime, dpp_selected_ids=dpp_ids,
                                  orth_sins=orth_sins),
                    orthogonal_sparks=orthogonal, wormhole_sparks=wormhole, bridge_sparks=bridges,
                    scene_sparks=scene_sparks, doc_sims=doc_sims, doc_quantiles=doc_quantiles)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument('--model', required=True)
    parser.add_argument('--manifest', required=True)
    parser.add_argument('--index', required=True)
    parser.add_argument('--test-mode', action='store_true')
    args = parser.parse_args()
    os.environ.update(HF_HOME=str(Path(args.index) / 'hf-cache'), HF_HUB_OFFLINE='1',
                      TRANSFORMERS_OFFLINE='1', HF_HUB_DISABLE_TELEMETRY='1',
                      HF_HUB_DISABLE_IMPLICIT_TOKEN='1', TOKENIZERS_PARALLELISM='false',
                      OMP_NUM_THREADS='2', OPENBLAS_NUM_THREADS='2')
    sys.stdout.reconfigure(encoding='utf-8')
    sys.stdin.reconfigure(encoding='utf-8')
    sys.stderr.reconfigure(encoding='utf-8')
    socket.socket.connect = lambda *a, **k: (_ for _ in ()).throw(
        RuntimeError('local semantic worker has no network permission'))
    sha = lambda b: hashlib.sha256(b).hexdigest()

    def emit(value):
        print(json.dumps(value, ensure_ascii=False, separators=(',', ':')), flush=True)

    started = time.perf_counter()
    model_dir = Path(args.model).resolve()
    index_dir = Path(args.index).resolve()
    index_dir.mkdir(parents=True, exist_ok=True)
    manifest = json.loads(Path(args.manifest).read_text(encoding='utf-8'))
    revision = manifest['revision']
    rules = 'bge-zh-384-overlap48-cls-normalized-v1'
    assert revision == MODEL_REVISION
    for item in manifest['files']:
        file = model_dir / item['file']
        assert file.is_relative_to(model_dir) and sha(file.read_bytes()) == item['sha256'], 'model identity mismatch'
    import torch
    import psutil
    from sentence_transformers import SentenceTransformer
    torch.set_num_threads(2)
    torch.set_num_interop_threads(1)
    model = SentenceTransformer(str(model_dir), device='cpu', local_files_only=True,
                                trust_remote_code=False, token=False,
                                cache_folder=str(index_dir / 'hf-cache'))
    prefix = QUERY_PREFIX
    rows = {}
    generation = encoded = queries = 0
    geometry_engine = SemanticGeometryEngine()

    def windows(text, query=False):
        offsets = model.tokenizer(text, add_special_tokens=False, return_offsets_mapping=True,
                                  verbose=False)['offset_mapping']
        count = len(offsets)
        result = []
        for start in ([0] if count <= 384 else range(0, count, 336)):
            end = min(start + 384, count)
            a = offsets[start][0] if count else 0
            b = offsets[end - 1][1] if count else 0
            piece = text[a:b] if count > 384 else text
            value = (prefix if query else '') + piece
            assert len(model.tokenizer(value, add_special_tokens=True, verbose=False)['input_ids']) <= 512
            result.append({'text': value, 'chunk_start': start, 'chunk_end': end,
                           'char_start': a, 'char_end': b})
            if end >= count:
                break
        return result

    def encode(texts):
        return model.encode(texts, batch_size=16, normalize_embeddings=True,
                            convert_to_numpy=True, show_progress_bar=False).astype(np.float32)

    def status():
        memory = psutil.Process().memory_info()
        return dict(model_revision=revision, index_generation=generation, records=len(rows),
                    chunks=sum(len(r['windows']) for r in rows.values()), encoded_records=encoded,
                    query_count=queries, rss_bytes=memory.rss,
                    peak_working_set_bytes=getattr(memory, 'peak_wset', memory.rss), pid=os.getpid())

    cache_state = 'missing'
    try:
        metadata = json.loads((index_dir / 'index.json').read_text(encoding='utf-8'))
        data = (index_dir / 'vectors.npy').read_bytes()
        assert (metadata['model_revision'] == revision and metadata['rules'] == rules
                and metadata['matrix_sha256'] == sha(data))
        matrix = np.load(index_dir / 'vectors.npy', allow_pickle=False)
        assert matrix.ndim == 2 and matrix.shape[1] == 512 and matrix.dtype == np.float32
        for row in metadata['rows']:
            a = row['offset']
            b = a + len(row['windows'])
            assert 0 <= a <= b <= len(matrix)
            rows[row['event_id']] = {'text_hash': row['text_hash'], 'windows': row['windows'],
                                     'vectors': matrix[a:b].copy()}
        generation = metadata['index_generation']
        cache_state = 'loaded'
        del matrix, data, metadata
    except FileNotFoundError:
        pass
    except Exception:
        rows.clear()
        cache_state = 'invalid_rebuild_required'
    gc.collect()
    emit(dict(event='ready', load_ms=(time.perf_counter() - started) * 1000,
              index_cache=cache_state, **status()))
    vector_cache = {r['text_hash']: (r['windows'], r['vectors']) for r in rows.values()}
    for line in sys.stdin:
        request = {}
        started_request = time.perf_counter()
        try:
            request = json.loads(line)
            request_id = request['request_id']
            op = request['op']
            reply = {'request_id': request_id, 'op': op}
            if op == 'shutdown':
                emit(dict(reply, status='bye', **status()))
                break
            if op == 'status':
                gc.collect()
                emit(dict(reply, status='ok', **status()))
                continue
            if op == 'test_exit' and args.test_mode:
                os._exit(19)
            if request.get('delay_ms') and args.test_mode:
                time.sleep(min(request['delay_ms'], 1000) / 1000)
            if op == 'upsert':
                if len(request['records']) > 16:
                    raise ValueError('bounded upsert batch exceeded')
                pending = []
                changed = 0
                changed_ids = []
                overwritten = False
                for record in request['records']:
                    if (record.get('delivery') not in ['received', 'delivered']
                            or record.get('role') not in ['user', 'assistant']
                            or record.get('native_id', 1) <= 0 or record.get('suppressed', False)):
                        continue
                    text = record['text']
                    digest = sha(text.encode('utf-8'))
                    if record.get('text_hash', digest) != digest:
                        raise ValueError('source hash mismatch')
                    event_id = record['event_id']
                    if event_id in rows and rows[event_id]['text_hash'] == digest:
                        continue
                    changed += 1
                    changed_ids.append(event_id)
                    overwritten = overwritten or event_id in rows
                    if digest in vector_cache:
                        chunks, vectors = vector_cache[digest]
                        rows[event_id] = {'text_hash': digest, 'windows': chunks, 'vectors': vectors.copy()}
                        continue
                    chunks = windows(text)
                    pending.append((event_id, digest, chunks))
                if pending:
                    vectors = encode([c['text'] for _, _, w in pending for c in w])
                    offset = 0
                    for event, digest, chunks in pending:
                        cleaned = [{k: v for k, v in c.items() if k != 'text'} for c in chunks]
                        rows[event] = {'text_hash': digest, 'windows': cleaned,
                                       'vectors': vectors[offset:offset + len(chunks)].copy()}
                        offset += len(chunks)
                        vector_cache[digest] = (cleaned, rows[event]['vectors'])
                    encoded += len(pending)
                if changed:
                    generation += 1
                    geometry_engine.after_upsert(rows, changed_ids, overwritten)
                emit(dict(reply, status='ok', changed=changed,
                          elapsed_ms=(time.perf_counter() - started_request) * 1000, **status()))
                continue
            if op == 'prune':
                keep = set(request['event_ids'])
                gone = [event_id for event_id in rows if event_id not in keep]
                for event_id in gone:
                    del rows[event_id]
                used = {r['text_hash'] for r in rows.values()}
                vector_cache = {k: v for k, v in vector_cache.items() if k in used}
                if gone:
                    generation += 1
                    geometry_engine.after_prune(len(gone))
                emit(dict(reply, status='ok', removed=len(gone), **status()))
                continue
            if op == 'commit':
                geometry_ms = None
                if geometry_engine.iso is not None or request.get('enable_geometry'):
                    geometry_started = time.perf_counter()
                    geometry_engine.ensure(rows)
                    geometry_ms = (time.perf_counter() - geometry_started) * 1000
                metadata = []
                parts = []
                offset = 0
                for event, row in rows.items():
                    metadata.append(dict(event_id=event, text_hash=row['text_hash'],
                                         windows=row['windows'], offset=offset))
                    parts.append(row['vectors'])
                    offset += len(row['windows'])
                matrix = np.vstack(parts) if parts else np.zeros((0, 512), dtype=np.float32)
                with (index_dir / 'vectors.next.npy').open('wb') as stream:
                    np.save(stream, matrix, allow_pickle=False)
                meta = dict(model_revision=revision, rules=rules, index_generation=generation,
                            rows=metadata, matrix_sha256=sha((index_dir / 'vectors.next.npy').read_bytes()))
                (index_dir / 'index.next.json').write_text(json.dumps(meta, ensure_ascii=False), encoding='utf-8')
                os.replace(index_dir / 'vectors.next.npy', index_dir / 'vectors.npy')
                os.replace(index_dir / 'index.next.json', index_dir / 'index.json')
                del matrix
                gc.collect()
                extra = (dict(elapsed_ms=(time.perf_counter() - started_request) * 1000,
                              geometry_ms=geometry_ms) if request.get('enable_geometry') else {})
                emit(dict(reply, status='ok', **extra, **status()))
                continue
            if op == 'query':
                text = request['text']
                query_hash = sha(text.encode('utf-8'))
                if (query_hash != request['query_hash'] or request['model_revision'] != revision
                        or request['index_generation'] != generation):
                    emit(dict(reply, status='identity_mismatch', query_hash=query_hash, **status()))
                    continue
                enable_geometry = request.get('enable_geometry', False) is True
                encode_started = time.perf_counter()
                if enable_geometry:
                    query_chunks = windows(text, True)
                    doc_chunks = windows(text)
                    scene_text = str(request.get('scene_text') or '')[:96]
                    scene_chunks = windows(scene_text) if scene_text.strip() else []
                    # Exactly one model call: prefixed direct lookup, raw doc and raw scene.
                    encoded_query = encode([c['text'] for c in query_chunks + doc_chunks + scene_chunks])
                    q_query = encoded_query[:len(query_chunks)]
                    q_doc_raw = encoded_query[len(query_chunks):len(query_chunks) + len(doc_chunks)]
                    scene_raw = (np.mean(encoded_query[len(query_chunks) + len(doc_chunks):], axis=0)
                                 if scene_chunks else None)
                else:
                    q_query = encode([c['text'] for c in windows(text, True)])
                encode_ms = (time.perf_counter() - encode_started) * 1000
                geometry_started = time.perf_counter()
                query_rows = []
                # Full EventId ranking, no pre-mask top24 and no similarity floor.
                for event, row in rows.items():
                    score = np.max(q_query @ row['vectors'].T, axis=0)
                    best = int(np.argmax(score))
                    query_rows.append(dict(event_id=event, text_hash=row['text_hash'],
                                           score=float(score[best]), **row['windows'][best]))
                query_rows.sort(key=lambda r: -r['score'])
                queries += 1
                extra = {}
                if enable_geometry:
                    probes = [eid for eid in dict.fromkeys(request.get('probe_event_ids') or [])
                              if isinstance(eid, str)][:16]
                    recent = [eid for eid in request.get('recent_event_ids') or [] if isinstance(eid, str)]
                    extra = geometry_engine.query(
                        rows, q_doc_raw, recent, scene_raw=scene_raw, probe_event_ids=probes,
                        query_event_id=str(request.get('query_event_id') or ''),
                        explicit_lookup=request.get('explicit_lookup', False) is True)
                    extra['encode_ms'] = encode_ms
                    extra['geometry_ms'] = (time.perf_counter() - geometry_started) * 1000
                emit(dict(reply, status='ok', query_hash=query_hash,
                          conversation_generation=request['conversation_generation'],
                          candidates=query_rows, elapsed_ms=(time.perf_counter() - started_request) * 1000,
                          **extra, **status()))
                continue
            raise ValueError('unknown operation')
        except Exception as exc:
            # Never log source/query text, keys or stack traces.
            emit(dict(request_id=request.get('request_id', ''), status='error',
                      error=type(exc).__name__, **status()))
            print('semantic_worker_error ' + type(exc).__name__, file=sys.stderr, flush=True)


if __name__ == '__main__':
    main()
