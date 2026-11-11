"""Synthetic equipment/kill fixtures plus real native handovers for new story contracts."""
import copy,hashlib,json,uuid
from field_quest_checks import FieldQuestChecks

MONEY_TEMPLATES={'5449016a4bdc2d6f028b456f','5696686a4bdc2da3298b456a','569668774bdc2da2298b4568'}

class StoryQuestChecks(FieldQuestChecks):
 def __init__(self,request,profile_path,quests,rules,templates):
  super().__init__(request,profile_path,quests,{})
  self.story=rules;self.templates=templates;self.fixture_ids=set();self.checks=[];self.branch_checks=[];self.permanent_before={};self.growth_checks=[]
 def route(self,qid):
  return next(((g,r) for g in self.story['choices'] for r in g['routes'] if qid in r['quests']),None)
 def reachable(self,qid):
  info=self.route(qid)
  if not info:return True
  ledger=self.saved().get('spt',{}).get('emberStoryChoices',{});chosen=ledger.get(info[0]['id'],{}).get('quest')
  return not chosen or chosen==info[1]['choice']
 def seed(self,requirements,fir=True,objectives=None,qid=None):
  p=copy.deepcopy(self.pmc());inventory=p['Inventory'];allitems=inventory['items'];removed=set(self.fixture_ids)
  for i in allitems:
   if i.get('parentId')==inventory['equipment'] and i.get('slotId') in ['Backpack','TacticalVest']:removed.add(i['_id'])
  changed=True
  while changed:
   changed=False
   for i in allitems:
    if i.get('parentId') in removed and i['_id'] not in removed:removed.add(i['_id']);changed=True
  inventory['items']=[i for i in allitems if i['_id'] not in removed];grids=[]
  # Real equipped container types and valid cell placements; no stash forgery.
  for slot,tpl in [('Backpack','5df8a4d786f77412672a1e3b'),('TacticalVest','5ca20abf86f77418567a43f2')]:
   oid=uuid.uuid4().hex[:24];inventory['items'].append(dict(_id=oid,_tpl=tpl,parentId=inventory['equipment'],slotId=slot,upd=dict(StackObjectsCount=1)))
   for grid in self.templates[tpl]['_props']['Grids']:grids.append(dict(parent=oid,name=grid['_name'],w=grid['_props']['cellsH'],h=grid['_props']['cellsV'],used=set()))
  incoming=[]
  for tpl,n in requirements:
   props=self.templates[tpl]['_props'];stack_size=props['StackMaxSize'] if tpl in MONEY_TEMPLATES else 1
   for offset in range(0,n,stack_size):
    incoming.append(dict(_id=uuid.uuid4().hex[:24],_tpl=tpl,upd=dict(StackObjectsCount=min(stack_size,n-offset),SpawnedInSession=fir),_width=props.get('Width',1),_height=props.get('Height',1)))
  incoming.sort(key=lambda i:i['_width']*i['_height'],reverse=True);self.fixture_ids={i['_id'] for i in incoming}
  for i in incoming:
   placed=False
   for grid in grids:
    for rotation in [0,1]:
     w,h=(i['_width'],i['_height']) if rotation==0 else (i['_height'],i['_width'])
     for y in range(grid['h']-h+1):
      for x in range(grid['w']-w+1):
       cells={(xx,yy) for xx in range(x,x+w) for yy in range(y,y+h)}
       if not cells&grid['used']:
        i.update(parentId=grid['parent'],slotId=grid['name'],location=dict(x=x,y=y,r=rotation,isSearched=True));grid['used']|=cells;placed=True;break
      if placed:break
     if placed:break
    if placed:break
   assert placed,('QA containers cannot hold fixture',i['_tpl'])
   i.pop('_width');i.pop('_height');inventory['items'].append(i)
  for c in objectives or []:
   if c['conditionType'] in ['FindItem','PlaceBeacon','LeaveItemAtLocation']:
    status=next(q for q in p['Quests'] if q['qid']==qid);status.setdefault('completedConditions',[]).append(c['id'])
   else:p.setdefault('TaskConditionCounters',{})[c['id']]=dict(id=c['id'],type=c['conditionType'],sourceId=qid,value=c['value'])
  self.fixture(p)
  actual={i['_id'] for i in self.pmc()['Inventory']['items']};assert self.fixture_ids<=actual,'Native survived return must retain the synthetic carried items.'
  return incoming
 def ready(self,qid):
  quest=self.quests[qid]
  if not quest['conditions']['AvailableForFinish']:
   info=self.route(qid)
   assert info and info[0].get('commitStage')=='complete','Only authored read/send choices may have no item or combat objectives.'
   if info[1]['choice']==qid:
    assert info[0]['id'] not in self.saved().get('spt',{}).get('emberStoryChoices',{}),'Preparing a draft must not send it.'
   self.permanent_before[qid]=self.permanent(self.pmc())
   self.checks.append(dict(quest=qid,explicitReadOrSendCompletion=True,acceptanceDidNotSend=info[1]['choice']==qid))
   return
  before=self.hash_inventory();bad=self.action('QuestComplete',qid,allow_error=True);assert self.rejected(bad) and self.hash_inventory()==before,'Story completion must wait for objectives.'
  conditions=quest['conditions']['AvailableForFinish'];hands=[c for c in conditions if c['conditionType']=='HandoverItem'];combat=[c for c in conditions if c['conditionType']=='CounterCreator'];plants=[c for c in conditions if c['conditionType'] in ['PlaceBeacon','LeaveItemAtLocation']]
  finds=[c for c in conditions if c['conditionType']=='FindItem']
  fir_handover=next((c for c in hands if c.get('onlyFoundInRaid')),None)
  if fir_handover:
   c=fir_handover;bad_items=self.seed([(c['target'][0],1)],fir=False);before=self.hash_inventory()
   bad=self.action('QuestHandover',qid,dict(conditionId=c['id'],items=[dict(id=bad_items[0]['_id'],count=1)]),True)
   assert self.rejected(bad) and self.hash_inventory()==before,'Non-FIR handover must not consume the item.'
  if plants:
   self.seed([],objectives=combat+plants[:-1],qid=qid);assert self.rejected(self.action('QuestComplete',qid,allow_error=True)),'Missing planted objective must block completion.'
  incoming=self.seed([(c['target'][0],c['value']) for c in hands],fir=bool(fir_handover),objectives=combat+plants+finds,qid=qid)
  before=copy.deepcopy(self.pmc()['TaskConditionCounters']);self.action('QuestAccept',qid);assert self.pmc()['TaskConditionCounters']==before,'Reacceptance must not erase combat progress.'
  for c in hands:
   eligible=[i for i in incoming if i['_tpl'] in c['target']];requests=[dict(id=i['_id'],count=i['upd']['StackObjectsCount']) for i in eligible]
   wrong_id=next(i['_id'] for i in self.pmc()['Inventory']['items'] if i['_tpl'] not in c['target'])
   before=self.hash_inventory();wrong=self.action('QuestHandover',qid,dict(conditionId=c['id'],items=[dict(id=wrong_id,count=1)]),True)
   assert self.rejected(wrong) and self.hash_inventory()==before,'Wrong template must not be consumed.'
   bad=self.action('QuestHandover',qid,dict(conditionId=c['id'],items=[requests[0],requests[0]]),True);assert self.rejected(bad) and self.hash_inventory()==before,'Duplicate item IDs must not consume inventory.'
   if len(requests)>1:
    first=self.action('QuestHandover',qid,dict(conditionId=c['id'],items=requests[:1]));assert not first.get('warnings'),first
    assert self.rejected(self.action('QuestComplete',qid,allow_error=True)),'Partial handover must not finish the quest.'
    rest=requests[1:]
   else:rest=requests
   reply=self.action('QuestHandover',qid,dict(conditionId=c['id'],items=rest));assert not reply.get('warnings'),reply
   before=self.hash_inventory();self.action('QuestHandover',qid,dict(conditionId=c['id'],items=rest),True);assert self.hash_inventory()==before,'Replayed handover must not consume other inventory.'
  if self.route(qid) and self.route(qid)[1]['choice']==qid:self.check_choice(qid)
  self.permanent_before[qid]=self.permanent(self.pmc())
  self.checks.append(dict(quest=qid,realNativeHandover=True,syntheticCombatFixture=bool(combat),syntheticPlantFixture=bool(plants),syntheticFindFixture=bool(finds),missingPlantBlocksCompletion=bool(plants),noEarlyCompletion=True,reacceptPreservesCounters=True,noInvalidHandoverConsumption=True))
 def check_choice(self,qid):
  group,route=self.route(qid);other=next(r for r in group['routes'] if r['choice']!=route['choice']);ledger=self.saved()['spt']['emberStoryChoices'][group['id']];assert ledger['quest']==qid
  before=self.hash_inventory();assert self.rejected(self.action('QuestAccept',other['choice'],allow_error=True));assert self.rejected(self.action('QuestComplete',other['choice'],allow_error=True));assert self.hash_inventory()==before
  self.request('/client/game/logout');saved=self.saved();states={q['qid']:q['status'] for q in saved['characters']['pmc']['Quests']};assert states[other['choice']]==5
  shown={q['_id'] for q in self.request('/client/quest/list')};assert not set(other['quests'][1:])&shown
  self.branch_checks.append(dict(group=group['id'],route=route['id'],oppositeFailed=True,oppositeAcceptAndCompleteRejected=True,unselectedFollowupsHidden=True,choicePersisted=True))
 def permanent(self,p):
  return dict(skills={s['Id']:s.get('Progress',0) for s in p['Skills']['Common']},bonuses=p.get('Bonuses',[]))
 def after_complete(self,qid):
  self.request('/client/game/logout');before=self.saved();completed=self.permanent(before['characters']['pmc'])
  info=self.route(qid)
  if info and info[0].get('commitStage')=='complete' and info[1]['choice']==qid:self.check_choice(qid)
  if qid in self.permanent_before:
   initial=self.permanent_before[qid]
   for r in self.quests[qid]['rewards']['Success']:
    if r['type']=='Skill':assert abs(completed['skills'][r['target']]-min(5100,initial['skills'][r['target']]+r['value']))<.001,('Native training points differ',qid,r,initial,completed)
    if r['type']=='StashRows':
     total=lambda state:sum(b.get('value',0) for b in state['bonuses'] if b['type']=='StashRows')
     assert total(completed)-total(initial)==r['value'],('Native stash expansion differs',qid,initial,completed)
  messages=lambda p:[m for d in p['dialogues'].values() for m in d.get('messages',[]) if m.get('templateId')==qid+' successMessageText'];count=len(messages(before));assert count==1,(qid,count)
  self.action('QuestComplete',qid);self.action('QuestAccept',qid);self.request('/client/game/logout');after=self.saved();assert len(messages(after))==1,'Story rewards must be issued once.'
  assert self.permanent(after['characters']['pmc'])==completed,'Replayed completion must not duplicate permanent skills or stash bonuses.'
  growth=[r for r in self.quests[qid]['rewards']['Success'] if r['type'] in ['Skill','StashRows']]
  if growth:self.growth_checks.append(dict(quest=qid,rewards=[dict(type=r['type'],target=r.get('target'),value=r['value']) for r in growth],nativeAppliedExactlyOnce=True,replayPreservesPermanentGrowth=True))
 def buy_alternatives(self,manifest,assort,offers=None):
  offered={i['_id'] for i in assort['items']};bought=[]
  for name,oid in (offers or self.story['offers']).items():
   offer=next(o for o in manifest['offers'] if o['id']==oid)
   if oid not in offered:continue
   incoming=self.seed([(x['_tpl'],x['count']) for x in offer['barter']]);scheme=[dict(id=i['_id'],count=1) for i in incoming]
   before={i['_id'] for i in self.pmc()['Inventory']['items']};reply=self.request('/client/game/profile/items/moving',dict(data=[dict(Action='TradingConfirm',type='buy_from_trader',tid=manifest['traderId'],item_id=oid,count=1,scheme_id=0,scheme_items=scheme)],tm=0));assert not reply.get('warnings'),reply.get('warnings')
   after=self.pmc()['Inventory']['items'];roots=[i for i in after if i['_id'] not in before and i['_tpl']==offer['tpl']];assert len(roots)==1,(name,roots)
   if name=='greenHp':assert sum(i.get('upd',{}).get('StackObjectsCount',1) for i in after if i.get('parentId')==roots[0]['_id'] and i['_tpl']=='59e4d3d286f774176a36250a')==20,'The exchanged ammunition box must actually contain twenty rounds.'
   if name=='repairKit':assert roots[0].get('upd',{}).get('RepairKit',{}).get('Resource',self.templates[offer['tpl']]['_props']['MaxRepairResource'])>0,'The bartered repair kit must not be empty.'
   bought.append(name)
  return bought
