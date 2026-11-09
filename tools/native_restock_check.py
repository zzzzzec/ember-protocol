"""Exercise native buying and real local-raid returns on the dedicated Development QA account."""
from pathlib import Path
import argparse,copy,json,ssl,time,urllib.request,zlib,uuid

def read(path):return json.loads(path.read_text(encoding='utf-8-sig'))
def main():
 parser=argparse.ArgumentParser();parser.add_argument('--game-root',type=Path,required=True);parser.add_argument('--port',type=int,required=True);parser.add_argument('--report',type=Path,required=True);a=parser.parse_args()
 root=a.game_root.resolve();state=read(root/'.sptz/installed.json');assert state['instanceKind']=='Development' and state['port']==a.port and a.port!=6969
 session='';trader=read(root/'SPT/user/mods/Codex-ZombieSurvival/config.json')['traderId'];rub='5449016a4bdc2d6f028b456f';bandage='afbec67057c47f1e93fb1ce0'
 def request(route,body=None,allow_error=False):
  req=urllib.request.Request(f'https://127.0.0.1:{a.port}'+route,data=json.dumps(body or {}).encode(),headers={'Content-Type':'application/json','Cookie':'PHPSESSID='+session,'responsecompressed':'0','requestcompressed':'0'})
  with urllib.request.urlopen(req,context=ssl._create_unverified_context(),timeout=120) as r:raw=r.read()
  for mode in [None,zlib.MAX_WBITS,-zlib.MAX_WBITS,zlib.MAX_WBITS|16]:
   try:
    text=(raw if mode is None else zlib.decompress(raw,mode)).decode('utf-8-sig').strip()
    if len(text)==24 and all(c in '0123456789abcdef' for c in text.lower()):return text
    result=json.loads(text)
    if isinstance(result,dict) and 'err' in result:
     if result['err']:
      if allow_error:return dict(rejected=True)
      raise AssertionError('Native request failed: '+route)
     return result.get('data')
    return result
   except (ValueError,zlib.error,UnicodeDecodeError):pass
  raise AssertionError('Cannot decode '+route)
 def pmc():return next(p for p in request('/client/game/profile/list') if p['Info']['Side'].lower()!='savage')
 def assort():return {p['_id']:p for p in request('/client/trading/api/getTraderAssort/'+trader)['items'] if p.get('parentId')=='hideout'}
 def cash():return sum(p.get('upd',{}).get('StackObjectsCount',1) for p in pmc()['Inventory']['items'] if p['_tpl']==rub)
 def buy(count,allow_error=False,merchant=None,offer_id=None,unit_price=None):
  merchant=merchant or trader;offer_id=offer_id or bandage
  if unit_price is None:unit_price=request("/client/trading/api/getTraderAssort/"+merchant)["barter_scheme"][offer_id][0][0]["count"]
  price=int(round(unit_price*count));scheme=[]
  for p in pmc()['Inventory']['items']:
   if p['_tpl']!=rub:continue
   amount=min(price,p.get('upd',{}).get('StackObjectsCount',1));scheme.append(dict(id=p['_id'],count=amount));price-=amount
   if price==0:break
  assert price==0,'QA account needs reward money or a fresh starting purse.'
  return request('/client/game/profile/items/moving',dict(data=[dict(Action='TradingConfirm',type='buy_from_trader',tid=merchant,item_id=offer_id,count=count,scheme_id=0,scheme_items=scheme)],tm=0),allow_error)
 def start():
  time.sleep(1.05) # Native server IDs contain a seconds-resolution timestamp.
  return request('/client/match/local/start',dict(location='sandbox',playerSide='Pmc',shouldSkipLootGeneration=True,timeVariant='CURR'))
 def end(raid,outcome,profile=None):
  body=dict(serverId=raid['serverId'],results=dict(profile=copy.deepcopy(pmc()) if profile is None else profile,result=outcome,exitName='QA',inSession=False,favorite=False,playTime=60),lostInsuredItems=[],transferItems={})
  request('/client/match/local/end',body);return body
 qa_name='Ember-Restock-QA-'+uuid.uuid4().hex[:8]
 request('/launcher/profile/register',dict(username=qa_name,edition='Standard'))
 profiles=[f for f in (root/'SPT/user/profiles').glob('*.json') if read(f).get('info',{}).get('username')==qa_name];assert len(profiles)==1;session=profiles[0].stem
 request('/client/game/profile/create',dict(side='Usec',nickname='EmberRestockQA',headId='5cde96047d6c8b20b577f016',voiceId='5fc1223595572123ae7384a3'))
 request('/client/game/start');request('/client/trading/api/traderSettings')
 assert cash()==18000,'Restock suite must start with the real new-character budget.'
 # Multiple settlement/replay cases need test capital after verifying the true new-start purse.
 fixture=copy.deepcopy(pmc());pocket=next(i for i in fixture['Inventory']['items'] if i.get('slotId')=='Pockets');credit=uuid.uuid4().hex[:24]
 fixture['Inventory']['items'].append(dict(_id=credit,_tpl=rub,parentId=pocket['_id'],slotId='pocket1',location=dict(x=0,y=0,r=0),upd=dict(StackObjectsCount=200000,SpawnedInSession=True)))
 end(start(),'SURVIVED',fixture)
 moved=request('/client/game/profile/items/moving',dict(data=[dict(Action='Move',item=credit,to=dict(id=pmc()['Inventory']['stash'],container='hideout',location=dict(x=0,y=8,r=0)))],tm=0));assert not moved.get('warnings'),moved.get('warnings')
 assert cash()>=200000,'QA credit must be acquired through raid inventory merge, not a forged stash update.'
 expected={'759076688ecde0eab1c4b6d9':3,bandage:6,'363bee7173ce6c4203082758':6,'ec65cda4378b36df52c19994':3}
 stock=assort()
 for offer,limit in expected.items():assert stock[offer]['upd']['BuyRestrictionMax']==limit
 assert request('/client/trading/api/getTraderAssort/'+trader)['nextResupply']==2147483647
 if stock[bandage]['upd']['BuyRestrictionCurrent']:
  end(start(),'LEFT');stock=assort()
 result=buy(6);assert not result.get('warnings');assert assort()[bandage]['upd']['BuyRestrictionCurrent']==6
 before=cash();denied=buy(1,True);assert denied.get('rejected') or denied.get('warnings');assert cash()==before
 fence_id='579dc571d53a0658a154fbec'
 def fence():return request('/client/trading/api/getTraderAssort/'+fence_id)
 def fence_ids():return {x['_id'] for x in fence()['items'] if x.get('parentId')=='hideout'}
 before_fence=fence_ids()
 death=start();assert fence_ids()==before_fence,'Deployment alone must not regenerate Fence.'
 assert assort()[bandage]['upd']['BuyRestrictionCurrent']==6,'Starting a raid must not reset quota.'
 body=end(death,'KILLED');stock=assort();assert stock[bandage]['upd']['BuyRestrictionCurrent']==0 and stock[bandage]['upd']['StackObjectsCount']==6
 after_fence=fence_ids();assert after_fence!=before_fence,'Death return must refresh Fence too.'
 result=buy(1);assert not result.get('warnings');assert assort()[bandage]['upd']['BuyRestrictionCurrent']==1
 request('/client/match/local/end',body);assert fence_ids()==after_fence,'Replayed settlement must not regenerate Fence.'
 assert assort()[bandage]['upd']['BuyRestrictionCurrent']==1,'Replayed settlement must not refill.'
 end(start(),'SURVIVED');assert assort()[bandage]['upd']['BuyRestrictionCurrent']==0
 for outcome in ['LEFT','RUNNER']:
  result=buy(1);assert not result.get('warnings');assert assort()[bandage]['upd']['BuyRestrictionCurrent']==1
  end(start(),outcome);assert assort()[bandage]['upd']['BuyRestrictionCurrent']==0,('Terminal return did not restock',outcome)
 # Exercise the restored native Fence trading route with one affordable stock item.
 market=fence();choices=[]
 for root_item in market['items']:
  if root_item.get('parentId')!='hideout' or root_item.get('upd',{}).get('StackObjectsCount',0)<1:continue
  scheme=market['barter_scheme'].get(root_item['_id'],[])
  if len(scheme)==1 and len(scheme[0])==1 and scheme[0][0]['_tpl']==rub and 0<scheme[0][0]['count']<=cash():choices.append((scheme[0][0]['count'],root_item['_id']))
 assert choices,'The sampled Fence market has no affordable item for the QA purchase.'
 price,offer_id=min(choices);before_cash=cash();result=buy(1,merchant=fence_id,offer_id=offer_id,unit_price=price);assert not result.get('warnings') and cash()<before_cash
 request('/client/game/logout')
 saved=read(profiles[0]);ledger=saved['spt']['emberRaidRestock'];assert ledger['count']>=2
 report=dict(passed=True,port=a.port,version=state['version'],levelOneMedicalLimits=expected,realNativeBuyLimitEnforced=True,freshDedicatedQaAccount=True,startingBudget=18000,failedExtraPurchaseDoesNotConsumeMoney=True,noRestockOnDeployment=True,deathReturnRestocks=True,survivedReturnRestocks=True,leftAndRunthroughReturnsRestock=True,replayedSettlementDoesNotRestock=True,persistedRaidLedger=True,fencePurchaseWorks=True,fenceRestocksOnDeath=True,fenceDoesNotRefreshOnReplay=True,playerInstanceUsed=False,gameUiOrInRaidAcceptance=False)
 a.report.parent.mkdir(parents=True,exist_ok=True);a.report.write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8');print(json.dumps(report,indent=2))
if __name__=='__main__':main()
