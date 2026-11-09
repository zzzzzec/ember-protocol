"""Verify loyalty boundaries and economy payloads using a disposable Development profile."""
from pathlib import Path
import argparse,copy,json,ssl,time,urllib.request,uuid,zlib
from native_check import contains, effective_custom_recipe

def read(p):return json.loads(p.read_text(encoding='utf-8-sig'))
def main():
 p=argparse.ArgumentParser();p.add_argument('--game-root',type=Path,required=True);p.add_argument('--port',type=int,required=True);p.add_argument('--report',type=Path,required=True);p.add_argument('--recipes-report',type=Path,required=True);a=p.parse_args();root=a.game_root.resolve();state=read(root/'.sptz/installed.json')
 assert state['instanceKind']=='Development' and state['port']==a.port and a.port!=6969
 mod=root/'SPT/user/mods/Codex-ZombieSurvival';cfg=read(mod/'config.json');qdata=read(mod/'data/trader.json');session=''
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
 name='Ember-Economy-QA-'+uuid.uuid4().hex[:8];request('/launcher/profile/register',dict(username=name,edition='Standard'));files=[f for f in (root/'SPT/user/profiles').glob('*.json') if read(f).get('info',{}).get('username')==name];assert len(files)==1;session=files[0].stem
 request('/client/game/profile/create',dict(side='Usec',nickname='EmberEconomyQA',headId='5cde96047d6c8b20b577f016',voiceId='5fc1223595572123ae7384a3'));request('/client/game/start')
 settings=request('/client/trading/api/traderSettings');settings={t['_id']:t for t in (settings.values() if isinstance(settings,dict) else settings)}
 qid=cfg['traderId'];fid=cfg['fence']['traderId'];qlevels=settings[qid]['loyaltyLevels'];flevels=settings[fid]['loyaltyLevels']
 assert qlevels==qdata['base']['loyaltyLevels']
 assert [x['minLevel'] for x in qlevels]==[1,8,18,30]
 assert [x['minStanding'] for x in qlevels]==[0,.2,.5,.9]
 assert [x['minLevel'] for x in flevels]==cfg['fence']['loyaltyLevelRequirements']
 assert all(x['buy_price_coef']==cfg['fence']['buyPriceCoef'] for x in flevels)
 assert all((100-q['buy_price_coef'])>(100-f['buy_price_coef']) for q in qlevels for f in flevels)
 handbook_data=request('/client/handbook/templates');item_data=request('/client/items')
 a.recipes_report.parent.mkdir(parents=True,exist_ok=True);a.recipes_report.with_name('effective-tables.json').write_text(json.dumps(dict(handbook=handbook_data,items=item_data)),encoding='utf-8')
 handbook={i['Id']:i['Price'] for i in handbook_data['Items']};assert all(handbook[k]==v for k,v in cfg['economy']['handbookPrices'].items())
 valuation=read(mod/'economy-report.json');assert all(handbook[x['tpl']]==x['price'] for x in valuation['valuations'])
 recipes=request('/client/hideout/production/recipes');recipes=recipes['recipes'] if isinstance(recipes,dict) else recipes
 byid={r['_id']:r for r in recipes}
 multiplier=read(mod/'startup-report.json')['customRecipeTimeMultiplier']
 for r in read(mod/'data/recipes.json'):assert contains(byid[r['_id']],effective_custom_recipe(r,multiplier)),r['_id']
 for rid,maximum in read(mod/'data/camp-growth.json')['nativeRecipeBatchCaps'].items():assert byid[rid]['count']==maximum,(rid,byid[rid]['count'],maximum)
 a.recipes_report.parent.mkdir(parents=True,exist_ok=True);a.recipes_report.write_text(json.dumps(recipes,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
 def pmc():return next(p for p in request('/client/game/profile/list') if p['Info']['Side'].lower()!='savage')
 exp=request('/client/globals')['config']['exp']['level']['exp_table'];cases=[];fence_samples=[]
 def inspect_fence(market):
  for item in market['items']:
   if item.get('parentId')!='hideout' or item.get('upd',{}).get('StackObjectsCount',0)<1:continue
   tpl=item['_tpl']
   while tpl in item_data:
    if tpl in {'5795f317245977243854e041','5448bf274bdc2dfc2f8b456a','62f109593b54472778797866'}:
     assert handbook.get(item['_tpl'],0)>1,('Placeholder-priced container in Fence stock',item['_tpl'])
     break
    tpl=item_data[tpl]['_parent']
   oid=item['_id'];scheme=market['barter_scheme'].get(oid,[])
   assert len(scheme)==1 and len(scheme[0])==1 and scheme[0][0]['_tpl']=='5449016a4bdc2d6f028b456f' and scheme[0][0]['count']>0,('Fence stock must be cash only',oid,scheme)
   def remaining_value(i,root=False):
    props=item_data[i['_tpl']]['_props'];upd=i.get('upd',{});v=handbook.get(i['_tpl'],0)
    if 'Repairable' in upd:
     v*=min(1,upd['Repairable']['MaxDurability']/max(props.get('Durability',props.get('MaxDurability',1)),1))
    if 'MedKit' in upd:v*=min(1,upd['MedKit']['HpResource']/max(props.get('MaxHpResource',1),1))
    if 'FoodDrink' in upd:v*=min(1,upd['FoodDrink']['HpPercent']/max(props.get('MaxResource',1),1))
    if 'Key' in upd and props.get('MaximumNumberOfUsage',0)>0:v*=max(0,1-upd['Key'].get('NumberOfUsages',0)/props['MaximumNumberOfUsage'])
    if 'Resource' in upd:v*=.1+.9*min(1,upd['Resource'].get('Value',0)/max(props.get('MaxResource',1),1))
    if 'RepairKit' in upd:v*=min(1,upd['RepairKit'].get('Resource',0)/max(props.get('MaxRepairResource',1),1))
    return v*(1 if root else upd.get('StackObjectsCount',1))
   total=remaining_value(item,True);pending=[oid];seen={oid}
   for parent in pending:
    for child in market['items']:
     if child.get('parentId')==parent and child['_id'] not in seen:
      seen.add(child['_id']);pending.append(child['_id']);total+=remaining_value(child)
   fence_samples.append(dict(tpl=item['_tpl'],asking=scheme[0][0]['count'],conditionAdjustedResaleUpperBound=int(total*.4),wornOrUsed=any(k in item.get('upd',{}) for k in ['Repairable','MedKit','FoodDrink','Resource','Key'])))
 def fixture(level,qstanding,fstanding):
  time.sleep(1.05);raid=request('/client/match/local/start',dict(location='sandbox',playerSide='Pmc',shouldSkipLootGeneration=True,timeVariant='CURR'));profile=copy.deepcopy(pmc());profile['Info']['Experience']=sum(row['exp'] for row in exp[:level]);profile['Info']['Level']=level
  profile['TradersInfo'][qid]['standing']=qstanding;profile['TradersInfo'][fid]['standing']=fstanding
  for merchant in [qid,fid]:profile['TradersInfo'][merchant]['salesSum']=0
  request('/client/match/local/end',dict(serverId=raid['serverId'],results=dict(profile=profile,result='SURVIVED',exitName='QA economy boundary fixture',inSession=False,favorite=False,playTime=60),lostInsuredItems=[],transferItems={}))
  request('/client/game/start');request('/client/trading/api/getTraderAssort/'+qid);inspect_fence(request('/client/trading/api/getTraderAssort/'+fid));request('/client/game/logout');actual=read(files[0])['characters']['pmc'];assert actual['Info']['Level']==level,(level,actual['Info']['Level']);return actual
 for level,standing,expected in [(1,0,1),(7,.9,1),(8,.19,1),(8,.2,2),(17,.9,2),(18,.49,2),(18,.5,3),(29,.9,3),(30,.89,3),(30,.9,4)]:
  profile=fixture(level,standing,0);actual=profile['TradersInfo'][qid].get('loyaltyLevel',0);assert actual==expected,(level,standing,expected,actual,profile['TradersInfo'][qid],profile['Info'].get('Level'));cases.append(dict(merchant='quartermaster',level=level,standing=standing,loyalty=actual))
 for level,standing,expected in [(19,6,1),(20,5.99,1),(20,6,2)]:
  profile=fixture(level,.9,standing);market=request('/client/trading/api/getTraderAssort/'+fid);inspect_fence(market);assert all(x<=expected for x in market['loyal_level_items'].values()),'Fence stock must respect both level and standing';actual=profile['TradersInfo'][fid].get('loyaltyLevel',0);assert actual==expected,(level,standing,expected,actual);cases.append(dict(merchant='fence',level=level,standing=standing,loyalty=actual))
 request('/client/game/logout')
 report=dict(passed=True,version=state['version'],quartermasterLevels=qlevels,fenceLevels=flevels,boundaryCases=cases,clientEconomyPayloadsMatch=True,fenceSamples=fence_samples,fencePriceCandidates=[x for x in fence_samples if x['asking']<x['conditionAdjustedResaleUpperBound']],effectiveRecipes=len(recipes),handbookOverridesMatch=True,unifiedValuations=len(valuation['valuations']),playerInstanceUsed=False,syntheticXpAndStandingFixtures=True,visibleTradePriceOrLongCampaignAcceptance=False)
 report['fenceCashOnly']=True
 assert not report['fencePriceCandidates'],report['fencePriceCandidates'][:8]
 a.report.parent.mkdir(parents=True,exist_ok=True);a.report.write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n',encoding='utf-8');print('PASS %s native loyalty boundaries; %s effective recipes; economy payloads match.'%(len(cases),len(recipes)))
if __name__=='__main__':main()
