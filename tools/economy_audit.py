"""Read-only economy review. Uses matching local game tables; never opens a player profile."""
import argparse, collections, json, math
from pathlib import Path

RUB = '5449016a4bdc2d6f028b456f'
def read(path): return json.loads(Path(path).read_text(encoding='utf-8-sig'))
def main():
 p=argparse.ArgumentParser();p.add_argument('--game-root',type=Path,required=True);p.add_argument('--report',type=Path,required=True);p.add_argument('--source-root',type=Path,default=Path(__file__).resolve().parents[1]);p.add_argument('--live-recipes',type=Path);p.add_argument('--live-tables',type=Path);a=p.parse_args()
 root=a.source_root;data=root/'content/server/data';game=a.game_root/'SPT/SPT_Data/database'
 cfg=read(root/'config/gameplay.json');meta=read(data/'manifest.json');trader=read(data/'trader.json');quests=read(data/'quests.json');recipes=read(data/'recipes.json');loc=read(data/'locales.json')['ch'];items=read(game/'templates/items.json')
 prices={i['Id']:i['Price'] for i in read(game/'templates/handbook.json')['Items']}
 if a.live_tables:
  tables=read(a.live_tables);items=tables['items'];prices={i['Id']:i['Price'] for i in tables['handbook']['Items']}
 prices.update(cfg['economy']['handbookPrices'])
 native_fence=read(game/'traders/579dc571d53a0658a154fbec/base.json')
 qrate=max((100-l['buy_price_coef'])/100 for l in trader['base']['loyaltyLevels']);frate=(100-cfg['fence'].get('buyPriceCoef',native_fence['loyaltyLevels'][0]['buy_price_coef']))/100;rate=max(qrate,frate)
 name=lambda tpl:loc.get(tpl+' ShortName',items.get(tpl,{}).get('_name',tpl))
 def category(tpl):
  props=items.get(tpl,{}).get('_props',{})
  if props.get('IsStationaryWeapon'):return 'other'
  if 'ammoType' in props:return 'ammo'
  if tpl in ['5d02778e86f774203e7dedbe','5d02797c86f774203f38e30a']:return 'advancedMedical'
  categories={'5422acb9af1c889c16000029':'pistol' if props.get('weapClass')=='pistol' else 'longGun','543be5cb4bdc2deb348b4568':'ammo','644120aa86ffbe10ee032b6f':'plate','5448e8d04bdc2ddf718b4569':'food','5448e8d64bdc2dce718b4568':'drink','5448f3a64bdc2d60728b456a':'stim','5448f3a14bdc2d27728b4569':'drug','5448f39d4bdc2d0a728b4568':'advancedMedical' if props.get('MaxHpResource',0)>=600 else 'medical','5448f3ac4bdc2dce718b4569':'medical','5448e54d4bdc2dcc718b4568':'armor','5448e5724bdc2ddf718b4568':'armor'}
  seen=set();current=tpl
  while current in items and current not in seen:
   seen.add(current)
   if current in categories:return categories[current]
   if float(props.get('armorClass',0) or 0)>0:return 'armor'
   current=items[current].get('_parent','')
  slots=props.get('StackSlots',[])
  if '5485a8684bdc2da71d8b4567' in json.dumps(slots):return 'ammo'
  return 'other'
 def unpack(tpl):
  slots=items.get(tpl,{}).get('_props',{}).get('StackSlots',[])
  if len(slots)==1:
   filters=slots[0].get('_props',{}).get('filters',[])
   if len(filters)==1 and len(filters[0].get('Filter',[]))==1:
    ammo=filters[0]['Filter'][0]
    if category(ammo)=='ammo':return ammo,slots[0]['_max_count']
  return tpl,1
 def value(tpl):
  inner,count=unpack(tpl)
  return max(prices.get(tpl,0),prices.get(inner,0)*count)
 assortment=trader['assort'];all_items=assortment['items'];byid={i['_id']:i for i in all_items}
 def tree(oid):
  result=[byid[oid]];seen={oid}
  for item in result:
   for child in all_items:
    if child.get('parentId')==item['_id'] and child['_id'] not in seen:result.append(child);seen.add(child['_id'])
  return result
 reference_rows=[]
 if cfg['economy'].get('retailReference',False):
  candidates=collections.defaultdict(list)
  for offer in meta['offers']:
   if offer['price']>0 and ('retailTemplates' not in cfg['economy'] or offer['tpl'] in cfg['economy']['retailTemplates']):candidates[offer['tpl']].append(offer)
  resolving=set();resolved={}
  def resolve(tpl):
   if tpl in resolved:return resolved[tpl]
   if tpl not in candidates:return prices.get(tpl,0)
   assert tpl not in resolving,('Cash reference cycle',tpl)
   resolving.add(tpl);choices=[]
   for offer in candidates[tpl]:
    child_value=sum(resolve(i['_tpl'])*i.get('upd',{}).get('StackObjectsCount',1) for i in tree(offer['id'])[1:])
    assert child_value<offer['price'],('Attachments exceed complete retail offer',offer['name'],offer['price'],child_value)
    choices.append(offer['price']-child_value)
   result=min(choices);resolving.remove(tpl);resolved[tpl]=result;return result
  for tpl in candidates:resolve(tpl)
  reference_rows=[dict(tpl=t,name=name(t),before=prices.get(t,0),reference=v) for t,v in resolved.items()]
  prices.update(resolved)
  for tpl,item in items.items():
   slots=item.get('_props',{}).get('StackSlots',[])
   if len(slots)==1 and slots[0].get('_name')=='cartridges':
    ammo,count=unpack(tpl)
    if ammo in resolved and category(tpl)=='ammo':prices[tpl]=1
 cash={};offer_rows=[];routes=[];flips=[]
 for o in meta['offers']:
  price=o['price'];oid=o['id'];rootitem=byid[oid];tpl=o['tpl'];inner,count=unpack(tpl)
  # A trader root's StackObjectsCount is stock, not the quantity in one purchase.
  base=prices.get(tpl,0)+sum(prices.get(i['_tpl'],0)*i.get('upd',{}).get('StackObjectsCount',1) for i in tree(oid)[1:]);resale=math.floor(base*rate)
  row=dict(id=oid,name=o['name'],tpl=tpl,category=category(tpl),tier=o['level'],unlock=o['unlock'],cash=price,quota=o['limit'],resaleUpperBound=resale)
  if price>0:
   cash[inner]=min(cash.get(inner,float('inf')),price/count)
   routes.append(dict(id=oid,kind='cash',inputs={RUB:price},output=inner,count=count))
   if resale>price:flips.append(row)
  else:
   requirements=assortment['barter_scheme'][oid][0];row['inputs']=[dict(tpl=x['_tpl'],name=name(x['_tpl']),count=x['count']) for x in requirements]
   row['inputResale']=round(sum(value(x['_tpl'])*x['count']*rate for x in requirements),2)
   routes.append(dict(id=oid,kind='barter',inputs={x['_tpl']:x['count'] for x in requirements},output=inner,count=count))
  offer_rows.append(row)
 for row in offer_rows:
  inner,count=unpack(row['tpl']);row['cashAlternative']=None if inner not in cash else round(cash[inner]*count,2)
 native=read(game/'hideout/production.json');native=native.get('recipes',[]) if isinstance(native,dict) else native
 if a.live_recipes:native=read(a.live_recipes)
 else:
  for r in native:
   tpl=r['endProduct'];cat=category(tpl);props=items.get(tpl,{}).get('_props',{});cal=props.get('Caliber','')
   if cat in ['medical','advancedMedical','drug','stim','food','drink']:r['count']=min(r.get('count',1),1 if cat in ['advancedMedical','stim'] else 2)
   if cat=='ammo':r['count']=min(r.get('count',1),4 if any(s in cal for s in ['762x51','762x54','9x39','86x70','127x']) else 12 if any(s in cal for s in ['545x39','556x45','762x39']) else 24)
   cap=read(data/'camp-growth.json')['nativeRecipeBatchCaps'].get(r['_id'])
   if cap is not None:r['count']=min(r['count'],cap)
 customids={r['_id'] for r in recipes};effective=native if a.live_recipes else [r for r in native if r['_id'] not in customids]+recipes
 recipe_rows=[]
 for r in effective:
  consumed=collections.defaultdict(float);resources=[];tools=[];gates=[]
  for req in r['requirements']:
   if req['type']=='Item':consumed[req['templateId']]+=req['count']
   elif req['type']=='Resource':
    tpl=req['templateId'];cap=max(items.get(tpl,{}).get('_props',{}).get('Resource',0),items.get(tpl,{}).get('_props',{}).get('MaxResource',0),1);consumed[tpl]+=req.get('resource',req.get('count',0))/cap;resources.append(req)
   elif req['type']=='Tool':tools.append(req['templateId'])
   elif req['type']=='QuestComplete':gates.append(req.get('questId',req.get('questid')))
  tpl=r['endProduct'];inner,quantity=unpack(tpl);count=r['count']*quantity
  cost=sum(value(t)*n*rate for t,n in consumed.items());out=value(tpl)*r['count']*rate;direct=cash.get(inner)
  row=dict(id=r['_id'],custom=r['_id'] in customids,name=name(tpl),tpl=tpl,category=category(tpl),count=r['count'],minutes=round(r['productionTime']/60,2),inputResale=round(cost,2),outputResaleUpperBound=round(out,2),cashAlternative=None if direct is None else round(direct*count,2),inputs=[dict(tpl=t,name=name(t),count=n) for t,n in consumed.items()],tools=tools,resources=resources,questGates=gates,areaRequirements=[req for req in r['requirements'] if req['type']=='Area'],continuous=r.get('continuous',False))
  recipe_rows.append(row)
  if consumed and not row['continuous']:routes.append(dict(id=r['_id'],kind='craft',inputs=dict(consumed),output=inner,count=count))
 # Optimistic acquisition graph: relax cash-purchasable inputs, tools/caps/gates/fuel excluded.
 costs={RUB:1.0};profit_routes=[]
 for _ in range(len(routes)):
  changed=False
  for route in routes:
   if all(t in costs for t in route['inputs']):
    unit=sum(costs[t]*n for t,n in route['inputs'].items())/route['count']
    if unit+1e-8<costs.get(route['output'],float('inf')):costs[route['output']]=unit;changed=True
  if not changed:break
 for route in routes:
  if all(t in costs for t in route['inputs']):
   cost=sum(costs[t]*n for t,n in route['inputs'].items());resale=value(route['output'])*route['count']*rate
   if resale>cost+1:profit_routes.append(dict(id=route['id'],kind=route['kind'],name=name(route['output']),inputCashLowerBound=round(cost,2),outputResaleUpperBound=round(resale,2)))
 quest_rows=[];quest_meta={q['id']:q for q in meta['quests']}
 for qid,q in quests.items():
  rewards=q['rewards']['Success'];money=sum(i.get('value',0) for i in rewards if i['type']=='Item' and i.get('items',[{}])[0].get('_tpl')==RUB);xp=sum(i.get('value',0) for i in rewards if i['type']=='Experience');standing=sum(i.get('value',0) for i in rewards if i['type']=='TraderStanding');handovers=[]
  for c in q['conditions']['AvailableForFinish']:
   if c['conditionType'] in ['HandoverItem','FindItem']:handovers.append(dict(type=c['conditionType'],targets=c.get('target'),count=c.get('value'),fir=c.get('onlyFoundInRaid',False)))
  quest_rows.append(dict(id=qid,name=loc.get(qid+' name',qid),lane=quest_meta[qid]['lane'],xp=xp,cash=money,standing=standing,prerequisites=[c['target'] for c in q['conditions']['AvailableForStart'] if c['conditionType']=='Quest'],handovers=handovers,rewardItemValue=sum(value(i['_tpl'])*(i.get('upd',{}).get('StackObjectsCount',1) if i.get('slotId') is None else 1) for rw in rewards if rw['type']=='Item' for i in rw.get('items',[]) if i['_tpl']!=RUB)))
 corpse=cfg.get('corpseLoot',{});corpse_rows=[]
 for mapid,spec in cfg['maps'].items():
  chance=corpse['baseChance']+max(0,spec['rank']-1)*corpse['perRankBonus'] if corpse.get('enabled') else 0
  corpse_rows.append(dict(map=mapid,population=spec['total'],supplyChance=round(chance,4),expectedSupplyBodies=round(spec['total']*chance,2)))
 report=dict(version=cfg['version'],retailReferencePrices=reference_rows,scope=dict(offers=len(offer_rows),barters=len(meta['barters']),fenceBarters=0,customCrafts=len(recipes),effectiveCrafts=len(effective),quests=len(quests)),buybackRates=dict(quartermaster=qrate,fence=frate),quartermasterLevels=trader['base']['loyaltyLevels'],directCashFlipCandidates=flips,cashReachableProfitCandidates=profit_routes,offers=offer_rows,fenceOffers=[],recipes=recipe_rows,quests=quest_rows,corpse=corpse_rows,questTotals=dict(cash=sum(q['cash'] for q in quest_rows),xp=sum(q['xp'] for q in quest_rows)),limitations=['Nominal full-condition handbook values; worn/resource-used items yield less.','Resale graph ignores availability, quest/tier gates, quotas, tool capital, power, water and time. Positive candidates need route review.','FIR turn-ins, maps, seasonal sources and combat success require actual play observations.','Native recipes are source-projected unless --live-recipes is supplied.'])
 a.report.parent.mkdir(parents=True,exist_ok=True);a.report.write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
 print(json.dumps({k:report[k] for k in ['version','scope','buybackRates','questTotals','directCashFlipCandidates','cashReachableProfitCandidates']},ensure_ascii=False,indent=2))
if __name__=='__main__':main()
