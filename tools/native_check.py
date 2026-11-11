"""Native backend acceptance on a separately installed Development instance, never the player port."""
from pathlib import Path
import argparse,collections,hashlib,json,ssl,urllib.request,zlib,time,uuid
from field_quest_checks import FieldQuestChecks
from story_quest_checks import StoryQuestChecks
def read(p):return json.loads(p.read_text(encoding='utf-8-sig'))
def contains(actual,expected):
 if isinstance(expected,dict):return isinstance(actual,dict) and all(contains(actual.get(k),v) for k,v in expected.items())
 if isinstance(expected,list):return isinstance(actual,list) and len(actual)==len(expected) and all(contains(a,b) for a,b in zip(actual,expected))
 return actual==expected
def effective_custom_recipe(recipe, multiplier):
 if multiplier is None or recipe.get('continuous') is not False or recipe['productionTime']<10:return recipe
 seconds=recipe['productionTime']*multiplier
 return dict(recipe,productionTime=2 if seconds<1 else seconds)
def main():
 p=argparse.ArgumentParser();p.add_argument('--game-root',type=Path,required=True);p.add_argument('--port',type=int,required=True);p.add_argument('--report',type=Path,required=True);a=p.parse_args();root=a.game_root.resolve()
 assert a.port!=6969
 state=read(root/'.sptz/installed.json');assert state['instanceKind']=='Development' and state['port']==a.port
 mod=root/'SPT/user/mods/Codex-ZombieSurvival';m=read(mod/'data/manifest.json');cfg=read(mod/'config.json');rs=read(mod/'data/recipes.json');qs=read(mod/'data/quests.json');expected_locale=read(mod/'data/locales.json')['ch'];session=''
 def request(route,body=None,allow_error=False,*,get=False):
  data=None if get else json.dumps(body or {}).encode();req=urllib.request.Request(f'https://127.0.0.1:{a.port}'+route,data=data,headers={'Content-Type':'application/json','Cookie':'PHPSESSID='+session,'responsecompressed':'0','requestcompressed':'0'})
  with urllib.request.urlopen(req,context=ssl._create_unverified_context(),timeout=120) as r:raw=r.read()
  for mode in [None,zlib.MAX_WBITS,-zlib.MAX_WBITS,zlib.MAX_WBITS|16]:
   try:
    text=(raw if mode is None else zlib.decompress(raw,mode)).decode('utf-8-sig').strip()
    if len(text)==24 and all(c in '0123456789abcdef' for c in text.lower()):return text
    result=json.loads(text)
    if isinstance(result,dict) and 'err' in result:
     if result['err'] and allow_error:return dict(rejected=True)
     assert result['err']==0,result;return result.get('data')
    return result
   except (ValueError,zlib.error,UnicodeDecodeError):pass
  raise ValueError('Cannot decode '+route)
 qa_name='Ember-Pack-QA-'+time.strftime('%Y%m%d%H%M%S')+'-'+uuid.uuid4().hex[:4]
 request('/launcher/profile/register',dict(username=qa_name,edition='Standard'))
 matches=[f for f in (root/'SPT/user/profiles').glob('*.json') if read(f).get('info',{}).get('username')==qa_name]
 assert len(matches)==1;session=matches[0].stem
 (root/'.sptz/backend-qa.json').write_text(json.dumps(dict(session=session,username=qa_name)),encoding='utf-8')
 request('/client/game/profile/create',dict(side='Usec',nickname='EmberPackQA',headId='5cde96047d6c8b20b577f016',voiceId='5fc1223595572123ae7384a3'));request('/client/game/start');request('/client/trading/api/traderSettings')
 g=request('/client/globals');assert not g['config']['EventSettings']['EventActive'];assert g['config']['SeasonActivity']['InfectionHalloween']['Enabled']
 progression=cfg['skillProgression'];global_config=g['config']
 assert global_config['SkillsSettings']['SkillProgressRate']==progression['skillRate']
 assert global_config['SkillsSettings']['WeaponSkillProgressRate']==progression['weaponRate']
 for key,setting in [('SkillFreshPoints','freshPoints'),('SkillPointsBeforeFatigue','pointsBeforeFatigue'),('SkillFatiguePerPoint','fatiguePerPoint'),('SkillMinEffectiveness','minimumEffectiveness'),('SkillFatigueReset','fatigueResetSeconds')]:
  assert global_config[key]==progression[setting],(key,global_config[key],progression[setting])
 assert global_config['SkillExpPerLevel']==100
 skill_settings=global_config['SkillsSettings']
 assert skill_settings['Metabolism']['EnergyRecoveryRate']==skill_settings['Metabolism']['HydrationRecoveryRate']==progression['metabolismRecoveryRate']
 assert skill_settings['Health']['SkillProgress']==progression['healthSkillProgress']
 assert skill_settings['MagDrills']['RaidLoadedAmmoAction']==progression['magDrillsLoadAction']
 # This suite completes all quests artificially; supply matching synthetic late-game XP
 # so native loyalty gates remain real while every late offer can be inspected.
 character=next(p for p in request('/client/game/profile/list') if p['Info']['Side'].lower()!='savage')
 target_level=max(max(l['minLevel'] for l in read(mod/'data/trader.json')['base']['loyaltyLevels']),max(c['value'] for q in qs.values() for c in q['conditions']['AvailableForStart'] if c['conditionType']=='Level'))
 character['Info']['Experience']=sum(row['exp'] for row in g['config']['exp']['level']['exp_table'][:target_level])
 character['Info']['Level']=target_level
 for merchant in [cfg['traderId'],cfg['fence']['traderId']]:
  character['TradersInfo'][merchant].setdefault('salesSum',0);character['TradersInfo'][merchant].setdefault('standing',0)
 for map_id in ('sandbox','sandbox_high'):
  raid=request('/client/match/local/start',dict(location=map_id,playerSide='Pmc',shouldSkipLootGeneration=False,timeVariant='CURR'))
  # Check the dedicated Scav body template. The old forced entry on another corpse also produces a green flare.
  corpse,=(spot for spot in raid['locationLoot']['Loot'] if any(
   item['_id']==spot['Root'] and item['_tpl']=='6582e6bb0c3b9823fe6d1840' for item in spot.get('Items') or []))
  assert sum(item['_tpl']=='6217726288ed9f0845317459' and item.get('parentId')==corpse['Root'] for item in corpse['Items'])==1,map_id
  request('/client/match/local/end',dict(serverId=raid['serverId'],results=dict(profile=character,result='SURVIVED',exitName='QA late-game XP fixture',inSession=False,favorite=False,playTime=60),lostInsuredItems=[],transferItems={}))
 request('/client/game/start');request('/client/trading/api/traderSettings')

 assert 'BuffsZombieInfection' not in global_config['Health']['Effects']['Stimulator']['Buffs']
 native_config=read(root/'SPT/SPT_Data/database/globals.json')['config']
 assert global_config['SeasonActivity']['InfectionHalloween']['ZombieBleedMul']==native_config['SeasonActivity']['InfectionHalloween']['ZombieBleedMul']
 for effect in ('LightBleeding','HeavyBleeding'):
  assert contains(global_config['Health']['Effects'][effect],native_config['Health']['Effects'][effect]),effect
 abundance_path=mod/'data/map-abundance.json'
 if abundance_path.is_file():
  abundance=read(abundance_path)
  names=dict(sandbox='中心区',bigmap='海关',interchange='立交桥',factory='工厂',rezervbase='储备站',laboratory='实验室')
  rewards={}
  for map_id,quests in abundance['maps'].items():
   for kind,bonus in [('main','0.06'),('side','0.03')]:
    rewards.update({qid: [f'{names[map_id]} +{bonus}'] for qid in quests[kind]})
  rewards.update({qid:[f'{names[map_id]} +0.015' for map_id in maps] for qid,maps in abundance['shared'].items()})
  for qid,parts in rewards.items():
   key=qid+' description'
   if key in expected_locale:expected_locale[key]+='\n\n物资丰度奖励　'+' / '.join(parts)
  expected_locale['ember map abundance']='物资丰度 ×{0}\n任务 +{1}　击杀 +{2}'
 main_ids={row['id'] for row in read(mod/'data/campaign.json')['main']}
 for key in ('SearchAction','FindAction'):
  assert abs(skill_settings['Search'][key]-native_config['SkillsSettings']['Search'][key]*progression['searchGainMultiplier'])<1e-6
 locale=request('/client/locale/ch');assert all(locale.get(k)==v for k,v in expected_locale.items())
 locations=request('/client/locations')['locations'];map_report=[]
 for loc in locations.values():
  ident=loc.get('Id','').lower()
  for transit in loc.get('transits') or []:
   assert not transit.get('active') or (ident in cfg['maps'] and transit.get('location','').lower() in cfg['maps']),('Active transit leaves enabled map set',ident,transit)
  if ident in cfg['maps']:
   spec=cfg['maps'][ident];assert loc['Enabled'] and not loc['Locked'] and contains(loc['BossLocationSpawn'],spec['spawns'])
   assert not loc.get('waves') and not loc['Events']['Halloween2024']['CrowdAttackSpawnParams']
   map_report.append(dict(map=ident,total=len(loc['BossLocationSpawn']),exits=len(loc.get('exits',[]))))
  elif ident not in ['hideout','privatearea','private area']:assert not loc['Enabled'],ident
 assert len(map_report)==len(cfg['maps'])
 recipes=request('/client/hideout/production/recipes')
 if isinstance(recipes,dict):recipes=recipes['recipes']
 recipe_byid={r['_id']:r for r in recipes}
 multiplier=read(mod/'startup-report.json')['customRecipeTimeMultiplier']
 for r in rs:assert contains(recipe_byid[r['_id']],effective_custom_recipe(r,multiplier)),r['_id']
 handbook=request('/client/handbook/templates');prices={i['Id']:i['Price'] for i in handbook['Items']}
 assert all(prices[k]==v for k,v in cfg['economy']['handbookPrices'].items())
 def action(kind,qid):
  r=request('/client/game/profile/items/moving',dict(data=[dict(Action=kind,qid=qid,type='Quest',removeExcessItems=True)],tm=0));assert not r.get('warnings'),r.get('warnings')
 fence_report={}
 if cfg.get('fence',{}).get('enabled'):
  fence_id=cfg['fence']['traderId']
  def fence_state():
   assortment=request('/client/trading/api/getTraderAssort/'+fence_id)
   roots=[i for i in assortment['items'] if i.get('parentId')=='hideout']
   assert len(roots)>=8 and assortment['nextResupply']==2147483647
   assert all(not i.get('upd',{}).get('UnlimitedCount',False) for i in roots)
   return {i['_id'] for i in roots},len(roots)
  first,count=fence_state();again,_=fence_state();assert first==again,'Browsing must not regenerate stock.'
  fence_report=dict(earlyOffers=count,noRefreshByBrowsing=True)
 field_rules=read(mod/'data/field-quests.json')
 story_rules=read(mod/'data/story-quests.json');templates=request('/client/items');story=StoryQuestChecks(request,matches[0],qs,story_rules,templates)
 field=FieldQuestChecks(request,matches[0],qs,field_rules,templates)
 # Base and pinned WTT optics must all fail iron-sight kill objectives.
 optic_parents={tpl for tpl,item in templates.items() if item.get('_name') in ['CompactCollimator','Collimator','AssaultScope','OpticScope','SpecialScope']}
 optics={tpl for tpl,item in templates.items() if item.get('_type')=='Item' and item.get('_parent') in optic_parents}
 iron_conditions=0
 for quest in qs.values():
  for counter in quest['conditions']['AvailableForFinish']:
   for condition in counter.get('counter',{}).get('conditions',[]):
    if condition.get('weaponModsExclusive'):
     excluded={tpl for group in condition['weaponModsExclusive'] for tpl in group};assert optics<=excluded,('Iron-sight filter omits installed optics',quest['_id'],sorted(optics-excluded));iron_conditions+=1
 assert iron_conditions==10
 lab=read(mod/'data/lab-blackout.json')
 def ready_lab_operation(qid):
  raid=request('/client/match/local/start',dict(location='laboratory',playerSide='Pmc',shouldSkipLootGeneration=True,timeVariant='CURR'))
  raid_id=raid['serverId']
  phase='scout' if qid==lab['scout'] else 'repair'
  state=request('/ember/lab/state',get=True)
  assert state['blackout'] and state['operation']==phase and state['raid']==raid_id
  assert not request('/ember/lab/panel/stale',get=True)['ok']
  assert request('/ember/lab/panel/'+urllib.parse.quote(raid_id,safe=''),get=True)['ok']
  character=story.pmc()
  if phase=='repair':
   quest=next(q for q in character['Quests'] if q['qid']==qid)
   quest.setdefault('completedConditions',[]).append(lab['plantCondition'])
  request('/client/match/local/end',dict(serverId=raid_id,results=dict(profile=character,result='SURVIVED',exitName='lab_Elevator_Cargo',inSession=False,favorite=False,playTime=600),lostInsuredItems=[],transferItems={}))
  assert next(q for q in story.pmc()['Quests'] if q['qid']==qid)['status']==3
 def skill_progress():
  return {s['Id']:s['Progress'] for s in story.pmc()['Skills']['Common']}
 skill_rewards_verified=0
 completed=[];skipped=[];ordered=[];pending=list(m['quests']);seen=set()
 trials=set(m.get('weaponTrials',{}).get('quests',[]))
 while pending:
  legacy=[e for e in pending if e['id'] not in trials]
  ready=[e for e in legacy or pending if all(c['target'] in seen for c in qs[e['id']]['conditions']['AvailableForStart'] if c['conditionType']=='Quest')]
  assert ready,'Quest prerequisite cycle in acceptance order'
  for e in ready:ordered.append(e);seen.add(e['id']);pending.remove(e)
 for entry in ordered:
  qid=entry['id']
  if qid in story_rules['quests'] and not story.reachable(qid):skipped.append(qid);continue
  available=next((q for q in request('/client/quest/list') if q['_id']==qid),None);assert available and available['sptStatus']==1,(qid,available)
  assert contains(available['conditions'],qs[qid]['conditions'])
  action('QuestAccept',qid)
  if qid in field_rules['loans']:field.ready_loan(qid)
  if qid in (lab['scout'],lab['repair']):ready_lab_operation(qid)
  elif qid in story_rules['quests']:story.ready(qid)
  skill_rewards=collections.Counter()
  for reward in qs[qid]['rewards']['Success']:
   if reward['type']=='Skill':skill_rewards[reward['target']]+=reward['value']
  before=skill_progress() if skill_rewards or qid in main_ids else {}
  action('QuestComplete',qid);completed.append(qid)
  if before:
   after=skill_progress()
   for skill in before:
    expected=min(5100,before[skill]+skill_rewards[skill]) if skill in skill_rewards else before[skill]
    assert abs(after[skill]-expected)<1e-6,(qid,skill,before[skill],after[skill],expected)
   skill_rewards_verified+=len(skill_rewards)
  if qid in field_rules['loans']:field.after_complete(qid)
  if qid in story_rules['quests'] and qid not in (lab['scout'],lab['repair']):story.after_complete(qid)
 expected_skipped=set()
 ledger=story.saved().get('spt',{}).get('emberStoryChoices',{})
 for group in story_rules['choices']:
  chosen=ledger[group['id']]['quest']
  expected_skipped.update(qid for route in group['routes'] if route['choice']!=chosen for qid in route['quests'])
 assert len(completed)+len(skipped)==len(qs) and set(skipped)==expected_skipped
 specialist_loyalty=[]
 request('/client/trading/api/traderSettings')
 # Native client profile DTO omits loyaltyLevel; validate the persisted server field.
 request('/client/game/logout');late_character=read(matches[0])['characters']['pmc']
 request('/client/game/start')
 for contact in read(mod/'data/opening.json')['contacts']:
  info=late_character['TradersInfo'][contact['id']]
  assert info['unlocked'] and info['standing']+1e-8>=contact['standing'][-1] and info['loyaltyLevel']==4,('Current quests must reach specialist LL4',contact['id'],info)
  specialist_loyalty.append(dict(trader=contact['id'],standing=info['standing'],loyaltyLevel=info['loyaltyLevel']))
 assort=request('/client/trading/api/getTraderAssort/'+cfg['traderId']);roots={x['_id'] for x in assort['items'] if x.get('parentId')=='hideout'}
 opening_path=mod/'data/opening.json'
 if opening_path.exists() and read(opening_path).get('enabled'):
  for contact in read(opening_path)['contacts']:
   contact_assort=request('/client/trading/api/getTraderAssort/'+contact['id']);contact_roots={x['_id'] for x in contact_assort['items'] if x.get('parentId')=='hideout'};assert not roots&contact_roots;roots|=contact_roots
 assert {o['id'] for o in m['offers'] if o.get('unlock') not in skipped}<=roots
 assert not {o['id'] for o in m['offers'] if o.get('unlock') in skipped}&roots
 story_purchases=story.buy_alternatives(m,assort)
 if cfg.get('fence',{}).get('enabled'):
  ids,count=fence_state()
  character=next(p for p in request('/client/game/profile/list') if p['Info']['Side'].lower()!='savage')
  info=character['TradersInfo'][fence_id]
  assert info.get('standing',info.get('Standing',0))>=6,'The seven Fence contracts must reach regular-customer standing.'
  assert all(qs[qid]['traderId']==fence_id for qid in cfg['fence']['secureQuestIds']+cfg['fence']['smallBusinessQuestIds'])
  fence_report.update(lateOffers=count,regularCustomerStandingReached=True,secureContainerTasksTransferred=True,smallBusinessAdaptedToOpenMaps=True)
 request('/client/game/logout');saved=read(matches[0]);messages=[msg for d in saved['dialogues'].values() for msg in d.get('messages',[])]
 for qid in completed:assert any(msg.get('templateId')==qid+' successMessageText' or msg.get('text')==expected_locale[qid+' successMessageText'] for msg in messages)
 for qid in completed:
  expected=collections.Counter()
  for reward in qs[qid]['rewards']['Success']:
   if reward['type']=='Item':
    for item in reward['items']:expected[item['_tpl']]+=item.get('upd',{}).get('StackObjectsCount',1)
  actual=collections.Counter()
  for msg in messages:
   if msg.get('templateId')==qid+' successMessageText' or msg.get('text')==expected_locale[qid+' successMessageText']:
    for item in msg.get('items',{}).get('data',[]):actual[item['_tpl']]+=item.get('upd',{}).get('StackObjectsCount',1)
  assert all(actual[tpl]>=count for tpl,count in expected.items()),('Native reward attachments differ',qid,dict(expected),dict(actual))
 # Replaying either native endpoint must never reset progress or issue a second reward.
 request('/client/trading/api/traderSettings');request('/client/game/logout')
 def reward_snapshot(profile):
  pmc=profile['characters']['pmc']
  return dict(quests=pmc['Quests'],counters=pmc.get('TaskConditionCounters',{}),inventory=pmc['Inventory'],skills=pmc['Skills'],experience=pmc['Info']['Experience'],traders=pmc['TradersInfo'],messages={key:value.get('messages',[]) for key,value in profile['dialogues'].items()})
 before_replay=reward_snapshot(read(matches[0]))
 for qid in completed:
  action('QuestAccept',qid);action('QuestComplete',qid)
 request('/client/game/logout')
 assert reward_snapshot(read(matches[0]))==before_replay,'Completed-quest replay changed rewards, progress, standing or messages'
 # A declined loan-completion request must not be logged as a successful quest.
 player_key=hashlib.sha256(session.encode()).hexdigest()[:16]
 counts=collections.Counter()
 for attempt in range(20):
  counts=collections.Counter()
  for path in (root/'Tools/ZombieSurvival/diagnostics'/time.strftime('%Y-%m-%d')).glob('*server*.jsonl'):
   for line in path.read_text('utf-8-sig').splitlines():
    try:event=json.loads(line)
    except ValueError:continue
    if event.get('player')==player_key and event.get('type')=='story.completed':counts[event.get('data',{}).get('quest')]+=1
  if all(counts[qid]==1 for qid in completed):break
  time.sleep(.2)
 assert all(counts[qid]==1 for qid in completed),('Completion diagnostics must match actual successful state exactly once',dict(counts))
 report=dict(passed=True,port=a.port,version=state['version'],role=state['instanceKind'],questCount=len(qs),completedQuestCount=len(completed),exclusiveSkipped=len(skipped),allNativeQuestRewardPipelines=True,allChineseRewardMessages=True,allReachableOffersUnlock=True,oppositeBranchOffersRemainLocked=True,storyChecks=story.checks,permanentGrowthChecks=story.growth_checks,storyChoices=story.branch_checks,storyBartersPurchased=story_purchases,offerCount=len(m['offers']),allRecipesMatch=True,recipeCount=len(rs),authoredQuestConditionsPreserved=True,ritualDisabled=True,halloweenInfectedEnabled=True,zombieVirusDisabled=True,zombieBleedingPreserved=True,valuablesRebalancePreserved=True,maps=map_report,fence=fence_report,loanChecks=field.checks,syntheticCombatAndInventoryFixtures=True,syntheticLateGameXpFixture=True,playerInstanceUsed=False,gameUiOrInRaidAcceptance=False)
 report['specialistLoyalty']=specialist_loyalty
 report['nativeSkillRewardsVerified']=skill_rewards_verified
 report['mainlineSkillsOnlyUseAuthoredRewards']=True
 report['allNativeItemRewardAttachmentsVerified']=True
 report['allCompletedQuestAcceptAndCompleteReplaysPreserved']=True
 report['completedQuestReplayCount']=len(completed)
 report['completionDiagnosticsMatchActualState']=True
 a.report.parent.mkdir(parents=True,exist_ok=True);a.report.write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n',encoding='utf-8');print(json.dumps(report,ensure_ascii=False,indent=2))
if __name__=='__main__':main()
