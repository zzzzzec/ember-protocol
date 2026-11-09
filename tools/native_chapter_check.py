"""Create real 2.4 task states, then verify them after a Development-only 2.5 upgrade."""
import argparse,copy,json,ssl,time,urllib.request,uuid,zlib
from pathlib import Path
from native_later_contacts_check import read,write
from story_quest_checks import StoryQuestChecks

def main():
    ap=argparse.ArgumentParser();ap.add_argument('--game-root',type=Path,required=True);ap.add_argument('--port',type=int,required=True)
    ap.add_argument('--stage',choices=['fresh','verify','resume'],required=True);ap.add_argument('--report',type=Path,required=True);a=ap.parse_args()
    root=a.game_root.resolve();installed=read(root/'.sptz/installed.json')
    assert installed['instanceKind']=='Development' and installed['port']==a.port and a.port!=6969
    assert installed['version']==('2.4.0' if a.stage=='fresh' else '2.5.0')
    data=root/'SPT/user/mods/Codex-ZombieSurvival/data';quests=read(data/'quests.json');main_ids=[r['id'] for r in read(data/'campaign.json')['main']]
    checkpoint=root/'.sptz/chapter-qa.json';session=''
    def request(route,body=None):
        req=urllib.request.Request('https://127.0.0.1:'+str(a.port)+route,data=json.dumps(body or {}).encode(),headers={'Content-Type':'application/json','Cookie':'PHPSESSID='+session,'responsecompressed':'0','requestcompressed':'0'})
        with urllib.request.urlopen(req,context=ssl._create_unverified_context(),timeout=90) as response:raw=response.read()
        for mode in [None,zlib.MAX_WBITS,-zlib.MAX_WBITS,31]:
            try:
                text=(raw if mode is None else zlib.decompress(raw,mode)).decode('utf-8-sig').strip()
                if len(text)==24 and all(c in '0123456789abcdef' for c in text.lower()):return text
                result=json.loads(text)
                if isinstance(result,dict) and 'err' in result:assert result['err']==0,result;return result.get('data')
                return result
            except (ValueError,zlib.error,UnicodeDecodeError):pass
        raise AssertionError('Cannot decode '+route)
    def pmc():return next(p for p in request('/client/game/profile/list') if p['Info']['Side'].lower()!='savage')
    def action(kind,qid,**extra):
        event=dict(Action=kind,qid=qid,type='Quest',removeExcessItems=True);event.update(extra)
        result=request('/client/game/profile/items/moving',dict(data=[event],tm=0));assert not result.get('warnings'),result.get('warnings');return result
    def fixture(p):
        time.sleep(1.05);raid=request('/client/match/local/start',dict(location='sandbox',playerSide='Pmc',shouldSkipLootGeneration=True,timeVariant='CURR'))
        request('/client/match/local/end',dict(serverId=raid['serverId'],results=dict(profile=p,result='SURVIVED',exitName='QA chapter progress fixture',inSession=False,favorite=False,playTime=60),lostInsuredItems=[],transferItems={}))
    def snapshot(saved):
        p=saved['characters']['pmc']
        return dict(inventory=p['Inventory'],quests=p['Quests'],counters=p.get('TaskConditionCounters',{}),experience=p['Info']['Experience'],messages={k:v.get('messages',[]) for k,v in saved.get('dialogues',{}).items()})
    if a.stage=='fresh':
        rows=[]
        for index in [25,26,27]:
            username='Ember-Chapter-QA-'+uuid.uuid4().hex[:10];session='';request('/launcher/profile/register',dict(username=username,edition='Standard'))
            matches=[p for p in (root/'SPT/user/profiles').glob('*.json') if read(p).get('info',{}).get('username')==username];assert len(matches)==1
            path=matches[0];session=path.stem
            request('/client/game/profile/create',dict(side='Usec',nickname='EmberChapterQA',headId='5cde96047d6c8b20b577f016',voiceId='5fc1223595572123ae7384a3'))
            request('/client/game/start');p=copy.deepcopy(pmc());table=request('/client/globals')['config']['exp']['level']['exp_table']
            p['Info']['Level']=30;p['Info']['Experience']=sum(r['exp'] for r in table[:30])
            stamp=int(time.time());p['Quests']=[dict(qid=qid,startTime=stamp,status=4,statusTimers={'4':stamp},completedConditions=[]) for qid in main_ids[:index]]
            fixture(p);request('/client/game/start');qid=main_ids[index];assert quests[qid]['traderId']=='4b7e8889344316d03e8369b7'
            action('QuestAccept',qid);condition=quests[qid]['conditions']['AvailableForFinish'][0]
            if index==25:
                helper=StoryQuestChecks(request,path,quests,read(data/'story-quests.json'),request('/client/items'))
                incoming=helper.seed([(condition['target'][0],1)])
                action('QuestHandover',qid,conditionId=condition['id'],items=[dict(id=incoming[0]['_id'],count=1)])
                assert pmc()['TaskConditionCounters'][condition['id']]['value']==1<condition['value']
                kind='one_of_two_usb_drives_really_handed_in'
            else:
                p=copy.deepcopy(pmc());value=17 if index==26 else 1
                p.setdefault('TaskConditionCounters',{})[condition['id']]=dict(id=condition['id'],sourceId=qid,type='CounterCreator',value=value)
                if index==27:next(q for q in p['Quests'] if q['qid']==qid)['status']=3
                fixture(p);kind='partial_combat_counter_fixture' if index==26 else 'ready_to_complete_fixture'
            request('/client/game/logout');saved=read(path)
            expected_status=3 if index==27 else 2;assert next(q for q in saved['characters']['pmc']['Quests'] if q['qid']==qid)['status']==expected_status
            rows.append(dict(session=session,username=username,quest=qid,kind=kind,expected=snapshot(saved)))
        write(checkpoint,dict(sourceVersion=installed['version'],rows=rows));write(a.report,dict(passedFresh=True,sourceVersion=installed['version'],cohorts=3,realPartialNativeHandover=True,syntheticPrerequisitesCombatAndXp=True,playerInstanceUsed=False))
        print('PASS real 2.4 acceptance, partial USB handover, partial combat and ready states created.');return
    saved_checkpoint=read(checkpoint);assert saved_checkpoint['sourceVersion']=='2.4.0'
    for row in saved_checkpoint['rows']:
        session=row['session'];path=root/'SPT/user/profiles'/(session+'.json');saved=read(path)
        assert saved['info']['username']==row['username'] and row['username'].startswith('Ember-Chapter-QA-')
        request('/client/game/start');request('/client/trading/api/traderSettings');qid=row['quest']
        shown=next(q for q in request('/client/quest/list') if q['_id']==qid)
        assert shown['traderId']==quests[qid]['traderId']!='4b7e8889344316d03e8369b7'
        action('QuestAccept',qid);request('/client/game/logout');after=read(path)
        assert snapshot(after)==row['expected'],('Migrated chapter quest lost progress, inventory, XP or messages',row['kind'])
    result=read(a.report);result.update(passed=True,targetVersion=installed['version'],threeOriginalTaskStatesAndMailPreserved=True,newOwnersReturnedByNativeQuestList=True,reacceptDoesNotResetOrReissue=True)
    if a.stage=='resume':result['secondRestartPreserved']=True
    write(a.report,result);print('PASS three real 2.4 cohorts preserve partial/ready progress and messages under new owners.')

if __name__=='__main__':main()
