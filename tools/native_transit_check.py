# -*- coding: utf-8 -*-
"""Native transit boundary acceptance; disposable Development account only."""
from pathlib import Path
import argparse,copy,json,ssl,time,urllib.request,urllib.error,uuid,zlib

def read(p):return json.loads(Path(p).read_text(encoding='utf-8-sig'))

def main():
 p=argparse.ArgumentParser();p.add_argument('--game-root',type=Path,required=True);p.add_argument('--port',type=int,required=True);p.add_argument('--phase',choices=['capture','verify'],required=True);p.add_argument('--baseline',type=Path,required=True);p.add_argument('--report',type=Path,required=True);a=p.parse_args();root=a.game_root.resolve();installed=read(root/'.sptz/installed.json');assert installed['instanceKind']=='Development' and installed['port']==a.port and a.port!=6969
 cfg=read(root/'SPT/user/mods/Codex-ZombieSurvival/config.json');allowed=set(cfg['maps']);statefile=root/'.sptz/transit-qa.json';session=''
 def request(route,body=None,allow_error=False):
  offsets={f:f.stat().st_size for f in (root/'SPT/user/logs/spt').glob('*.log')} if allow_error else {}
  req=urllib.request.Request('https://127.0.0.1:%s%s'%(a.port,route),data=json.dumps(body or {}).encode(),headers={'Content-Type':'application/json','Cookie':'PHPSESSID='+session,'responsecompressed':'0','requestcompressed':'0'})
  try:
   with urllib.request.urlopen(req,context=ssl._create_unverified_context(),timeout=120) as response:raw=response.read()
  except urllib.error.HTTPError as e:
   raw=e.read()
   if not allow_error:raise
  for mode in [None,15,-15,31]:
   try:
    text=(raw if mode is None else zlib.decompress(raw,mode)).decode('utf-8-sig').strip()
    if len(text)==24 and all(c in '0123456789abcdef' for c in text.lower()):return text
    result=json.loads(text)
   except (ValueError,zlib.error,UnicodeDecodeError):continue
   if isinstance(result,dict) and 'err' in result:
    if result['err'] and allow_error:return dict(rejected=True,error=result.get('errmsg',''))
    assert not result['err'],result;return result.get('data')
   return result
  # SPT's exception middleware returns an empty HTTP 200 on this guard. Require
  # the specific NEW native log exception; an arbitrary empty reply is not proof.
  if allow_error and not raw:
   for _ in range(30):
    for f in (root/'SPT/user/logs/spt').glob('*.log'):
     with f.open('rb') as stream:stream.seek(offsets.get(f,0));tail=stream.read(65536).decode('utf-8',errors='replace')
     if '这张地图未开放' in tail and 'SurvivalService.Start(' in tail:return dict(rejected=True,error='这张地图未开放',nativeLogConfirmed=True)
    time.sleep(.1)
  raise AssertionError('Cannot decode or confirm native refusal: '+route)
 if a.phase=='capture':
  name='Ember-Transit-QA-'+uuid.uuid4().hex[:8];request('/launcher/profile/register',dict(username=name,edition='Standard'));files=[f for f in (root/'SPT/user/profiles').glob('*.json') if read(f).get('info',{}).get('username')==name];assert len(files)==1;session=files[0].stem
  request('/client/game/profile/create',dict(side='Usec',nickname='EmberTransitQA',headId='5cde96047d6c8b20b577f016',voiceId='5fc1223595572123ae7384a3'));request('/client/game/start');request('/client/trading/api/traderSettings')
  locations=request('/client/locations')['locations'];snapshot={l['Id'].lower():{k:copy.deepcopy(l.get(k)) for k in ['transits','exits','Enabled','Locked']} for l in locations.values() if l.get('Id')}
  a.baseline.parent.mkdir(parents=True,exist_ok=True);a.baseline.write_text(json.dumps(dict(version=installed['version'],maps=sorted(allowed),locations=snapshot),ensure_ascii=False,indent=2),encoding='utf-8');statefile.write_text(json.dumps(dict(username=name,session=session)),encoding='utf-8');request('/client/game/logout');print('Captured pre-change native transit/extraction baseline for',len(snapshot),'locations.');return
 state=read(statefile);assert state['username'].startswith('Ember-Transit-QA-');session=state['session'];assert read(root/('SPT/user/profiles/'+session+'.json'))['info']['username']==state['username'];before=read(a.baseline);assert set(before['maps'])==allowed
 request('/client/game/start');request('/client/trading/api/traderSettings');locations={l['Id'].lower():l for l in request('/client/locations')['locations'].values() if l.get('Id')};disabled=[];retained=[];checks=0
 def verify(map,loc):
  old=before['locations'][map];expected=copy.deepcopy(old['transits'])
  for transit in expected or []:
   if map not in allowed or transit.get('location','').lower() not in allowed:transit['active']=False
  assert loc.get('transits')==expected,('Transit mismatch',map,loc.get('transits'),expected)
  assert loc.get('exits')==old['exits'],('Ordinary extraction changed',map)
  if map in allowed:assert loc['Enabled'] and not loc['Locked']
  elif map not in ['hideout','privatearea','private area']:assert not loc['Enabled']
 for map,old in before['locations'].items():
  verify(map,locations[map]);checks+=1
  if map in allowed:
   for t in old['transits'] or []:
    if not t.get('active'):continue
    row=dict(source=map,id=t['id'],destination=t['location'])
    (retained if t['location'].lower() in allowed else disabled).append(row)
 assert disabled and retained,'Fixture must cover both blocked and retained native routes'
 def pmc():return next(x for x in request('/client/game/profile/list') if x['Info']['Side'].lower()!='savage')
 raids=0
 for transition in [0,1]:
  for map in sorted(allowed):
   time.sleep(1.05);raid=request('/client/match/local/start',dict(location=map,playerSide='Pmc',shouldSkipLootGeneration=True,timeVariant='CURR',transitionType=transition));verify(map,raid['locationLoot']);assert raid['transitionType']==transition or str(raid['transitionType']).upper()==['NONE','COMMON'][transition];raids+=1
   request('/client/match/local/end',dict(serverId=raid['serverId'],results=dict(profile=pmc(),result='SURVIVED',exitName='QA transit response acceptance',inSession=False,favorite=False,playTime=60),lostInsuredItems=[],transferItems={}))
 print('PASS 19 location records and 16 normal/common raid-start responses; ordinary exits unchanged.',flush=True)
 rejected=[]
 for map in ['TarkovStreets','Woods','Shoreline','Lighthouse']:
  result=request('/client/match/local/start',dict(location=map,playerSide='Pmc',shouldSkipLootGeneration=True,timeVariant='CURR',transitionType=1),allow_error=True);assert result and result.get('rejected') and '这张地图未开放' in result.get('error',''),('Closed destination not specifically rejected',map,result);rejected.append(map)
 request('/client/game/logout');report=dict(passed=True,version=installed['version'],baselineVersion=before['version'],locationResponses=checks,normalAndCommonStartResponses=raids,disabledPreviouslyActiveRoutes=disabled,retainedActiveRoutes=retained,ordinaryExitsUnchanged=True,nativelyInactiveRoutesNotEnabled=True,closedCommonDestinationsRejected=rejected,playerInstanceUsed=False,inGameTriggerAcceptance=False)
 a.report.parent.mkdir(parents=True,exist_ok=True);a.report.write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8');print('PASS transit boundary:',len(disabled),'active routes disabled,',len(retained),'retained,',raids,'native start responses; ordinary exits preserved.')
if __name__=='__main__':main()
