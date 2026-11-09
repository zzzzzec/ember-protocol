"""Development-only completion-confirmed story decisions across two controlled restarts.

prepare -> restart backend -> commit -> restart backend -> replay.
All progress, deaths and inventory fixtures belong to new disposable QA accounts.
"""
import argparse
import concurrent.futures
import copy
import hashlib
import json
import ssl
import time
import urllib.request
import uuid
import zlib
from pathlib import Path


def read(path):
    return json.loads(path.read_text('utf-8-sig'))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--game-root', type=Path, required=True)
    parser.add_argument('--port', type=int, required=True)
    parser.add_argument('--report', type=Path, required=True)
    parser.add_argument('--phase', choices=['prepare', 'commit', 'replay', 'ui-check'], required=True)
    args = parser.parse_args()
    root = args.game_root.resolve()
    installed = read(root / '.sptz/installed.json')
    assert installed['instanceKind'] == 'Development' and installed['port'] == args.port and args.port != 6969
    mod = root / 'SPT/user/mods/Codex-ZombieSurvival'
    rules, quests = read(mod / 'data/story-quests.json'), read(mod / 'data/quests.json')
    group = next(g for g in rules['choices'] if g['id'] == 'family_reply')
    assert group['commitStage'] == 'complete'
    marker_path = root / '.sptz/decision-qa.json'
    session = ''

    def request(route, body=None, allow_error=False):
        req = urllib.request.Request('https://127.0.0.1:%s%s' % (args.port, route), data=json.dumps(body or {}).encode(), headers={'Content-Type': 'application/json', 'Cookie': 'PHPSESSID=' + session, 'responsecompressed': '0', 'requestcompressed': '0'})
        try:
            with urllib.request.urlopen(req, context=ssl._create_unverified_context(), timeout=120) as resp:
                raw = resp.read()
        except urllib.error.HTTPError:
            if allow_error:
                return {'rejected': True}
            raise
        for mode in [None, zlib.MAX_WBITS, -zlib.MAX_WBITS, 31]:
            try:
                text = (raw if mode is None else zlib.decompress(raw, mode)).decode('utf-8-sig').strip()
                if len(text) == 24 and all(c in '0123456789abcdef' for c in text.lower()):
                    return text
                result = json.loads(text)
                if isinstance(result, dict) and 'err' in result:
                    if result['err'] and allow_error:
                        return {'rejected': True}
                    assert result['err'] == 0, result
                    return result.get('data')
                return result
            except (ValueError, zlib.error, UnicodeDecodeError):
                pass
        raise AssertionError('Cannot decode ' + route)

    def action(kind, qid):
        return request('/client/game/profile/items/moving', dict(data=[dict(Action=kind, qid=qid)], tm=0))

    def rejected(kind, qid):
        result = request('/client/game/profile/items/moving', dict(data=[dict(Action=kind, qid=qid)], tm=0), True)
        return result.get('rejected') or result.get('warnings')

    def pmc():
        return next(p for p in request('/client/game/profile/list') if p['Info']['Side'].lower() != 'savage')

    def saved():
        request('/client/game/logout')
        return read(root / 'SPT/user/profiles' / (session + '.json'))

    def fixture(profile, result='SURVIVED'):
        time.sleep(1.05)
        raid = request('/client/match/local/start', dict(location='sandbox', playerSide='Pmc', shouldSkipLootGeneration=True, timeVariant='CURR'))
        request('/client/match/local/end', dict(serverId=raid['serverId'], results=dict(profile=profile, result=result, exitName='QA decision fixture', inSession=False, favorite=False, playTime=60), lostInsuredItems=[], transferItems={}))

    def login(account):
        nonlocal session
        path = root / 'SPT/user/profiles' / (account['session'] + '.json')
        assert read(path)['info']['username'] == account['username'] and account['username'].startswith('Ember-Decision-QA-')
        session = account['session']
        request('/client/game/start')

    def states(profile):
        return {q['qid']: q['status'] for q in profile['characters']['pmc']['Quests']}

    def no_send():
        profile = saved()
        assert 'family_reply' not in profile.get('spt', {}).get('emberStoryChoices', {})
        assert all(states(profile).get(route['choice']) in [2, 3] for route in group['routes'])
        assert not [message for dialog in profile['dialogues'].values() for message in dialog.get('messages', []) if message.get('templateId') in [route['choice'] + ' successMessageText' for route in group['routes']]]

    def snapshot():
        profile = saved()
        return dict(inventory=profile['characters']['pmc']['Inventory']['items'], xp=profile['characters']['pmc']['Info']['Experience'], success=[(m.get('_id'), m.get('templateId')) for d in profile['dialogues'].values() for m in d.get('messages', []) if m.get('templateId', '').endswith(' successMessageText')], receipt=profile.get('spt', {}).get('emberStoryChoices', {}).get('family_reply'))

    def committed(expected=None, followup=True):
        profile = saved()
        receipt = profile['spt']['emberStoryChoices']['family_reply']
        route = next(r for r in group['routes'] if r['choice'] == receipt['quest'])
        if expected:
            assert route['id'] == expected
        other = next(r for r in group['routes'] if r['choice'] != route['choice'])
        assert states(profile)[route['choice']] == 4 and states(profile)[other['choice']] == 5
        failed_mail = lambda p: [m for d in p['dialogues'].values() for m in d.get('messages', []) if m.get('templateId') == other['choice'] + ' failMessageText']
        assert len(failed_mail(profile)) == 1, 'The withdrawn draft has one archive notice, not repeated failure mail.'
        action('QuestFail', other['choice'])
        action('QuestFail', route['choice'])
        after_fail_replays = saved()
        assert len(failed_mail(after_fail_replays)) == 1 and states(after_fail_replays)[route['choice']] == 4
        for key in ['recipient', 'scope', 'payloadKey', 'payload']:
            assert receipt[key] == route[key], key
        assert receipt['commitStage'] == 'complete' and receipt['payloadHash'] == hashlib.sha256(route['payload'].encode()).hexdigest() == route['payloadHash']
        assert rejected('QuestAccept', other['choice']) and rejected('QuestComplete', other['choice'])
        shown = {q['_id']: q for q in request('/client/quest/list')}
        assert not set(other['quests'][1:]) & set(shown)
        for qid in route['quests'][:2 if followup else 1]:
            messages = [m for d in profile['dialogues'].values() for m in d.get('messages', []) if m.get('templateId') == qid + ' successMessageText']
            assert len(messages) == 1, (qid, len(messages))
        return route

    if args.phase == 'prepare':
        accounts = []
        for kind in ['signed', 'relay', 'race', 'ui']:
            session = ''
            name = 'Ember-Decision-QA-' + kind + '-' + uuid.uuid4().hex[:6]
            request('/launcher/profile/register', dict(username=name, edition='Standard'))
            matches = [p for p in (root / 'SPT/user/profiles').glob('*.json') if read(p).get('info', {}).get('username') == name]
            assert len(matches) == 1
            session = matches[0].stem
            request('/client/game/profile/create', dict(side='Usec', nickname='EmberUITest' if kind == 'ui' else 'EmberDecisionQA', headId='5cde96047d6c8b20b577f016', voiceId='5fc1223595572123ae7384a3'))
            request('/client/game/start')
            assert rejected('QuestAccept', group['routes'][0]['choice'])
            profile = copy.deepcopy(pmc())
            main = read(mod / 'data/campaign.json')['main'][:9]
            for quest in main:
                qid = quest['id']
                old = next((q for q in profile['Quests'] if q['qid'] == qid), None)
                if old is None:
                    profile['Quests'].append(dict(qid=qid, startTime=int(time.time()), status=4, statusTimers={'4': int(time.time())}, completedConditions=[]))
                else:
                    old['status'] = 4
            fixture(profile)
            request('/client/trading/api/traderSettings')
            if kind != 'ui':
                for route in group['routes']:
                    assert not action('QuestAccept', route['choice']).get('warnings')
                no_send()
                fixture(copy.deepcopy(pmc()), 'KILLED')
                no_send()
            accounts.append(dict(kind=kind, session=session, username=name))
        marker_path.write_text(json.dumps(dict(accounts=accounts), indent=2) + '\n', encoding='utf-8')
        report = dict(version=installed['version'], preparedBothDraftsWithoutSending=True, deathDoesNotChoose=True, lockedAcceptRejected=True, preparedRestartChecked=False, committedRestartChecked=False, accounts=[a['kind'] for a in accounts], syntheticPrerequisiteAndDeathFixtures=True, playerInstanceUsed=False, gameUiOrInRaidAcceptance=False)
        args.report.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
        print('PASS prepare: both drafts survive death without sending; four isolated QA accounts created.')
        return

    accounts = read(marker_path)['accounts']
    report = read(args.report)
    if args.phase == 'ui-check':
        login(next(a for a in accounts if a['kind'] == 'ui'))
        route = committed(followup=False)
        report['uiSentRoute'] = route['id']
        report['nativeReceiptAfterActualUiCompletion'] = True
    elif args.phase == 'commit':
        results = []
        for account in accounts:
            if account['kind'] == 'ui':
                continue
            login(account)
            no_send()
            if account['kind'] == 'race':
                with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
                    list(pool.map(lambda r: request('/client/game/profile/items/moving', dict(data=[dict(Action='QuestComplete', qid=r['choice'])], tm=0), True), group['routes']))
                chosen = saved()['spt']['emberStoryChoices']['family_reply']['quest']
                route = next(r for r in group['routes'] if r['choice'] == chosen)
            else:
                route = next(r for r in group['routes'] if r['id'] == account['kind'])
                assert not action('QuestComplete', route['choice']).get('warnings')
            committed(route['id'], False)
            reply = route['quests'][1]
            assert not action('QuestAccept', reply).get('warnings')
            assert not action('QuestComplete', reply).get('warnings')
            committed(route['id'])
            before = snapshot()
            for qid in route['quests']:
                action('QuestComplete', qid)
                action('QuestAccept', qid)
            assert snapshot() == before
            results.append(dict(account=account['kind'], route=route['id'], receiptHash=before['receipt']['payloadHash'], successMailOnce=True, replayPreservesInventoryXpAndReceipt=True))
        report.update(preparedRestartChecked=True, twoProfilesKeepIndependentChoices=True, competingCompletionsCommitExactlyOne=True, commits=results)
        print('PASS commit: two independent routes and racing completions; exact payload receipts and replay boundaries.')
    else:
        for account in accounts:
            if account['kind'] == 'ui':
                continue
            login(account)
            route = committed()
            before = snapshot()
            for qid in route['quests']:
                action('QuestComplete', qid)
                action('QuestAccept', qid)
            assert snapshot() == before
            fixture(copy.deepcopy(pmc()), 'KILLED')
            committed(route['id'])
            after_death = snapshot()
            assert after_death['receipt'] == before['receipt'] and after_death['success'] == before['success']
            for qid in route['quests']:
                action('QuestComplete', qid)
            assert snapshot() == after_death
        report.update(committedRestartChecked=True, deathAfterSendingPreservesChoiceAndMail=True, repeatedFailCannotDuplicateMailOrDowngradeSuccess=True, passed=True)
        print('PASS replay: committed replies and once-only XP/mail survive the second backend restart.')
    args.report.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')


if __name__ == '__main__':
    main()
