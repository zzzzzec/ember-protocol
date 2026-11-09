"""Native generation and affordability acceptance, restricted to a disposable Development account."""
from pathlib import Path
import argparse,collections,json,ssl,time,urllib.request,uuid,zlib

def read(p):return json.loads(Path(p).read_text(encoding='utf-8-sig'))

def main():
 p=argparse.ArgumentParser();p.add_argument('--game-root',type=Path,required=True);p.add_argument('--port',type=int,required=True);p.add_argument('--report',type=Path,required=True);a=p.parse_args();root=a.game_root.resolve();state=read(root/'.sptz/installed.json');assert state['instanceKind']=='Development' and state['port']==a.port and a.port!=6969
 mod=root/'SPT/user/mods/Codex-ZombieSurvival';cfg=read(mod/'config.json');rules=read(mod/'threat-generator.json');archetypes={x['key']:x for x in rules['archetypes']};session='';rub='5449016a4bdc2d6f028b456f'
 def request(route,body=None,allow_error=False):
  req=urllib.request.Request('https://127.0.0.1:%s%s'%(a.port,route),data=json.dumps(body or {}).encode(),headers={'Content-Type':'application/json','Cookie':'PHPSESSID='+session,'responsecompressed':'0','requestcompressed':'0'})
  with urllib.request.urlopen(req,context=ssl._create_unverified_context(),timeout=120) as response:raw=response.read()
  for mode in [None,15,-15,31]:
   try:
    text=(raw if mode is None else zlib.decompress(raw,mode)).decode('utf-8-sig').strip()
    if len(text)==24 and all(c in '0123456789abcdef' for c in text.lower()):return text
    data=json.loads(text)
   except (ValueError,zlib.error,UnicodeDecodeError):continue
   if isinstance(data,dict) and 'err' in data:
    if data['err'] and allow_error:return dict(rejected=True)
    assert not data['err'],data;return data.get('data')
   return data
  raise AssertionError('Cannot decode '+route)
 name='Ember-Threat-QA-'+uuid.uuid4().hex[:8];request('/launcher/profile/register',dict(username=name,edition='Standard'));files=[f for f in (root/'SPT/user/profiles').glob('*.json') if read(f).get('info',{}).get('username')==name];assert len(files)==1;session=files[0].stem
 request('/client/game/profile/create',dict(side='Usec',nickname='EmberThreatQA',headId='5cde96047d6c8b20b577f016',voiceId='5fc1223595572123ae7384a3'));request('/client/game/start');request('/client/trading/api/traderSettings')
 def pmc():return next(x for x in request('/client/game/profile/list') if x['Info']['Side'].lower()!='savage')
 actor=pmc();assert sum(x['Health']['Maximum'] for x in actor['Health']['BodyParts'].values())==440
 assert sum(x.get('upd',{}).get('StackObjectsCount',1) for x in actor['Inventory']['items'] if x['_tpl']==rub)==18000
 assert all(any(i['_tpl']==tpl for i in actor['Inventory']['items']) for tpl in ['5e831507ea0a7c419c2f9bd9','544fb3364bdc2d34748b456a'])
 time.sleep(1.05);reset=request('/client/match/local/start',dict(location='sandbox',playerSide='Pmc',shouldSkipLootGeneration=True,timeVariant='CURR'));request('/client/match/local/end',dict(serverId=reset['serverId'],results=dict(profile=pmc(),result='SURVIVED',exitName='QA initial restock',inSession=False,favorite=False,playTime=60),lostInsuredItems=[],transferItems={}))
 market=request('/client/trading/api/getTraderAssort/'+cfg['traderId']);manifest=read(mod/'data/manifest.json');offer=next(x for x in manifest['offers'] if x['tpl']=='573719762459775a626ccbc1' and x['level']==1);cost=market['barter_scheme'][offer['id']][0][0]['count'];assert cost==offer['price'] and offer['id'] in market['loyal_level_items']
 cash=next(i for i in pmc()['Inventory']['items'] if i['_tpl']==rub);reply=request('/client/game/profile/items/moving',dict(data=[dict(Action='TradingConfirm',type='buy_from_trader',tid=cfg['traderId'],item_id=offer['id'],count=96,scheme_id=0,scheme_items=[dict(id=cash['_id'],count=96*cost)])],tm=0));assert not reply.get('warnings'),reply.get('warnings')
 assert sum(x.get('upd',{}).get('StackObjectsCount',1) for x in pmc()['Inventory']['items'] if x['_tpl']==rub)==18000-96*cost
 market=request('/client/trading/api/getTraderAssort/'+cfg['traderId']);remaining=next(i['upd']['BuyRestrictionCurrent'] for i in market['items'] if i['_id']==offer['id']);assert remaining==96
 native_h={i['Id']:i['Price'] for i in read(root/'SPT/SPT_Data/database/templates/handbook.json')['Items']};handbook={i['Id']:i['Price'] for i in request('/client/handbook/templates')['Items']}
 assert all(handbook[tpl]==native_h[tpl] for tpl in ['5755356824597772cb798962','59faff1d86f7746c51718c9c'])
 assert handbook[offer['tpl']]==cfg['economy']['handbookPrices'][offer['tpl']]
 items=request('/client/items');rows=[];plate_fractions=[]
 for map in ['sandbox','bigmap','interchange','factory4_day','rezervbase','laboratory']:
  time.sleep(1.05);raid=request('/client/match/local/start',dict(location=map,playerSide='Pmc',shouldSkipLootGeneration=True,timeVariant='CURR'));spec=cfg['maps'][map];assert len(raid['locationLoot']['BossLocationSpawn'])==spec['total']
  bots=[]
  for role,difficulty,total in [('infectedAssault','normal',spec['total']-spec['shooters']),('infectedPmc','hard',spec['shooters'])]:
   while total:
    count=min(total,100);made=request('/client/game/bot/generate',dict(conditions=[dict(Role=role,Difficulty=difficulty,Limit=count)]));assert count<=len(made)<=max(count,cfg['loading']['profileBatchSize']),(map,role,count,len(made));bots.extend(made[:count]);total-=count
  assert len({b['_id'] for b in bots})==spec['total'];types=collections.Counter();maxarmor=0
  for bot in bots:
   bid=bot['_id'];assert bid.startswith('7a32%02x'%rules['revision']);unit=archetypes[int(bid[6:8],16)];types[unit['id']]+=1
   assert bot['Health']['BodyParts']['Head']['Health']['Maximum']==unit['head'];assert bot['Health']['BodyParts']['Chest']['Health']['Maximum']==unit['chest']
   inventory=bot['Inventory']['items'];ids={i['_id'] for i in inventory}
   for slot,tpl in [('ArmorVest',unit['armorTpl']),('Headwear',unit['helmetTpl'])]:
    equipped=[i for i in inventory if i.get('slotId')==slot];assert len(equipped)==(1 if tpl else 0)
    if tpl:assert equipped[0]['_tpl']==tpl
   for i in inventory:
    if i.get('parentId') and i.get('slotId') not in ['hideout'] and i.get('parentId')!=bot['Inventory']['equipment']:assert i['parentId'] in ids,(map,'orphan item')
    armor=int(items[i['_tpl']]['_props'].get('armorClass',0));maxarmor=max(maxarmor,armor)
    if armor:assert i.get('upd',{}).get('Repairable',{}).get('Durability',0)>0
    if items[i['_tpl']]['_props'].get('armorPlateColliders'):
     maximum=items[i['_tpl']]['_props']['MaxDurability'];repair=i['upd']['Repairable'];fraction=repair['Durability']/maximum;plate_fractions.append(fraction)
     assert .14<=fraction<.71 and repair['MaxDurability']<=maximum*.90 and repair['Durability']<=repair['MaxDurability']
  rows.append(dict(map=map,total=len(bots),composition=dict(types),highestNativeArmorClass=maxarmor,cap=raid['locationLoot']['BotMax']))
  request('/client/match/local/end',dict(serverId=raid['serverId'],results=dict(profile=pmc(),result='SURVIVED',exitName='QA threat acceptance',inSession=False,favorite=False,playTime=60),lostInsuredItems=[],transferItems={}))
 market=request('/client/trading/api/getTraderAssort/'+cfg['traderId']);assert next(i['upd']['BuyRestrictionCurrent'] for i in market['items'] if i['_id']==offer['id'])==0
 assert rows[0]['highestNativeArmorClass']==0 and rows[-1]['highestNativeArmorClass']==5
 assert len(plate_fractions)>100 and sum(v<=.51 for v in plate_fractions)/len(plate_fractions)>.85
 request('/client/game/logout');report=dict(passed=True,version=state['version'],maps=rows,newStart440=True,plateWear=dict(samples=len(plate_fractions),minimum=min(plate_fractions),maximum=max(plate_fractions),mean=sum(plate_fractions)/len(plate_fractions),fractionAtMostHalf=sum(v<=.51 for v in plate_fractions)/len(plate_fractions)),startingRoubles=18000,pmAnd48Rounds=6500+48*cost,pmPurchase96Cost=96*cost,quotaRestoredAfterDeployment=True,ordinaryReferencePricesUnchanged=True,playerInstanceUsed=False,liveArmorImpactAndFpsAcceptance=False)
 a.report.parent.mkdir(parents=True,exist_ok=True);a.report.write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n',encoding='utf-8');print(json.dumps(report,ensure_ascii=False,indent=2))
if __name__=='__main__':main()
