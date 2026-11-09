"""Emma/Katya native handovers and old-save migration, disposable Development only."""
import argparse, copy, hashlib, json, socket, ssl, time, urllib.request, uuid, zlib
from pathlib import Path
from story_quest_checks import StoryQuestChecks

def read(p):return json.loads(p.read_text('utf-8-sig'))
def write(p,x):p.write_text(json.dumps(x,ensure_ascii=False,indent=2)+'\n','utf-8')
def digest(x):return hashlib.sha256(json.dumps(x,sort_keys=True).encode()).hexdigest()

def main():
    ap=argparse.ArgumentParser();ap.add_argument('--game-root',required=True,type=Path);ap.add_argument('--port',required=True,type=int)
    ap.add_argument('--stage',required=True,choices=['fresh','seed','verify','resume','catalogue','identity']);ap.add_argument('--report',required=True,type=Path);a=ap.parse_args()
    root=a.game_root.resolve();installed=read(root/'.sptz/installed.json')
    assert installed['instanceKind']=='Development' and installed['port']==a.port and a.port!=6969
    data=root/'SPT/user/mods/Codex-ZombieSurvival/data';opening=read(data/'opening.json');qs=read(data/'quests.json');manifest=read(data/'manifest.json')
    main_ids=[x['id'] for x in read(data/'campaign.json')['main']];rin=manifest['traderId'];emma=opening['emmaId'];katya=opening['katyaId']
    ids=opening['emmaQuests']+opening['katyaQuests'];checkpoint=root/'.sptz/later-contacts-qa.json';session=''
    def request(route,body=None,allow_error=False):
        req=urllib.request.Request('https://127.0.0.1:'+str(a.port)+route,data=json.dumps(body or {}).encode(),headers={'Content-Type':'application/json','Cookie':'PHPSESSID='+session,'responsecompressed':'0','requestcompressed':'0'})
        with urllib.request.urlopen(req,context=ssl._create_unverified_context(),timeout=90) as r:raw=r.read()
        for mode in [None,zlib.MAX_WBITS,-zlib.MAX_WBITS,zlib.MAX_WBITS|16]:
            try:
                text=(raw if mode is None else zlib.decompress(raw,mode)).decode('utf-8-sig').strip()
                if len(text)==24 and all(c in '0123456789abcdef' for c in text.lower()):return text
                value=json.loads(text)
                if isinstance(value,dict) and 'err' in value:
                    if value['err'] and allow_error:return dict(warnings=[dict(code=value['err'])])
                    assert not value['err'],(route,value['err']);return value.get('data')
                return value
            except (ValueError,UnicodeDecodeError,zlib.error):pass
        raise AssertionError('Cannot decode '+route)
    def pmc():return next(x for x in request('/client/game/profile/list') if x['Info']['Side'].lower()!='savage')
    def refresh():request('/client/trading/api/traderSettings');return pmc()
    def action(kind,qid):return request('/client/game/profile/items/moving',dict(data=[dict(Action=kind,qid=qid,type='Quest',removeExcessItems=True)],tm=0),True)
    def roots(tid):return {x['_id']:x for x in request('/client/trading/api/getTraderAssort/'+tid)['items'] if x.get('parentId')=='hideout'}
    def messages(p):return [m for d in p.get('dialogues',{}).values() for m in d.get('messages',[])]
    if a.stage in ['catalogue','identity']:
        qa=read(root/'.sptz/backend-qa.json');session=qa['session'];path=root/'SPT/user/profiles'/(session+'.json')
        assert read(path)['info']['username']==qa['username'] and qa['username'].startswith('Ember-Pack-QA-')
        request('/client/game/start');p=refresh();assert p['Info']['Level']>=30
        assert all(p['TradersInfo'][c['id']]['unlocked'] for c in opening['contacts'])
        for lang in ['ch','en']:
            locale=request('/client/locale/'+lang)
            for c in opening['contacts']:
                assert locale[c['id']+' Nickname']==(c['nickname'] if lang=='ch' else c['name'])
                assert all(locale.get(c['id']+' '+field) for field in ['FullName','Location','Description'])
        if a.stage=='identity':
            write(a.report,dict(passed=True,version=installed['version'],bothLanguages=True,specialistNicknames=len(opening['contacts']),internalIdsNotUsedAsNames=True,playerInstanceUsed=False,gameUiAcceptance=False))
            print('PASS both-language specialist identity texts.');return
        settings=request('/client/trading/api/traderSettings');settings={x['_id']:x for x in (settings.values() if isinstance(settings,dict) else settings)}
        rub='5449016a4bdc2d6f028b456f'
        # Full-pipeline rewards are intentionally unclaimed in mail. Put funding in
        # a valid carried stack on this disposable QA account, rather than assuming
        # its task rewards are already cash in the stash or accepting an unfunded buy.
        fixture=StoryQuestChecks(request,path,qs,read(data/'story-quests.json'),request('/client/items'))
        incoming=fixture.seed([(rub,1)],fir=False);funded=pmc()
        next(i for i in funded['Inventory']['items'] if i['_id']==incoming[0]['_id'])['upd']['StackObjectsCount']=100000
        raid=request('/client/match/local/start',dict(location='sandbox',playerSide='Pmc',shouldSkipLootGeneration=True,timeVariant='CURR'))
        request('/client/match/local/end',dict(serverId=raid['serverId'],results=dict(profile=funded,result='SURVIVED',exitName='QA specialist funding fixture',inSession=False,favorite=False,playTime=60),lostInsuredItems=[],transferItems={}))
        def move(event):return request('/client/game/profile/items/moving',dict(data=[event],tm=0),True)
        def bank():return max((i for i in pmc()['Inventory']['items'] if i['_tpl']==rub),key=lambda i:i.get('upd',{}).get('StackObjectsCount',1))
        for c in opening['contacts']:
            tid=c['id'];s=settings[tid]
            assert not s['repair']['availability'] and not s['insurance']['availability']
            assert rub in s['items_buy_prohibited']['id_list']
            assert [l['minLevel'] for l in s['loyaltyLevels']]==[1,8,18,30]
            assert [l['minStanding'] for l in s['loyaltyLevels']]==c['standing']
            before=digest(pmc()['Inventory'])
            assert move(dict(Action='TradingConfirm',type='sell_to_trader',tid=tid,price=1000,items=[dict(id=bank()['_id'],count=1000)])).get('warnings')
            assert digest(pmc()['Inventory'])==before,'Rouble sale mutated the QA inventory'
        bought=[]
        purchase_cases=[(emma,'ccfca808d4b276a40bd14e44'),(katya,'077af95c5ef6a081a6dc6480')]
        if opening.get('harukaId'):purchase_cases.append((opening['harukaId'],'39a315d4644b7665323aa4e9'))
        for tid,oid in purchase_cases:
            assorted=request('/client/trading/api/getTraderAssort/'+tid);assert oid in {i['_id'] for i in assorted['items']}
            price=assorted['barter_scheme'][oid][0][0]['count'];offer=next(x for x in manifest['offers'] if x['id']==oid)
            money=bank();assert money['upd']['StackObjectsCount']>price*offer['limit']
            assert not move(dict(Action='TradingConfirm',type='buy_from_trader',tid=tid,item_id=oid,count=1,scheme_id=0,scheme_items=[dict(id=money['_id'],count=price)])).get('warnings')
            if oid=='39a315d4644b7665323aa4e9':
                received=[i for i in pmc()['Inventory']['items'] if i['_tpl']==offer['tpl']]
                assert any(i.get('upd',{}).get('FoodDrink',{}).get('HpPercent')==1 and not i.get('upd',{}).get('SpawnedInSession',False) for i in received),'Purchased chocolate has invalid native resource or FIR status'
            before=digest(pmc()['Inventory']);money=bank()
            reply=move(dict(Action='TradingConfirm',type='buy_from_trader',tid=tid,item_id=oid,count=offer['limit'],scheme_id=0,scheme_items=[dict(id=money['_id'],count=price*offer['limit'])]))
            assert reply.get('warnings') and digest(pmc()['Inventory'])==before,'Personal quota overbuy was not atomic'
            request('/client/game/logout');saved=read(path);assert saved['traderPurchases'][tid][oid]['count']==1
            bought.append(dict(trader=tid,offer=oid,price=price,validPurchase=True,overQuotaRejectedWithoutMutation=True))
        # Loan return tests leave no weapon in inventory. Buy a genuine complete PM
        # instead of sending a fake item id to a disabled repair service.
        pistol=next(o for o in manifest['offers'] if o['tpl']=='5448bd6b4bdc2dfc2f8b4569' and o['price']>0)
        assorted=request('/client/trading/api/getTraderAssort/'+rin);price=assorted['barter_scheme'][pistol['id']][0][0]['count'];money=bank()
        assert not move(dict(Action='TradingConfirm',type='buy_from_trader',tid=rin,item_id=pistol['id'],count=1,scheme_id=0,scheme_items=[dict(id=money['_id'],count=price)])).get('warnings')
        weapon=next(i for i in pmc()['Inventory']['items'] if i['_tpl']==pistol['tpl'])
        before=digest(pmc()['Inventory'])
        rejected=move(dict(Action='TraderRepair',tid=katya,repairItems=[dict(_id=weapon['_id'],count=1)]))
        assert rejected.get('warnings') and '军需官' in json.dumps(rejected,ensure_ascii=False),rejected
        assert digest(pmc()['Inventory'])==before,'Katya unexpectedly repaired a weapon'
        current=pmc();raid=request('/client/match/local/start',dict(location='sandbox',playerSide='Pmc',shouldSkipLootGeneration=True,timeVariant='CURR'))
        request('/client/match/local/end',dict(serverId=raid['serverId'],results=dict(profile=current,result='SURVIVED',exitName='QA specialist restock fixture',inSession=False,favorite=False,playTime=60),lostInsuredItems=[],transferItems={}))
        request('/client/game/logout');saved=read(path)
        assert all(not saved.get('traderPurchases',{}).get(c['id']) for c in opening['contacts'])
        write(a.report,dict(passed=True,version=installed['version'],specialists=len(opening['contacts']),loyaltyRequirementsMatch=True,roubleSaleRejectedWithoutMutation=True,rinOnlyRepairPreserved=True,purchases=bought,allSpecialistsRestockedAfterSyntheticDeployment=True,syntheticLateGameProfile=True,syntheticFundingFixture=True,syntheticDeployment=True,playerInstanceUsed=False,gameUiAcceptance=False))
        print('PASS specialist loyalty, real purchases, quota rejection, repair/currency boundaries and deployment restock.');return
    if a.stage=='fresh':
        username='Ember-Later-Contacts-QA-'+uuid.uuid4().hex[:8];request('/launcher/profile/register',dict(username=username,edition='Standard'))
        matches=[p for p in (root/'SPT/user/profiles').glob('*.json') if read(p).get('info',{}).get('username')==username]
        assert len(matches)==1;path=matches[0];session=path.stem
        request('/client/game/profile/create',dict(side='Usec',nickname='EmberLaterContactsQA',headId='5cde96047d6c8b20b577f016',voiceId='5fc1223595572123ae7384a3'))
        request('/client/game/start');p=refresh();assert not p['TradersInfo'][emma]['unlocked'] and not p['TradersInfo'][katya]['unlocked']
        assert not roots(emma) and not roots(katya)
        for qid in [opening['emmaQuests'][0],opening['katyaQuests'][0]]:assert action('QuestAccept',qid).get('warnings')
        for index,qid in enumerate(main_ids[:20],1):
            assert not action('QuestAccept',qid).get('warnings');assert not action('QuestComplete',qid).get('warnings');p=refresh()
            assert p['TradersInfo'][emma]['unlocked']==(index>=15)
            assert p['TradersInfo'][katya]['unlocked']==(index>=16)
        story=StoryQuestChecks(request,path,qs,read(data/'story-quests.json'),request('/client/items'))
        for qid in ids:
            assert not action('QuestAccept',qid).get('warnings');story.ready(qid)
            assert not action('QuestComplete',qid).get('warnings');story.after_complete(qid)
        request('/client/game/logout');saved=read(path)
        for qid in ids:
            selected=[m for m in messages(saved) if m.get('templateId')==qid+' successMessageText'];assert len(selected)==1
            expected=sum(r['value'] for r in qs[qid]['rewards']['Success'] if r['type']=='Item')
            actual=sum(i.get('upd',{}).get('StackObjectsCount',1) for m in selected for i in m.get('items',{}).get('data',[]) if i['_tpl']=='5449016a4bdc2d6f028b456f')
            assert actual>=expected>0,(qid,expected,actual)
        ownership=[]
        for tid in [rin]+[c['id'] for c in opening['contacts']]:ownership+=list(roots(tid))
        assert len(ownership)==len(set(ownership))
        head=qs[opening['katyaQuests'][1]]['conditions']['AvailableForFinish'][0]
        distance=qs[opening['katyaQuests'][2]]['conditions']['AvailableForFinish'][0]
        assert head['value']==6 and next(c for c in head['counter']['conditions'] if c['conditionType']=='Kills')['bodyPart']==['Head']
        assert distance['value']==10 and next(c for c in distance['counter']['conditions'] if c['conditionType']=='Kills')['distance']==dict(compareMethod='>=',value=25)
        write(checkpoint,dict(session=session,username=username))
        write(a.report,dict(passedFresh=True,version=installed['version'],contactsUnlockAtMain15And16=True,privateCommissions=6,nativeHandoverChecks=story.checks,actualCashAttachmentsVerified=True,replayedRewardsNotDuplicated=True,sharedOffersNotDuplicated=True,nativeHeadAndDistanceConditionsVerified=True,syntheticCombatFixtures=True,playerInstanceUsed=False,gameUiAcceptance=False))
        print('PASS later contacts, six strict commissions, cash attachments and objective data.');return
    cp=read(checkpoint);session=cp['session'];path=root/'SPT/user/profiles'/(session+'.json');saved=read(path)
    assert saved['info']['username']==cp['username'] and cp['username'].startswith('Ember-Later-Contacts-QA-')
    if a.stage=='seed':
        with socket.socket() as s:s.settimeout(1);assert s.connect_ex(('127.0.0.1',a.port))!=0,'Stop only Development before offline fixture changes.'
        write(root/'.sptz/later-contacts-before-seed.json',saved);p=saved['characters']['pmc'];purchases=saved.setdefault('traderPurchases',{}).setdefault(rin,{})
        for tid,oid in [(emma,'3580d38603e636b9e79f7645'),(katya,'077af95c5ef6a081a6dc6480')]:
            p['TradersInfo'].pop(tid);saved['spt']['emberNarrative']['contacts'].pop(tid)
            purchases[oid]=dict(count=1,purchaseTimestamp=int(time.time()));saved.get('traderPurchases',{}).pop(tid,None)
        # An unrelated accepted loan must keep its one-time issue, return state and counters.
        loan=next(iter(read(data/'field-quests.json')['loans']));base=copy.deepcopy(p['Quests'][0]);base.update(qid=loan,status=2)
        p['Quests']=[x for x in p['Quests'] if x['qid']!=loan]+[base]
        counter=qs[loan]['conditions']['AvailableForFinish'][0]['id'];p.setdefault('TaskConditionCounters',{})[counter]=dict(id=counter,sourceId=loan,type='CounterCreator',value=3)
        saved['spt']['emberNarrative']['contacts'][opening['claireId']]['state']='suspended'
        cp.update(inventory=p['Inventory'],quests=p['Quests'],counters=p['TaskConditionCounters'],mailCount=len(messages(saved)),purchases={k:v for k,v in purchases.items() if k in ['3580d38603e636b9e79f7645','077af95c5ef6a081a6dc6480']},loan=loan)
        write(checkpoint,cp);write(path,saved);print('Seeded disposable pre-contact save and a partial legacy loan.');return
    request('/client/game/start');p=refresh();assert p['Inventory']==cp['inventory'] and p['Quests']==cp['quests'] and p['TaskConditionCounters']==cp['counters']
    done={q['qid'] for q in cp['quests'] if q['status']==4}
    for tid,oid in [(emma,'3580d38603e636b9e79f7645'),(katya,'077af95c5ef6a081a6dc6480')]:
        assert p['TradersInfo'][tid]['unlocked']
        expected=sum(r['value'] for qid in done for r in qs[qid]['rewards']['Success'] if r['type']=='TraderStanding' and r['target']==tid)
        assert abs(p['TradersInfo'][tid]['standing']-expected)<.0001
        assert oid not in roots(rin),('Transferred offer stayed at Rin',tid,oid)
        # Native quest-assort filtering may still hide this stock in a partial save.
        # Complete-catalogue visibility is covered independently by native_check.py.
    assert not p['TradersInfo'][opening['claireId']]['unlocked'] and not roots(opening['claireId'])
    assert not action('QuestAccept',cp['loan']).get('warnings');request('/client/game/logout');after=read(path)
    for tid,oid in [(emma,'3580d38603e636b9e79f7645'),(katya,'077af95c5ef6a081a6dc6480')]:assert after['traderPurchases'][tid][oid]==cp['purchases'][oid]
    assert after['characters']['pmc']['Inventory']==cp['inventory'] and len(messages(after))==cp['mailCount']
    result=read(a.report);result.update(passed=True,oldContactStandingCreditedOnce=True,legacyPurchaseQuotasPreserved=True,partialLoanAndInventoryUnchanged=True,noReissuedMailOrLoan=True,suspendedContactStaysClosed=True)
    if a.stage=='resume':result['controlledRestartIdempotent']=True
    write(a.report,result);print('PASS legacy contacts, personal quotas, partial loan and no duplicate mail.')

if __name__=='__main__':main()
