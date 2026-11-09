"""Verify fixed normal/crisis pools via Development's real local-raid endpoints."""
from pathlib import Path
import argparse,collections,copy,json,ssl,time,urllib.request,uuid,zlib

def read(p):return json.loads(p.read_text(encoding='utf-8-sig'))
def main():
 p=argparse.ArgumentParser();p.add_argument('--game-root',type=Path,required=True);p.add_argument('--port',type=int,required=True);p.add_argument('--report',type=Path,required=True);a=p.parse_args();root=a.game_root.resolve();state=read(root/'.sptz/installed.json')
 assert state['instanceKind']=='Development' and state['port']==a.port and a.port!=6969
 mod=root/'SPT/user/mods/Codex-ZombieSurvival';cfg=read(mod/'config.json');campaign=read(mod/'data/campaign.json');session=''
 def request(route,body=None):
  req=urllib.request.Request('https://127.0.0.1:%s%s'%(a.port,route),data=json.dumps(body or {}).encode(),headers={'Content-Type':'application/json','Cookie':'PHPSESSID='+session,'responsecompressed':'0','requestcompressed':'0'})
  with urllib.request.urlopen(req,context=ssl._create_unverified_context(),timeout=120) as resp:raw=resp.read()
  for mode in [None,zlib.MAX_WBITS,-zlib.MAX_WBITS,31]:
   try:
    text=(raw if mode is None else zlib.decompress(raw,mode)).decode('utf-8-sig').strip()
    if len(text)==24 and all(c in '0123456789abcdef' for c in text.lower()):return text
    result=json.loads(text)
    if isinstance(result,dict) and 'err' in result:assert result['err']==0,result;return result.get('data')
    return result
   except (ValueError,zlib.error,UnicodeDecodeError):pass
  raise AssertionError('Cannot decode '+route)
 name='Ember-Population-QA-'+uuid.uuid4().hex[:8];request('/launcher/profile/register',dict(username=name,edition='Standard'));files=[f for f in (root/'SPT/user/profiles').glob('*.json') if read(f).get('info',{}).get('username')==name];assert len(files)==1;session=files[0].stem
 request('/client/game/profile/create',dict(side='Usec',nickname='EmberDensityQA',headId='5cde96047d6c8b20b577f016',voiceId='5fc1223595572123ae7384a3'));request('/client/game/start');request('/client/trading/api/traderSettings')
 def pmc():return next(p for p in request('/client/game/profile/list') if p['Info']['Side'].lower()!='savage')
 def check(loc,total,shooters):
  pool=loc['BossLocationSpawn'];mapid=loc['Id'].lower();cap=min(total,cfg['waves']['aliveCaps'][mapid]);assert len(pool)==total and loc['BotMax']==cap and loc['BotMaxPlayer']==cap
  assert sum(s['BossName']=='infectedPmc' for s in pool)==shooters
  assert all(s['Time']==-1 for s in pool) and not loc['waves'] and not loc['Events']['Halloween2024']['CrowdAttackSpawnParams']
  return collections.Counter(s['BossZone'] for s in pool)
 base=[]
 for loc in request('/client/locations')['locations'].values():
  key=loc.get('Id','').lower()
  if key not in cfg['maps']:continue
  spec=cfg['maps'][key];assert request('/singleplayer/settings/bot/maxCap/'+loc['Id'])==cfg['waves']['aliveCaps'][key];allocation=check(loc,spec['total'],spec['shooters']);assert dict(allocation)==spec['density']['allocation'];base.append(dict(map=key,total=spec['total'],shooters=spec['shooters'],allocation=dict(allocation)))
 assert len(base)==len(cfg['maps'])
 def start(map):
  time.sleep(1.05)
  return request('/client/match/local/start',dict(location=map,playerSide='Pmc',shouldSkipLootGeneration=True,timeVariant='CURR'))
 def end(raid,profile):request('/client/match/local/end',dict(serverId=raid['serverId'],results=dict(profile=profile,result='SURVIVED',exitName='QA population fixture',inSession=False,favorite=False,playTime=60),lostInsuredItems=[],transferItems={}))
 def complete(profile,qid):
  q=next((q for q in profile['Quests'] if q['qid']==qid),None)
  if q is None:q=dict(qid=qid,startTime=int(time.time()),status=4,statusTimers={'4':int(time.time())},completedConditions=[]);profile['Quests'].append(q)
  q['status']=4
 seed=pmc()
 for e in campaign['events']:complete(seed,e['starts'])
 initial=start('sandbox');check(initial['locationLoot'],30,1);end(initial,seed)
 crises=[]
 for event in campaign['events']:
  map=event['map'];raid=start(map);allocation=check(raid['locationLoot'],event['total'],event['shooters']);baseline=cfg['maps'][map];assert event['total']>baseline['total']
  for zone,n in baseline['density']['allocation'].items():assert abs(allocation[zone]-n*event['total']/baseline['total'])<1,('Crisis lost authored hotspot weighting',map,zone,dict(allocation))
  profile=pmc();complete(profile,event['ends']);end(raid,profile)
  normal=start(map);restored=check(normal['locationLoot'],baseline['total'],baseline['shooters']);assert dict(restored)==baseline['density']['allocation'];end(normal,pmc())
  crises.append(dict(map=map,total=event['total'],shooters=event['shooters'],allocation=dict(allocation),normalRestoredAfterResolution=True))
 request('/client/game/logout')
 report=dict(passed=True,version=state['version'],baseMaps=base,crises=crises,exactFixedPools=True,nativeConstructorCaps=True,unchangedShooterCounts=True,finiteWaveBudget=True,openingAllAtOnce=False,crisisUsesBuildingWeights=True,playerInstanceUsed=False,gameUiOrInRaidFpsAcceptance=False)
 a.report.write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n',encoding='utf-8');print(json.dumps(report,ensure_ascii=False,indent=2))
if __name__=='__main__':main()