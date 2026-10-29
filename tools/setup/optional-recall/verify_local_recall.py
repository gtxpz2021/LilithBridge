"""Offline production-worker check on temporary synthetic records only."""
from pathlib import Path
import argparse, hashlib, importlib.metadata, json, os, runpy, socket, subprocess, sys, tempfile

WORKER_HASH='6ef8145e129c4399150367df4a6f41490349203c00eab898a0948eb6725d4036'
MANIFEST_HASH='c0f597c0cd4a19a54e08b3d589633b94154ea6f76df9c23365a9ff212eb07c17'
LOCK_HASH='17f66187aae50df1e0613305b0f1d8f42b2665ea499d8f1952483ca85d566891'
REVISION='7999e1d3359715c523056ef9478215996d62a620'
def digest(path):
    with Path(path).open('rb') as stream: return hashlib.file_digest(stream,'sha256').hexdigest()
def require(value):
    if not value: raise RuntimeError('Local recall verification failed')
def child():
    worker=Path(sys.argv[2]).resolve()
    require(digest(worker)==WORKER_HASH)
    def denied(*a,**k): raise RuntimeError('Offline verification')
    socket.socket.connect=denied
    socket.socket.connect_ex=denied
    socket.socket.bind=denied
    socket.socket.sendto=denied
    socket.create_connection=denied
    socket.getaddrinfo=denied
    sys.argv=[str(worker)]+sys.argv[3:]
    runpy.run_path(str(worker),run_name='__main__')
def main():
    p=argparse.ArgumentParser()
    for name in ('worker','model','work','lock'): p.add_argument('--'+name,required=True,type=Path)
    args=p.parse_args()
    require(digest(args.worker)==WORKER_HASH and digest(args.worker.parent/'model-manifest.json')==MANIFEST_HASH and digest(args.lock)==LOCK_HASH)
    rows=json.loads(args.lock.read_text(encoding='utf-8'))['wheels']
    require(len(rows)==34 and all(importlib.metadata.version(row['package'])==row['version'] for row in rows))
    manifest=json.loads((args.worker.parent/'model-manifest.json').read_text(encoding='utf-8'))
    require(len(manifest['files'])==11)
    for row in manifest['files']:
        file=args.model/row['file']
        require(file.stat().st_size==row['bytes'] and digest(file)==row['sha256'])
    args.work.mkdir(parents=True,exist_ok=True)
    text='下雨出门要带雨伞。'
    requests=[
        dict(request_id='u',op='upsert',records=[dict(event_id='SYNTH-RAIN',delivery='delivered',role='assistant',native_id=1,text='明天有暴雨，出门记得带雨伞和雨衣。')]),
        dict(request_id='c',op='commit'),
        dict(request_id='q',op='query',text=text,query_hash=hashlib.sha256(text.encode()).hexdigest(),model_revision=REVISION,index_generation=1,conversation_generation=1),
        dict(request_id='x',op='shutdown')]
    env={k:v for k,v in os.environ.items() if k.upper() in {'SYSTEMROOT','WINDIR','COMSPEC','PATH','PATHEXT','PROCESSOR_ARCHITECTURE','NUMBER_OF_PROCESSORS','TEMP','TMP'}}
    env.update(HF_HUB_OFFLINE='1',TRANSFORMERS_OFFLINE='1',HF_HUB_DISABLE_TELEMETRY='1',HF_HUB_DISABLE_IMPLICIT_TOKEN='1',TOKENIZERS_PARALLELISM='false')
    with tempfile.TemporaryDirectory(prefix='synthetic-',dir=args.work) as temp:
        command=[sys.executable,'-I','-X','utf8','-B',str(Path(__file__).resolve()),'--child',str(args.worker),'--model',str(args.model),'--manifest',str(args.worker.parent/'model-manifest.json'),'--index',temp]
        result=subprocess.run(command,input='\n'.join(json.dumps(row,ensure_ascii=False) for row in requests)+'\n',stdout=subprocess.PIPE,stderr=subprocess.PIPE,encoding='utf-8',errors='replace',timeout=150,env=env,creationflags=getattr(subprocess,'CREATE_NO_WINDOW',0))
        require(result.returncode==0)
        replies=[json.loads(line) for line in result.stdout.splitlines() if line.strip()]
        require(len(replies)==5 and replies[0]['event']=='ready' and replies[0]['records']==0)
        require([row.get('request_id') for row in replies[1:]]==['u','c','q','x'])
        require(all(row.get('status')=='ok' for row in replies[1:4]) and replies[4]['status']=='bye')
        require(replies[3]['candidates'][0]['event_id']=='SYNTH-RAIN')
        import numpy as np, torch
        matrix=np.load(Path(temp)/'vectors.npy',allow_pickle=False)
        require(matrix.shape==(1,512) and np.isfinite(matrix).all() and torch.version.cuda is None)
        require((torch.tensor([[1.,2.]])@torch.tensor([[3.],[4.]])).item()==11.)
    print(json.dumps(dict(status='passed',scope='offline production worker; synthetic temporary record; no game integration',model_files=11,embedding_dimensions=512,cpu_operation=11,versions=34),ensure_ascii=False))
if __name__=='__main__':
    try:
        if len(sys.argv)>1 and sys.argv[1]=='--child': child()
        else: main()
    except Exception as error:
        print('Local recall check stopped: '+type(error).__name__,file=sys.stderr)
        sys.exit(1)
