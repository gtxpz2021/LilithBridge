"""Private JSONL child process; no listener, credential, game object or service."""
from pathlib import Path
import argparse,os,sys,json,hashlib,time,gc,socket
p=argparse.ArgumentParser();p.add_argument('--model',required=True);p.add_argument('--manifest',required=True);p.add_argument('--index',required=True);p.add_argument('--test-mode',action='store_true');args=p.parse_args()
os.environ.update(HF_HOME=str(Path(args.index)/'hf-cache'),HF_HUB_OFFLINE='1',TRANSFORMERS_OFFLINE='1',HF_HUB_DISABLE_TELEMETRY='1',HF_HUB_DISABLE_IMPLICIT_TOKEN='1',TOKENIZERS_PARALLELISM='false',OMP_NUM_THREADS='2',OPENBLAS_NUM_THREADS='2')
sys.stdout.reconfigure(encoding='utf-8');sys.stdin.reconfigure(encoding='utf-8');sys.stderr.reconfigure(encoding='utf-8')
socket.socket.connect=lambda *a,**k:(_ for _ in ()).throw(RuntimeError('local semantic worker has no network permission'))
sha=lambda b:hashlib.sha256(b).hexdigest()
def emit(value):print(json.dumps(value,ensure_ascii=False,separators=(',',':')),flush=True)
started=time.perf_counter();model_dir=Path(args.model).resolve();index_dir=Path(args.index).resolve();index_dir.mkdir(parents=True,exist_ok=True)
manifest=json.loads(Path(args.manifest).read_text(encoding='utf-8'));revision=manifest['revision'];rules='bge-zh-384-overlap48-cls-normalized-v1'
assert revision=='7999e1d3359715c523056ef9478215996d62a620'
for f in manifest['files']:
    file=model_dir/f['file'];assert file.is_relative_to(model_dir) and sha(file.read_bytes())==f['sha256'],'model identity mismatch'
import numpy as np,torch,psutil
from sentence_transformers import SentenceTransformer
torch.set_num_threads(2);torch.set_num_interop_threads(1)
model=SentenceTransformer(str(model_dir),device='cpu',local_files_only=True,trust_remote_code=False,token=False,cache_folder=str(index_dir/'hf-cache'))
prefix='为这个句子生成表示以用于检索相关文章：';rows={};generation=0;encoded=0;queries=0
def windows(text,query=False):
    offsets=model.tokenizer(text,add_special_tokens=False,return_offsets_mapping=True,verbose=False)['offset_mapping'];count=len(offsets)
    result=[]
    for start in ([0] if count<=384 else range(0,count,336)):
        end=min(start+384,count);a=offsets[start][0] if count else 0;b=offsets[end-1][1] if count else 0
        piece=text[a:b] if count>384 else text;value=(prefix if query else '')+piece
        assert len(model.tokenizer(value,add_special_tokens=True,verbose=False)['input_ids'])<=512
        result.append({'text':value,'chunk_start':start,'chunk_end':end,'char_start':a,'char_end':b})
        if end>=count:break
    return result
def encode(texts):return model.encode(texts,batch_size=16,normalize_embeddings=True,convert_to_numpy=True,show_progress_bar=False).astype(np.float32)
def status():
    m=psutil.Process().memory_info()
    return dict(model_revision=revision,index_generation=generation,records=len(rows),chunks=sum(len(r['windows']) for r in rows.values()),encoded_records=encoded,query_count=queries,rss_bytes=m.rss,peak_working_set_bytes=getattr(m,'peak_wset',m.rss),pid=os.getpid())
cache_state='missing'
try:
    metadata=json.loads((index_dir/'index.json').read_text(encoding='utf-8'));data=(index_dir/'vectors.npy').read_bytes()
    assert metadata['model_revision']==revision and metadata['rules']==rules and metadata['matrix_sha256']==sha(data)
    matrix=np.load(index_dir/'vectors.npy',allow_pickle=False);assert matrix.ndim==2 and matrix.shape[1]==512 and matrix.dtype==np.float32
    for row in metadata['rows']:
        a=row['offset'];b=a+len(row['windows']);assert 0<=a<=b<=len(matrix)
        rows[row['event_id']]={'text_hash':row['text_hash'],'windows':row['windows'],'vectors':matrix[a:b].copy()}
    generation=metadata['index_generation'];cache_state='loaded';del matrix,data,metadata
except FileNotFoundError:pass
except Exception:
    rows.clear();cache_state='invalid_rebuild_required'
gc.collect();emit(dict(event='ready',load_ms=(time.perf_counter()-started)*1000,index_cache=cache_state,**status()))
vector_cache={r['text_hash']:(r['windows'],r['vectors']) for r in rows.values()}
for line in sys.stdin:
    request={};t=time.perf_counter()
    try:
        request=json.loads(line);id=request['request_id'];op=request['op'];reply={'request_id':id,'op':op}
        if op=='shutdown':emit(dict(reply,status='bye',**status()));break
        if op=='status':gc.collect();emit(dict(reply,status='ok',**status()));continue
        if op=='test_exit' and args.test_mode:os._exit(19)
        if request.get('delay_ms') and args.test_mode:time.sleep(min(request['delay_ms'],1000)/1000)
        if op=='upsert':
            if len(request['records'])>16:raise ValueError('bounded upsert batch exceeded')
            pending=[];changed=0
            for r in request['records']:
                if r.get('delivery') not in ['received','delivered'] or r.get('role') not in ['user','assistant'] or r.get('native_id',1)<=0 or r.get('suppressed',False):continue
                text=r['text'];digest=sha(text.encode('utf-8'))
                if r.get('text_hash',digest)!=digest:raise ValueError('source hash mismatch')
                if r['event_id'] in rows and rows[r['event_id']]['text_hash']==digest:continue
                changed+=1
                if digest in vector_cache:
                    chunks,vectors=vector_cache[digest];rows[r['event_id']]={'text_hash':digest,'windows':chunks,'vectors':vectors.copy()};continue
                chunks=windows(text);pending.append((r['event_id'],digest,chunks))
            if pending:
                vectors=encode([c['text'] for _,_,w in pending for c in w]);offset=0
                for event,digest,chunks in pending:
                    cleaned=[{k:v for k,v in c.items() if k!='text'} for c in chunks]
                    rows[event]={'text_hash':digest,'windows':cleaned,'vectors':vectors[offset:offset+len(chunks)].copy()};offset+=len(chunks)
                    vector_cache[digest]=(cleaned,rows[event]['vectors'])
                encoded+=len(pending)
            if changed:generation+=1
            emit(dict(reply,status='ok',changed=changed,elapsed_ms=(time.perf_counter()-t)*1000,**status()));continue
        if op=='prune':
            keep=set(request['event_ids']);gone=[id for id in rows if id not in keep]
            for id in gone:del rows[id]
            used={r['text_hash'] for r in rows.values()};vector_cache={k:v for k,v in vector_cache.items() if k in used}
            if gone:generation+=1
            emit(dict(reply,status='ok',removed=len(gone),**status()));continue
        if op=='commit':
            metadata=[];parts=[];offset=0
            for event,r in rows.items():
                metadata.append(dict(event_id=event,text_hash=r['text_hash'],windows=r['windows'],offset=offset));parts.append(r['vectors']);offset+=len(r['windows'])
            matrix=np.vstack(parts) if parts else np.zeros((0,512),dtype=np.float32)
            with (index_dir/'vectors.next.npy').open('wb') as f:np.save(f,matrix,allow_pickle=False)
            meta=dict(model_revision=revision,rules=rules,index_generation=generation,rows=metadata,matrix_sha256=sha((index_dir/'vectors.next.npy').read_bytes()))
            (index_dir/'index.next.json').write_text(json.dumps(meta,ensure_ascii=False),encoding='utf-8')
            os.replace(index_dir/'vectors.next.npy',index_dir/'vectors.npy');os.replace(index_dir/'index.next.json',index_dir/'index.json');del matrix
            gc.collect();emit(dict(reply,status='ok',**status()));continue
        if op=='query':
            text=request['text'];query_hash=sha(text.encode('utf-8'))
            if query_hash!=request['query_hash'] or request['model_revision']!=revision or request['index_generation']!=generation:
                emit(dict(reply,status='identity_mismatch',query_hash=query_hash,**status()));continue
            q=encode([c['text'] for c in windows(text,True)]);query_rows=[]
            # Full EventId ranking, no pre-mask top24 and no similarity floor.
            for event,r in rows.items():
                score=np.max(q @ r['vectors'].T,axis=0);i=int(np.argmax(score));query_rows.append(dict(event_id=event,text_hash=r['text_hash'],score=float(score[i]),**r['windows'][i]))
            query_rows.sort(key=lambda r:-r['score']);queries+=1
            emit(dict(reply,status='ok',query_hash=query_hash,conversation_generation=request['conversation_generation'],candidates=query_rows,elapsed_ms=(time.perf_counter()-t)*1000,**status()));continue
        raise ValueError('unknown operation')
    except Exception as exc:
        # No source text, query, key or stack is logged as part of a failure.
        emit(dict(request_id=request.get('request_id',''),status='error',error=type(exc).__name__,**status()))
        print('semantic_worker_error '+type(exc).__name__,file=sys.stderr,flush=True)
