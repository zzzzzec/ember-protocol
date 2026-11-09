"""Validate owned quest artwork and compile explicit image assignments."""
import argparse
import hashlib
import json
import re
import struct
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def compile_quest_art(write=False):
    data = ROOT / 'content/server/data'
    source = json.loads((ROOT / 'content/narrative/quest-illustrations.json').read_text('utf-8-sig'))
    assert source['schema'] == 1
    quests = json.loads((data / 'quests.json').read_text('utf-8-sig'))
    assets, routes, files = {}, set(), set()
    for asset in source['assets']:
        key, route, filename = asset['key'], asset['route'], asset['file']
        assert re.fullmatch(r'[a-z]+', key) and key not in assets, ('Duplicate or invalid artwork key', key)
        assert re.fullmatch(r'/files/quest/icon/ember_[a-z]+_scene_v[1-9][0-9]*', route) and route not in routes
        assert re.fullmatch(r'quest-[a-z]+-scene-v[1-9][0-9]*\.png', filename) and filename not in files
        raw = (data / filename).read_bytes()
        assert raw[:8] == b'\x89PNG\r\n\x1a\n' and raw[12:16] == b'IHDR', ('Not a PNG', filename)
        width, height = struct.unpack('>II', raw[16:24])
        assert [width, height] == [asset['width'], asset['height']] and 1.6 <= width / height <= 1.9
        assert 400 <= width <= 3840 and 225 <= height <= 2160, ('Unexpected image dimensions', filename)
        assert hashlib.sha256(raw).hexdigest() == asset['sha256'], ('Artwork changed without review', filename)
        assets[key] = asset
        routes.add(route)
        files.add(filename)
    assert assets and source['quests']
    changes = []
    for qid, key in source['quests'].items():
        assert qid in quests and key in assets, ('Unknown task or artwork', qid, key)
        image = assets[key]['route'] + '.png'
        if quests[qid]['image'] != image:
            quests[qid]['image'] = image
            if 'quests.json' not in changes:
                changes.append('quests.json')
    runtime = {'schema': 1, 'assets': source['assets']}
    runtime_path = data / 'quest-illustrations.json'
    if not runtime_path.exists() or json.loads(runtime_path.read_text('utf-8-sig')) != runtime:
        changes.append('quest-illustrations.json')
    if write:
        for filename, content in [('quests.json', quests), ('quest-illustrations.json', runtime)]:
            if filename in changes:
                (data / filename).write_text(json.dumps(content, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    return {'assets': len(assets), 'assignedQuests': len(source['quests']), 'changedFiles': changes, 'written': write}


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--write', action='store_true')
    result = compile_quest_art(parser.parse_args().write)
    print(json.dumps(result, indent=2))
    if not result['written'] and result['changedFiles']:
        raise SystemExit('Quest artwork is not compiled; run tools/compile_quest_art.py --write')
