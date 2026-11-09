"""Build a local support archive. Nothing is uploaded; never include account credentials."""
from pathlib import Path
import datetime as dt, json, hashlib, re, zipfile, argparse

ROOT=Path(__file__).resolve().parents[2]
TOOLS=ROOT/'Tools/ZombieSurvival'
DIAG=TOOLS/'diagnostics'

def read(p):return json.loads(p.read_text(encoding='utf-8-sig'))
def key(s):return hashlib.sha256(s.encode()).hexdigest()[:16]

def main():
    global ROOT,TOOLS,DIAG
    parser=argparse.ArgumentParser();parser.add_argument('--days',type=int,default=3);parser.add_argument('--game-root',type=Path);args=parser.parse_args()
    if args.game_root:ROOT=args.game_root.resolve();TOOLS=ROOT/'Tools/ZombieSurvival';DIAG=TOOLS/'diagnostics'
    if not (ROOT/'SPT/SPT.Server.exe').exists():raise SystemExit('Provide --game-root pointing to an installed SPT instance.')
    sessions=[];profiles=[]
    for mode,backend in [('play',ROOT/'SPT'),('dev',TOOLS/'dev/SPT')]:
        for p in (backend/'user/profiles').glob('*.json'):
            sessions.append(p.stem)
            try:
                raw=read(p);pmc=raw.get('characters',{}).get('pmc',{})
                profiles.append(dict(mode=mode,player=key(p.stem),level=pmc.get('Info',{}).get('Level'),experience=pmc.get('Info',{}).get('Experience'),health=pmc.get('Health'),inventory=pmc.get('Inventory'),quests=pmc.get('Quests'),counters=pmc.get('TaskConditionCounters'),skills=pmc.get('Skills'),bonuses=pmc.get('Bonuses'),quartermaster=pmc.get('TradersInfo',{}).get('4b7e8889344316d03e8369b7'),unlocked=pmc.get('UnlockedInfo'),storyChoices=raw.get('spt',{}).get('emberStoryChoices',{})))
            except Exception as e:profiles.append(dict(mode=mode,player=key(p.stem),readError=type(e).__name__))
    def scrub(text):
        for s in sessions:text=text.replace(s,'player-'+key(s))
        return re.sub(r'(?i)(?:-token=|PHPSESSID=|Authorization:\s*(?:Bearer\s+)?)[^\s,;]+','[redacted]',text)
    def sha(p):return hashlib.sha256(p.read_bytes()).hexdigest()
    cutoff=dt.datetime.now().timestamp()-max(1,min(30,args.days))*86400
    selected=[]
    for day in DIAG.glob('????-??-??'):
        if day.is_dir() and not day.is_symlink():selected.extend(p for p in day.glob('*.jsonl') if p.stat().st_mtime>=cutoff)
    for base in [ROOT/'SPT/user/logs',TOOLS/'dev/SPT/user/logs']:
        selected.extend(sorted(base.rglob('*.log'),key=lambda p:p.stat().st_mtime,reverse=True)[:3])
    for folder in sorted((ROOT/'Logs').glob('log_*'),key=lambda p:p.stat().st_mtime,reverse=True)[:3]:selected.extend(folder.glob('*.log'))
    selected.extend(p for p in [ROOT/'BepInEx/LogOutput.log',ROOT/'BepInEx/plugins/Codex-ZombieSurvival/raid-diagnostics.txt'] if p.exists())
    player_log=Path.home()/'AppData/LocalLow/Battlestate Games/EscapeFromTarkov/Player.log'
    if player_log.exists():selected.append(player_log)
    small=[TOOLS/'last-update.json',ROOT/'SPT/user/mods/Codex-ZombieSurvival/config.json',ROOT/'SPT/user/mods/Codex-ZombieSurvival/data/campaign.json']
    mod=ROOT/'SPT/user/mods/Codex-ZombieSurvival'
    small.extend(mod/name for name in ['startup-errors.jsonl','startup-report.json','economy-report.json','data/story-quests.json','data/camp-growth.json'])
    selected.extend(p for p in small if p.exists())
    version={}
    for p in [ROOT/'SPT/user/mods/Codex-ZombieSurvival/Codex.ZombieSurvival.dll',ROOT/'BepInEx/plugins/Codex-ZombieSurvival/Codex.ZombieSurvival.Client.dll']:
        if p.exists():version[str(p.relative_to(ROOT))]=sha(p)
    svm={str(p.relative_to(ROOT)):sha(p) for p in (ROOT/'SPT/user/mods/[SVM] Server Value Modifier').rglob('*') if p.is_file()}
    out=DIAG/'support';out.mkdir(parents=True,exist_ok=True)
    archive=out/('sptz-diagnostics-'+dt.datetime.now().strftime('%Y%m%d-%H%M%S')+'.zip')
    with zipfile.ZipFile(archive,'w',zipfile.ZIP_DEFLATED) as z:
        z.writestr('summary.json',scrub(json.dumps(dict(createdUtc=dt.datetime.now(dt.timezone.utc).isoformat(),days=args.days,versions=version,svmHashes=svm,profiles=profiles),ensure_ascii=False,indent=2)))
        for p in sorted(set(selected)):
            # Bound third-party/native logs; retain their newest 20 MiB.
            with p.open('rb') as f:
                size=p.stat().st_size;f.seek(max(0,size-20*1024*1024));raw=f.read()
            if size>len(raw):raw=raw.partition(b'\n')[2]
            name='client/Player.log' if p==player_log else str(p.relative_to(ROOT)).replace('\\','/')
            z.writestr(name,scrub(raw.decode('utf-8-sig',errors='replace')))
    print('Local diagnostics archive:',archive)
    print('Files:',len(set(selected)),'; profiles summarized:',len(profiles),'; no upload performed.')

if __name__=='__main__':main()
