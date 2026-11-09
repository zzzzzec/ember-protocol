"""Read-only map contract checks on the isolated UI-QA backend."""
from pathlib import Path
from native_check import contains
import json,ssl,urllib.request,zlib
ROOT=Path(__file__).resolve().parent.parent
def read(p):return json.loads(p.read_text(encoding='utf-8-sig'))
d=Path(read(ROOT/'.local/workspace.json')['instances']['Development']['root']);state=read(d/'.sptz/installed.json');qa=read(d/'.sptz/ui-qa.json')
assert state['instanceKind']=='Development' and state['port']==6974 and qa['username'].startswith('Ember-UI-QA-')
r=urllib.request.Request('https://127.0.0.1:6974/client/locations',data=b'{}',headers={'Content-Type':'application/json','Cookie':'PHPSESSID='+qa['session'],'requestcompressed':'0','responsecompressed':'0'})
with urllib.request.urlopen(r,context=ssl._create_unverified_context(),timeout=60) as response:raw=response.read()
try:c=json.loads(raw)
except ValueError:c=json.loads(zlib.decompress(raw))
assert c['err']==0
expected=read(ROOT/'config/gameplay.json');maps=expected['maps'];caps=expected['waves']['aliveCaps'];result=[]
for loc in c['data']['locations'].values():
 key=loc.get('Id','').lower()
 if key not in maps:continue
 assert loc['Enabled'] and loc['OldSpawn'] and not loc['NewSpawn']
 assert not loc.get('waves') and not loc['NonWaveGroupScenario']['Enabled']
 assert loc['BotMax']==loc['BotMaxPlayer']==caps[key]
 assert len(loc['BossLocationSpawn'])==maps[key]['total']
 assert contains(loc['BossLocationSpawn'],maps[key]['spawns'])
 assert not loc['Events']['Halloween2024']['CrowdAttackSpawnParams']
 result.append(dict(map=key,total=len(loc['BossLocationSpawn']),cap=caps[key],onlyFiniteSpawnPath=True))
assert len(result)==len(maps)
report=dict(passed=True,readOnly=True,realPlayerUsed=False,maps=result,liveClientAcceptance=False)
(ROOT/'artifacts/native-loading-check.json').write_text(json.dumps(report,indent=2),encoding='utf-8');print('PASS all 8 native map variants: authored full plans, finite caps, no secondary spawn paths.')
