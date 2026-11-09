"""Local-only capture, validation and packaging. Python 3.8+, standard library only."""
from pathlib import Path
import argparse,fnmatch,hashlib,json,re,shutil,subprocess,sys,time,zipfile
REPO=Path(__file__).resolve().parent.parent
def read(p):return json.loads(Path(p).read_text(encoding='utf-8-sig'))
def write(p,x):
    p=Path(p);p.parent.mkdir(parents=True,exist_ok=True);p.write_text(json.dumps(x,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
def sha(p,throttle=False):
    h=hashlib.sha256()
    with Path(p).open('rb') as f:
        while True:
            chunk=f.read(1024*1024)
            if not chunk:break
            h.update(chunk)
            if throttle:time.sleep(.001)
    return h.hexdigest()
def relpath(value):
    value=str(value).replace('\\','/')
    if not value or value.startswith('/') or ':' in value or any(x in ('','..','.') for x in value.split('/')):raise ValueError('Unsafe relative path: '+value)
    return value
def metadata():return read(REPO/'pack.json')
def capture(args):
    root=Path(args.game_root).resolve();assert (root/'SPT/SPTarkov.Server.Core.dll').is_file()
    cfgroot=REPO/'config/overrides'
    known_inactive=('ciallo.','com.acidphantasm.botplacementsystem','com.codex.spt.roguenomountedweapons','com.danw.','com.dvize.','me.sol.sain','net.skydust.')
    settings=[]
    for p in sorted((root/'BepInEx/config').glob('*.cfg')):
        if not p.name.startswith(known_inactive):settings.append(p)
    for folder in (root/'SPT/user/mods').iterdir():
        if folder.name=='Codex-ZombieSurvival':continue
        for p in folder.rglob('*.json'):
            relative=p.relative_to(folder)
            if len(relative.parts)<=2 and (p.name.lower()=='config.json' or p.name=='EttConfig.json'):settings.append(p)
    svm=root/'SPT/user/mods/[SVM] Server Value Modifier'
    loader=svm/'Loader/loader.json';selected=read(loader)['CurrentlySelectedPreset']
    if not isinstance(selected,str) or not selected or re.search(r'[<>:"/\\|?*\x00-\x1f]',selected) or selected in ('.','..') or selected!=selected.rstrip(' .'):raise ValueError('Unsafe SVM preset name.')
    settings += [loader,svm/'Presets'/(selected+'.json')]
    inspector=REPO/'.local/capture/assemblies.json'
    if not inspector.exists():raise ValueError('Run Inspect-Mods.ps1 before capture.')
    assemblies={x['path']:x for x in read(inspector)}
    for p in settings:
        if not p.is_file():raise ValueError('Missing selected config: '+str(p))
    overrides={str(p.relative_to(root)).replace('\\','/') for p in settings}
    # An explicit capture replaces the setting snapshot; removed mods/presets must not linger.
    assert cfgroot.resolve()==REPO.resolve()/'config/overrides'
    cfgroot.mkdir(parents=True,exist_ok=True)
    for previous in cfgroot.rglob('*'):
        if previous.is_file() and str(previous.relative_to(cfgroot)).replace('\\','/') not in overrides:previous.unlink()
    for p in settings:
        relative=p.relative_to(root);dest=cfgroot/relative;dest.parent.mkdir(parents=True,exist_ok=True);shutil.copy2(p,dest)
    components=[];files=[]
    firstparty={'Codex-ZombieSurvival','Codex-SkipPostRaidHealing','Codex-SkipPresetExitConfirmation','Codex-StashQuickSell'}
    for base in ['SPT/user/mods','BepInEx/plugins']:
        for part in sorted((root/base).iterdir()):
            if part.name in firstparty or (base=='BepInEx/plugins' and part.name=='spt'):continue
            component=str(part.relative_to(root)).replace('\\','/')
            candidates=[part] if part.is_file() else sorted(p for p in part.rglob('*') if p.is_file())
            entries=[]
            for p in candidates:
                relative=str(p.relative_to(root)).replace('\\','/')
                if relative in overrides:continue
                local=str(p.relative_to(part)).replace('\\','/') if part.is_dir() else p.name
                if any(x in p.parts for x in ['__pycache__','.git','obj','bin']):continue
                if p.suffix.lower() in ['.log','.jsonl','.pid','.tmp']:continue
                if part.name=='[SVM] Server Value Modifier' and local.startswith('Presets/'):continue
                entry=dict(path=relative,sha256=sha(p,p.stat().st_size>8*1048576),bytes=p.stat().st_size,component=component)
                entries.append(entry);files.append(entry)
            dllmeta=[assemblies[e['path']] for e in entries if e['path'] in assemblies]
            components.append(dict(id=component,files=len(entries),bytes=sum(e['bytes'] for e in entries),assemblies=dllmeta,acquisition='installed-source-or-private-archive'))
            print('Captured',component,len(entries),'files',round(sum(e['bytes'] for e in entries)/1048576,1),'MiB',flush=True)
    greed=root/'Greed.exe'
    if greed.exists():
        component='Greed.exe';files.append(dict(path=component,sha256=sha(greed),bytes=greed.stat().st_size,component=component));components.append(dict(id=component,files=1,bytes=greed.stat().st_size,acquisition='installed-source-or-private-archive'))
    basenames=['EscapeFromTarkov.exe','SPT/SPT.Server.exe','SPT/SPTarkov.Server.Core.dll','SPT/SPTarkov.Common.dll','SPT/SemanticVersioning.dll','SPT/0Harmony.dll','BepInEx/core/BepInEx.dll','BepInEx/core/0Harmony.dll','EscapeFromTarkov_Data/Managed/Assembly-CSharp.dll']
    basenames += [str(p.relative_to(root)).replace('\\','/') for p in sorted((root/'BepInEx/plugins/spt').rglob('*')) if p.is_file() and p.suffix=='.dll']
    baseline=[dict(path=p,sha256=sha(root/p),bytes=(root/p).stat().st_size) for p in basenames]
    lock=dict(formatVersion=1,sptVersion=metadata()['sptVersion'],gameVersion=metadata()['gameVersion'],components=components,files=files,baseFiles=baseline)
    write(REPO/'mods.lock.json',lock);write(REPO/'.local/source.json',dict(gameRoot=str(root)))
    print('Locked',len(components),'dependency components,',len(files),'files; config overrides',len(overrides),flush=True)
def validate():
    from compile_dialogue import compile_dialogue
    from compile_procurement import compile_procurement
    from compile_quest_art import compile_quest_art
    from compile_story_decisions import compile_story_decisions
    from compile_supply_routes import compile_supply_routes
    from compile_weapon_trials import compile_weapon_trials
    assert not compile_weapon_trials()['changedFiles'], 'Compile weapon trials before packaging.'
    assert not compile_supply_routes()['changedFiles'], 'Compile supply routes before packaging.'
    assert not compile_story_decisions()['changedFiles'], 'Compile decision content before packaging.'
    assert not compile_dialogue()['changedFields'], 'Compile reviewed dialogue before packaging.'
    assert not compile_procurement()['changedFiles'], 'Compile procurement before packaging.'
    assert not compile_quest_art()['changedFiles'], 'Compile quest artwork before packaging.'
    config=read(REPO/'config/gameplay.json');data=REPO/'content/server/data';qs=read(data/'quests.json');m=read(data/'manifest.json');recipes=read(data/'recipes.json');trader=read(data/'trader.json');locales=read(data/'locales.json')
    caps=read(data/'camp-growth.json')['nativeRecipeBatchCaps'];assert all(len(k)==24 and isinstance(v,int) and 1<=v<=20 and k not in {r['_id'] for r in recipes} for k,v in caps.items())
    counts=metadata()['counts'];assert (len(qs),len(m['offers']),len(m['barters']),len(recipes))==tuple(counts[k] for k in ['quests','offers','barters','crafts'])
    blackout=read(data/'lab-blackout.json')
    assert blackout['schema']==1
    event_quests=[blackout[k] for k in ('scout','parts','repair')]
    assert len(set(event_quests))==3 and set(event_quests)<=set(qs)
    for qid, prerequisite in zip(event_quests,[blackout['trigger'],*event_quests]):
        assert any(c['conditionType']=='Quest' and c['target']==prerequisite and c['status']==[4]
                   for c in qs[qid]['conditions']['AvailableForStart'])
    repair_conditions={c['id']:c for c in qs[blackout['repair']]['conditions']['AvailableForFinish']}
    assert repair_conditions[blackout['plantCondition']]['zoneId']==blackout['plantZone']
    assert repair_conditions[blackout['plantCondition']]['conditionType']=='PlaceBeacon'
    quest_loot=read(data/'quest-loot.json')
    assert quest_loot['schema']==1 and len(set(quest_loot['containers']))==len(quest_loot['containers'])
    assert all(re.fullmatch('[0-9a-f]{24}',tpl) for tpl in quest_loot['containers'])
    targets={tpl for quest in qs.values() for c in quest['conditions']['AvailableForFinish'] if c['conditionType'] in ['FindItem','HandoverItem'] for tpl in c.get('target',[])}
    assert len({row['tpl'] for row in quest_loot['items']})==len(quest_loot['items'])
    assert all(row['tpl'] in targets and 1<=row['minimumRank']<=6 and 0<row['weightFraction']<=.01 for row in quest_loot['items'])
    assert sum(row['weightFraction'] for row in quest_loot['items'])<=.01
    assert len(set(qs))==counts['quests'] and {q['id'] for q in m['quests']}==set(qs)
    assert len({r['_id'] for r in recipes})==counts['crafts'] and len({o['id'] for o in m['offers']})==counts['offers']
    assert config['traderId']==m['traderId'] and config['disableLegacyRitual'] is True
    assert config['disableVanillaTraderGifts'] is True and config['disableVanillaCoopRewards'] is True
    assert config['scarcity']['fullAmmoPacks'] is True
    for lang in ['ch','en']:
        for qid in qs:
            for field in ['name','description','successMessageText']:assert qid+' '+field in locales[lang]
    active=set();visited=set()
    def visit(qid):
        if qid in visited:return
        assert qid not in active,('Quest dependency cycle',qid);active.add(qid)
        for c in qs[qid]['conditions']['AvailableForStart']:
            if c['conditionType']=='Quest':assert c['target'] in qs;visit(c['target'])
        active.remove(qid);visited.add(qid)
    for qid in qs:visit(qid)
    opening=read(data/'opening.json')
    assert opening['schemaVersion']==2 and len(opening['initialQuests'])==2
    contacts=opening['contacts'];contact_ids={c['id'] for c in contacts}
    assert len(contact_ids)==len(contacts) and opening['ninaId'] in contact_ids and opening['claireId'] in contact_ids
    moved=[oid for c in contacts for oid in c['offers']]
    assert len(moved)==len(set(moved)) and set(moved)<={o['id'] for o in m['offers']}
    assert all(c['unlockQuest'] in qs and (data/c['avatarFile']).is_file() and len(c['standing'])==4 for c in contacts)
    for c in contacts:
        credit=sum(r.get('value',0) for q in qs.values() for r in q['rewards']['Success'] if r['type']=='TraderStanding' and r.get('target')==c['id'])
        assert credit+1e-8>=c['standing'][-1],('Specialist loyalty cannot reach its final tier',c['id'])
    for qid in opening['claireQuests']:
        assert qs[qid]['traderId']==opening['claireId'] and not qs[qid]['rewards']['Started']
        for lang in ['ch','en']:assert qs[qid]['startedMessageText'] in locales[lang]
    client=(REPO/'src/client/Client.cs').read_text('utf-8-sig')
    for contact in contacts:
        assert contact['id'] in client,('Contact is not recognized by the client UI',contact['id'])
        for lang in ['ch','en']:
            for field in ['Nickname','FullName','Location','Description']:
                assert locales[lang].get(contact['id']+' '+field),('Missing trader identity text',contact['id'],lang,field)
    for name in ['emma','katya','haruka']:
        for qid in opening[name+'Quests']:
            assert qs[qid]['traderId']==opening[name+'Id'] and qid in read(data/'story-quests.json')['quests']
            assert not qs[qid]['rewards']['Started'],('Private commission must not reissue loan equipment',qid)
    procurement=read(REPO/'config/procurement.json')
    assert all(x['tpl'] not in config['economy']['handbookPrices'] for x in procurement['cash']), 'Ordinary procurement must not reprice native resale values.'
    roots={qid for qid,q in qs.items() if not any(c['conditionType']=='Quest' for c in q['conditions']['AvailableForStart'])}
    assert roots==set(opening['initialQuests']),('Unexpected initial task exposure',roots)
    for qid,parent in opening['questRelease'].items():
        assert any(c['conditionType']=='Quest' and c['target']==parent and c['status']==[4] for c in qs[qid]['conditions']['AvailableForStart'])
    owners={oid:c['id'] for c in contacts for oid in c['offers']}
    for q in qs.values():
        assert q['traderId'] in {m['traderId'],config['fence']['traderId']}|contact_ids
        for r in q['rewards']['Success']:
            if r['type']=='AssortmentUnlock':assert r['traderId']==owners.get(r['target'],m['traderId'])
    for quest in qs.values():
        for phase,rewards in quest['rewards'].items():
            for reward in rewards:
                if reward['type'] in ['Item','AssortmentUnlock']:
                    assert reward.get('target') in {i['_id'] for i in reward.get('items',[])},('Reward root is missing',quest['_id'],phase,reward['id'])
        condition_ids={c['id'] for rows in quest['conditions'].values() for c in rows}
        for condition in quest['conditions']['AvailableForFinish']:
            for rule in condition.get('visibilityConditions',[]):
                if rule['conditionType']=='CompleteCondition':
                    assert rule['target'] in condition_ids,('Unknown quest visibility target',quest['_id'],condition['id'],rule['target'])
            for counter in condition.get('counter',{}).get('conditions',[]):
                if counter['conditionType']=='Kills':
                    for field in ['weaponModsInclusive','weaponModsExclusive']:
                        assert isinstance(counter.get(field,[]),list) and all(isinstance(group,list) for group in counter.get(field,[])),(quest['_id'],field)

    roots={i['_id']:i for i in trader['assort']['items'] if i.get('parentId')=='hideout'}
    assert set(roots)==set(trader['assort']['barter_scheme'])==set(trader['assort']['loyal_level_items'])
    for offer in m['offers']:
        assert offer['tpl']==roots[offer['id']]['_tpl'],('Manifest offer template mismatch',offer['id'])
    for offer in m['barters']:
        assert next(x for x in m['offers'] if x['id']==offer['id'])==offer,('Duplicated barter metadata mismatch',offer['id'])
    for phase,links in trader['questassort'].items():
        for offer,quest in links.items():assert offer in roots and isinstance(quest,str) and quest in qs,(phase,offer,quest)
    story=read(data/'story-quests.json')
    assert set(story['quests'])<=set(qs) and len(set(story['quests']))==len(story['quests'])
    ak50=read(data/'ak50.json')
    bench,deposit,handoff=(ak50['quests'][stage] for stage in ('workbench','deposit','handoff'))
    assert ak50['schema']==1 and len({bench,deposit,handoff})==3 and {bench,deposit,handoff}<=set(story['quests'])
    assert [qs[q]['traderId'] for q in (bench,deposit,handoff)]==[opening['katyaId'],opening['emmaId'],opening['katyaId']]
    for q,previous in ((deposit,bench),(handoff,deposit)):
        assert any(c['conditionType']=='Quest' and c['target']==previous and c['status']==[4]
                   for c in qs[q]['conditions']['AvailableForStart'])
    assert ak50['sourceMap'] in config['maps'] and ak50['sourceSpot']!=ak50['relocatedSpot']
    assert ak50['recipe']!=ak50['nativeRecipe']
    receiver,=[r for r in recipes if r['endProduct']==ak50['weapon']]
    assert receiver['_id']==ak50['recipe'] and receiver['locked']
    assert any(req['type']=='QuestComplete' and req['questId']==bench for req in receiver['requirements'])
    handoff_conditions=qs[handoff]['conditions']['AvailableForFinish']
    recovered={c['target'][0] for c in handoff_conditions if c['conditionType']=='FindItem'}
    stashed={c['target'][0] for c in handoff_conditions if c['conditionType']=='LeaveItemAtLocation' and c['zoneId']=='quest_terminal_bitcoin'}
    assert ak50['handguard'] in recovered and len(recovered)==3 and stashed==recovered|{ak50['weapon']}
    for tpl in ak50['ammo']:
        offer,=[o for o in m['offers'] if o['tpl']==tpl]
        assert offer['unlock']==handoff and owners[offer['id']]==opening['katyaId'] and offer['limit']<=4
    abundance=read(data/'map-abundance.json')
    normalize_map=lambda key: 'factory' if key.startswith('factory4_') else 'sandbox' if key=='sandbox_high' else key
    abundance_maps={normalize_map(key) for key in config['maps']}
    assert abundance['schema']==1 and set(abundance['maps'])==abundance_maps
    campaign=read(data/'campaign.json')
    campaign_main={row['id'] for row in campaign['main']}
    assert [row['index'] for row in campaign['main']]==list(range(1,len(campaign_main)+1))
    training=campaign['skillTraining']
    assert len({track['id'] for track in training})==len(training)
    trained=[skill for track in training for skill in track['skills']]
    assert len(set(trained))==len(trained)==27
    for edition in read(data/'profiles.json').values():
        for side in ('usec','bear'):
            assert set(trained)<={row['Id'] for row in edition[side]['character']['Skills']['Common']}
    assert {track['eliteQuest'] for track in training}=={32,33,34,35}
    assert max(track['eliteQuest'] for track in training)<len(campaign_main)
    awards=[]
    for rows in abundance['maps'].values():
        assert set(rows['main'])<=campaign_main and not set(rows['side'])&campaign_main
        assert 6*len(rows['main'])+3*len(rows['side'])>=36
        awards.extend(rows['main']+rows['side'])
    for maps in abundance['shared'].values():
        assert len(maps)==len(set(maps))==2 and set(maps)<=abundance_maps
    awards.extend(abundance['shared'])
    assert len(set(awards))==len(awards) and set(awards)<=set(qs)
    for group in story['choices']:
        assert len(group['routes'])==2
        assert len({q for route in group['routes'] for q in route['quests']})==sum(len(route['quests']) for route in group['routes'])
        for route in group['routes']:assert route['choice']==route['quests'][0] and set(route['quests'])<=set(story['quests'])
    for r in recipes:
        assert r['count']>0 and r['productionTime']>0
        for req in r['requirements']:
            if req['type']=='QuestComplete':assert req['questId'] in qs
    assert 0<=config['naturalAirdropChanceMultiplier']<=1
    for quest_id,quest in qs.items():
        for reward in quest['rewards']['Success']:
            if reward['type']!='ProductionScheme':continue
            matches=[r for r in recipes if r['locked'] and r['endProduct']==reward['items'][0]['_tpl']
                     and r['areaType']==int(reward['traderId'])
                     and any(a['type']=='Area' and a['requiredLevel']==reward['loyaltyLevel'] for a in r['requirements'])
                     and any(a.get('questId')==quest_id for a in r['requirements'])]
            assert len(matches)==1,(quest_id,reward['id'],len(matches))
    threat=read(REPO/'config/threat-generator.json')
    assert threat['enabled'] and set(threat['maps'])==set(config['maps'])
    assert len({a['key'] for a in threat['archetypes']})==len(threat['archetypes'])
    waves=config['waves'];assert waves['enabled'] is True and waves['replenishAt']==2 and waves['breatherSeconds']>=0 and waves['spawnSpacingSeconds']>=.1
    assert waves['mode']=='steady' and waves['pendingLimit']==2 and waves['spawnSpacingSeconds']>=4 and 0<=waves['spacingJitter']<=.5
    assert set(waves['aliveCaps'])==set(config['maps']) and all(1<=cap<=64 for cap in waves['aliveCaps'].values())
    for mapid,spec in config['maps'].items():
        assert spec['total']==len(spec['spawns']) and 0<=spec['shooters']<=spec['total']
        assert all(s['BossName'].startswith('infected') and s['Time']==-1 for s in spec['spawns'])
    for p in [REPO/'config',REPO/'content',REPO/'src']:
        for f in p.rglob('*'):
            if not f.is_file() or f.suffix not in ['.json','.cs','.csproj','.cfg','.props']:continue
            if any(part in ('bin','obj','__pycache__') for part in f.relative_to(p).parts):continue
            text=f.read_text(encoding='utf-8-sig')
            assert not re.search(r'(?i)[a-z]:[\\/]+SPT|PHPSESSID=[a-z0-9]{16,}|Bearer\s+[A-Za-z0-9_\-.]{20,}',text),f
    print(f"PASS content: {len(qs)} quests, {len(m['offers'])} shared offers, {len(m['barters'])} barters, {len(recipes)} crafts; 2 initial tasks, {len(contacts)} unlockable contacts, stable IDs and fixed map pools.")
def build(args):
    validate();root=Path(args.game_root).resolve();meta=metadata();lock=read(REPO/'mods.lock.json')
    for f in lock['baseFiles']:
        if sha(root/f['path'])!=f['sha256']:raise ValueError('Game reference mismatch: '+f['path'])
    out=REPO/'artifacts/build';out.mkdir(parents=True,exist_ok=True)
    for item in meta['projects']:
        target=out/Path(item['project']).parent.name;target.mkdir(parents=True,exist_ok=True)
        subprocess.run([args.dotnet,'build',str(REPO/item['project']),'-c','Release','-o',str(target),'-p:GameRoot='+str(root),'-p:ServerDir='+str(root/'SPT')],cwd=str(REPO),check=True)
    package=REPO/'dist'/('ember-protocol-'+meta['version'])
    if package.exists():
        assert package.parent==REPO/'dist';shutil.rmtree(package)
    payload=package/'payload';payload.mkdir(parents=True)
    def put(src,dest):
        p=payload/relpath(dest);p.parent.mkdir(parents=True,exist_ok=True)
        if Path(src).suffix.lower() in ['.json','.cfg','.py','.cmd']:
            p.write_bytes(Path(src).read_text(encoding='utf-8-sig').encode('utf-8'))
        elif Path(src).suffix.lower()=='.ps1':
            p.write_bytes(Path(src).read_text(encoding='utf-8-sig').replace('\n','\r\n').encode('utf-8-sig'))
        else:shutil.copy2(src,p)
    for item in meta['projects']:put(out/Path(item['project']).parent.name/item['dll'],item['target'])
    put(REPO/'src/client/Blackout.LICENSE','BepInEx/plugins/Codex-ZombieSurvival/Blackout.LICENSE')
    put(REPO/'src/stash-quick-sell/LICENSES/QuickSell-MIT.txt','BepInEx/plugins/Codex-StashQuickSell/LICENSES/QuickSell-MIT.txt')
    for p in (REPO/'content/server/data').iterdir():
        if p.is_file():put(p,'SPT/user/mods/Codex-ZombieSurvival/data/'+p.name)
    put(REPO/'config/gameplay.json','SPT/user/mods/Codex-ZombieSurvival/config.json')
    put(REPO/'config/threat-generator.json','SPT/user/mods/Codex-ZombieSurvival/threat-generator.json')
    put(REPO/'config/threat-generator.json','BepInEx/plugins/Codex-ZombieSurvival/threat-settings.json')
    write(payload/'BepInEx/plugins/Codex-ZombieSurvival/wave-settings.json',read(REPO/'config/gameplay.json')['waves'])
    write(payload/'BepInEx/plugins/Codex-ZombieSurvival/loading-settings.json',read(REPO/'config/gameplay.json')['loading'])
    for p in (REPO/'config/overrides').rglob('*'):
        if p.is_file():put(p,str(p.relative_to(REPO/'config/overrides')))
    for source,dest in [('Admin.ps1','Tools/EmberProtocol/Admin.ps1'),('Launch.ps1','Tools/EmberProtocol/Launch.ps1'),('collect-diagnostics.py','Tools/ZombieSurvival/collect-diagnostics.py'),('Collect-Diagnostics.cmd','Tools/ZombieSurvival/Collect-Diagnostics.cmd')]:put(REPO/'tools'/source,dest)
    entries=[dict(path=str(p.relative_to(payload)).replace('\\','/'),sha256=sha(p),bytes=p.stat().st_size) for p in sorted(payload.rglob('*')) if p.is_file()]
    assert not set(e['path'].lower() for e in entries)&set(e['path'].lower() for e in lock['files'])
    manifest=dict(formatVersion=1,name=meta['name'],version=meta['version'],sptVersion=meta['sptVersion'],gameVersion=meta['gameVersion'],files=entries,baseFiles=lock['baseFiles'],knownConflicts=meta['knownConflicts'],dependencyLockSha256=sha(REPO/'mods.lock.json'))
    write(package/'package.json',manifest);shutil.copy2(REPO/'mods.lock.json',package/'mods.lock.json')
    for name in ['Install.ps1','Rollback.ps1','Verify.ps1','Export-Dependencies.ps1','Common.ps1']:
        (package/name).write_text((REPO/'tools'/name).read_text(encoding='utf-8-sig'),encoding='utf-8-sig')
    shutil.copy2(REPO/'docs/INSTALL.zh-CN.md',package/'安装说明.md')
    zpath=package.parent/(package.name+'.zip')
    with zipfile.ZipFile(zpath,'w',zipfile.ZIP_DEFLATED) as z:
        for p in sorted(package.rglob('*')):
            if p.is_file():
                info=zipfile.ZipInfo(str(p.relative_to(package)).replace('\\','/'),date_time=(2020,1,1,0,0,0));info.compress_type=zipfile.ZIP_DEFLATED;info.external_attr=0o100644<<16
                z.writestr(info,p.read_bytes())
    write(REPO/'artifacts/build-report.json',dict(version=meta['version'],package=str(package.relative_to(REPO)),zip=str(zpath.relative_to(REPO)),payloadFiles=len(entries),dependencyFiles=len(lock['files']),zipSha256=sha(zpath)))
    print('Built',zpath,'payload files',len(entries))
def main():
    p=argparse.ArgumentParser();sub=p.add_subparsers(dest='command',required=True)
    c=sub.add_parser('capture');c.add_argument('--game-root',required=True)
    sub.add_parser('validate')
    b=sub.add_parser('build');b.add_argument('--game-root',required=True);b.add_argument('--dotnet',default='dotnet')
    a=p.parse_args()
    if a.command=='capture':capture(a)
    elif a.command=='build':build(a)
    else:validate()
if __name__=='__main__':main()
