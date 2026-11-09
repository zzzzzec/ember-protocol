"""Haruka supply/quest acceptance and two old-save cohorts, Development only."""
import argparse, copy, json, socket, ssl, time, urllib.request, uuid, zlib
from pathlib import Path
from native_later_contacts_check import read, write, digest
from story_quest_checks import StoryQuestChecks

def main():
    ap=argparse.ArgumentParser();ap.add_argument('--game-root',required=True,type=Path);ap.add_argument('--port',required=True,type=int)
    ap.add_argument('--stage',required=True,choices=['fresh','seed','verify','resume','replay']);ap.add_argument('--report',required=True,type=Path);a=ap.parse_args()
    root=a.game_root.resolve();installed=read(root/'.sptz/installed.json')
    assert installed['instanceKind']=='Development' and installed['port']==a.port and a.port!=6969
    data=root/'SPT/user/mods/Codex-ZombieSurvival/data';opening=read(data/'opening.json');quests=read(data/'quests.json');manifest=read(data/'manifest.json')
    main_ids=[x['id'] for x in read(data/'campaign.json')['main']];haruka=opening['harukaId'];rin=manifest['traderId'];session=''
    config=read(Path(__file__).resolve().parents[1]/'config/procurement.json');assert config['traderId']==haruka
    checkpoint=root/'.sptz/procurement-qa.json';rub='5449016a4bdc2d6f028b456f'
    def request(route,body=None,error=False):
        req=urllib.request.Request('https://127.0.0.1:'+str(a.port)+route,data=json.dumps(body or {}).encode(),headers={'Content-Type':'application/json','Cookie':'PHPSESSID='+session,'responsecompressed':'0','requestcompressed':'0'})
        with urllib.request.urlopen(req,context=ssl._create_unverified_context(),timeout=90) as r:raw=r.read()
        for mode in [None,zlib.MAX_WBITS,-zlib.MAX_WBITS,zlib.MAX_WBITS|16]:
            try:
                text=(raw if mode is None else zlib.decompress(raw,mode)).decode('utf-8-sig').strip()
                if len(text)==24 and all(c in '0123456789abcdef' for c in text.lower()):return text
                result=json.loads(text)
                if isinstance(result,dict) and 'err' in result:
                    if result['err'] and error:return dict(warnings=[dict(code=result['err'])])
                    assert not result['err'],(route,result['err']);return result.get('data')
                return result
            except (ValueError,UnicodeDecodeError,zlib.error):pass
        raise AssertionError('Cannot decode '+route)
    def pmc():return next(p for p in request('/client/game/profile/list') if p['Info']['Side'].lower()!='savage')
    def refresh():request('/client/trading/api/traderSettings');return pmc()
    def move(event):return request('/client/game/profile/items/moving',dict(data=[event],tm=0),True)
    def action(kind,qid):return move(dict(Action=kind,qid=qid,type='Quest',removeExcessItems=True))
    def assortment(tid):return request('/client/trading/api/getTraderAssort/'+tid)
    def roots(tid):return {x['_id']:x for x in assortment(tid)['items'] if x.get('parentId')=='hideout'}
    def mails(saved):return [m for d in saved.get('dialogues',{}).values() for m in d.get('messages',[])]
    def register():
        name='Ember-Procurement-QA-'+uuid.uuid4().hex[:8];request('/launcher/profile/register',dict(username=name,edition='Standard'))
        matches=[p for p in (root/'SPT/user/profiles').glob('*.json') if read(p).get('info',{}).get('username')==name]
        assert len(matches)==1;return matches[0].stem,matches[0],name
    def create():
        request('/client/game/profile/create',dict(side='Usec',nickname='EmberProcureQA',headId='5cde96047d6c8b20b577f016',voiceId='5fc1223595572123ae7384a3'));request('/client/game/start');p=refresh()
        # Contact/quest prerequisites remain native. Synthetic late XP lets this
        # suite inspect LL3 card offers without pretending main XP alone reaches it.
        g=request('/client/globals');p['Info']['Level']=30;p['Info']['Experience']=sum(x['exp'] for x in g['config']['exp']['level']['exp_table'][:30])
        raid=request('/client/match/local/start',dict(location='sandbox',playerSide='Pmc',shouldSkipLootGeneration=True,timeVariant='CURR'))
        request('/client/match/local/end',dict(serverId=raid['serverId'],results=dict(profile=p,result='SURVIVED',exitName='QA procurement XP fixture',inSession=False,favorite=False,playTime=60),lostInsuredItems=[],transferItems={}))
        request('/client/game/start');return refresh()
    def advance(qid):assert not action('QuestAccept',qid).get('warnings');assert not action('QuestComplete',qid).get('warnings');return refresh()
    def snapshot(saved):
        p=saved['characters']['pmc'];return dict(inventory=p['Inventory'],quests=p['Quests'],counters=p.get('TaskConditionCounters',{}),mailCount=len(mails(saved)))
    if a.stage=='replay':
        cp=read(checkpoint)['full'];session=cp['session'];path=root/'SPT/user/profiles'/(session+'.json')
        assert read(path)['info']['username']==cp['username'] and cp['username'].startswith('Ember-Procurement-QA-')
        request('/client/game/start');request('/client/game/logout');before=read(path)
        assert next(x for x in before['characters']['pmc']['Quests'] if x['qid']==main_ids[0])['status']==4
        assert not action('QuestAccept',main_ids[0]).get('warnings');assert not action('QuestComplete',main_ids[0]).get('warnings')
        request('/client/game/logout');after=read(path)
        def extra(saved):
            p=saved['characters']['pmc'];return dict(xp=p['Info']['Experience'],standing={k:v['standing'] for k,v in p['TradersInfo'].items()})
        preserved=snapshot(before)==snapshot(after) and extra(before)==extra(after)
        report=dict(passed=preserved,version=installed['version'],ordinaryCompletedMainReplayPreserved=preserved,addedMessages=len(mails(after))-len(mails(before)),addedExperience=after['characters']['pmc']['Info']['Experience']-before['characters']['pmc']['Info']['Experience'],playerInstanceUsed=False)
        write(a.report,report);assert preserved,'Ordinary completed-main replay changed rewards or progress'
        print('PASS ordinary main accept/complete replay preserves rewards, progress and messages.');return
    if a.stage=='fresh':
        session,path,name=register();p=create();assert not p['TradersInfo'][haruka]['unlocked'] and not roots(haruka)
        before=digest(p['Inventory']);assert move(dict(Action='TradingConfirm',type='buy_from_trader',tid=haruka,item_id=config['cash'][0]['id'],count=1,scheme_id=0,scheme_items=[])).get('warnings')
        assert action('QuestAccept',opening['harukaQuests'][0]).get('warnings') and digest(pmc()['Inventory'])==before
        for index,qid in enumerate(main_ids[:22],1):p=advance(qid);assert p['TradersInfo'][haruka]['unlocked']==(index>=22)
        stock=roots(haruka);cash=config['cash'][0];price=assortment(haruka)['barter_scheme'][cash['id']][0][0]['count']
        assert {x['id'] for x in config['cash'] if x['level']==1}<=set(stock)
        assert not {x['id'] for x in config['cash']+config['barters']}&set(roots(rin))
        assert not {'2132fbc66974da629a18cf9e','b035f233d38317aee5588932'}&set(stock)
        money=next(x for x in pmc()['Inventory']['items'] if x['_tpl']==rub);assert money['upd']['StackObjectsCount']>=price*2
        before_ids={x['_id'] for x in pmc()['Inventory']['items']}
        def buy_paper():return move(dict(Action='TradingConfirm',type='buy_from_trader',tid=haruka,item_id=cash['id'],count=1,scheme_id=0,scheme_items=[dict(id=money['_id'],count=price)]))
        assert not buy_paper().get('warnings')
        paper=next(x for x in pmc()['Inventory']['items'] if x['_id'] not in before_ids and x['_tpl']==cash['tpl']);assert not paper.get('upd',{}).get('SpawnedInSession',False)
        before=digest(pmc()['Inventory']);assert buy_paper().get('warnings') and digest(pmc()['Inventory'])==before
        qid=opening['harukaQuests'][0];assert not action('QuestAccept',qid).get('warnings');condition=quests[qid]['conditions']['AvailableForFinish'][0]
        before=digest(pmc()['Inventory']);assert move(dict(Action='QuestHandover',qid=qid,type='Quest',conditionId=condition['id'],items=[dict(id=paper['_id'],count=1)])).get('warnings')
        assert digest(pmc()['Inventory'])==before,'Purchased supply satisfied or was consumed by a FIR handover'
        fixture=StoryQuestChecks(request,path,quests,read(data/'story-quests.json'),request('/client/items'));barters=[]
        for qid in opening['harukaQuests']:
            if qid!=opening['harukaQuests'][0]:assert not action('QuestAccept',qid).get('warnings')
            fixture.ready(qid);assert not action('QuestComplete',qid).get('warnings');fixture.after_complete(qid)
            for b in config['barters']:
                if b['unlock']!=qid:continue
                assert b['id'] in roots(haruka)
                override=copy.deepcopy(manifest);override['traderId']=haruka
                barters+=fixture.buy_alternatives(override,assortment(haruka),{b['id']:b['id']})
        request('/client/game/logout');saved=read(path);private_checks=copy.deepcopy(fixture.checks)
        for qid in opening['harukaQuests']:
            selected=[m for m in mails(saved) if m.get('templateId')==qid+' successMessageText'];assert len(selected)==1
            expected=sum(z['value'] for z in quests[qid]['rewards']['Success'] if z['type']=='Item')
            actual=sum(i.get('upd',{}).get('StackObjectsCount',1) for m in selected for i in m.get('items',{}).get('data',[]) if i['_tpl']==rub)
            assert actual>=expected>0,(qid,expected,actual)
        advance(main_ids[22]);advance(main_ids[23]);assert {'2132fbc66974da629a18cf9e','b035f233d38317aee5588932'}<=set(roots(haruka))
        request('/client/game/logout');full=dict(session=session,username=name)
        # Second cohort: an ordinary pre-existing main quest, partially handed in.
        session,path,name=register();create()
        for qid in main_ids[:23]:advance(qid)
        qid=main_ids[23];assert not action('QuestAccept',qid).get('warnings')
        fixture=StoryQuestChecks(request,path,quests,read(data/'story-quests.json'),request('/client/items'))
        cond=quests[qid]['conditions']['AvailableForFinish'][0];incoming=fixture.seed([(cond['target'][0],1)])
        assert not move(dict(Action='QuestHandover',qid=qid,type='Quest',conditionId=cond['id'],items=[dict(id=incoming[0]['_id'],count=1)])).get('warnings')
        request('/client/game/logout');before=read(path);write(root/'.sptz/procurement-partial-before-replay.json',before)
        partial=dict(session=session,username=name);assert not action('QuestAccept',qid).get('warnings');request('/client/game/logout');after=read(path)
        preserved=snapshot(before)==snapshot(after)
        write(checkpoint,dict(full=full,partial=partial))
        report=dict(passedFresh=preserved,version=installed['version'],harukaUnlockAtMain22=True,uncontactedTradeAndTaskRejected=True,newCashStockOwnedOnce=True,realCashPurchaseNonFir=True,secondPurchaseRejectedWithoutMutation=True,purchasedSupplyCannotSatisfyFirTask=True,strictQuestChecks=private_checks,harukaActualCashAttachmentsVerified=True,newBartersPurchased=barters,labCardsUnlockAtMain24=True,partialMainReplayPreserved=preserved,syntheticMainAndCombatProgress=True,syntheticLateXpFixture=True,playerInstanceUsed=False,gameUiAcceptance=False)
        write(a.report,report);assert preserved,'Repeated acceptance reset partial ordinary-main progress or repeated its mail'
        print('PASS Haruka purchase/FIR isolation, private handovers, barters, card gating and partial main replay.');return
    cp=read(checkpoint)
    if a.stage=='seed':
        with socket.socket() as s:s.settimeout(1);assert s.connect_ex(('127.0.0.1',a.port))!=0,'Stop Development before offline QA seeding'
        for kind in ['full','partial']:
            row=cp[kind];path=root/'SPT/user/profiles'/(row['session']+'.json');saved=read(path)
            assert saved['info']['username']==row['username'] and row['username'].startswith('Ember-Procurement-QA-')
            p=saved['characters']['pmc'];p['TradersInfo'].pop(haruka);saved['spt']['emberNarrative']['contacts'].pop(haruka)
            if kind=='full':
                saved.setdefault('traderPurchases',{}).pop(haruka,None);record=dict(count=1,purchaseTimestamp=int(time.time()))
                saved['traderPurchases'].setdefault(rin,{})['2132fbc66974da629a18cf9e']=record;row['purchase']=record
                saved['spt']['emberNarrative']['contacts'][opening['claireId']]['state']='suspended'
            row.update(expected=snapshot(saved));write(path,saved)
        write(checkpoint,cp);print('Seeded two disposable 2.3-style profiles, including a real partial SSD handover.');return
    for kind in ['full','partial']:
        row=cp[kind];session=row['session'];path=root/'SPT/user/profiles'/(session+'.json');saved=read(path)
        assert saved['info']['username']==row['username'] and row['username'].startswith('Ember-Procurement-QA-')
        request('/client/game/start');p=refresh();done={x['qid'] for x in row['expected']['quests'] if x['status']==4}
        expected=sum(z['value'] for qid in done for z in quests[qid]['rewards']['Success'] if z['type']=='TraderStanding' and z['target']==haruka)
        assert p['TradersInfo'][haruka]['unlocked'] and abs(p['TradersInfo'][haruka]['standing']-expected)<.0001
        if kind=='partial':
            shown=next(x for x in request('/client/quest/list') if x['_id']==main_ids[23]);assert shown['traderId']==opening['claireId']
            assert not action('QuestAccept',main_ids[23]).get('warnings')
        else:assert not p['TradersInfo'][opening['claireId']]['unlocked'] and not roots(opening['claireId'])
        request('/client/game/logout');after=read(path);assert snapshot(after)==row['expected'],('Legacy progress/mail/inventory changed',kind)
        if kind=='full':assert after['traderPurchases'][haruka]['2132fbc66974da629a18cf9e']==row['purchase']
    result=read(a.report);result.update(passed=True,legacyContactStandingCreditedOnce=True,labCardPurchaseQuotaMigrated=True,partialHandoverAndInventoryPreserved=True,oldMainOwnerChangesWithoutReissuingMail=True,suspendedContactStaysClosed=True)
    if a.stage=='resume':result['controlledRestartIdempotent']=True
    write(a.report,result);print('PASS two legacy cohorts, partial handover, card quota, standing and message preservation.')

if __name__=='__main__':main()
