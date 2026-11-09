"""Test the AK-50 quest chain and its native production on a disposable Development profile."""
import argparse
import json
import ssl
import time
import urllib.request
import uuid
import zlib
from pathlib import Path

from story_quest_checks import StoryQuestChecks


def read(path):
    return json.loads(path.read_text('utf-8-sig'))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--game-root', required=True, type=Path)
    parser.add_argument('--port', required=True, type=int)
    parser.add_argument('--report', required=True, type=Path)
    options = parser.parse_args()
    root = options.game_root.resolve()
    state = read(root / '.sptz/installed.json')
    assert state['instanceKind'] == 'Development' and state['port'] == options.port and options.port != 6969
    data = root / 'SPT/user/mods/Codex-ZombieSurvival/data'
    spec = read(data / 'ak50.json')
    quests = read(data / 'quests.json')
    story = read(data / 'story-quests.json')
    bench, deposit, handoff = (spec['quests'][stage] for stage in ('workbench', 'deposit', 'handoff'))
    trader = quests[handoff]['traderId']
    offers = [o for o in read(data / 'manifest.json')['offers'] if o['unlock'] == handoff]
    offer_ids = {o['id'] for o in offers}
    session = ''

    def request(route, body=None, allow_error=False):
        req = urllib.request.Request(f'https://127.0.0.1:{options.port}{route}', data=json.dumps(body or {}).encode(),
                                     headers={'Content-Type': 'application/json', 'Cookie': 'PHPSESSID=' + session,
                                              'requestcompressed': '0', 'responsecompressed': '0'})
        with urllib.request.urlopen(req, context=ssl._create_unverified_context(), timeout=90) as response:
            raw = response.read()
        for mode in (None, 15, -15, 31):
            try:
                message = (raw if mode is None else zlib.decompress(raw, mode)).decode('utf-8-sig').strip()
                if len(message) == 24 and all(c in '0123456789abcdef' for c in message.lower()):
                    return message
                result = json.loads(message)
            except (ValueError, zlib.error, UnicodeDecodeError):
                continue
            if isinstance(result, dict) and 'err' in result:
                if result['err'] and allow_error:
                    return {'rejected': True}
                assert result['err'] == 0, (route, result['err'], result.get('errmsg'))
                return result.get('data')
            return result
        raise AssertionError('Cannot decode ' + route)

    name = 'Ember-AK50-QA-' + uuid.uuid4().hex[:8]
    request('/launcher/profile/register', dict(username=name, edition='Standard'))
    profiles = [path for path in (root / 'SPT/user/profiles').glob('*.json')
                if read(path).get('info', {}).get('username') == name]
    assert len(profiles) == 1
    session = profiles[0].stem
    request('/client/game/profile/create', dict(side='Usec', nickname='EmberAK50QA',
            headId='5cde96047d6c8b20b577f016', voiceId='5fc1223595572123ae7384a3'))
    request('/client/game/start')
    tester = StoryQuestChecks(request, profiles[0], quests, story, request('/client/items'))

    def refresh():
        request('/client/game/start')
        request('/client/trading/api/traderSettings')

    def shown():
        return {q['_id']: q for q in request('/client/quest/list')}

    def assert_blocked(qid):
        assert tester.rejected(tester.action('QuestAccept', qid, allow_error=True)), qid

    def action(data):
        result = request('/client/game/profile/items/moving', dict(data=[data], tm=0))
        assert not result.get('warnings'), result.get('warnings')
        return result

    def finish(qid):
        reply = tester.action('QuestComplete', qid)
        assert not reply.get('warnings'), reply.get('warnings')
        assert next(q for q in tester.pmc()['Quests'] if q['qid'] == qid)['status'] == 4
        refresh()

    assert not set((bench, deposit, handoff)) & set(shown())
    for qid in (bench, deposit, handoff):
        assert_blocked(qid)
    profile = tester.pmc()
    level = 25
    table = request('/client/globals')['config']['exp']['level']['exp_table']
    profile['Info']['Level'] = level
    profile['Info']['Experience'] = sum(row['exp'] for row in table[:level])
    stamp = int(time.time())
    profile['Quests'] = [dict(qid=row['id'], startTime=stamp, status=4, statusTimers={'4': stamp}, completedConditions=[])
                        for row in read(data / 'campaign.json')['main'][:21]]
    next(a for a in profile['Hideout']['Areas'] if a['type'] == 10)['level'] = 3
    next(a for a in profile['Hideout']['Areas'] if a['type'] == 4)['level'] = 1
    tester.fixture(profile)
    refresh()
    assert bench in shown() and deposit not in shown() and handoff not in shown()
    recipes = request('/client/hideout/production/recipes')
    recipes = {r['_id']: r for r in (recipes['recipes'] if isinstance(recipes, dict) else recipes)}
    recipe = recipes[spec['recipe']]
    assert spec['nativeRecipe'] not in recipes and [r['_id'] for r in recipes.values()
                                                  if r['endProduct'] == spec['weapon']] == [spec['recipe']]
    assert spec['recipe'] not in tester.pmc()['UnlockedInfo']['unlockedProductionRecipe']
    tester.action('QuestAccept', bench)
    tester.ready(bench)
    finish(bench)
    assert spec['recipe'] in tester.pmc()['UnlockedInfo']['unlockedProductionRecipe']
    assert deposit in shown() and handoff not in shown()
    tester.action('QuestAccept', deposit)
    tester.ready(deposit)
    finish(deposit)
    assert handoff in shown()
    assert not offer_ids & {i['_id'] for i in request('/client/trading/api/getTraderAssort/' + trader)['items']}

    assert not tester.action('QuestAccept', handoff).get('warnings')
    finds = [c for c in quests[handoff]['conditions']['AvailableForFinish'] if c['conditionType'] == 'FindItem']
    available = {f['target'][0] for f in finds}
    counts = {}
    for map_id in ('bigmap', 'rezervbase', spec['sourceMap']):
        time.sleep(1.05)
        raid = request('/client/match/local/start', dict(location=map_id, playerSide='Pmc',
                       shouldSkipLootGeneration=False, timeVariant='CURR'))
        points = [s for s in raid['locationLoot']['Loot'] if not s.get('IsContainer') and
                  any(i['_tpl'] in available for i in s['Items'])]
        templates = {i['_tpl'] for point in points for i in point['Items'] if i['_tpl'] in available}
        assert len(templates) == 1, (map_id, templates)
        counts[map_id] = len(points)
        available -= templates
        if map_id == spec['sourceMap']:
            assert templates == {spec['handguard']} and [p['Id'] for p in points] == [spec['relocatedSpot']]
            assert any(s['Id'] == spec['sourceSpot'] for s in raid['locationLoot']['Loot'])
        request('/client/match/local/end', dict(serverId=raid['serverId'],
                results=dict(profile=tester.pmc(), result='SURVIVED', exitName='QA AK-50 loot',
                             inSession=False, favorite=False, playTime=60), lostInsuredItems=[], transferItems={}))
    assert not available
    print('PASS chain gating, real handovers, blueprint and three generated part sources', flush=True)

    fuel = tester.seed([('5d1b371186f774253763a656', 1)])[0]
    profile = tester.pmc()
    item = next(i for i in profile['Inventory']['items'] if i['_id'] == fuel['_id'])
    props = tester.templates[fuel['_tpl']]['_props']
    item['upd']['Resource'] = dict(Value=max(props.get('MaxResource', 0), props.get('Resource', 0)))
    tester.fixture(profile)
    action(dict(Action='HideoutPutItemsInAreaSlots', areaType=4, items={'0': dict(id=fuel['_id'], count=1)}))
    action(dict(Action='HideoutToggleArea', areaType=4, enabled=True))
    inputs = [r for r in recipe['requirements'] if r['type'] in ('Item','Tool')]
    incoming = tester.seed([(r['templateId'], r.get('count', 1)) for r in inputs], fir=False)
    items, tools = [], []
    for req in inputs:
        selected = [dict(id=i['_id'], count=1) for i in incoming if i['_tpl'] == req['templateId']]
        assert len(selected) == req.get('count', 1)
        (tools if req['type'] == 'Tool' else items).extend(selected)
    recipe_id = spec['recipe']
    action(dict(Action='HideoutSingleProductionStart', recipeId=recipe_id, items=items, tools=tools, timestamp=int(time.time())))
    production = tester.pmc()['Hideout']['Production'][recipe_id]
    assert production['inProgress'] and 0 < production['ProductionTime'] <= 120, 'Use an accelerated Development SVM preset.'
    time.sleep(production['ProductionTime'] + 3)
    refresh()
    action(dict(Action='HideoutTakeProduction', recipeId=recipe_id))
    inv = tester.pmc()['Inventory']['items']
    crafted, = [i for i in inv if i['_tpl'] == spec['weapon']]
    assert not any(i.get('parentId') == crafted['_id'] for i in inv), 'Craft must make the bare receiver.'

    tester.ready(handoff)
    finish(handoff)
    tester.after_complete(handoff)
    message, = [m for d in tester.saved()['dialogues'].values() for m in d['messages']
                if m.get('templateId') == handoff + ' successMessageText']
    attachments = message['items']['data']
    rifle, = [i for i in attachments if i['_tpl'] == spec['weapon']]
    loaded_magazine, = [i for i in attachments if i.get('parentId') == rifle['_id'] and i.get('slotId') == 'mod_magazine']
    rounds, = [i for i in attachments if i.get('parentId') == loaded_magazine['_id'] and i.get('slotId') == 'cartridges']
    assert rounds['_tpl'] in spec['ammo'] and rounds['upd']['StackObjectsCount'] == 10
    assortment = request('/client/trading/api/getTraderAssort/' + trader)
    stock = {i['_id']: i for i in assortment['items']}
    assert offer_ids <= set(stock)
    assert all(stock[o['id']]['upd']['BuyRestrictionMax'] == o['limit'] for o in offers)
    bonus = request('/ember/map/abundance')
    assert bonus['bigmap']['quests'] >= .015 and bonus['rezervbase']['quests'] >= .015
    report = dict(version=state['version'], drop_points=counts, real_receiver_craft=True, real_handovers=True,
                  synthetic_field_find_and_plant_progress=True, real_native_quest_rewards=True,
                  one_reward_rifle=True, loaded_rounds=10, limited_offers=len(offers), player_instance_used=False)
    options.report.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print('PASS AK-50 workbench craft, gated handoff and one-time rifle reward')


if __name__ == '__main__':
    main()
