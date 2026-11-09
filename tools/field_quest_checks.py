"""Native loan acceptance using synthetic combat/inventory fixtures on Development only."""
import copy,hashlib,json,time,uuid

class FieldQuestChecks:
 def __init__(self,request,profile_path,quests,rules,templates=None):
  self.request=request;self.profile_path=profile_path;self.quests=quests;self.rules=rules;self.checks=[];self.templates=templates or {}
 def pmc(self):return next(p for p in self.request('/client/game/profile/list') if p['Info']['Side'].lower()!='savage')
 def saved(self):return json.loads(self.profile_path.read_text(encoding='utf-8-sig'))
 def messages(self,qid):return [m for d in self.saved()['dialogues'].values() for m in d.get('messages',[]) if m.get('templateId')==qid+' startedMessageText']
 def issued(self,qid,tpl):return sum(i.get('upd',{}).get('StackObjectsCount',1) for m in self.messages(qid) for i in m.get('items',{}).get('data',[]) if i['_tpl']==tpl)
 def action(self,kind,qid,extra=None,allow_error=False):
  body=dict(Action=kind,qid=qid,type='Quest',removeExcessItems=True);body.update(extra or {})
  return self.request('/client/game/profile/items/moving',dict(data=[body],tm=0),allow_error)
 def rejected(self,r):return r.get('rejected') or bool(r.get('warnings'))
 def fixture(self,p,outcome='SURVIVED'):
  time.sleep(1.05)
  raid=self.request('/client/match/local/start',dict(location='sandbox',playerSide='Pmc',shouldSkipLootGeneration=True,timeVariant='CURR'))
  self.request('/client/match/local/end',dict(serverId=raid['serverId'],results=dict(profile=p,result=outcome,exitName='QA field fixture',inSession=False,favorite=False,playTime=60),lostInsuredItems=[],transferItems={}))
 def hash_inventory(self):return hashlib.sha256(json.dumps(self.pmc()['Inventory']['items'],sort_keys=True).encode()).hexdigest()
 def return_gun(self,qid,root,allow_error=False):return self.action('QuestHandover',qid,dict(conditionId=self.rules['loans'][qid]['returnCondition'],items=[dict(id=root,count=1)]),allow_error)
 def ready_loan(self,qid):
  spec=self.rules['loans'][qid];quest=self.quests[qid];tpl=spec['weapon'];p=self.pmc()
  issued=qid in self.rules['issuedOnce'];assert self.issued(qid,tpl)==int(issued)
  if issued:source=quest['rewards']['Started'][0]['items']
  else:
   source=[];selected={spec['referenceOffer']}
   for item in self.request('/client/trading/api/getTraderAssort/'+quest['traderId'])['items']:
    if item['_id'] in selected or item.get('parentId') in selected:
     source.append(item);selected.add(item['_id'])
   assert source and source[0]['_tpl']==tpl,'The self-supplied assembly must be purchasable when the trial opens.'
  tree=copy.deepcopy(source);ids={i['_id']:uuid.uuid4().hex[:24] for i in tree}
  for i in tree:
   i['_id']=ids[i['_id']]
   if i.get('parentId') in ids:i['parentId']=ids[i['parentId']]
  root=tree[0]['_id'];slot='Holster' if self.templates.get(tpl,{}).get('_props',{}).get('weapClass')=='pistol' else 'FirstPrimaryWeapon'
  tree[0]['parentId']=p['Inventory']['equipment'];tree[0]['slotId']=slot;tree[0].pop('location',None)
  inventory=p['Inventory']['items'];occupied={i['_id'] for i in inventory if i.get('parentId')==p['Inventory']['equipment'] and i.get('slotId')==slot}
  while True:
   children={i['_id'] for i in inventory if i.get('parentId') in occupied}
   if children<=occupied:break
   occupied.update(children)
  p['Inventory']['items']=[i for i in inventory if i['_id'] not in occupied]
  p['Inventory']['items']+=tree
  self.fixture(p)
  before=self.hash_inventory();bad=self.return_gun(qid,root,True);assert self.rejected(bad) and self.hash_inventory()==before,'Early return must preserve the whole inventory.'
  bad=self.action('QuestComplete',qid,allow_error=True);assert self.rejected(bad),'Loan cannot complete before field work and return.'
  p=self.pmc();objectives=[c for c in quest['conditions']['AvailableForFinish'] if c['conditionType']!='WeaponAssembly']
  for c in objectives:p.setdefault('TaskConditionCounters',{})[c['id']]=dict(id=c['id'],type=c['conditionType'],sourceId=qid,value=c['value'])
  self.fixture(p)
  before=copy.deepcopy(self.pmc()['TaskConditionCounters']);reply=self.action('QuestAccept',qid);assert not reply.get('warnings')
  assert self.pmc()['TaskConditionCounters']==before and self.issued(qid,tpl)==int(issued),'Repeated acceptance must not reset kills or duplicate the weapon.'
  # Real native handover must reject missing parts without consuming anything.
  p=self.pmc();mag=next((i for i in p['Inventory']['items'] if i.get('parentId')==root and i.get('slotId')=='mod_magazine'),None)
  if mag is None:mag=next(i for i in p['Inventory']['items'] if i.get('parentId')==root)
  p['Inventory']['items']=[i for i in p['Inventory']['items'] if i['_id']!=mag['_id']]
  self.fixture(p);before=self.hash_inventory();bad=self.return_gun(qid,root,True);assert self.rejected(bad) and self.hash_inventory()==before
  # Use real native death cleanup to lose the equipped fixture rifle.
  self.fixture(self.pmc(),'KILLED');p=self.pmc()
  assert not any(i['_id']==root for i in p['Inventory']['items']),'Native death must actually remove the first equipped rifle.'
  assert all(p['TaskConditionCounters'][c['id']]['value']>=c['value'] for c in objectives),'Death must preserve the objective fixture.'
  replacement=copy.deepcopy(tree);ids2={i['_id']:uuid.uuid4().hex[:24] for i in replacement}
  for i in replacement:
   i['_id']=ids2[i['_id']]
   if i.get('parentId') in ids2:i['parentId']=ids2[i['parentId']]
  replacement[0]['upd']['SpawnedInSession']=False;replacement[0]['upd']['Repairable']['Durability']=50
  p['Inventory']['items']+=replacement;root=replacement[0]['_id'];self.fixture(p)
  assert all(self.pmc()['TaskConditionCounters'][c['id']]['value']>=c['value'] for c in objectives),'Death return must preserve combat fixture progress.'
  assert self.issued(qid,tpl)==int(issued),'Death or losing the inventory weapon must not issue another weapon.'
  unrelated={i['_id']:i for i in self.pmc()['Inventory']['items'] if i['_id'] not in set(ids2.values())}
  reply=self.return_gun(qid,root);assert not reply.get('warnings'),reply.get('warnings')
  after=self.pmc();assert all(i['_id'] not in set(ids2.values()) for i in after['Inventory']['items']),'Native handover must consume the whole replacement assembly.'
  assert {i['_id']:i for i in after['Inventory']['items']}==unrelated,'Returning the rifle must preserve unrelated inventory.'
  assert after['TaskConditionCounters'][spec['returnCondition']]['value']==1
  assert (qid in self.saved()['spt'].get('emberFieldIssue',{}))==issued,'Issue marker must match the actual issue mode.'
  self.checks.append(dict(quest=qid,oneRifleInNativeMail=issued,noEarlyReturn=True,noEarlyCompletion=True,replayPreservesProgress=True,noMissingMagazineConsumption=True,deathPreservesFixtureCounters=True,noReplacementIssue=True,equivalentNonFirRifleAccepted=True,minimumDurabilityAccepted=True,wholeTreeConsumed=True))
 def after_complete(self,qid):
  self.request('/client/game/logout')
  before=len([m for d in self.saved()['dialogues'].values() for m in d.get('messages',[]) if m.get('templateId')==qid+' successMessageText'])
  self.request('/client/game/start')
  self.action('QuestComplete',qid)
  self.request('/client/game/logout')
  after=len([m for d in self.saved()['dialogues'].values() for m in d.get('messages',[]) if m.get('templateId')==qid+' successMessageText'])
  assert after==before,'Replayed loan completion must not repeat item rewards.'
  self.request('/client/game/start')
