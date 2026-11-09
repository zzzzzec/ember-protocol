"""Compile explicit send-on-completion decisions; never rewrite pre-existing quest rules."""
import argparse
import copy
import hashlib
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def identity(key):
    return hashlib.sha256(('ember-receipts-family:' + key).encode()).hexdigest()[:24]


def compile_story_decisions(write=False):
    data = ROOT / 'content/server/data'
    read = lambda path: json.loads(path.read_text('utf-8-sig'))
    source = read(ROOT / 'config/story-decisions.json')
    assert source['schema'] == 1
    quests, manifest, story, locales = [read(data / name) for name in ['quests.json', 'manifest.json', 'story-quests.json', 'locales.json']]
    dialogue = read(ROOT / 'content/narrative/family-replies.zh-CN.json')
    assert dialogue['schema'] == 1 and dialogue['language'] == 'ch'
    template = quests['b94857eaafa2fe9660702151']
    groups, keys, generated = [], {}, {}
    for authored in source['choices']:
        assert authored['commitStage'] == 'complete' and authored['prerequisite'] in quests
        group = {key: copy.deepcopy(authored[key]) for key in ['id', 'commitStage', 'rejectionMessage']}
        group['routes'] = []
        for route in authored['routes']:
            qid, reply = identity(route['key']), identity(route['followupKey'])
            keys[route['key']], keys[route['followupKey']] = qid, reply
            compiled = {key: route[key] for key in ['id', 'recipient', 'scope', 'authorization', 'payloadKey', 'payload']}
            compiled.update(choice=qid, quests=[qid, reply], payloadHash=hashlib.sha256(route['payload'].encode()).hexdigest())
            group['routes'].append(compiled)
            assert route['payload'] in dialogue['quests'][qid]['description'], 'Shown draft must be the actual persisted payload'
            for current, prerequisite, title, trader, xp in [
                (qid, authored['prerequisite'], route['title'], '09ce24e1c055c45828a1922f', 150),
                (reply, qid, route['followupTitle'], '39a130d714ed6d61f6b1c2aa', 250),
            ]:
                entry = dialogue['quests'][current]
                assert entry['speaker'] == trader and entry['name'] == title
                quest = copy.deepcopy(template)
                for field, value in list(quest.items()):
                    if isinstance(value, str) and value.startswith(template['_id'] + ' '):
                        quest[field] = current + value[len(template['_id']):]
                quest.update(_id=current, QuestName=title, traderId=trader, canShowNotificationsInGame=False, instantComplete=False)
                condition = copy.deepcopy(template['conditions']['AvailableForStart'][0])
                condition.update(id=identity(current + ':prerequisite'), target=prerequisite, status=[4])
                quest['conditions'] = dict(AvailableForStart=[condition], AvailableForFinish=[], Fail=[])
                if current == qid:
                    other = next(r for r in authored['routes'] if r['id'] != route['id'])
                    failed = copy.deepcopy(condition)
                    failed.update(id=identity(current + ':opposite-sent'), target=identity(other['key']))
                    quest['conditions']['Fail'] = [failed]
                reward = copy.deepcopy(template['rewards']['Success'][0])
                reward.update(id=identity(current + ':experience'), value=xp)
                quest['rewards'] = dict(Started=[], Success=[reward], Fail=[])
                quest['image'] = '/files/quest/icon/ember_' + ('nina' if current == qid else 'claire') + '_scene_v1.png'
                generated[current] = quest
                for lang in ['ch', 'en']:
                    loc = locales[lang]
                    for field in ['name', 'description', 'startedMessageText', 'successMessageText', 'failMessageText', 'acceptPlayerMessage', 'completePlayerMessage', 'declinePlayerMessage', 'changeQuestMessageText', 'note']:
                        loc[current + ' ' + field] = title if field == 'name' else ''
                    loc[current + ' description'] = entry['description']
                    loc[current + ' startedMessageText'] = entry['accepted']
                    loc[current + ' successMessageText'] = entry['success']
                    loc[current + ' acceptPlayerMessage'] = '先读草稿，尚不发送。' if current == qid else '让我看看回信。'
                    loc[current + ' completePlayerMessage'] = '按这份草稿发送回信，收回另一稿。' if current == qid else '回信我读完了。'
                    loc[current + ' failMessageText'] = '另一份回信已经寄出。这份草稿留档，不再发送。'
                    if current == qid:
                        loc[failed['id']] = '另一份回信已确认发送'
        assert len(group['routes']) == 2
        groups.append(group)
    assert set(dialogue['quests']) == set(generated), 'Every decision dialogue must own exactly one compiled quest'
    existing = {row['id']: row for row in manifest['quests']}
    for index, (qid, quest) in enumerate(generated.items()):
        assert qid not in quests or qid in story.get('decisionQuestKeys', {}).values(), ('Quest collision', qid)
        quests[qid] = quest
        existing[qid] = dict(id=qid, lane='receipts-family', index=index, count=0)
    manifest['quests'] = list(existing.values())
    story['quests'] = list(dict.fromkeys(story['quests'] + list(generated)))
    group_ids = {group['id'] for group in groups}
    story['choices'] = [group for group in story['choices'] if group['id'] not in group_ids] + groups
    story['decisionQuestKeys'] = keys
    story['version'] = source['version']
    changes = []
    for name, content in [('quests.json', quests), ('manifest.json', manifest), ('story-quests.json', story), ('locales.json', locales)]:
        path = data / name
        if read(path) != content:
            changes.append(name)
            if write:
                path.write_text(json.dumps(content, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    return dict(quests=len(generated), changedFiles=changes, written=write)


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--write', action='store_true')
    result = compile_story_decisions(parser.parse_args().write)
    print(json.dumps(result, indent=2))
    if not result['written'] and result['changedFiles']:
        raise SystemExit('Decision content is not compiled; run tools/compile_story_decisions.py --write')
