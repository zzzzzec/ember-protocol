"""Native recipe queues with real wall-clock timers; synthetic prerequisites on Development only."""
from pathlib import Path
import argparse,copy,hashlib,json,ssl,time,urllib.request,urllib.error,uuid,zlib

def read(p):return json.loads(p.read_text(encoding='utf-8-sig'))
def main():
 p=argparse.ArgumentParser();p.add_argument('--game-root',type=Path,required=True);p.add_argument('--port',type=int,required=True);p.add_argument('--report',type=Path,required=True);p.add_argument('--phase',choices=['start','tick'],default='tick');a=p.parse_args();root=a.game_root.resolve();state=read(root/'.sptz/installed.json');assert state['instanceKind']=='Development' and state['port']==a.port and a.port!=6969
 cfg=read(root/'SPT/user/mods/Codex-ZombieSurvival/config.json');session='';rub='5449016a4bdc2d6f028b456f';usd='5696686a4bdc2da3298b456a'
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
 def action(data,error=False):return request('/client/game/profile/items/moving',dict(data=[data],tm=0),error)
 def fingerprint():
  p=pmc();return hashlib.sha256(json.dumps(dict(inventory=p['Inventory'],traders=p['TradersInfo'],experience=p['Info']['Experience']),sort_keys=True).encode()).hexdigest()
 mod=root/'SPT/user/mods/Codex-ZombieSurvival';camp=read(mod/'data/camp-growth.json');live_recipes=request('/client/hideout/production/recipes');recipes={r['_id']:r for r in (live_recipes['recipes'] if isinstance(live_recipes,dict) else live_recipes)};quests=read(mod/'data/quests.json');rules=read(mod/'data/story-quests.json');templates=request('/client/items');marker_path=root/'.sptz/craft-qa.json'
 from story_quest_checks import StoryQuestChecks
 if a.phase=='start':
  assert not marker_path.exists(),'A crafting QA run already exists; use --phase tick.'
  name='Ember-Craft-QA-'+uuid.uuid4().hex[:7];request('/launcher/profile/register',dict(username=name,edition='Standard'));files=[f for f in (root/'SPT/user/profiles').glob('*.json') if read(f).get('info',{}).get('username')==name];assert len(files)==1;session=files[0].stem
  request('/client/game/profile/create',dict(side='Usec',nickname='EmberCraftQA',headId='5cde96047d6c8b20b577f016',voiceId='5fc1223595572123ae7384a3'));request('/client/game/start');request('/client/trading/api/traderSettings');marker=dict(session=session,username=name,runs={},completed={},startedUtc=int(time.time()))
  marker_path.write_text(json.dumps(marker),encoding='utf-8')
 else:marker=read(marker_path);session=marker['session'];assert marker['username'].startswith('Ember-Craft-QA-');files=[root/'SPT/user/profiles'/(session+'.json')];assert read(files[0])['info']['username']==marker['username'];request('/client/game/start')
 tester=StoryQuestChecks(request,files[0],quests,rules,templates)
 def action(body):
  result=request('/client/game/profile/items/moving',dict(data=[body],tm=0));assert not result.get('warnings'),result.get('warnings');return result
 def save():
  request('/client/game/logout');marker_path.write_text(json.dumps(marker,ensure_ascii=False,indent=2)+chr(10),encoding='utf-8')
 def count(tpl):return sum(i.get('upd',{}).get('StackObjectsCount',1) for i in pmc()['Inventory']['items'] if i['_tpl']==tpl)
 if a.phase=='start':
  # Synthetic quest and facility prerequisites only; no real player or profile-file edits.
  profile=copy.deepcopy(pmc());table=request('/client/globals')['config']['exp']['level']['exp_table'];profile['Info']['Experience']=sum(row['exp'] for row in table[:20]);profile['Info']['Level']=20
  unlocks={x['questId'] for rid in camp['recipes'].values() for x in recipes[rid]['requirements'] if x['type']=='QuestComplete'}
  for qid in unlocks:profile['Quests'].append(dict(qid=qid,startTime=int(time.time()),status=4,statusTimers={'4':int(time.time())},completedConditions=[]))
  tester.fixture(profile);request('/client/trading/api/traderSettings')
  for area,level in [(4,1),(7,2),(8,2),(10,1)]:
   while next(x['level'] for x in pmc()['Hideout']['Areas'] if x['type']==area)<level:
    action(dict(Action='HideoutUpgrade',areaType=area,items=[]));action(dict(Action='HideoutUpgradeComplete',areaType=area))
  incoming=tester.seed([('5d1b371186f774253763a656',1)]);fuel=incoming[0];profile=copy.deepcopy(pmc());stored=next(i for i in profile['Inventory']['items'] if i['_id']==fuel['_id']);props=templates[fuel['_tpl']]['_props'];stored['upd']['Resource']=dict(Value=max(props.get('MaxResource',0),props.get('Resource',0)));assert stored['upd']['Resource']['Value']>0;tester.fixture(profile)
  action(dict(Action='HideoutPutItemsInAreaSlots',areaType=4,items={'0':dict(id=fuel['_id'],count=1)}));action(dict(Action='HideoutToggleArea',areaType=4,enabled=True))
  assert set(camp['recipes'].values())<=set(pmc()['UnlockedInfo']['unlockedProductionRecipe'])
 # Update native progress from real elapsed wall time. Do not rewrite timers or recipe durations.
 request('/client/game/start');profile=pmc()
 for key,run in list(marker['runs'].items()):
  rid=camp['recipes'][key];production=profile['Hideout']['Production'][rid]
  if production.get('Progress',0)+.05<production['ProductionTime']:continue
  assert int(time.time())-run['startUtc']+5>=run['seconds']*.9,(key,'timer unexpectedly accelerated',run,production)
  r=recipes[rid];before_ids={i['_id'] for i in pmc()['Inventory']['items']};before=count(r['endProduct']);tools={t:count(t) for t in run['tools']};action(dict(Action='HideoutTakeProduction',recipeId=rid));after=count(r['endProduct']);assert after-before==r['count'],(key,r['count'],before,after)
  for tpl,n in run['tools'].items():
   assert count(tpl)-tools[tpl]==n,('Tool not returned',key,tpl)
   if run.get('nonFirInputs'):assert all(not i.get('upd',{}).get('SpawnedInSession',False) for i in pmc()['Inventory']['items'] if i['_id'] not in before_ids and i['_tpl']==tpl),('Tool FIR changed',key,tpl)
  outputs=[i for i in pmc()['Inventory']['items'] if i['_id'] not in before_ids and i['_tpl']==r['endProduct']];assert outputs and all(i.get('upd',{}).get('SpawnedInSession') is True for i in outputs),('Native craft outputs lost FIR',key,outputs)
  marker['completed'][key]=dict(output=r['endProduct'],count=after-before,toolCountsReturned=run['tools'],elapsedSeconds=int(time.time())-run['startUtc'],recipeSeconds=run['seconds'],nativeProgressBeforeClaim=production.get('Progress'),firOutputs=True)
  before_replay=fingerprint();replay=request('/client/game/profile/items/moving',dict(data=[dict(Action='HideoutTakeProduction',recipeId=rid)],tm=0),True)
  marker['completed'][key]['replayClaimPreservesInventory']=fingerprint()==before_replay
  marker['completed'][key]['replayWarnings']=replay.get('warnings',[])
  del marker['runs'][key]
 # Backfill replay evidence for batches completed by an earlier version of this QA probe.
 for key,completed in marker['completed'].items():
  if completed.get('replayClaimPreservesInventory') is True:continue
  before_replay=fingerprint();rid=camp['recipes'][key];replay=request('/client/game/profile/items/moving',dict(data=[dict(Action='HideoutTakeProduction',recipeId=rid)],tm=0),True)
  assert fingerprint()==before_replay,('Completed batch replay changed inventory',key)
  completed['replayClaimPreservesInventory']=True;completed['replayWarnings']=replay.get('warnings',[])
 # One active recipe per facility; later stages start after the preceding batch is collected.
 order={7:['dressings','tourniquets','field-ai2'],8:['water','rations'],10:['splints']};selected=[]
 for area,keys in order.items():
  if any(recipes[camp['recipes'][k]]['areaType']==area for k in marker['runs']):continue
  key=next((k for k in keys if k not in marker['completed']),None)
  if key:selected.append(key)
 if selected:
  requirements=[]
  for key in selected:
   for req in recipes[camp['recipes'][key]]['requirements']:
    if req['type'] in ['Item','Tool']:requirements.append((req['templateId'],req.get('count',1)))
  incoming=tester.seed(requirements,fir=False);used=set()
  for key in selected:
   r=recipes[camp['recipes'][key]];selected_items=[];selected_tools=[];tool_counts={}
   for req in r['requirements']:
    if req['type'] not in ['Item','Tool']:continue
    eligible=[i for i in incoming if i['_id'] not in used and i['_tpl']==req['templateId']][:req.get('count',1)];assert len(eligible)==req.get('count',1)
    used.update(i['_id'] for i in eligible);target=selected_tools if req['type']=='Tool' else selected_items;target.extend(dict(id=i['_id'],count=1) for i in eligible)
    if req['type']=='Tool':tool_counts[req['templateId']]=req.get('count',1)
   action(dict(Action='HideoutSingleProductionStart',recipeId=r['_id'],items=selected_items,tools=selected_tools,timestamp=int(time.time())))
   actual=pmc()['Hideout']['Production'][r['_id']];assert actual['inProgress'] and actual['ProductionTime']>=r['productionTime']*.8,(key,actual)
   ids={i['_id'] for i in pmc()['Inventory']['items']};assert not ids&{x['id'] for x in selected_items+selected_tools},('Inputs not removed',key)
   marker['runs'][key]=dict(startUtc=int(time.time()),seconds=actual['ProductionTime'],tools=tool_counts,allInputsAndToolsRemovedAtStart=True,nonFirInputs=True)
 save();assert all(x.get('replayClaimPreservesInventory',True) for x in marker['completed'].values()),'Duplicate native craft claim changed inventory';report=dict(passed=len(marker['completed'])==6,version=state['version'],completed=marker['completed'],active=marker['runs'],syntheticQuestFacilityAndInventoryFixtures=True,actualRecipeTimesUnmodified=True,oneQueuePerFacility=True,playerInstanceUsed=False,visibleHideoutUiAcceptance=False)
 a.report.parent.mkdir(parents=True,exist_ok=True);a.report.write_text(json.dumps(report,ensure_ascii=False,indent=2)+chr(10),encoding='utf-8');print(json.dumps(dict(completed=list(marker['completed']),active=marker['runs']),ensure_ascii=False,indent=2))
if __name__=='__main__':main()
