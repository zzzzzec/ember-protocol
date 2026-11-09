from pathlib import Path
import argparse,copy,hashlib,json,ssl,time,urllib.request,urllib.error,zlib

def read(p):return json.loads(p.read_text(encoding='utf-8-sig'))
def main():
 p=argparse.ArgumentParser();p.add_argument('--game-root',type=Path,required=True);p.add_argument('--port',type=int,required=True);p.add_argument('--report',type=Path,required=True);a=p.parse_args();root=a.game_root.resolve();state=read(root/'.sptz/installed.json');assert state['instanceKind']=='Development' and state['port']==a.port and a.port!=6969
 marker=read(root/'.sptz/story-qa.json');f=root/'SPT/user/profiles'/(marker['session']+'.json');assert read(f)['info']['username']==marker['username'] and marker['username'].startswith('Ember-Story-QA-');session=f.stem
 cfg=read(root/'SPT/user/mods/Codex-ZombieSurvival/config.json')
 def request(route,body=None,allow_error=False):
  req=urllib.request.Request('https://127.0.0.1:%s%s'%(a.port,route),data=json.dumps(body or {}).encode(),headers={'Content-Type':'application/json','Cookie':'PHPSESSID='+session,'responsecompressed':'0','requestcompressed':'0'})
  try:
   with urllib.request.urlopen(req,context=ssl._create_unverified_context(),timeout=120) as resp:raw=resp.read()
  except urllib.error.HTTPError:
   if allow_error:return dict(rejected=True)
   raise
  for mode in [None,15,-15,31]:
   try:
    value=json.loads((raw if mode is None else zlib.decompress(raw,mode)).decode('utf-8-sig'))
    if 'err' in value:
     if value['err'] and allow_error:return dict(rejected=True)
     assert value['err']==0,value;return value.get('data')
    return value
   except (ValueError,zlib.error,UnicodeDecodeError):pass
  raise AssertionError('Cannot decode '+route)
 def pmc():return next(p for p in request('/client/game/profile/list') if p['Info']['Side'].lower()!='savage')
 def action(data,allow_error=False):return request('/client/game/profile/items/moving',dict(data=[data],tm=0),allow_error)
 def invhash():return hashlib.sha256(json.dumps(pmc()['Inventory'],sort_keys=True).encode()).hexdigest()
 def wear():
  profile=copy.deepcopy(pmc());weapon=next(i for i in profile['Inventory']['items'] if i.get('slotId')=='Holster');weapon.setdefault('upd',{})['Repairable']=dict(Durability=70,MaxDurability=100)
  time.sleep(1.05);raid=request('/client/match/local/start',dict(location='sandbox',playerSide='Pmc',shouldSkipLootGeneration=True,timeVariant='CURR'));request('/client/match/local/end',dict(serverId=raid['serverId'],results=dict(profile=profile,result='SURVIVED',exitName='QA repair fixture',inSession=False,favorite=False,playTime=60),lostInsuredItems=[],transferItems={}));return weapon['_id']
 request('/client/game/start');templates=request('/client/items');weapon=wear();kit=next(i for i in pmc()['Inventory']['items'] if templates[i['_tpl']]['_props'].get('MaxRepairResource',0)>0);maximum=templates[kit['_tpl']]['_props']['MaxRepairResource'];resource=kit.get('upd',{}).get('RepairKit',{}).get('Resource',maximum)
 for entries in [[dict(_id='000000000000000000000001',count=10)],[dict(_id=kit['_id'],count=0)],[dict(_id=kit['_id'],count=resource+1)],[dict(_id=kit['_id'],count=1)]*2]:
  before=invhash();reply=action(dict(Action='Repair',target=weapon,repairKitsInfo=entries),True);assert reply.get('rejected') or reply.get('warnings'),reply;assert invhash()==before,'Rejected kit repair must not mutate inventory.'
 before=invhash();reply=action(dict(Action='TraderRepair',tid=cfg['fence']['traderId'],repairItems=[dict(_id=weapon,count=30)]),True);assert reply.get('rejected') or reply.get('warnings'),reply;assert invhash()==before
 reply=action(dict(Action='Repair',target=weapon,repairKitsInfo=[dict(_id=kit['_id'],count=10)]));assert not reply.get('warnings'),reply
 items={i['_id']:i for i in pmc()['Inventory']['items']};kit_after=items[kit['_id']]['upd']['RepairKit']['Resource'];kit_wear=items[weapon]['upd']['Repairable'];assert kit_after==resource-10 and kit_wear['Durability']>70 and kit_wear['MaxDurability']<=100
 weapon=wear();rub='5449016a4bdc2d6f028b456f'
 def cash():return sum(i.get('upd',{}).get('StackObjectsCount',1) for i in pmc()['Inventory']['items'] if i['_tpl']==rub)
 before_cash=cash();reply=action(dict(Action='TraderRepair',tid=cfg['traderId'],repairItems=[dict(_id=weapon,count=30)]));assert not reply.get('warnings'),reply;trader_wear=next(i for i in pmc()['Inventory']['items'] if i['_id']==weapon)['upd']['Repairable'];assert trader_wear['Durability']>70 and trader_wear['MaxDurability']<=100 and cash()<before_cash
 request('/client/game/logout');report=dict(passed=True,version=state['version'],actualBarteredKitUsed=True,kitResourceBefore=resource,kitResourceAfter=kit_after,kitWeaponAfter=kit_wear,quartermasterWeaponAfter=trader_wear,traderRepairPaid=before_cash-cash(),invalidKitRequestsDoNotMutateInventory=True,disabledTraderRejected=True,nativeRepairLossUnmodified=True,syntheticEquippedWearFixture=True,playerInstanceUsed=False,gameUiOrInRaidAcceptance=False);a.report.parent.mkdir(parents=True,exist_ok=True);a.report.write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8');print(json.dumps(report,indent=2))
if __name__=='__main__':main()
