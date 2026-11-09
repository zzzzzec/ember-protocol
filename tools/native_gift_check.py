"""Native gift/login/attachment checks. Disposable Development profile only."""
from pathlib import Path
import argparse, copy, json, socket, ssl, time, urllib.request, uuid, zlib

def read(path):
    return json.loads(path.read_text('utf-8-sig'))

def write(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + '\n', 'utf-8')

def main():
    p = argparse.ArgumentParser()
    p.add_argument('--game-root', required=True, type=Path)
    p.add_argument('--port', required=True, type=int)
    p.add_argument('--stage', required=True, choices=['create', 'seed', 'verify', 'resume'])
    p.add_argument('--report', required=True, type=Path)
    a = p.parse_args()
    root = a.game_root.resolve()
    installed = read(root / '.sptz/installed.json')
    assert installed['instanceKind'] == 'Development' and installed['port'] == a.port and a.port != 6969
    checkpoint = root / '.sptz/gift-qa.json'
    catalog = read(root / 'SPT/SPT_Data/configs/gifts.json')['gifts']
    ids = ['MechanicGiftDay1', 'PraporGiftDay1', 'PraporGiftDay2']
    gifts = {key: catalog[key] for key in ids}
    coop = read(root / 'SPT/SPT_Data/configs/trader.json')['fence']['coopExtractGift']
    for index, template in enumerate(coop['messageLocaleIds']):
        gifts['CoopExtractFixture'+str(index)] = dict(trader='579dc571d53a0658a154fbec',
            localeTextId=template, items=copy.deepcopy(gifts['MechanicGiftDay1']['items']))
    signatures = {(g['trader'], g['localeTextId']) for g in gifts.values()}
    session = ''

    def request(route, body=None):
        req = urllib.request.Request('https://127.0.0.1:' + str(a.port) + route,
            data=json.dumps(body or {}).encode(), headers={'Content-Type': 'application/json',
            'Cookie': 'PHPSESSID=' + session, 'responsecompressed': '0', 'requestcompressed': '0'})
        with urllib.request.urlopen(req, context=ssl._create_unverified_context(), timeout=60) as r:
            raw = r.read()
        for mode in [None, zlib.MAX_WBITS, -zlib.MAX_WBITS, zlib.MAX_WBITS | 16]:
            try:
                text = (raw if mode is None else zlib.decompress(raw, mode)).decode('utf-8-sig').strip()
                if len(text) == 24 and all(c in '0123456789abcdef' for c in text.lower()):
                    return text
                result = json.loads(text)
                if isinstance(result, dict) and 'err' in result:
                    assert not result['err'], ('Native request rejected', route, result['err'])
                    return result.get('data')
                return result
            except (ValueError, UnicodeDecodeError, zlib.error):
                pass
        raise AssertionError('Cannot decode ' + route)

    def messages(profile):
        return [(tid, m) for tid, dialogue in profile.get('dialogues', {}).items()
                for m in dialogue.get('messages', [])]

    if a.stage == 'create':
        username = 'Ember-Gift-QA-' + uuid.uuid4().hex[:8]
        request('/launcher/profile/register', dict(username=username, edition='Standard'))
        matches = [f for f in (root / 'SPT/user/profiles').glob('*.json')
                   if read(f).get('info', {}).get('username') == username]
        assert len(matches) == 1
        session = matches[0].stem
        request('/client/game/profile/create', dict(side='Usec', nickname='GiftQA',
            headId='5cde96047d6c8b20b577f016', voiceId='5fc1223595572123ae7384a3'))
        request('/client/game/start')
        request('/client/trading/api/traderSettings')
        mod = root / 'SPT/user/mods/Codex-ZombieSurvival'
        first = read(mod / 'data/campaign.json')['main'][0]['id']
        for action in ['QuestAccept', 'QuestComplete']:
            result = request('/client/game/profile/items/moving', dict(data=[dict(
                Action=action, qid=first, type='Quest', removeExcessItems=True)], tm=0))
            assert not result.get('warnings'), result.get('warnings')
        request('/client/game/logout')
        profile = read(matches[0])
        assert not any((tid, m.get('templateId')) in signatures for tid, m in messages(profile))
        rewards = [m for _, m in messages(profile) if m.get('templateId') == first + ' successMessageText']
        assert len(rewards) == 1 and rewards[0].get('items', {}).get('data')
        write(checkpoint, dict(session=session, username=username, quest=first,
            inventory=profile['characters']['pmc']['Inventory'], authoredReward=copy.deepcopy(rewards[0])))
        print('PASS fresh login blocks Mechanic gift and preserves native authored quest reward.')
        return

    saved = read(checkpoint)
    session = saved['session']
    path = root / 'SPT/user/profiles' / (session + '.json')
    assert read(path)['info']['username'] == saved['username'] and saved['username'].startswith('Ember-Gift-QA-')
    if a.stage == 'seed':
        with socket.socket() as s:
            s.settimeout(1)
            assert s.connect_ex(('127.0.0.1', a.port)) != 0, 'Stop Development backend before seeding fixture.'
        profile = read(path)
        profile['characters']['pmc']['Info']['RegistrationDate'] = int(time.time()) - 3 * 86400
        seeded = []
        def parcel(gift, trader, collected=False):
            stash = uuid.uuid4().hex[:24]
            items = copy.deepcopy(gift['items'])
            remap = {item['_id']: uuid.uuid4().hex[:24] for item in items}
            for item in items:
                item['_id'] = remap[item['_id']]
                item['parentId'] = remap.get(item.get('parentId'), stash)
            message = dict(_id=uuid.uuid4().hex[:24], uid=trader, type=13, dt=int(time.time()),
                templateId=gift['localeTextId'], text='', hasRewards=not collected,
                rewardCollected=collected, items=dict(stash=stash, data=[] if collected else items),
                maxStorageTime=72 * 3600)
            dialogue = profile.setdefault('dialogues', {}).setdefault(trader,
                dict(_id=trader, messages=[], attachmentsNew=0, new=0, type=2))
            dialogue['messages'].append(message)
            dialogue['attachmentsNew'] = dialogue.get('attachmentsNew', 0) + (0 if collected else 1)
            return message
        for key, gift in gifts.items():
            message = parcel(gift, gift['trader'])
            seeded.append(dict(giftId=key, trader=gift['trader'], message=message))
        claimed = parcel(gifts['MechanicGiftDay1'], gifts['MechanicGiftDay1']['trader'], True)
        quarter = read(root / 'SPT/user/mods/Codex-ZombieSurvival/config.json')['traderId']
        # Same locale template under another sender must not be mistaken for the native gift.
        unrelated = parcel(gifts['MechanicGiftDay1'], quarter)
        saved.update(seeded=seeded, claimed=claimed, unrelated=unrelated)
        write(path, profile)
        write(checkpoint, saved)
        print('Prepared catalog/coop gifts, one collected gift and one nonmatching sender in disposable QA only.')
        return

    request('/client/game/start')
    request('/client/trading/api/traderSettings')
    # Exercise the native collect-all endpoint; bypassing the merchant UI must not reveal quarantined items.
    for trader in {x['trader'] for x in saved['seeded']}:
        attachment_response = request('/client/mail/dialog/getAllAttachments', dict(dialogId=trader))
        rendered = json.dumps(attachment_response)
        for entry in saved['seeded']:
            for item in entry['message']['items']['data']:
                assert item['_id'] not in rendered
    request('/client/game/logout')
    profile = read(path)
    by_id = {m['_id']: m for _, m in messages(profile)}
    for entry in saved['seeded']:
        m = by_id[entry['message']['_id']]
        assert not m.get('items') and m.get('hasRewards') is False
    assert by_id[saved['claimed']['_id']] == saved['claimed'], 'Collected gifts must remain untouched.'
    assert by_id[saved['unrelated']['_id']]['items'] == saved['unrelated']['items']
    reward = by_id[saved['authoredReward']['_id']]
    assert reward['items'] == saved['authoredReward']['items'] and reward['hasRewards']
    assert profile['characters']['pmc']['Inventory'] == saved['inventory']
    quarantine = profile['spt']['emberBlockedGifts']
    assert len(quarantine) == len(gifts) and {x['messageId'] for x in quarantine} == {x['message']['_id'] for x in saved['seeded']}
    for archived in quarantine:
        original = next(x['message']['items'] for x in saved['seeded'] if x['message']['_id'] == archived['messageId'])
        assert archived['items'] == original
    eligible_messages = [m for tid, m in messages(profile) if (tid, m.get('templateId')) in signatures]
    assert len(eligible_messages) == len(gifts)+1, 'Native gifts were resent on login.'
    assert not any(x.get('giftId') in ids for x in profile['spt'].get('receivedGifts', []))
    report = dict(passed=True, version=installed['version'], freshMechanicGiftBlocked=True,
        dayOneAndDayTwoGiftsBlocked=True, pendingGiftMessagesQuarantined=len(gifts), coopGiftMailQuarantined=True, quarantinePayloadPreserved=True,
        nativeCollectAllCannotClaimBlockedItems=True, collectedGiftUntouched=True,
        exactSenderAndTemplateMatching=True, authoredQuestRewardUnchanged=True, stashInventoryUnchanged=True,
        backendRestartIdempotent=a.stage == 'resume', playerInstanceUsed=False, gameUiAcceptance=False)
    write(a.report, report)
    print(json.dumps(report, indent=2))

if __name__ == '__main__':
    main()
