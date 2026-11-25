"""Offline unit coverage for WP-1; no model weights, archive or golden fixture writes."""
from pathlib import Path
import hashlib
import importlib.util
import math
import tempfile
import time
import unittest

import numpy as np

MODULE_PATH = Path(__file__).with_name('semantic_worker.py')
SPEC = importlib.util.spec_from_file_location('semantic_worker_geometry', MODULE_PATH)
WORKER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(WORKER)


def unit(value):
    array = np.asarray(value, dtype=np.float32)
    return array / max(float(np.linalg.norm(array)), 1e-8)


def row(vectors):
    vectors = np.asarray(vectors, dtype=np.float32)
    if vectors.ndim == 1:
        vectors = vectors[None, :]
    vectors = WORKER._norm_rows(vectors)
    windows = []
    offset = 0
    for vector in vectors:
        length = len(vector)
        windows.append(dict(chunk_start=offset, chunk_end=offset + length,
                            char_start=offset, char_end=offset + length))
        offset += length
    return dict(text_hash=hashlib.sha256(vectors.tobytes()).hexdigest(),
                vectors=vectors, windows=windows)


def rows_for(vectors, prefix='ev'):
    return {f'{prefix}{i}': row(v) for i, v in enumerate(vectors)}


def angle(a, b):
    return float(math.acos(np.clip(float(np.dot(unit(a), unit(b))), -1.0, 1.0)))


class SemanticGeometryTests(unittest.TestCase):
    def test_prefix_isolation_parity(self):
        raw = unit([1.0, 0.0, 0.0, 0.0])
        prefixed = unit([math.cos(0.34), math.sin(0.34), 0.0, 0.0])
        rows = rows_for([raw])
        engine = WORKER.SemanticGeometryEngine()
        result = engine.query(rows, raw, [], probe_event_ids=['ev0'], query_event_id='current')
        self.assertLess(angle(raw, rows['ev0']['vectors'][0]), 0.05)
        self.assertGreater(angle(prefixed, rows['ev0']['vectors'][0]), 0.30)
        self.assertAlmostEqual(result['doc_sims']['ev0'], 1.0, places=4)
        self.assertNotAlmostEqual(result['doc_sims']['ev0'], float(np.dot(prefixed, raw)), places=3)

    def test_tiered_whitening_boundaries_and_multiwindow_mean(self):
        rng = np.random.default_rng(19)
        with tempfile.TemporaryDirectory() as tmp:
            disk = Path(tmp) / 'vectors.npy'
            original = rng.normal(size=(3, 16)).astype(np.float32)
            np.save(disk, original, allow_pickle=False)
            before = hashlib.sha256(disk.read_bytes()).hexdigest()
            for count, expected in ((30, 0), (100, 1), (250, 3)):
                rows = rows_for(rng.normal(size=(count, 16)), prefix=f'n{count}-')
                engine = WORKER.SemanticGeometryEngine()
                self.assertEqual(engine.ensure(rows)['d'], expected)
            self.assertEqual(hashlib.sha256(disk.read_bytes()).hexdigest(), before)
        first = unit([1, 0, 0, 0])
        second = unit([0, 1, 0, 0])
        rows = {'long': row(np.stack([first, second]))}
        engine = WORKER.SemanticGeometryEngine()
        iso = engine.ensure(rows)
        expected = unit((first + second) / 2)
        self.assertLess(angle(iso['x_iso'][0], expected), 1e-5)
        self.assertGreater(angle(iso['x_iso'][0], first), 0.70)

    def test_incremental_o_n_commit_parity_overwrite_and_speed(self):
        rng = np.random.default_rng(23)
        vectors = WORKER._norm_rows(rng.normal(size=(90, 24)).astype(np.float32))
        rows = rows_for(vectors[:30])
        engine = WORKER.SemanticGeometryEngine()
        engine.ensure(rows)
        rows['ev30'] = row(vectors[30])
        started = time.perf_counter()
        engine.after_upsert(rows, ['ev30'], False)
        elapsed_ms = (time.perf_counter() - started) * 1000
        self.assertFalse(engine.dirty)
        self.assertLess(elapsed_ms, 10.0)
        incremental = np.array(engine.iso['r_k'], copy=True)
        rebuilt = WORKER._build_iso_state_chunked(vectors[:31], [f'ev{i}' for i in range(31)])
        self.assertLess(float(np.max(np.abs(incremental - rebuilt['r_k']))), 1e-4)
        rows['ev4'] = row(unit(rng.normal(size=24)))
        engine.after_upsert(rows, ['ev4'], True)
        self.assertTrue(engine.dirty)
        engine.ensure(rows)
        self.assertEqual(len(engine.iso['id_to_idx']), len(rows))
        self.assertEqual(engine.iso['x_iso'].shape[0], len(rows))
        boundary_rows = rows_for(vectors[:63], prefix='b')
        boundary = WORKER.SemanticGeometryEngine()
        boundary.ensure(boundary_rows)
        boundary_rows['b63'] = row(vectors[63])
        boundary.after_upsert(boundary_rows, ['b63'], False)
        self.assertTrue(boundary.dirty)
        boundary.ensure(boundary_rows)
        self.assertEqual(boundary.iso['d'], 1)
        self.assertEqual(boundary.iso['x_iso'].shape[0], 64)
        del boundary_rows['b2']
        boundary.after_prune(1)
        self.assertTrue(boundary.dirty)
        boundary.ensure(boundary_rows)
        self.assertNotIn('b2', boundary.iso['id_to_idx'])
        self.assertEqual(boundary.iso['x_iso'].shape[0], 63)
        capped = WORKER.SemanticGeometryEngine()
        capped_rows = rows_for(vectors[:5], prefix='c')
        capped.ensure(capped_rows)
        capped.iso['since_rebuild'] = 40
        capped_rows['c5'] = row(vectors[5])
        capped.after_upsert(capped_rows, ['c5'], False)
        self.assertTrue(capped.dirty)

    def test_qr_annihilates_oscillating_subspace(self):
        a = unit([1, 0, 0, 0, 0, 0])
        b = unit([0, 1, 0, 0, 0, 0])
        c = unit([0, 0, 1, 0, 0, 0])
        recent = rows_for([a, b, a, b], prefix='recent')
        noise = [unit([0, 0, 0.01 * (i + 1), 1, 0.2, 0.1]) for i in range(9)]
        targets = rows_for([c, -c, unit([0, 0, 0, 1, 0, 0]),
                            unit([0, 0, 0, 0, 1, 0]), unit([0, 0, 0, 0, 0, 1])] + noise,
                           prefix='target')
        rows = {**recent, **targets}
        result = WORKER.SemanticGeometryEngine().query(rows, c, list(recent), query_event_id='now')
        self.assertGreaterEqual(result['geometry']['novelty_eta'], 0.99)
        self.assertEqual(result['orthogonal_sparks'][0]['event_id'], 'target0')

    def test_triangle_completion_wormhole(self):
        q_doc = unit([1, 0, 0])
        prior = unit([0, 1, 0])
        intersection = unit([1, 1, 0])
        hist = [intersection] + [-q_doc] * 5 + [-prior] * 5 + [unit([0, 0, 1])]
        meta = [dict(event_id=f'h{i}', text_hash='x', chunk_start=0, chunk_end=1) for i in range(len(hist))]
        hits = WORKER._find_wormhole_sparks(np.stack(hist), meta, q_doc, 'current-event', [prior], ['prior-event'])
        self.assertTrue(hits)
        self.assertEqual(hits[0]['anchor_a'], 'current-event')
        self.assertEqual(hits[0]['anchor_b'], 'prior-event')
        self.assertEqual(WORKER._find_wormhole_sparks(np.stack(hist), meta, q_doc, 'current-event',
                                                     [q_doc, q_doc], ['old-a', 'old-b']), [])

    def test_salience_gate_silence_and_non_paradox(self):
        q_doc = unit([1, 0, 0, 0])
        silent_rows = rows_for([unit([0, 1, 0, 0])] * 48)
        silent = WORKER.SemanticGeometryEngine().query(silent_rows, q_doc, [])
        self.assertTrue(silent['salience_gated'])
        self.assertFalse(silent['orthogonal_sparks'] or silent['wormhole_sparks']
                         or silent['bridge_sparks'] or silent['scene_sparks'])
        good = [unit([1, 0.01 * (i + 1), 0, 0]) for i in range(25)]
        noise = [unit([0, 0, 1 + i * 0.01, 1]) for i in range(30)]
        strong = WORKER.SemanticGeometryEngine().query(rows_for(good + noise), q_doc, [])
        self.assertGreaterEqual(strong['salience_z'], 2.2)
        self.assertFalse(strong['salience_gated'])

    def test_greedy_dpp_suppresses_collinear_clones(self):
        vectors = np.stack([unit([1, 0, 0]), unit([1, 0.01, 0]), unit([1, 0.02, 0]),
                            unit([1, 0.03, 0]), unit([1, 0.04, 0]), unit([0, 1, 0])])
        scores = np.asarray([1.0, 0.99, 0.98, 0.97, 0.96, 0.80], dtype=np.float32)
        ids = [f'ev{i}' for i in range(6)]
        selected, _ = WORKER._greedy_dpp(vectors, scores, ids)
        self.assertEqual(selected[0], 'ev0')
        self.assertEqual(selected[1], 'ev5')

    def test_greedy_dpp_handles_all_negative_csls(self):
        # H-4: all-negative CSLS must still yield an ordered, non-empty selection.
        eye = np.eye(4, dtype=np.float32)
        scores = np.asarray([-0.8, -0.5, -0.3, -0.2], dtype=np.float32)
        ids = [f'ev{i}' for i in range(4)]
        selected, orth_sins = WORKER._greedy_dpp(eye, scores, ids)
        self.assertTrue(selected)
        self.assertEqual(selected[0], 'ev3')
        self.assertEqual(len(selected), len(set(selected)))
        self.assertTrue(all(eid in orth_sins for eid in selected))

    def test_two_hop_b1_bridge_scene_cone_and_probe(self):
        dimension = 8
        eye = np.eye(dimension, dtype=np.float32)
        recent = rows_for([eye[6], eye[7], eye[6], eye[7]], prefix='recent')
        targets = rows_for([eye[2], eye[3], -eye[2], -eye[3], eye[1], -eye[1],
                            unit(eye[1] + eye[4]), unit(eye[1] - eye[4]),
                            unit(-eye[2] + eye[4]), unit(-eye[3] + eye[4]), -eye[0]], prefix='target')
        abstracts = {
            'derived/b1/via': row(unit(eye[0] + eye[2])),
            'derived/note/other': row(-eye[0]),
            'derived/note/other2': row(-eye[0]),
            'derived/lesson/other3': row(-eye[0]),
            'derived/note/other4': row(-eye[0]),
            'derived/lesson/other5': row(-eye[0]),
            'derived/generated_reply/hidden': row(eye[2]),
        }
        rows = {**recent, **targets, **abstracts}
        engine = WORKER.SemanticGeometryEngine()
        result = engine.query(rows, eye[0], list(recent), scene_raw=eye[3],
                              probe_event_ids=['target10'], query_event_id='current')
        self.assertIn('target10', result['doc_sims'])
        self.assertIn('target10', result['doc_quantiles'])
        self.assertLessEqual(result['doc_quantiles']['target10'], 0.35)
        via_sparks = [s for s in result['bridge_sparks'] if s['bridge_via'] == 'derived/b1/via']
        self.assertTrue(via_sparks)
        # M-2: the middle node's own text hash must travel with the spark so C#
        # can validate it against the active local b1/note/lesson record even
        # when it never entered the q_query direct Top-K.
        self.assertTrue(all(s.get('bridge_via_hash') == rows['derived/b1/via']['text_hash']
                            for s in via_sparks))
        scene_targets = rows_for([eye[3], unit(eye[3] + eye[4]), unit(eye[3] - eye[4]),
                                  unit(eye[3] + eye[5]), eye[5], eye[2], -eye[1],
                                  eye[1], eye[2], eye[0]], prefix='scene')
        scene_quiet = rows_for([eye[5]] * 20, prefix='quiet')
        scene_rows = {**recent, **scene_targets, **scene_quiet}
        scene_result = WORKER.SemanticGeometryEngine().query(
            scene_rows, eye[0], list(recent), scene_raw=eye[3], query_event_id='scene9')
        self.assertTrue(scene_result['scene_sparks'])
        self.assertGreaterEqual(scene_result['scene_sparks'][0]['cone_score'],
                                scene_result['scene_sparks'][-1]['cone_score'])
        self.assertTrue(any(s['event_id'] in {'scene0', 'scene1', 'scene2', 'scene3'}
                            for s in scene_result['scene_sparks']))
        self.assertTrue(all(s['event_id'] != 'derived/generated_reply/hidden'
                            for key in ('wormhole_sparks', 'bridge_sparks') for s in result[key]))

    def test_doc_sims_bounded_not_full_corpus(self):
        # H-2: a 250-event archive must not be copied wholesale into doc_sims.
        rng = np.random.default_rng(20261120)
        vectors = [unit(rng.standard_normal(8).astype(np.float32)) for _ in range(250)]
        rows = rows_for(vectors, prefix='ev')
        query_vec = vectors[7]
        probe_ids = ['ev3', 'ev111', 'ev249']
        engine = WORKER.SemanticGeometryEngine()
        reply = engine.query(rows, query_vec, [], probe_event_ids=probe_ids,
                             query_event_id='current_query')

        self.assertLess(len(reply['doc_sims']), 80)
        self.assertEqual(len(reply['doc_quantiles']), len(reply['doc_sims']))
        for eid in probe_ids:
            self.assertIn(eid, reply['doc_sims'])
            self.assertIn(eid, reply['doc_quantiles'])
            self.assertGreater(reply['doc_quantiles'][eid], 0.0)
            self.assertLessEqual(reply['doc_quantiles'][eid], 1.0)

        # Quantiles must still be exact full-corpus empirical quantiles.
        iso = engine.ensure(rows)
        ids = list(rows.keys())
        q_doc = engine._project(WORKER._event_vector(np.asarray(query_vec, dtype=np.float32)), iso)
        sims = iso['x_iso'] @ q_doc
        ordered = np.sort(sims)
        for eid in probe_ids:
            value = float(sims[iso['id_to_idx'][eid]])
            expected = float(np.searchsorted(ordered, value, side='right') / len(ids))
            self.assertAlmostEqual(reply['doc_quantiles'][eid], expected, delta=1e-5)

    def test_two_subspace_csls_immunity_and_dpp_purity(self):
        rng = np.random.default_rng(20261125)
        base_vecs = [unit(rng.standard_normal(16).astype(np.float32)) for _ in range(40)]
        exp_rows = {f'ev{i}': {**row(v), 'kind': 'utterance', 'lifecycle': 'active'}
                    for i, v in enumerate(base_vecs)}
        msg_vec = unit(base_vecs[0] * 0.85 + base_vecs[1] * 0.15)
        exp_rows['derived/message/message/m0'] = {**row(msg_vec), 'kind': 'message', 'lifecycle': 'active'}

        pure_engine = WORKER.SemanticGeometryEngine()
        pure_iso = pure_engine.ensure(exp_rows)
        pure_rk_ev0 = float(pure_iso['r_k'][pure_iso['id_to_idx']['ev0']])

        # Cluster 12 derived notes/lessons/readings/generated_replies tightly around ev0.
        mixed_rows = dict(exp_rows)
        for j in range(12):
            jitter = unit(base_vecs[0] + 0.002 * (j + 1) * base_vecs[2])
            kind = ('note', 'lesson', 'reading', 'generated_reply')[j % 4]
            mixed_rows[f'derived/{kind}/d{j}'] = {
                **row(jitter), 'kind': kind, 'lifecycle': 'active'
            }

        mixed_engine = WORKER.SemanticGeometryEngine()
        mixed_iso = mixed_engine.ensure(mixed_rows)
        mixed_rk_ev0 = float(mixed_iso['r_k'][mixed_iso['id_to_idx']['ev0']])
        self.assertAlmostEqual(pure_rk_ev0, mixed_rk_ev0, places=6)

        reply = mixed_engine.query(mixed_rows, base_vecs[0], [], query_event_id='now')
        dpp_ids = reply['geometry']['dpp_selected_ids']
        self.assertTrue(dpp_ids)
        self.assertIn('derived/message/message/m0', dpp_ids)
        for eid in dpp_ids:
            self.assertIn(WORKER._infer_kind(eid, mixed_rows[eid]), ('utterance', 'message'))

    def test_settled_notes_excluded_from_x_abs_bridge_relays(self):
        dimension = 8
        eye = np.eye(dimension, dtype=np.float32)
        recent = rows_for([eye[6], eye[7], eye[6], eye[7]], prefix='recent')
        targets = rows_for([eye[2], eye[3], -eye[2], -eye[3], eye[1], -eye[1],
                            unit(eye[1] + eye[4]), unit(eye[1] - eye[4]),
                            unit(-eye[2] + eye[4]), unit(-eye[3] + eye[4]), -eye[0]], prefix='target')
        settled_abstracts = {
            'derived/note/shelved_via': {**row(unit(eye[0] + eye[2])), 'kind': 'note', 'lifecycle': 'settled'},
            'derived/note/other1': {**row(-eye[0]), 'kind': 'note', 'lifecycle': 'active'},
            'derived/note/other2': {**row(-eye[0]), 'kind': 'note', 'lifecycle': 'active'},
            'derived/lesson/other3': {**row(-eye[0]), 'kind': 'lesson', 'lifecycle': 'active'},
            'derived/note/other4': {**row(-eye[0]), 'kind': 'note', 'lifecycle': 'active'},
            'derived/lesson/other5': {**row(-eye[0]), 'kind': 'lesson', 'lifecycle': 'active'},
        }
        rows_settled = {**recent, **targets, **settled_abstracts}
        res_settled = WORKER.SemanticGeometryEngine().query(
            rows_settled, eye[0], list(recent), query_event_id='current')
        self.assertEqual([s for s in res_settled['bridge_sparks']
                          if s['bridge_via'] == 'derived/note/shelved_via'], [])

        # Re-activating the same note immediately restores its bridge eligibility.
        rows_settled['derived/note/shelved_via']['lifecycle'] = 'active'
        res_active = WORKER.SemanticGeometryEngine().query(
            rows_settled, eye[0], list(recent), query_event_id='current')
        self.assertTrue(any(s['bridge_via'] == 'derived/note/shelved_via'
                            for s in res_active['bridge_sparks']))

    def test_legacy_backward_compatibility(self):
        # The geometry block is optional and isolated behind an exact True flag.
        # No fields are appended to the legacy reply unless that branch executes.
        source = MODULE_PATH.read_text(encoding='utf-8')
        self.assertIn("enable_geometry = request.get('enable_geometry', False) is True", source)
        self.assertIn("q_query = encode([c['text'] for c in windows(text, True)])", source)
        self.assertIn('extra = {}\n                if enable_geometry:', source)
        engine = WORKER.SemanticGeometryEngine()
        self.assertIsNone(engine.iso)
        self.assertTrue(engine.dirty)


if __name__ == '__main__':
    unittest.main(verbosity=2)
