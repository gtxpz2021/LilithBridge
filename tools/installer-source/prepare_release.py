"""Build-time metadata synchronization only. Never loads or runs the product."""
from pathlib import Path
import argparse,json,re,hashlib,urllib.parse
R=Path(__file__).resolve().parent;SETUP=R.parent/'setup'
def digest(p):
 h=hashlib.sha256()
 with p.open('rb') as f:
  for b in iter(lambda:f.read(4*1024*1024),b''):h.update(b)
 return h.hexdigest()
def require(value,message):
 if not value:raise ValueError(message)
def pin(value):require(isinstance(value,str) and re.fullmatch('[0-9a-f]{64}',value),'Expected exact lowercase SHA256');return value
def literal(value):require(isinstance(value,str) and not any(ord(c)<32 for c in value),'Control characters refused');return json.dumps(value,ensure_ascii=False)
def hosts(values):
 require(isinstance(values,list) and values and all(isinstance(x,str) and re.fullmatch(r'[a-z0-9-]+(?:\.[a-z0-9-]+)+',x) for x in values),'Exact HTTPS hosts required');return values
def https(value,allowed):
 u=urllib.parse.urlsplit(value);require(u.scheme=='https' and not u.username and not u.password and not u.query and not u.fragment and (u.port is None or u.port==443) and u.hostname in hosts(allowed),'HTTPS source and exact host mismatch');return value
def main():
 p=argparse.ArgumentParser();p.add_argument('--profile',type=Path,default=R/'release-profile.json');a=p.parse_args();profile=json.loads(a.profile.read_text('utf8'));require(profile['schema']==1,'Unsupported release profile')
 version=profile['assembly_version'];require(re.fullmatch(r'\d+\.\d+\.\d+\.\d+',version) and all(int(x)<65536 for x in version.split('.')),'Four-part assembly version required')
 for n in ['installer_schema','config_schema','handoff_protocol']:require(type(profile[n]) is int and profile[n]>0,'Positive schema/protocol required')
 target=pin(profile['product_sha256']);require(digest(SETUP/'payload/LilithBridge.dll')==target,'Product bytes differ from release profile')
 checks=[('model-manifest.json','model_manifest_sha256'),('optional-recall/wheels.lock.json','wheel_manifest_sha256'),('optional/semantic-worker/semantic_worker.py','worker_sha256')]
 for rel,key in checks:require(digest(SETUP/rel)==pin(profile[key]),'Locked component changed: '+rel)
 model=json.loads((SETUP/'model-manifest.json').read_text('utf8'));require(model['revision']==profile['model_revision'] and len(model['files'])==11,'Model contract changed; review worker and helper pins together')
 wheel=json.loads((SETUP/'optional-recall/wheels.lock.json').read_text('utf8'));require(len(wheel['wheels'])==34,'Dependency contract changed; explicit review required')
 # A new image does not acquire data-reader rights just by editing this profile.
 for name in ['PersistentStateCompatibility.cs','ReflectionWorkCompatibility.cs']:
  require(target in (R/'src'/name).read_text('utf8'),'Product has no explicit reviewed reader binding: '+name)
 verifier=(SETUP/'optional-recall/verify_local_recall.py').read_text('utf8')
 for name,key in [('WORKER_HASH','worker_sha256'),('MANIFEST_HASH','model_manifest_sha256'),('LOCK_HASH','wheel_manifest_sha256')]:
  m=re.search(r'^'+name+r'\s*=\s*[\"\x27]([0-9a-f]+)',verifier,re.M);require(m and m[1]==profile[key],'Verifier pin differs: '+name)
 assets=profile['assets'];sources=profile['sources']
 for asset in assets.values():
  require(Path(asset['name']).name==asset['name'] and '/' not in asset['name'] and '\\' not in asset['name'],'Invalid asset filename');pin(asset['sha256']);require(type(asset['bytes']) is int and 0<asset['bytes']<2147483648,'Asset size out of range');https(asset['url'],asset['hosts'])
 for base,key in [('pypi','pypi'),('torch','torch'),('modelscope','modelscope')]:https(sources[base+'_prefix'],sources[key+'_hosts'])
 hosts(sources['hf_hosts'])
 pending={}
 def replace(rel,pattern,value):
  file=R/rel;text=pending.get(file,file.read_bytes().decode('utf8'));changed,count=re.subn(pattern,lambda m:value,text,flags=re.M);require(count==1,'Source metadata slot changed: '+rel+' / '+pattern);pending[file]=changed
 def const(rel,field,value,kind='string'):
  rendered=literal(value) if kind=='string' else str(value);replace(rel,r'(?:public |internal |private )?const '+kind+r' '+re.escape(field)+r'\s*=\s*[^;]+;', 'public const '+kind+' '+field+' = '+rendered+';')
 for rel in ['src/Program.cs','launcher/Bootstrap.cs']:replace(rel,r'\[assembly: AssemblyVersion\("[^"]+"\)\]', '[assembly: AssemblyVersion('+literal(version)+')]')
 replace('launcher/Bootstrap.cs',r'Text = "LilithBridge 安装[^"\r\n]*";', 'Text = '+literal(profile['launcher_caption'])+';')
 for field,key in [('Build','build'),('ProductHash','product_sha256'),('Caption','caption')]:const('src/StorySetup.cs',field,profile[key])
 replace('src/Wizard.Core.cs',r'public const int InstallerSchema = \d+, SupportedConfigSchema = \d+;', 'public const int InstallerSchema = '+str(profile['installer_schema'])+', SupportedConfigSchema = '+str(profile['config_schema'])+';')
 const('src/Installer.Handoff.cs','Protocol',profile['handoff_protocol'],'int')
 for rel,field,key in [('src/Setup.Core.cs','ModelRevision','model_revision'),('src/Setup.Core.cs','ManifestHash','model_manifest_sha256'),('src/Wizard.Provisioning.cs','WorkerHash','worker_sha256'),('src/TrustedDownloads.cs','WheelManifestHash','wheel_manifest_sha256')]:const(rel,field,profile[key])
 for key,field in [('python','Python'),('loader','Loader')]:
  x=assets[key];args=[literal(x['name']),literal(x['url']),str(x['bytes'])+'L',literal(x['sha256'])]+[literal(h) for h in x['hosts']]
  replace('src/TrustedDownloads.cs',r'public static readonly DownloadSpec '+field+r' = [^\r\n]+;', 'public static readonly DownloadSpec '+field+' = new DownloadSpec('+', '.join(args)+');')
 for field,key in [('MirrorPypiPrefix','pypi_prefix'),('MirrorTorchPrefix','torch_prefix'),('ModelScopePrefix','modelscope_prefix')]:const('src/TrustedDownloads.cs',field,sources[key])
 for field,key in [('MirrorPypiHosts','pypi_hosts'),('MirrorTorchHosts','torch_hosts'),('ModelScopeHosts','modelscope_hosts'),('HfHosts','hf_hosts')]:
  replace('src/TrustedDownloads.cs',r'internal static readonly string\[\] '+field+r' = [^\r\n]+;', 'internal static readonly string[] '+field+' = new[] { '+', '.join(literal(x) for x in sources[key])+' };')
 manifest=json.loads((SETUP/'install-manifest.json').read_text('utf8'));manifest.update(id=profile['manifest_id'],plugin_sha256=target,min_installer_schema=profile.get('min_installer_schema',profile['installer_schema']),config_schema=profile['config_schema'])
 require(re.fullmatch(r'[A-Za-z0-9._-]{1,64}',manifest['id']),'Invalid manifest id')
 # All checks and replacements are prepared before changing any source file.
 for path,text in pending.items():path.write_bytes(text.encode('utf8'))
 (SETUP/'install-manifest.json').write_bytes((json.dumps(manifest,ensure_ascii=False,indent=2)+'\n').replace('\n','\r\n').encode('utf8'))
 print(json.dumps({'build':profile['build'],'product_sha256':target,'profile_sha256':digest(a.profile),'component_identity_checks':'static SHA/manifest only','runtime_or_install_tests':False},ensure_ascii=False))
if __name__=='__main__':main()
