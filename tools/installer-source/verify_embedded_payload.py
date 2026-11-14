"""Build-time static check for the current single-resource CLR launcher.
Reads bytes only: never loads the assembly, extracts an installation or invokes a process.
"""
from pathlib import Path
import struct,re,json,hashlib,zipfile,io,argparse
HASH=lambda b:hashlib.sha256(b).hexdigest()
def inspect(path):
 data=path.read_bytes()
 u16=lambda o:struct.unpack_from('<H',data,o)[0]
 u32=lambda o:struct.unpack_from('<I',data,o)[0]
 assert data[:2]==b'MZ'
 nt=u32(0x3c);assert data[nt:nt+4]==b'PE\0\0'
 count=u16(nt+6);opt=nt+24;magic=u16(opt)
 assert magic in [0x10b,0x20b]
 dd=opt+(112 if magic==0x20b else 96)
 cli_rva=u32(dd+14*8)
 secs=nt+24+u16(nt+20)
 def at(rva):
  for i in range(count):
   s=secs+40*i;va=u32(s+12);rawsize=u32(s+16)
   if va<=rva<va+max(u32(s+8),rawsize):
    offset=rva-va
    assert offset<rawsize
    return u32(s+20)+offset
  raise ValueError('RVA not backed by a section')
 cli=at(cli_rva);resource_size=u32(cli+28);res=at(u32(cli+24))
 length=u32(res);payload=data[res+4:res+4+length]
 assert len(payload)==length and length+4<=resource_size and payload[:2]==b'PK'
 # Read compiled user-string literals without loading CLR code.
 meta=at(u32(cli+8));assert data[meta:meta+4]==b'BSJB'
 cur=(meta+16+u32(meta+12)+3)&~3
 streams=u16(cur+2);cur+=4;tables={}
 for _ in range(streams):
  off=u32(cur);size=u32(cur+4);start=cur+8;end=data.index(0,start)
  name=data[start:end].decode('ascii');tables[name]=data[meta+off:meta+off+size];cur=(end+1+3)&~3
 us=tables['#US'];cur=1;strings=[]
 while cur<len(us):
  b=us[cur]
  if b<128:n=b;prefix=1
  elif b<192:n=((b&63)<<8)|us[cur+1];prefix=2
  elif b<224:n=((b&31)<<24)|(us[cur+1]<<16)|(us[cur+2]<<8)|us[cur+3];prefix=4
  else:raise ValueError('Invalid compressed heap length')
  cur+=prefix
  if n==0:continue
  value=us[cur:cur+n];cur+=n
  assert len(value)==n
  text=value[:-1].decode('utf-16le')
  if re.fullmatch('[0-9a-f]{64}',text):strings.append(text)
 const=sorted(set(strings));assert len(const)==1,('Ambiguous hash literals',const)
 resource_hash=HASH(payload)
 with zipfile.ZipFile(io.BytesIO(payload)) as z:
  assert z.testzip() is None
  manifest=json.loads(z.read('setup/install-manifest.json'))
  dll=HASH(z.read('setup/payload/LilithBridge.dll'))
  assert dll==manifest['plugin_sha256']
  info={'entries':len(z.infolist()),'product_dll_sha256':dll,'manifest_id':manifest['id'],'all_zip_crc_passed':True}
 receipt={'path':str(path),'exe_bytes':len(data),'exe_sha256':HASH(data),'embedded_payload_bytes':length,'embedded_payload_sha256':resource_hash,'compiled_hash_literals':const,'compiled_hash_matches_embedded_payload':const==[resource_hash],**info,'assembly_loaded_or_executed':False}
 return receipt

def main():
 p=argparse.ArgumentParser()
 p.add_argument('--exe',type=Path,required=True)
 p.add_argument('--payload',type=Path,required=True)
 p.add_argument('--receipt',type=Path,required=True)
 a=p.parse_args()
 result=inspect(a.exe)
 payload=a.payload.read_bytes()
 result.update(expected_payload_bytes=len(payload),expected_payload_sha256=HASH(payload))
 result['all_identity_checks_passed']=(
  result['compiled_hash_matches_embedded_payload']
  and result['embedded_payload_bytes']==len(payload)
  and result['embedded_payload_sha256']==HASH(payload)
 )
 a.receipt.parent.mkdir(parents=True,exist_ok=True)
 a.receipt.write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
 if not result['all_identity_checks_passed']:
  raise ValueError('LB_BUILD_EMBEDDED_PAYLOAD_IDENTITY_MISMATCH; candidate not executed')
 print(json.dumps({'launcher_payload_static_identity':'passed','exe_sha256':result['exe_sha256'],'payload_sha256':result['embedded_payload_sha256'],'assembly_executed':False}))
if __name__=='__main__':main()

