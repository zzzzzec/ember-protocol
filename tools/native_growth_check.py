"""Native permanent-growth and new barter checks, restricted to a completed Development QA account."""
from pathlib import Path
import argparse,copy,hashlib,json,ssl,time,urllib.request,urllib.error,zlib
from story_quest_checks import StoryQuestChecks

def read(p):return json.loads(Path(p).read_text(encoding='utf-8-sig'))
def main():
 p=argparse.ArgumentParser();p.add_argument('--game-root',type=Path,required=True);p.add_argument('--port',type=int,required=True);p.add_argument('--report',type=Path,required=True);p.add_argument('--recheck-restart',action='store_true');a=p.parse_args();root=a.game_root.resolve();state=read(root/'.sptz/installed.json');assert state['instanceKind']=='Development' and state['port']==a.port and a.port!=6969
 marker=read(root/'.sptz/backend-qa.json');assert marker['username'].startswith('Ember-Pack-QA-');session=marker['session'];profile_path=root/'SPT/user/profiles'/(session+'.json');assert read(profile_path)['info']['username']==marker['username']
 mod=root/'SPT/user/mods/Codex-ZombieSurvival';camp=read(mod/'data/camp-growth.json');manifest=read(mod/'data/manifest.json');quests=read(mod/'data/quests.json');rules=read(mod/'data/story-quests.json')
 def request(route,body=None,allow_error=False):
  req=urllib.request.Request('https://127.0.0.1:%s%s'%(a.port,route),data=json.dumps(body or {}).encode(),headers={'Content-Type':'application/json','Cookie':'PHPSESSID='+session,'responsecompressed':'0','requestcompressed':'0'})
  try:
   with urllib.request.urlopen(req,context=ssl._create_unverified_context(),timeout=120) as response:raw=response.read()
  except urllib.error.HTTPError:
   if allow_error:return dict(rejected=True)
   raise
  for mode in [None,15,-15,31]:
   try:
    text=(raw if mode is None else zlib.decompress(raw,mode)).decode('utf-8-sig').strip()
    if len(text)==24 and all(c in '0123456789abcdef' for c in text.lower()):return text
    value=json.loads(text)
    if isinstance(value,dict) and 'err' in value:
     if value['err'] and allow_error:return dict(rejected=True)
     assert not value['err'],value;return value.get('data')
    return value
   except (ValueError,zlib.error,UnicodeDecodeError):pass
  raise AssertionError('Cannot decode '+route)
 def pmc():return next(p for p in request('/client/game/profile/list') if p['Info']['Side'].lower()!='savage')
 def snapshot():
  request('/client/game/logout');s=read(profile_path);p=s['characters']['pmc'];return dict(skills={x['Id']:x.get('Progress',0) for x in p['Skills']['Common']},stashRows=sum(x.get('value',0) for x in p.get('Bonuses',[]) if x['type']=='StashRows'),quests={x['qid']:x['status'] for x in p['Quests'] if x['qid'] in camp['quests'].values()},rewardMessages=sum(1 for d in s['dialogues'].values() for x in d.get('messages',[]) if x.get('templateId') in {q+' successMessageText' for q in camp['quests'].values()}))
 request('/client/game/start');tester=StoryQuestChecks(request,profile_path,quests,rules,request('/client/items'))
 if a.recheck_restart:
  expected=read(root/'.sptz/growth-qa.json');assert snapshot()==expected,'Permanent growth changed across backend restart.'
  for qid in camp['quests'].values():tester.action('QuestComplete',qid);tester.action('QuestAccept',qid)
  assert snapshot()==expected,'Completion replay after restart duplicated permanent rewards.'
  report=read(a.report);report['restartAndPostRestartReplayPreserveGrowth']=True;a.report.write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n',encoding='utf-8');print('PASS six stash rows and native training persist across restart without duplicate rewards.');return
 before=snapshot();assert before['stashRows']==camp['stashRows']==6 and len(before['quests'])==27 and set(before['quests'].values())=={4} and before['rewardMessages']==27,before
 for skill,points in camp['trainingPoints'].items():assert before['skills'][skill]>=points,(skill,points,before['skills'][skill])
 for qid in camp['quests'].values():tester.action('QuestComplete',qid);tester.action('QuestAccept',qid)
 assert snapshot()==before,'Repeated accepts/completions changed permanent rewards.'
 assort=request('/client/trading/api/getTraderAssort/'+manifest['traderId']);barters={k:v for k,v in camp['offers'].items() if k!='marker'};purchases=tester.buy_alternatives(manifest,assort,barters);assert set(purchases)==set(barters),purchases
 # Actual native death processing must retain training and stash expansion.
 tester.fixture(copy.deepcopy(pmc()),'KILLED');assert snapshot()==before,'Death must not remove or duplicate permanent growth.'
 for qid in camp['quests'].values():tester.action('QuestComplete',qid)
 assert snapshot()==before
 (root/'.sptz/growth-qa.json').write_text(json.dumps(before),encoding='utf-8')
 report=dict(passed=True,version=state['version'],campQuestCount=27,permanentStashRows=6,nativeSkillPointsAtLeast=camp['trainingPoints'],newBartersPurchased=purchases,questRewardMessages=27,replayedAcceptsAndCompletionsPreserveGrowth=True,nativeDeathPreservesGrowth=True,restartAndPostRestartReplayPreserveGrowth=False,syntheticCombatPlantAndInventoryFixtures=True,playerInstanceUsed=False,gameUiOrInRaidAcceptance=False)
 a.report.parent.mkdir(parents=True,exist_ok=True);a.report.write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n',encoding='utf-8');print('PASS all 27 contracts, six native barter purchases, six permanent stash rows, native training and death/replay persistence.')
if __name__=='__main__':main()
