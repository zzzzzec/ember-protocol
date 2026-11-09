from pathlib import Path
import argparse,copy,hashlib,json,ssl,time,urllib.request,urllib.error,uuid,zlib

def read(p):return json.loads(p.read_text(encoding='utf-8-sig'))
def main():
 p=argparse.ArgumentParser();p.add_argument('--game-root',type=Path,required=True);p.add_argument('--port',type=int,required=True);p.add_argument('--report',type=Path,required=True);a=p.parse_args();root=a.game_root.resolve();state=read(root/'.sptz/installed.json');assert state['instanceKind']=='Development' and state['port']==a.port and a.port!=6969
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
 name='Ember-Currency-QA-'+uuid.uuid4().hex[:7];request('/launcher/profile/register',dict(username=name,edition='Standard'));files=[f for f in (root/'SPT/user/profiles').glob('*.json') if read(f).get('info',{}).get('username')==name];assert len(files)==1;session=files[0].stem
 request('/client/game/profile/create',dict(side='Usec',nickname='EmberCashQA',headId='5cde96047d6c8b20b577f016',voiceId='5fc1223595572123ae7384a3'));request('/client/game/start');settings=request('/client/trading/api/traderSettings');settings={t['_id']:t for t in (settings.values() if isinstance(settings,dict) else settings)}
 for tid in [cfg['traderId'],cfg['fence']['traderId']]:assert rub in settings[tid]['items_buy_prohibited']['id_list']
 profile=pmc();cash=next(i for i in profile['Inventory']['items'] if i['_tpl']==rub);ordinary=next(i for i in profile['Inventory']['items'] if i.get('slotId')=='Holster')
 for tid,entries in [(cfg['traderId'],[dict(id=cash['_id'],count=1000)]),(cfg['fence']['traderId'],[dict(id=cash['_id'],count=1000)]),(cfg['traderId'],[dict(id=ordinary['_id'],count=1),dict(id=cash['_id'],count=1000)])]:
  before=fingerprint();result=action(dict(Action='TradingConfirm',type='sell_to_trader',tid=tid,price=400,items=entries),True);assert result.get('rejected') or result.get('warnings'),result;assert fingerprint()==before,'Currency rejection must precede all inventory, sales and XP mutation.'
 # An ordinary native purchase still spends roubles normally.
 market=request('/client/trading/api/getTraderAssort/'+cfg['traderId']);offer='afbec67057c47f1e93fb1ce0';cost=market['barter_scheme'][offer][0][0]['count'];before_cash=sum(i.get('upd',{}).get('StackObjectsCount',1) for i in pmc()['Inventory']['items'] if i['_tpl']==rub)
 result=action(dict(Action='TradingConfirm',type='buy_from_trader',tid=cfg['traderId'],item_id=offer,count=1,scheme_id=0,scheme_items=[dict(id=cash['_id'],count=cost)]));assert not result.get('warnings'),result
 after_cash=sum(i.get('upd',{}).get('StackObjectsCount',1) for i in pmc()['Inventory']['items'] if i['_tpl']==rub);assert before_cash-after_cash==cost
 # A carried foreign currency stack goes through an ordinary survived QA fixture, then the native sell route.
 profile=copy.deepcopy(pmc());pockets=next(i for i in profile['Inventory']['items'] if i.get('slotId')=='Pockets');foreign=dict(_id=uuid.uuid4().hex[:24],_tpl=usd,parentId=pockets['_id'],slotId='pocket4',location=dict(x=0,y=0,r=0,isSearched=True),upd=dict(StackObjectsCount=20));profile['Inventory']['items'].append(foreign)
 time.sleep(1.05);raid=request('/client/match/local/start',dict(location='sandbox',playerSide='Pmc',shouldSkipLootGeneration=True,timeVariant='CURR'));request('/client/match/local/end',dict(serverId=raid['serverId'],results=dict(profile=profile,result='SURVIVED',exitName='QA currency fixture',inSession=False,favorite=False,playTime=60),lostInsuredItems=[],transferItems={}))
 assert any(i['_id']==foreign['_id'] for i in pmc()['Inventory']['items']);handbook=request('/client/handbook/templates');price=round(next(i['Price'] for i in handbook['Items'] if i['Id']==usd)*20*.4);before_cash=sum(i.get('upd',{}).get('StackObjectsCount',1) for i in pmc()['Inventory']['items'] if i['_tpl']==rub)
 result=action(dict(Action='TradingConfirm',type='sell_to_trader',tid=cfg['traderId'],price=price,items=[dict(id=foreign['_id'],count=20)]));assert not result.get('warnings'),result;assert not any(i['_id']==foreign['_id'] for i in pmc()['Inventory']['items']);after_cash=sum(i.get('upd',{}).get('StackObjectsCount',1) for i in pmc()['Inventory']['items'] if i['_tpl']==rub);assert after_cash-before_cash==price
 request('/client/game/logout');report=dict(passed=True,version=state['version'],uiTraderPayloadBlocksRoubles=True,bothTradersRejectRoubleSales=True,mixedSaleAtomic=True,rejectedSalesPreserveInventoryXpAndTraderStats=True,nativeRoublePurchaseWorks=True,foreignCurrencySaleWorks=True,playerInstanceUsed=False,syntheticCurrencyFixture=True,liveUiAcceptance=False);a.report.parent.mkdir(parents=True,exist_ok=True);a.report.write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8');print('PASS rouble sale rejection, atomic mixed sale, purchases and foreign currency exchange.')
if __name__=='__main__':main()
