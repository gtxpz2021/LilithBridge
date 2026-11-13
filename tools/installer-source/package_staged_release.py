"""Package approved staging roots only; never installs or runs the candidate."""
from pathlib import Path
import argparse,json,hashlib,zipfile,re,shutil
def sha(p):
 h=hashlib.sha256()
 with p.open('rb') as f:
  for b in iter(lambda:f.read(4*1024*1024),b''):h.update(b)
 return h.hexdigest()
def write(p,obj):p.write_text(json.dumps(obj,ensure_ascii=False,indent=2)+'\n',encoding='utf8')
def approved(root,manifest):
 root=root.resolve();doc=json.loads((root/manifest).read_text('utf8'));rows=doc['files'];paths=[x['path'] if isinstance(x,dict) else x for x in rows]
 if manifest=='SOURCE-FILES.json':paths.append(manifest)
 if len(paths)!=len(set(x.casefold() for x in paths)):raise ValueError('Duplicate approved path')
 result=[]
 for value in sorted(paths):
  rel=Path(value)
  if rel.is_absolute() or '..' in rel.parts or '\\' in value or ':' in value:raise ValueError('Unsafe approved path')
  p=root/rel
  for q in [p,*p.parents]:
   if q.is_symlink() or getattr(q,'is_junction',lambda:False)():raise ValueError('Linked stage path refused')
   if q==root:break
  if not p.is_file():raise ValueError('Approved file missing: '+value)
  result.append((value,p))
 if manifest=='PUBLIC-FILES.json':
  inventory=json.loads((root/'PACKAGE-FILES.json').read_text('utf8'))['files'];expected={r['path'] for r in inventory}|{'PACKAGE-FILES.json'}
  if set(paths)!=expected:raise ValueError('Whitelist and inventory differ')
  for row in inventory:
   p=root/row['path']
   if p.stat().st_size!=row['bytes'] or sha(p)!=row['sha256']:raise ValueError('Stage identity changed: '+row['path'])
 elif manifest=='SOURCE-FILES.json':
  for row in rows:
   if isinstance(row,dict):
    p=root/row['path']
    if p.stat().st_size!=row['bytes'] or sha(p)!=row['sha256']:raise ValueError('Source identity changed: '+row['path'])
 return result
def archive(rows,p):
 with zipfile.ZipFile(p,'w',allowZip64=True,compresslevel=5) as z:
  for name,f in rows:
   info=zipfile.ZipInfo(name,(2026,1,1,0,0,0));info.external_attr=0o100644<<16;info.compress_type=zipfile.ZIP_STORED if f.suffix.lower() in {'.whl','.nupkg','.zip','.exe'} else zipfile.ZIP_DEFLATED
   with f.open('rb') as src,z.open(info,'w',force_zip64=True) as out:
    while b:=src.read(4*1024*1024):out.write(b)
 return {'path':str(p.resolve()),'bytes':p.stat().st_size,'sha256':sha(p)}
def main():
 p=argparse.ArgumentParser();p.add_argument('phase',choices=['prepare','finalize']);p.add_argument('--profile',type=Path,default=Path(__file__).with_name('release-profile.json'));p.add_argument('--thin',type=Path);p.add_argument('--payload',type=Path);p.add_argument('--source',type=Path);p.add_argument('--out',type=Path,required=True);a=p.parse_args();profile=json.loads(a.profile.read_text('utf8'));base=profile['product_version'].split('-')[0];suffix=profile['artifact_suffix']
 if not re.fullmatch(r'[A-Za-z0-9._-]+',base+'-'+suffix):raise ValueError('Invalid artifact identity')
 out=a.out.resolve();plan=out/'build-plan.json'
 if a.phase=='prepare':
  if out.exists():raise ValueError('Use a new isolated output directory')
  if a.thin is None or a.payload is None:raise ValueError('Two approved staging roots required')
  thin=approved(a.thin,'PUBLIC-FILES.json');payload=approved(a.payload,'PUBLIC-FILES.json')
  if sum(f.stat().st_size for _,f in payload)>134217728:raise ValueError('Payload exceeds current launcher unpack budget')
  for root in [a.thin,a.payload]:
   if sha(root/'setup/payload/LilithBridge.dll')!=profile['product_sha256']:raise ValueError('Product binding differs')
   manifest=json.loads((root/'setup/install-manifest.json').read_text('utf8'))
   if manifest['plugin_sha256']!=profile['product_sha256'] or manifest['id']!=profile['manifest_id']:raise ValueError('Installer manifest binding differs')
  if a.source:approved(a.source,'SOURCE-FILES.json')
  out.mkdir(parents=True);small=archive(thin,out/('LilithBridge-installer-'+base+'-'+suffix+'.zip'))
  if small['bytes']>52428800:raise ValueError('Online update ZIP exceeds 50MiB')
  fat=archive(payload,out/('SetupPayload-'+base+'-'+suffix+'.zip'))
  write(plan,{'profile_sha256':sha(a.profile),'thin_update':small,'embedded_payload':fat,'source_root':str(a.source.resolve()) if a.source else None,'setup_path':str(out/('LilithBridge-Setup-'+base+'-'+suffix+'.exe')),'no_install_run_or_simulated_tests':True})
  print(plan)
 else:
  data=json.loads(plan.read_text('utf8'))
  if data['profile_sha256']!=sha(a.profile):raise ValueError('Release profile changed after payload preparation')
  exe=Path(data['setup_path'])
  if not exe.is_file():raise ValueError('Compile the matching single-file launcher first')
  artifacts={'setup':{'path':str(exe),'bytes':exe.stat().st_size,'sha256':sha(exe)},'thin_update':data['thin_update']}
  # Library test ZIP contains exactly the final EXE, no extra directory contents.
  artifacts['candidate_test_zip']=archive([(exe.name,exe)],out/('LilithBridge-Setup-'+base+'-'+suffix+'-candidate-test.zip'))
  if data['source_root']:
   source=Path(data['source_root']);approved(source,'SOURCE-FILES.json')
   generated=out/'PayloadIdentity.g.cs'
   if generated.is_file():
    rel='tools/installer-source/launcher/PayloadIdentity.g.cs';target=source/rel;target.parent.mkdir(parents=True,exist_ok=True);shutil.copy2(generated,target)
    doc=json.loads((source/'SOURCE-FILES.json').read_text('utf8'));rows={x['path']:x for x in doc['files']};rows[rel]={'path':rel,'bytes':target.stat().st_size,'sha256':sha(target)};doc['files']=[rows[x] for x in sorted(rows)];write(source/'SOURCE-FILES.json',doc)
   artifacts['clean_source']=archive(approved(source,'SOURCE-FILES.json'),out/('LilithBridge-source-'+base+'-'+suffix+'.zip'))
  (out/'SHA256SUMS.txt').write_text(''.join(x['sha256']+'  '+Path(x['path']).name+'\n' for x in artifacts.values()),encoding='utf8')
  result={'build':profile['build'],'product_version':profile['product_version'],'product_sha256':profile['product_sha256'],'profile_sha256':data['profile_sha256'],'artifacts':artifacts,'embedded_payload':data['embedded_payload'],'no_install_run_or_simulated_tests':True,'publicly_released':False}
  write(out/'BUILD-RESULT.json',result);print(json.dumps(result,ensure_ascii=False,indent=2))
if __name__=='__main__':main()
