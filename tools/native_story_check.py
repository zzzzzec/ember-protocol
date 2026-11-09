"""Exercise the alternative story route, persistent choice and competing accepts on Development."""
from pathlib import Path
import argparse,copy,json,ssl,time,urllib.request,uuid,zlib,concurrent.futures
from story_quest_checks import StoryQuestChecks

def read(p):return json.loads(p.read_text(encoding='utf-8-sig'))
def main():
 p=argparse.ArgumentParser();p.add_argument('--game-root',type=Path,required=True);p.add_argument('--port',type=int,required=True);p.add_argument('--report',type=Path,required=True);p.add_argument('--recheck-restart',action='store_true');a=p.parse_args();root=a.game_root.resolve();state=read(root/'.sptz/installed.json');assert state['instanceKind']=='Development' and state['port']==a.port and a.port!=6969
 mod=root/'SPT/user/mods/Codex-ZombieSurvival';qs=read(mod/'data/quests.json');rules=read(mod/'data/story-quests.json');manifest=read(mod/'data/manifest.json');cfg=read(mod/'config.json');session=''
 def request(route,body=None,allow_error=False):
  req=urllib.request.Request('https://127.0.0.1:%s%s'%(a.port,route),data=json.dumps(body or {}).encode(),headers={'Content-Type':'application/json','Cookie':'PHPSESSID='+session,'responsecompressed':'0','requestcompressed':'0'})
  try:
   with urllib.request.urlopen(req,context=ssl._create_unverified_context(),timeout=120) as resp:raw=resp.read()
  except urllib.error.HTTPError:
   if allow_error:return dict(rejected=True)
   raise
  for mode in [None,zlib.MAX_WBITS,-zlib.MAX_WBITS,31]:
   try:
    text=(raw if mode is None else zlib.decompress(raw,mode)).decode('utf-8-sig').strip()
    if len(text)==24 and all(c in '0123456789abcdef' for c in text.lower()):return text
    result=json.loads(text)
    if isinstance(result,dict) and 'err' in result:
     if result['err'] and allow_error:return dict(rejected=True)
     assert result['err']==0,result;return result.get('data')
    return result
   except (ValueError,zlib.error,UnicodeDecodeError):pass
  raise AssertionError('Cannot decode '+route)
 def pmc():return next(p for p in request('/client/game/profile/list') if p['Info']['Side'].lower()!='savage')
 def start():
  time.sleep(1.05);return request('/client/match/local/start',dict(location='sandbox',playerSide='Pmc',shouldSkipLootGeneration=True,timeVariant='CURR'))
 def fixture(profile,outcome='SURVIVED'):
  raid=start();request('/client/match/local/end',dict(serverId=raid['serverId'],results=dict(profile=profile,result=outcome,exitName='QA story fixture',inSession=False,favorite=False,playTime=60),lostInsuredItems=[],transferItems={}))
 def complete_seed(profile,qid):
  existing=next((q for q in profile['Quests'] if q['qid']==qid),None)
  if existing is None:profile['Quests'].append(dict(qid=qid,startTime=int(time.time()),status=4,statusTimers={'4':int(time.time())},completedConditions=[]))
  else:existing['status']=4
 if a.recheck_restart:
  marker=read(root/'.sptz/story-qa.json');f=root/'SPT/user/profiles'/(marker['session']+'.json');assert read(f)['info']['username']==marker['username'] and marker['username'].startswith('Ember-Story-QA-');session=f.stem
  request('/client/game/start');tester=StoryQuestChecks(request,f,qs,rules,request('/client/items'));chosen=rules['questKeys']['cargo1'];tester.check_choice(chosen);tester.after_complete(rules['questKeys']['cargo3']);report=read(a.report);report['choiceAndRewardReplaySafeAfterBackendRestart']=True;a.report.write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n',encoding='utf-8');print('PASS saved route and one-time rewards after backend restart.');return
 def setup(kind):
  nonlocal session
  name='Ember-Story-QA-'+kind+'-'+uuid.uuid4().hex[:6];session='';request('/launcher/profile/register',dict(username=name,edition='Standard'));matches=[f for f in (root/'SPT/user/profiles').glob('*.json') if read(f).get('info',{}).get('username')==name];assert len(matches)==1;session=matches[0].stem
  request('/client/game/profile/create',dict(side='Usec',nickname='EmberStoryQA',headId='5cde96047d6c8b20b577f016',voiceId='5fc1223595572123ae7384a3'));request('/client/game/start');request('/client/trading/api/traderSettings');return matches[0],name
 def seed_dependencies(extra=()):
  profile=copy.deepcopy(pmc());table=request('/client/globals')['config']['exp']['level']['exp_table'];profile['Info']['Experience']=sum(row['exp'] for row in table[:30]);profile['Info']['Level']=30
  for merchant in [cfg['traderId'],cfg['fence']['traderId']]:profile['TradersInfo'][merchant]['standing']=6 if merchant==cfg['fence']['traderId'] else 1;profile['TradersInfo'][merchant]['salesSum']=0
  deps={c['target'] for qid in rules['quests'] for c in qs[qid]['conditions']['AvailableForStart'] if c['conditionType']=='Quest' and c['target'] not in rules['quests']}
  for qid in deps|set(extra):complete_seed(profile,qid)
  fixture(profile);request('/client/trading/api/getTraderAssort/'+cfg['traderId'])
 f,name=setup('cargo');tester=StoryQuestChecks(request,f,qs,rules,request('/client/items'))
 # The new server guard must reject locked content before any profile fixture is seeded.
 before=tester.hash_inventory();assert tester.rejected(tester.action('QuestAccept',rules['questKeys']['repair4'],allow_error=True));assert tester.hash_inventory()==before
 seed_dependencies();completed=[]
 for key,qid in rules['questKeys'].items():
  if key.startswith('aid'):continue
  available=next((q for q in request('/client/quest/list') if q['_id']==qid),None);assert available and available['sptStatus']==1,(key,available)
  reply=tester.action('QuestAccept',qid);assert not reply.get('warnings'),reply;tester.ready(qid);reply=tester.action('QuestComplete',qid);assert not reply.get('warnings'),reply;tester.after_complete(qid);completed.append(qid)
 assort=request('/client/trading/api/getTraderAssort/'+cfg['traderId']);roots={i['_id'] for i in assort['items']};assert rules['offers']['greenHp'] in roots and rules['offers']['salewa'] not in roots
 purchases=tester.buy_alternatives(manifest,assort);assert set(purchases)=={'parts','repairKit','greenHp'}
 request('/client/game/logout');saved=read(f);messages=[m for d in saved['dialogues'].values() for m in d.get('messages',[]) if m.get('templateId')==rules['questKeys']['cargo3']+' successMessageText'];assert len(messages)==1
 gifts=messages[0].get('items',{}).get('data',[]);box=next(i for i in gifts if i['_tpl']=='64acea2c03378853630da53e');assert sum(i.get('upd',{}).get('StackObjectsCount',1) for i in gifts if i.get('parentId')==box['_id'] and i['_tpl']=='59e4d3d286f774176a36250a')==20,'The once-only gift box must contain exactly twenty rounds.'
 (root/'.sptz/story-qa.json').write_text(json.dumps(dict(session=f.stem,username=name)),encoding='utf-8');cargo_checks=list(tester.checks);cargo_branch=list(tester.branch_checks)
 # Racing two mutually-exclusive accepts must commit exactly one route.
 race_file,_=setup('race');seed_dependencies([rules['questKeys']['car3']]);race=StoryQuestChecks(request,race_file,qs,rules,request('/client/items'));aquest=rules['questKeys']['aid1'];bquest=rules['questKeys']['cargo1']
 with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:list(pool.map(lambda qid:race.action('QuestAccept',qid,allow_error=True),[aquest,bquest]))
 request('/client/game/logout');saved=read(race_file);statuses={q['qid']:q['status'] for q in saved['characters']['pmc']['Quests']};assert sorted([statuses[aquest],statuses[bquest]])==[2,5],statuses
 choice=saved['spt']['emberStoryChoices']['night_car']['quest'];assert statuses[choice]==2
 # Death retains handed-in progress and does not reopen the other route.
 condition=next(c for c in qs[choice]['conditions']['AvailableForFinish'] if c['conditionType']=='HandoverItem');incoming=race.seed([(condition['target'][0],1)]);reply=race.action('QuestHandover',choice,dict(conditionId=condition['id'],items=[dict(id=incoming[0]['_id'],count=1)]));assert not reply.get('warnings'),reply
 before=copy.deepcopy(pmc()['TaskConditionCounters']);fixture(copy.deepcopy(pmc()),'KILLED');after=pmc();assert after['TaskConditionCounters']==before;other=bquest if choice==aquest else aquest;assert race.rejected(race.action('QuestAccept',other,allow_error=True))
 # Explicit failure is an administrative fixture, never an ordinary death condition.
 race.action('QuestFail',choice);assert race.rejected(race.action('QuestAccept',other,allow_error=True));request('/client/game/logout');assert read(race_file)['spt']['emberStoryChoices']['night_car']['quest']==choice
 report=dict(passed=True,version=state['version'],cargoRouteCompleted=len(completed),storyChecks=cargo_checks,cargoChoice=cargo_branch,bartersPurchased=purchases,giftBoxContainsExactly20=True,lockedAcceptRejected=True,competingAcceptsCommitExactlyOne=True,deathPreservesPartialHandover=True,deathAndFailureDoNotReopenOpposite=True,choiceAndRewardReplaySafeAfterBackendRestart=False,syntheticXpCombatAndEquipmentFixtures=True,playerInstanceUsed=False,gameUiOrInRaidAcceptance=False)
 a.report.parent.mkdir(parents=True,exist_ok=True);a.report.write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n',encoding='utf-8');print('PASS cargo route, three real barters, opposing accepts, death and failure boundaries.')
if __name__=='__main__':main()
