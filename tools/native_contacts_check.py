"""Contacts/release gates and old-save preservation, disposable Development account only."""
from pathlib import Path
import argparse, copy, hashlib, json, socket, ssl, time, urllib.request, uuid, zlib
from story_quest_checks import StoryQuestChecks

def read(p): return json.loads(p.read_text('utf-8-sig'))
def write(p, x): p.write_text(json.dumps(x, ensure_ascii=False, indent=2)+'\n', 'utf-8')
def digest(x): return hashlib.sha256(json.dumps(x, sort_keys=True).encode()).hexdigest()

def main():
    ap=argparse.ArgumentParser()
    ap.add_argument('--game-root', required=True, type=Path)
    ap.add_argument('--port', required=True, type=int)
    ap.add_argument('--stage', required=True, choices=['fresh','seed','verify','resume'])
    ap.add_argument('--report', required=True, type=Path)
    a=ap.parse_args();root=a.game_root.resolve();installed=read(root/'.sptz/installed.json')
    assert installed['instanceKind']=='Development' and installed['port']==a.port and a.port!=6969
    data=root/'SPT/user/mods/Codex-ZombieSurvival/data';opening=read(data/'opening.json');quests=read(data/'quests.json');manifest=read(data/'manifest.json')
    main_ids=[q['id'] for q in read(data/'campaign.json')['main']]
    nina=opening['ninaId'];claire=opening['claireId'];rin=manifest['traderId'];session=''
    checkpoint=root/'.sptz/contacts-qa.json'
    def request(route, body=None, allow_error=False):
        req=urllib.request.Request('https://127.0.0.1:'+str(a.port)+route, data=json.dumps(body or {}).encode(),headers={'Content-Type':'application/json','Cookie':'PHPSESSID='+session,'responsecompressed':'0','requestcompressed':'0'})
        with urllib.request.urlopen(req, context=ssl._create_unverified_context(), timeout=90) as response:raw=response.read()
        for mode in [None,zlib.MAX_WBITS,-zlib.MAX_WBITS,zlib.MAX_WBITS|16]:
            try:
                text=(raw if mode is None else zlib.decompress(raw,mode)).decode('utf-8-sig').strip()
                if len(text)==24 and all(c in '0123456789abcdef' for c in text.lower()):return text
                value=json.loads(text)
                if isinstance(value,dict) and 'err' in value:
                    if value['err'] and allow_error:return dict(warnings=[dict(code=value['err'])])
                    assert not value['err'],('Native request failed',route,value['err'])
                    return value.get('data')
                return value
            except (ValueError,UnicodeDecodeError,zlib.error):pass
        raise AssertionError('Cannot decode '+route)
    def action(kind,qid):return request('/client/game/profile/items/moving',dict(data=[dict(Action=kind,qid=qid,type='Quest',removeExcessItems=True)],tm=0))
    def pmc():return next(p for p in request('/client/game/profile/list') if p['Info']['Side'].lower()!='savage')
    def refresh():request('/client/trading/api/traderSettings');return pmc()
    def roots(tid):return {x['_id']:x for x in request('/client/trading/api/getTraderAssort/'+tid)['items'] if x.get('parentId')=='hideout'}
    def mail_count(p):return sum(len(d.get('messages',[])) for d in p.get('dialogues',{}).values())
    def trade(tid,oid,price=0):
        money=next(i for i in pmc()['Inventory']['items'] if i['_tpl']=='5449016a4bdc2d6f028b456f')
        return request('/client/game/profile/items/moving',dict(data=[dict(Action='TradingConfirm',type='buy_from_trader',tid=tid,item_id=oid,count=1,scheme_id=0,scheme_items=[dict(id=money['_id'],count=price)] if price else [])],tm=0),allow_error=True)
    if a.stage=='fresh':
        username='Ember-Contacts-QA-'+uuid.uuid4().hex[:8]
        request('/launcher/profile/register',dict(username=username,edition='Standard'))
        matches=[p for p in (root/'SPT/user/profiles').glob('*.json') if read(p).get('info',{}).get('username')==username]
        assert len(matches)==1;session=matches[0].stem
        request('/client/game/profile/create',dict(side='Usec',nickname='EmberContactsQA',headId='5cde96047d6c8b20b577f016',voiceId='5fc1223595572123ae7384a3'))
        request('/client/game/start');p=refresh()
        available={q['_id'] for q in request('/client/quest/list') if q['sptStatus']==1}
        assert available==set(opening['initialQuests']),available
        blocked=['725d6b8322ebd441266c86b7','8083060973b4cb1ee29eb30d']
        inventory=digest(p['Inventory'])
        for qid in blocked:assert action('QuestAccept',qid).get('warnings'),'Locked task accepted directly'
        assert digest(pmc()['Inventory'])==inventory
        for tid in [nina,claire]:
            assert not p['TradersInfo'][tid]['unlocked'] and not roots(tid)
            oid=next(c for c in opening['contacts'] if c['id']==tid)['offers'][0]
            assert trade(tid,oid).get('warnings'),'Uncontacted trader accepted a purchase'
        assert digest(pmc()['Inventory'])==inventory
        shotgun='8083060973b4cb1ee29eb30d'
        # Synthetic native completions test routing/unlocks; they do not prove combat.
        for index,qid in enumerate(main_ids[:7],1):
            assert not action('QuestAccept',qid).get('warnings')
            assert not action('QuestComplete',qid).get('warnings')
            p=refresh()
            if index==1:
                assert 'e0edbe238c5fd940c5c7cad8' in {q['_id'] for q in request('/client/quest/list') if q['sptStatus']==1}
            if index==2:assert p['TradersInfo'][nina]['unlocked'] and not p['TradersInfo'][claire]['unlocked']
            if index==3:assert not action('QuestAccept',shotgun).get('warnings')
        assert p['TradersInfo'][claire]['unlocked']
        story=StoryQuestChecks(request,matches[0],quests,read(data/'story-quests.json'),request('/client/items'))
        for qid in opening['claireQuests']:
            assert not action('QuestAccept',qid).get('warnings')
            story.ready(qid);assert not action('QuestComplete',qid).get('warnings');story.after_complete(qid)
        request('/client/game/logout');mail_profile=read(matches[0])
        for qid in opening['claireQuests']:
            assert any(m.get('templateId')==qid+' startedMessageText' for m in mail_profile['dialogues'][claire]['messages'])
            expected_cash=sum(r['value'] for r in quests[qid]['rewards']['Success'] if r['type']=='Item' and any(i['_tpl']=='5449016a4bdc2d6f028b456f' for i in r.get('items',[])))
            actual_cash=sum(i.get('upd',{}).get('StackObjectsCount',1) for m in mail_profile['dialogues'][claire]['messages'] if m.get('templateId')==qid+' successMessageText' for i in m.get('items',{}).get('data',[]) if i['_tpl']=='5449016a4bdc2d6f028b456f')
            assert actual_cash>=expected_cash>0,('Claire reward attachment missing',qid,expected_cash,actual_cash)
        owned=[]
        for tid in [rin,nina,claire]:owned+=list(roots(tid))
        assert len(owned)==len(set(owned)),'Duplicate stock after contact transfer'
        pliers='1c763c6979fa24963bb82284';offer=next(o for o in manifest['offers'] if o['id']==pliers)
        assert pliers in roots(claire) and pliers not in roots(rin)
        assert not trade(claire,pliers,offer['price']).get('warnings')
        request('/client/game/logout');saved=read(matches[0])
        purchase=saved['traderPurchases'][claire][pliers]
        assert purchase['count']==1,purchase
        write(checkpoint,dict(session=session,username=username,pliers=pliers,purchase=purchase,shotgun=shotgun))
        write(a.report,dict(passedFresh=True,version=installed['version'],initialAvailable=2,lockedTasksRejectDirectAccept=True,contactsUnlockAtMain2And7=True,uncontactedTradeRejected=True,sharedOffersNotDuplicated=True,claireNativePurchaseWorks=True,claireQuestChecks=story.checks,claireNativeStartedMail=True,claireActualCashAttachmentsVerified=True,syntheticMainProgress=True,playerInstanceUsed=False,gameUiAcceptance=False))
        print('PASS fresh opening, staged contacts, locked direct requests and native purchase.')
        return
    checkpoint_data=read(checkpoint);session=checkpoint_data['session'];path=root/'SPT/user/profiles'/(session+'.json');saved=read(path)
    assert saved['info']['username']==checkpoint_data['username'] and checkpoint_data['username'].startswith('Ember-Contacts-QA-')
    if a.stage=='seed':
        with socket.socket() as s:
            s.settimeout(1);assert s.connect_ex(('127.0.0.1',a.port))!=0,'Stop Development before offline fixture changes'
        write(root/'.sptz/contacts-qa-before-seed.json',saved)
        player=saved['characters']['pmc'];player['TradersInfo'].pop(claire)
        saved['spt']['emberNarrative']['contacts'].pop(claire)
        saved['traderPurchases'].setdefault(rin,{})[checkpoint_data['pliers']]=saved['traderPurchases'].pop(claire)[checkpoint_data['pliers']]
        # An old accepted task whose newly added main prerequisite is not complete.
        player['Quests']=[q for q in player['Quests'] if q['qid']!=main_ids[2]]
        status=next(q for q in player['Quests'] if q['qid']==main_ids[6])
        for qid in main_ids[7:9]:
            row=copy.deepcopy(status);row['qid']=qid;row['status']=4;player['Quests'].append(row)
        failed=copy.deepcopy(status);failed['qid']='2d97119e90162b58df39a77b';failed['status']=5;player['Quests'].append(failed)
        cached=copy.deepcopy(status);cached['qid']='119371f120f3232df3f55aa3';cached['status']=1;cached['startTime']=0
        player['Quests']=[q for q in player['Quests'] if q['qid']!=cached['qid']]+[cached]
        cid=quests[checkpoint_data['shotgun']]['conditions']['AvailableForFinish'][0]['id']
        player.setdefault('TaskConditionCounters',{})[cid]=dict(id=cid,type='CounterCreator',sourceId=checkpoint_data['shotgun'],value=3)
        saved['spt']['emberNarrative']['contacts'][nina]['state']='departed'
        inventory=copy.deepcopy(player['Inventory']);progress=copy.deepcopy(player['Quests']);counters=copy.deepcopy(player['TaskConditionCounters'])
        checkpoint_data.update(inventory=inventory,quests=progress,counters=counters,messages=mail_count(saved))
        write(checkpoint,checkpoint_data);write(path,saved)
        print('Seeded disposable old-save fixture; no Player data used.')
        return
    request('/client/game/start');p=refresh()
    assert p['TradersInfo'][claire]['unlocked']
    completed={q['qid'] for q in checkpoint_data['quests'] if q['status']==4}
    expected=sum(r['value'] for qid in completed for r in quests[qid]['rewards']['Success'] if r['type']=='TraderStanding' and r['target']==claire)
    assert abs(p['TradersInfo'][claire]['standing']-expected)<.0001
    assert not p['TradersInfo'][nina]['unlocked'] and p['TradersInfo'][nina]['disabled'] and not roots(nina),'Departed contact resurrected'
    visible=request('/client/quest/list')
    assert not any(q['_id']=='119371f120f3232df3f55aa3' for q in visible),'Cached unaccepted lane bypassed its new opening prerequisite'
    old=next(q for q in visible if q['_id']==checkpoint_data['shotgun'])
    assert old['sptStatus']==2,'Previously accepted task was hidden or reset'
    assert not action('QuestAccept',checkpoint_data['shotgun']).get('warnings')
    assert action('QuestAccept','2d97119e90162b58df39a77b').get('warnings'),'Failed old task reopened'
    p=pmc();assert p['Quests']==checkpoint_data['quests'] and p['TaskConditionCounters']==checkpoint_data['counters']
    assert p['Inventory']==checkpoint_data['inventory']
    request('/client/game/logout');after=read(path)
    assert after['traderPurchases'][claire][checkpoint_data['pliers']]==checkpoint_data['purchase']
    assert mail_count(after)==checkpoint_data['messages'],'Migration reissued mail/rewards'
    report=read(a.report);report.update(passed=True,legacyUnacceptedCachedTaskHiddenWithoutSaveChanges=True,legacyAcceptedProgressUnchanged=True,failedTaskNotReopened=True,legacyInventoryUnchanged=True,legacyRewardMailUnchanged=True,transferredPurchaseQuotaPreserved=True,existingContactStandingCreditedOnce=True,departedContactNotResurrected=True)
    if a.stage=='resume':report['controlledRestartIdempotent']=True
    write(a.report,report);print(json.dumps(report,indent=2))

if __name__=='__main__':main()
