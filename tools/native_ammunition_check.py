"""Check generated ammo packs and retail payloads on disposable Development only."""
from pathlib import Path
import argparse, copy, json, ssl, time, urllib.request, uuid, zlib
from story_quest_checks import StoryQuestChecks


def read(path):
    return json.loads(path.read_text('utf-8-sig'))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--game-root', required=True, type=Path)
    ap.add_argument('--port', required=True, type=int)
    ap.add_argument('--report', required=True, type=Path)
    args = ap.parse_args()
    root = args.game_root.resolve()
    state = read(root / '.sptz/installed.json')
    assert state['instanceKind'] == 'Development' and state['port'] == args.port and args.port != 6969
    mod = root / 'SPT/user/mods/Codex-ZombieSurvival'
    config = read(mod / 'config.json')
    assert config['scarcity']['fullAmmoPacks']
    session = ''

    def request(route, body=None):
        req = urllib.request.Request('https://127.0.0.1:%s%s' % (args.port, route), data=json.dumps(body or {}).encode(), headers={'Content-Type': 'application/json', 'Cookie': 'PHPSESSID=' + session, 'responsecompressed': '0', 'requestcompressed': '0'})
        with urllib.request.urlopen(req, context=ssl._create_unverified_context(), timeout=180) as response:
            raw = response.read()
        for mode in [None, 15, -15, 31]:
            try:
                text = (raw if mode is None else zlib.decompress(raw, mode)).decode('utf-8-sig').strip()
                if len(text) == 24 and all(c in '0123456789abcdef' for c in text.lower()):
                    return text
                value = json.loads(text)
                if isinstance(value, dict) and 'err' in value:
                    assert not value['err'], (route, value['err'])
                    return value.get('data')
                return value
            except (ValueError, UnicodeDecodeError, zlib.error):
                pass
        raise AssertionError('Cannot decode ' + route)

    name = 'Ember-Ammunition-QA-' + uuid.uuid4().hex[:8]
    request('/launcher/profile/register', dict(username=name, edition='Standard'))
    profiles = [p for p in (root / 'SPT/user/profiles').glob('*.json') if read(p).get('info', {}).get('username') == name]
    assert len(profiles) == 1
    session = profiles[0].stem
    request('/client/game/profile/create', dict(side='Usec', nickname='EmberAmmoQA', headId='5cde96047d6c8b20b577f016', voiceId='5fc1223595572123ae7384a3'))
    request('/client/game/start')
    templates = request('/client/items')
    handbook = {i['Id']: i['Price'] for i in request('/client/handbook/templates')['Items']}
    expected = config['economy']['handbookPrices']
    assert all(handbook[tpl] == price for tpl, price in expected.items())

    def describe(tpl):
        item = templates[tpl]
        if item.get('_parent') != '543be5cb4bdc2deb348b4568':
            return None
        slots = item.get('_props', {}).get('StackSlots', [])
        if len(slots) != 1 or slots[0].get('_name') != 'cartridges':
            return None
        filters = slots[0]['_props']['filters']
        if len(filters) != 1 or len(filters[0]['Filter']) != 1:
            return None
        return filters[0]['Filter'][0], slots[0]['_max_count']

    def item_arrays(node):
        if isinstance(node, list):
            if node and all(isinstance(i, dict) and '_tpl' in i and '_id' in i for i in node):
                yield node
            else:
                for child in node:
                    yield from item_arrays(child)
        elif isinstance(node, dict):
            for child in node.values():
                yield from item_arrays(child)

    def owned_ammunition(inventory):
        return {i['_id']: (i['_tpl'], i.get('upd', {}).get('StackObjectsCount', 1), i.get('parentId'), i.get('slotId')) for i in inventory['items'] if templates[i['_tpl']].get('_props', {}).get('ammoType') is not None or describe(i['_tpl']) is not None}

    # Carry a deliberately partial owned box through later generated-loot calls.
    # Return a synthetic inventory only to this newly created, isolated QA account.
    fixture_helper=StoryQuestChecks(request,profiles[0],{}, {},templates)
    box=fixture_helper.seed([('573724b42459776125652ac2',1)],fir=False)[0]
    box_id,child_id=box['_id'],uuid.uuid4().hex[:24]
    profile=copy.deepcopy(fixture_helper.pmc())
    profile['Inventory']['items'].append(dict(_id=child_id,_tpl='573719762459775a626ccbc1',parentId=box_id,slotId='cartridges',location=0,upd=dict(StackObjectsCount=3,SpawnedInSession=False)))
    fixture_helper.fixture(profile)
    assert next(i for i in fixture_helper.pmc()['Inventory']['items'] if i['_id']==child_id)['upd']['StackObjectsCount']==3
    packs, raids, native_normalizations = [], [], []
    for index in range(8):
        location = ['rezervbase', 'bigmap', 'interchange', 'sandbox'][index % 4]
        time.sleep(1.05)
        raid = request('/client/match/local/start', dict(location=location, playerSide='Pmc', shouldSkipLootGeneration=False, timeVariant='CURR'))
        assert 'locationLoot' in raid, 'No actual generated location loot returned'
        count = 0
        for items in item_arrays(raid['locationLoot']):
            for box in items:
                definition = describe(box['_tpl'])
                if definition is None:
                    continue
                ammo, capacity = definition
                children = [i for i in items if i.get('parentId') == box['_id'] and i.get('slotId') == 'cartridges']
                assert children and all(i['_tpl'] == ammo for i in children), box['_tpl']
                amounts = [i.get('upd', {}).get('StackObjectsCount', 1) for i in children]
                assert sum(amounts) == capacity, (box['_tpl'], amounts, capacity)
                assert all(0 < n <= templates[ammo]['_props']['StackMaxSize'] for n in amounts)
                assert sorted(i['location'] for i in children) == list(range(len(children)))
                assert handbook[box['_tpl']] == 1, 'Packaging must not double-count its cartridge value'
                packs.append(dict(map=location, tpl=box['_tpl'], ammunition=ammo, capacity=capacity, stacks=amounts))
                count += 1
        raids.append(dict(map=location, packs=count))
        profile = next(p for p in request('/client/game/profile/list') if p['Info']['Side'].lower() != 'savage')
        inventory = copy.deepcopy(profile['Inventory'])
        request('/client/match/local/end', dict(serverId=raid['serverId'], results=dict(profile=profile, result='SURVIVED', exitName='QA ammunition generation fixture', inSession=False, favorite=False, playTime=60), lostInsuredItems=[], transferItems={}))
        actual = next(p for p in request('/client/game/profile/list') if p['Info']['Side'].lower() != 'savage')
        if actual['Inventory'] != inventory:
            before_items={i['_id']:i for i in inventory['items']};after_items={i['_id']:i for i in actual['Inventory']['items']}
            native_normalizations.append(dict(map=location, changedInventoryFields=[k for k in set(actual['Inventory']) | set(inventory) if actual['Inventory'].get(k) != inventory.get(k)],itemOrderOnly=before_items==after_items,changedItemFields=[dict(tpl=before_items[i]['_tpl'],fields=[k for k in set(before_items[i])|set(after_items.get(i,{})) if before_items[i].get(k)!=after_items.get(i,{}).get(k)]) for i in before_items if before_items[i]!=after_items.get(i)]))
        assert owned_ammunition(actual['Inventory']) == owned_ammunition(inventory), 'Generated-loot check must not fill or mutate owned ammunition'
        assert next(i for i in actual['Inventory']['items'] if i['_id']==child_id)['upd']['StackObjectsCount']==3, 'Owned partial pack was refilled'
        if len(packs) >= 12 and len(raids) >= 4:
            break
    assert packs, 'No natural ammunition packs observed; sampling is inconclusive'
    request('/client/game/logout')
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(dict(passed=True, version=state['version'], generatedPacks=packs, sampledRaids=raids, ownedAmmunitionUnchanged=True,partialOwnedPackStaysAtThreeRounds=True,nativePostRaidInventoryChanges=native_normalizations, handbookMatches=True, playerInstanceUsed=False, visiblePackOrCombatAcceptance=False), ensure_ascii=False, indent=2) + '\n', 'utf-8')
    print('PASS %s naturally generated ammunition packs across %s synthetic raids.' % (len(packs), len(raids)))


if __name__ == '__main__':
    main()
