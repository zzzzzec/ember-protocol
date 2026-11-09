"""Compile reviewed Chinese dialogue without touching quest rules or player data."""
import argparse
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def compile_dialogue(write=False):
    entries = {}
    for path in sorted((ROOT / 'content/narrative').glob('*.zh-CN.json')):
        source = json.loads(path.read_text('utf-8-sig'))
        assert source['schema'] == 1 and source['language'] == 'ch', path
        assert not set(entries) & set(source['quests']), ('Duplicate dialogue', path)
        entries.update(source['quests'])
    assert entries, 'No reviewed dialogue source found'
    data = ROOT / 'content/server/data'
    quests = json.loads((data / 'quests.json').read_text('utf-8-sig'))
    locales_path = data / 'locales.json'
    locales = json.loads(locales_path.read_text('utf-8-sig'))
    changes = []
    fields = {'description': 'description', 'accepted': 'startedMessageText', 'success': 'successMessageText'}
    for qid, entry in entries.items():
        assert set(entry) == {'name', 'speaker', *fields}, (qid, 'unknown or missing dialogue field')
        quest = quests[qid]
        assert entry['speaker'] == quest['traderId'], (qid, 'dialogue speaker differs from actual trader')
        assert entry['name'] == locales['ch'][quest['name']], (qid, 'title differs from quest catalogue')
        for field, suffix in fields.items():
            value = entry[field]
            assert isinstance(value, str) and value.strip() == value and len(value) >= 20, (qid, field)
            assert '\ufffd' not in value and '\x00' not in value, (qid, 'damaged text')
            key = quest[suffix]
            if locales['ch'].get(key) != value:
                changes.append(key)
                locales['ch'][key] = value
    if write:
        locales_path.write_text(json.dumps(locales, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    return {'quests': len(entries), 'changedFields': changes, 'written': write}


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--write', action='store_true')
    args = parser.parse_args()
    result = compile_dialogue(args.write)
    print(json.dumps(result, ensure_ascii=False, indent=2))
    if not args.write and result['changedFields']:
        raise SystemExit('Reviewed dialogue has not been compiled; run tools/compile_dialogue.py --write')
