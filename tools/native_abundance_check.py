import argparse
import json
import ssl
import time
import urllib.request
import uuid
import zlib
from pathlib import Path


def read(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


parser = argparse.ArgumentParser(description='Verify map abundance through the native Development raid endpoints.')
parser.add_argument('--game-root', type=Path, required=True)
parser.add_argument('--port', type=int, required=True)
parser.add_argument('--report', type=Path, required=True)
parser.add_argument('--recheck-restart', action='store_true')
options = parser.parse_args()
root = options.game_root.resolve()
state = read(root / '.sptz/installed.json')
assert state['instanceKind'] == 'Development' and state['port'] == options.port and options.port != 6969
prefix = f'https://127.0.0.1:{options.port}'
marker = root / '.sptz/abundance-qa.json'
session = ''


def request(route, body=None):
    req = urllib.request.Request(prefix + route, data=None if body is None else json.dumps(body).encode(),
                                 headers={'Content-Type': 'application/json', 'Cookie': 'PHPSESSID=' + session,
                                          'responsecompressed': '0', 'requestcompressed': '0'})
    with urllib.request.urlopen(req, context=ssl._create_unverified_context(), timeout=90) as response:
        raw = response.read()
    for mode in [None, 15, -15, 31]:
        try:
            text = (raw if mode is None else zlib.decompress(raw, mode)).decode('utf-8-sig').strip()
            if len(text) == 24 and all(c in '0123456789abcdef' for c in text.lower()):
                return text
            result = json.loads(text)
        except (ValueError, zlib.error, UnicodeDecodeError):
            continue
        if isinstance(result, dict) and 'err' in result:
            assert not result['err'], (route, result.get('errmsg'))
            return result.get('data')
        return result
    raise AssertionError('Cannot decode ' + route)


def pmc():
    return next(p for p in request('/client/game/profile/list', {}) if p['Info']['Side'].lower() != 'savage')


def progress():
    return request('/ember/map/abundance')


def start(map_id, loot=False):
    time.sleep(1.05)
    raid = request('/client/match/local/start', {'location': map_id, 'playerSide': 'Pmc',
                   'shouldSkipLootGeneration': not loot, 'timeVariant': 'CURR'})
    assert raid and raid['serverId'], (map_id, 'native start returned no ID')
    return raid


def victim(location, role='infectedAssault'):
    return {'ProfileId': uuid.uuid4().hex[:24], 'Role': role, 'Location': location, 'Side': 'Savage'}


def end(raid, outcome='SURVIVED', victims=(), completed=()):
    profile = pmc()
    profile.setdefault('Stats', {}).setdefault('Eft', {})['Victims'] = list(victims)
    for qid in completed:
        q = next((q for q in profile['Quests'] if q['qid'] == qid), None)
        if q is None:
            q = dict(qid=qid, startTime=int(time.time()), statusTimers={'4': int(time.time())}, completedConditions=[])
            profile['Quests'].append(q)
        q['status'] = 4
    body = dict(serverId=raid['serverId'], results=dict(profile=profile, result=outcome, exitName='QA',
                inSession=False, favorite=False, playTime=60), lostInsuredItems=[], transferItems={})
    request('/client/match/local/end', body)
    return body


def measure(raid):
    loot = raid['locationLoot']['Loot']
    container = [s for s in loot if s.get('IsContainer')]
    loose = [s for s in loot if not s.get('IsContainer')]
    return {'loose': len(loose), 'container_items': sum(len(s['Items']) - 1 for s in container),
            'containers': len(container)}


if options.recheck_restart:
    account = read(marker)
    session = account['session']
    assert account['username'].startswith('Ember-Abundance-QA-')
    assert read(root / 'SPT/user/profiles' / (session + '.json'))['info']['username'] == account['username']
    request('/client/game/start', {})
    assert progress() == account['progress']
    print('PASS abundance persisted after backend restart')
    quests = {quest['_id']: quest for quest in request('/client/quest/list', {})}
    data = root / 'SPT/user/mods/Codex-ZombieSurvival/data'
    original = read(data / 'quests.json')
    opening = read(data / 'map-abundance.json')['maps']['sandbox']['main'][0]
    assert original[opening]['location'] == 'any' and quests[opening]['location'] == '653e6760052c01c1c805532f'
    assert any(q['location'] == 'any' and original[qid]['location'] == 'any' for qid, q in quests.items() if qid in original)
    assert quests[opening]['conditions'] == original[opening]['conditions'] or (
        [c['id'] for c in quests[opening]['conditions']['AvailableForFinish']] ==
        [c['id'] for c in original[opening]['conditions']['AvailableForFinish']])
    print('PASS native quest response shows Ground Zero while keeping objective IDs and unrestricted quests')
    report = read(options.report)
    report['persisted_after_restart'] = True
    report['quest_map_label_verified'] = True
    options.report.write_text(json.dumps(report, indent=2), encoding='utf-8')
    raise SystemExit()

username = 'Ember-Abundance-QA-' + uuid.uuid4().hex[:8]
request('/launcher/profile/register', dict(username=username, edition='Standard'))
profiles = [p for p in (root / 'SPT/user/profiles').glob('*.json') if read(p).get('info', {}).get('username') == username]
assert len(profiles) == 1
session = profiles[0].stem
request('/client/game/profile/create', dict(side='Usec', nickname='EmberAbundanceQA',
        headId='5cde96047d6c8b20b577f016', voiceId='5fc1223595572123ae7384a3'))
request('/client/game/start', {})
initial = progress()
assert len(initial) == 6 and all(p == {'total': 1, 'quests': 0, 'kills': 0} for p in initial.values()), initial
base_samples = []
for _ in range(5):
    raid = start('bigmap', loot=True)
    base_samples.append(measure(raid))
    end(raid)
v = victim('bigmap')
body = end(start('bigmap'), 'KILLED', [v, v, victim('bigmap', 'assault'), victim('laboratory')])
assert progress()['bigmap']['kills'] == 0.0005, progress()
request('/client/match/local/end', body)
assert progress()['bigmap']['kills'] == 0.0005
end(start('factory4_night'), 'LEFT', [victim('factory4_night', 'infectedPmc')])
assert progress()['factory']['kills'] == 0.0005 and progress()['laboratory']['kills'] == 0
end(start('sandbox_high'), 'RUNNER', [victim('Sandbox_high')])
assert progress()['sandbox']['kills'] == 0.0005
end(start('bigmap'), 'KILLED', [victim('bigmap') for _ in range(99)])
assert progress()['bigmap']['kills'] == 0.05
awards = read(root / 'SPT/user/mods/Codex-ZombieSurvival/data/map-abundance.json')['maps']['bigmap']
end(start('bigmap'), completed=[awards['main'][0], awards['side'][0]])
assert progress()['bigmap'] == dict(total=1.158, quests=.108, kills=.05), progress()['bigmap']
end(start('bigmap'), 'KILLED', [victim('bigmap') for _ in range(3040)], completed=awards['main'] + awards['side'])
assert progress()['bigmap'] == dict(total=3, quests=.432, kills=1.568), progress()['bigmap']
boosted_samples = []
for _ in range(5):
    raid = start('bigmap', loot=True)
    boosted_samples.append(measure(raid))
    end(raid)
assert sum(row['loose'] for row in boosted_samples) > sum(row['loose'] for row in base_samples), (base_samples, boosted_samples)
assert sum(row['container_items'] for row in boosted_samples) > sum(row['container_items'] for row in base_samples), (base_samples, boosted_samples)
marker.write_text(json.dumps(dict(session=session, username=username, progress=progress())), encoding='utf-8')
report = dict(version=state['version'], tested=['fresh', 'death', 'left', 'run-through', 'replay', 'victim map and role',
              'dedup', '100 kills', 'both caps', 'quest backfill', 'both loot sources'],
              base=base_samples, boosted=boosted_samples, state=progress())
options.report.parent.mkdir(parents=True, exist_ok=True)
options.report.write_text(json.dumps(report, indent=2), encoding='utf-8')
print(json.dumps(report, indent=2))
