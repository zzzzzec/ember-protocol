"""Read-only check that Development serves every owned quest illustration unchanged."""
import argparse
import hashlib
import json
import ssl
import urllib.request
from pathlib import Path


def read(path):
    return json.loads(path.read_text('utf-8-sig'))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--game-root', type=Path, required=True)
    parser.add_argument('--port', type=int, required=True)
    parser.add_argument('--report', type=Path, required=True)
    args = parser.parse_args()
    root = args.game_root.resolve()
    installed = read(root / '.sptz/installed.json')
    assert installed['instanceKind'] == 'Development' and installed['port'] == args.port and args.port != 6969
    data = root / 'SPT/user/mods/Codex-ZombieSurvival/data'
    artwork = read(data / 'quest-illustrations.json')
    quests = read(data / 'quests.json')
    results = []
    for asset in artwork['assets']:
        route = asset['route'] + '.png'
        request = urllib.request.Request('https://127.0.0.1:%s%s' % (args.port, route))
        with urllib.request.urlopen(request, context=ssl._create_unverified_context(), timeout=30) as response:
            raw = response.read()
            assert response.status == 200 and response.headers.get_content_type().startswith('image/'), route
        assert raw[:8] == b'\x89PNG\r\n\x1a\n', ('Response is not a PNG', route)
        assert hashlib.sha256(raw).hexdigest() == asset['sha256'], ('HTTP content differs from owned art', route)
        assert raw == (data / asset['file']).read_bytes(), ('Served file differs', route)
        used = [qid for qid, quest in quests.items() if quest['image'] == route]
        assert used, ('Unreferenced quest artwork', route)
        results.append(dict(key=asset['key'],bytes=len(raw),referencingQuests=len(used),httpImageAndHashMatch=True))
    report = dict(passed=True,version=installed['version'],assets=results,playerInstanceUsed=False,
                  profileMutated=False,gameUiOrInRaidAcceptance=False)
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(report, indent=2) + '\n', 'utf-8')
    print('PASS', len(results), 'owned PNG routes; HTTP bytes, hashes and task references match.')


if __name__ == '__main__':
    main()
