"""Opening unlock and split-stock acceptance; disposable Development accounts only."""
from pathlib import Path
import argparse,copy,json,ssl,time,urllib.request,zlib,uuid

def read(path):return json.loads(path.read_text('utf-8-sig'))

def main():
 p=argparse.ArgumentParser();p.add_argument('--game-root',type=Path,required=True);p.add_argument('--port',type=int,required=True);p.add_argument('--report',type=Path,required=True);p.add_argument('--resume',action='store_true');a=p.parse_args()
 root=a.game_root.resolve();state=read(root/'.sptz/installed.json');assert a.port!=6969 and state['instanceKind']=='Development' and state['port']==a.port
 mod=root/'SPT/user/mods/Codex-ZombieSurvival';opening=read(mod/'data/opening.json');cfg=read(mod/'config.json');quarter=cfg['traderId'];nina=opening['ninaId'];session='';checkpoint=root/'.sptz/opening-qa.json'
 def request(route,body=None,allow_error=False):
  req=urllib.request.Request('https://127.0.0.1:'+str(a.port)+route,data=json.dumps(body or {}).encode(),headers={'Content-Type':'application/json','Cookie':'PHPSESSID='+session,'responsecompressed':'0','requestcompressed':'0'})
  with urllib.request.urlopen(req,context=ssl._create_unverified_context(),timeout=60) as r:raw=r.read()
  for mode in [None,zlib.MAX_WBITS,-zlib.MAX_WBITS,zlib.MAX_WBITS|16]:
   try:
    text=(raw if mode is None else zlib.decompress(raw,mode)).decode('utf-8-sig').strip()
    if len(text)==24 and all(c in '0123456789abcdef' for c in text.lower()):return text
    value=json.loads(text)
    if isinstance(value,dict) and 'err' in value:
     if value['err']:
      if allow_error:return {'rejected':True}
      raise AssertionError('Native request failed: '+route)
     return value.get('data')
    return value
   except (ValueError,zlib.error,UnicodeDecodeError):pass
  raise AssertionError('Cannot decode '+route)
 def pmc():return next(x for x in request('/client/game/profile/list') if x['Info']['Side'].lower()!='savage')
 def assort(tid):return request('/client/trading/api/getTraderAssort/'+tid)
 def roots(tid):return {x['_id']:x for x in assort(tid)['items'] if x.get('parentId')=='hideout'}
 def action(kind,qid):
  result=request('/client/game/profile/items/moving',dict(data=[dict(Action=kind,qid=qid,type='Quest',removeExcessItems=True)],tm=0));assert not result.get('warnings'),result.get('warnings')
 if a.resume:
  saved=read(checkpoint);session=saved['session'];request('/client/game/start');request('/client/trading/api/traderSettings');assert pmc()['TradersInfo'][nina]['unlocked'];assert set(roots(nina))<=set(opening['medicalOffers']);assert not set(roots(nina))&set(roots(quarter))
  assert assort(nina)['nextResupply']==2147483647
  ledger=read(root/'SPT/user/profiles'/(session+'.json'))['spt']['emberNarrative'];assert ledger['contacts'][nina]['state']=='open'
  report=read(a.report);report['unlockPersistsAcrossBackendRestart']=True;a.report.write_text(json.dumps(report,indent=2)+'\n','utf-8');print('PASS opening contact survives a controlled backend restart.');return
 username='Ember-Opening-QA-'+uuid.uuid4().hex[:8];request('/launcher/profile/register',dict(username=username,edition='Standard'))
 profiles=[x for x in (root/'SPT/user/profiles').glob('*.json') if read(x).get('info',{}).get('username')==username];assert len(profiles)==1;session=profiles[0].stem
 request('/client/game/profile/create',dict(side='Usec',nickname='OpeningQA',headId='5cde96047d6c8b20b577f016',voiceId='5fc1223595572123ae7384a3'));request('/client/game/start');request('/client/trading/api/traderSettings')
 assert not pmc()['TradersInfo'][nina]['unlocked'];assert not roots(nina)
 before=copy.deepcopy(pmc()['Inventory']);denied=request('/client/game/profile/items/moving',dict(data=[dict(Action='TradingConfirm',type='buy_from_trader',tid=nina,item_id=opening['medicalOffers'][0],count=1,scheme_id=0,scheme_items=[])],tm=0),True);assert denied.get('rejected') or denied.get('warnings');assert pmc()['Inventory']==before
 campaign=read(mod/'data/campaign.json');quests=read(mod/'data/quests.json')
 for entry in campaign['main'][:2]:action('QuestAccept',entry['id']);action('QuestComplete',entry['id'])
 assert pmc()['TradersInfo'][nina]['unlocked'];assert abs(pmc()['TradersInfo'][nina]['standing']-.04)<.00001
 nroots=roots(nina);qroots=roots(quarter);assert nroots and set(nroots)<=set(opening['medicalOffers']);assert not set(nroots)&set(qroots);assert assort(nina)['nextResupply']==2147483647
 assert qroots['759076688ecde0eab1c4b6d9']['upd']['BuyRestrictionMax']==3
 source=read(mod/'data/trader.json')['assort'];source_ids={x['_id'] for x in source['items'] if x.get('parentId')=='hideout'}
 # Source assort can include quest-locked entries; the moved unconditional entries must be conserved exactly.
 for offer in opening['medicalOffers']:assert offer in source_ids and offer not in qroots
 for entry in campaign['main'][2:4]:action('QuestAccept',entry['id']);action('QuestComplete',entry['id'])
 assert quests[campaign['main'][3]['id']]['traderId']==nina
 def start():
  time.sleep(1.05);return request('/client/match/local/start',dict(location='sandbox',playerSide='Pmc',shouldSkipLootGeneration=True,timeVariant='CURR'))
 def end(raid,character):
  body=dict(serverId=raid['serverId'],results=dict(profile=character,result='SURVIVED',exitName='Opening QA fixture',inSession=False,favorite=False,playTime=60),lostInsuredItems=[],transferItems={});request('/client/match/local/end',body);return body
 fixture=copy.deepcopy(pmc());pocket=next(x for x in fixture['Inventory']['items'] if x.get('slotId')=='Pockets');slot='pocket4';removed={x['_id'] for x in fixture['Inventory']['items'] if x.get('parentId')==pocket['_id'] and x.get('slotId')==slot}
 for _ in range(len(fixture['Inventory']['items'])):
  more={x['_id'] for x in fixture['Inventory']['items'] if x.get('parentId') in removed}-removed
  if not more:break
  removed|=more
 fixture['Inventory']['items']=[x for x in fixture['Inventory']['items'] if x['_id'] not in removed];tape=uuid.uuid4().hex[:24]
 fixture['Inventory']['items'].append(dict(_id=tape,_tpl='5734795124597738002c6176',parentId=pocket['_id'],slotId=slot,location=dict(x=0,y=0,r=0),upd=dict(StackObjectsCount=1,SpawnedInSession=True)))
 end(start(),fixture)
 offer='4b1598bd3308fb2ab03d2516';result=request('/client/game/profile/items/moving',dict(data=[dict(Action='TradingConfirm',type='buy_from_trader',tid=nina,item_id=offer,count=1,scheme_id=0,scheme_items=[dict(id=tape,count=1)])],tm=0));assert not result.get('warnings'),result.get('warnings');assert roots(nina)[offer]['upd']['BuyRestrictionCurrent']==1
 assert roots(nina)[offer]['upd']['BuyRestrictionCurrent']==1,'Browsing must not refill Nina.'
 end(start(),copy.deepcopy(pmc()));assert roots(nina)[offer]['upd']['BuyRestrictionCurrent']==0;assert offer not in roots(quarter),'Restocking must not return moved offers to Rin.'
 request('/client/game/logout');saved=read(profiles[0]);assert saved['spt']['emberNarrative']['contacts'][nina]['state']=='open'
 checkpoint.write_text(json.dumps(dict(session=session,username=username)),'utf-8')
 report=dict(passed=True,version=state['version'],port=a.port,lockedBeforeContact=True,directLockedTradeRejectedWithoutInventoryChange=True,nativeTraderUnlockReward=True,medicalQuestOwnerIsNina=True,splitOffers=len(nroots),noDuplicatedSupply=True,ai2CashQuota=3,nativeNinaBarterPurchased=True,raidReturnRestocksNina=True,restockDoesNotReintroduceOffersToRin=True,persistedContactLedger=True,syntheticQuestCompletionsAndRaidInventory=True,playerInstanceUsed=False,gameUiOrInRaidAcceptance=False)
 a.report.parent.mkdir(parents=True,exist_ok=True);a.report.write_text(json.dumps(report,indent=2)+'\n','utf-8');print(json.dumps(report,indent=2))
if __name__=='__main__':main()
