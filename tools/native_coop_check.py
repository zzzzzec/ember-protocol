"""Synthetic native coop returns on a disposable Development profile; no game UI automation."""
from pathlib import Path
import argparse, collections, copy, hashlib, json, ssl, time, urllib.request, uuid, zlib

def read(p):return json.loads(p.read_text('utf-8-sig'))

def main():
    ap=argparse.ArgumentParser();ap.add_argument('--game-root',required=True,type=Path);ap.add_argument('--port',required=True,type=int);ap.add_argument('--report',required=True,type=Path)
    a=ap.parse_args();root=a.game_root.resolve();installed=read(root/'.sptz/installed.json')
    assert installed['instanceKind']=='Development' and installed['port']==a.port and a.port!=6969
    data=root/'SPT/user/mods/Codex-ZombieSurvival';cfg=read(data/'config.json');manifest=read(data/'data/manifest.json')
    assert cfg['disableVanillaCoopRewards'];native=read(root/'SPT/SPT_Data/configs/inraid.json');gift=read(root/'SPT/SPT_Data/configs/trader.json')['fence']['coopExtractGift']
    fence='579dc571d53a0658a154fbec';rin=cfg['traderId'];session=''
    def request(route,body=None):
        req=urllib.request.Request('https://127.0.0.1:'+str(a.port)+route,data=json.dumps(body or {}).encode(),headers={'Content-Type':'application/json','Cookie':'PHPSESSID='+session,'responsecompressed':'0','requestcompressed':'0'})
        with urllib.request.urlopen(req,context=ssl._create_unverified_context(),timeout=90) as response:raw=response.read()
        for mode in [None,zlib.MAX_WBITS,-zlib.MAX_WBITS,zlib.MAX_WBITS|16]:
            try:
                text=(raw if mode is None else zlib.decompress(raw,mode)).decode('utf-8-sig').strip()
                if len(text)==24 and all(c in '0123456789abcdef' for c in text.lower()):return text
                value=json.loads(text)
                if isinstance(value,dict) and 'err' in value:assert not value['err'],(route,value['err']);return value.get('data')
                return value
            except (ValueError,zlib.error,UnicodeDecodeError):pass
        raise AssertionError('Cannot decode '+route)
    username='Ember-Coop-QA-'+uuid.uuid4().hex[:8];request('/launcher/profile/register',dict(username=username,edition='Standard'))
    matches=[p for p in (root/'SPT/user/profiles').glob('*.json') if read(p).get('info',{}).get('username')==username]
    assert len(matches)==1;session=matches[0].stem;profile_path=matches[0]
    request('/client/game/profile/create',dict(side='Usec',nickname='EmberCoopQA',headId='5cde96047d6c8b20b577f016',voiceId='5fc1223595572123ae7384a3'))
    request('/client/game/start');request('/client/trading/api/traderSettings')
    def pmc():return next(p for p in request('/client/game/profile/list') if p['Info']['Side'].lower()!='savage')
    p=pmc();baseline_standing=p['TradersInfo'][fence]['standing'];baseline_counts=copy.deepcopy(p.get('CoopExtractCounts',{}))
    med=next(o for o in manifest['offers'] if o['id']=='759076688ecde0eab1c4b6d9')
    money=next(i for i in p['Inventory']['items'] if i['_tpl']=='5449016a4bdc2d6f028b456f')
    reply=request('/client/game/profile/items/moving',dict(data=[dict(Action='TradingConfirm',type='buy_from_trader',tid=rin,item_id=med['id'],count=1,scheme_id=0,scheme_items=[dict(id=money['_id'],count=med['price'])])],tm=0));assert not reply.get('warnings')
    def template_counts(p):
        c=collections.Counter()
        for i in p['Inventory']['items']:c[i['_tpl']]+=i.get('upd',{}).get('StackObjectsCount',1)
        return c
    before_items=template_counts(pmc());returns=[]
    for exit_name in native['coopExtracts']:
        time.sleep(1.05);raid=request('/client/match/local/start',dict(location='sandbox',playerSide='Pmc',shouldSkipLootGeneration=True,timeVariant='CURR'))
        body=dict(serverId=raid['serverId'],results=dict(profile=pmc(),result='SURVIVED',exitName=exit_name,inSession=False,favorite=False,playTime=60),lostInsuredItems=[],transferItems={})
        request('/client/match/local/end',body);request('/client/game/logout');saved=read(profile_path);p=pmc()
        assert p['TradersInfo'][fence]['standing']==baseline_standing,'Coop granted native Fence standing'
        assert p.get('CoopExtractCounts',{})==baseline_counts,'Native coop benefit counter changed'
        assert template_counts(p)==before_items,'Coop supplied unexpected inventory'
        assert not [m for m in saved.get('dialogues',{}).get(fence,{}).get('messages',[]) if m.get('templateId') in gift['messageLocaleIds']],'Coop gift mail was sent'
        assert saved['spt']['emberRaidRestock']['count']==len(returns)+1,'Coop return did not replenish normally'
        returns.append(dict(exit=exit_name,returnSaved=True,noGift=True,noStanding=True,raidRestock=True))
    assort=request('/client/trading/api/getTraderAssort/'+rin);item=next(i for i in assort['items'] if i['_id']==med['id'])
    assert item['upd'].get('BuyRestrictionCurrent',0)==0
    first=next(q['id'] for q in manifest['quests'] if q['lane']=='campaign' and q['index']==0)
    for action in ['QuestAccept','QuestComplete']:
        result=request('/client/game/profile/items/moving',dict(data=[dict(Action=action,qid=first,type='Quest',removeExcessItems=True)],tm=0));assert not result.get('warnings')
    request('/client/game/logout');saved=read(profile_path)
    assert any(m.get('templateId')==first+' successMessageText' and m.get('items',{}).get('data') for m in saved['dialogues'][rin]['messages'])
    report=dict(passed=True,version=installed['version'],coopReturns=returns,noGiftMail=True,noExtraFenceStanding=True,existingStandingUnchanged=True,inventoryUnchangedByCoop=True,personalMedicalQuotaRestocked=True,authoredQuestRewardPreserved=True,syntheticRaidReturns=True,playerInstanceUsed=False,gameUiOrInRaidAcceptance=False)
    a.report.write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n','utf-8');print(json.dumps(report,ensure_ascii=False,indent=2))

if __name__=='__main__':main()
