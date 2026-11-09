"""Development-only, restart-separated native local-admin acceptance. Never targets Player."""
from pathlib import Path
import argparse,json,ssl,urllib.request,zlib,uuid,subprocess,os,shutil,hashlib,socket
REPO=Path(__file__).resolve().parent.parent
PS=Path(os.environ.get('SystemRoot',r'C:\Windows'))/'System32/WindowsPowerShell/v1.0/powershell.exe'
def read(p):return json.loads(p.read_text(encoding='utf-8-sig'))
def main():
 p=argparse.ArgumentParser();p.add_argument('--game-root',type=Path,required=True);p.add_argument('--port',type=int,required=True);p.add_argument('--phase',choices=['prepare','verify','verify-replay','prepare-invalid','mark-invalid','verify-invalid','restore-invalid'],required=True);p.add_argument('--report',type=Path,required=True);a=p.parse_args();root=a.game_root.resolve();installed=read(root/'.sptz/installed.json');assert installed['instanceKind']=='Development' and installed['port']==a.port and a.port!=6969
 statefile=root/'.sptz/admin-qa.json';session=''
 def request(route,body=None):
  req=urllib.request.Request('https://127.0.0.1:%s%s'%(a.port,route),data=json.dumps(body or {}).encode(),headers={'Content-Type':'application/json','Cookie':'PHPSESSID='+session,'responsecompressed':'0','requestcompressed':'0'})
  with urllib.request.urlopen(req,context=ssl._create_unverified_context(),timeout=120) as response:raw=response.read()
  for mode in [None,15,-15,31]:
   try:
    text=(raw if mode is None else zlib.decompress(raw,mode)).decode('utf-8-sig').strip()
    if len(text)==24 and all(c in '0123456789abcdef' for c in text.lower()):return text
    data=json.loads(text)
   except (ValueError,zlib.error,UnicodeDecodeError):continue
   if isinstance(data,dict) and 'err' in data:assert not data['err'],route;return data.get('data')
   return data
  raise AssertionError('Decode '+route)
 def cli(*args):
  r=subprocess.run([str(PS),'-NoProfile','-ExecutionPolicy','Bypass','-File',str(REPO/'tools/Admin.ps1'),'-GameRoot',str(root),*args],capture_output=True,encoding='utf-8',errors='replace',timeout=30);assert r.returncode==0,r.stdout+r.stderr;return json.loads(r.stdout)
 if a.phase in ['prepare-invalid','mark-invalid','verify-invalid','restore-invalid']:
  invalid_state=root/'.sptz/admin-invalid-qa.json';backup=root/'.sptz/admin-invalid-qa-before.json'
  if a.phase=='prepare-invalid':
   name='Ember-Admin-QA-Bad-'+uuid.uuid4().hex[:8];request('/launcher/profile/register',dict(username=name,edition='Standard'));files=[f for f in (root/'SPT/user/profiles').glob('*.json') if read(f).get('info',{}).get('username')==name];assert len(files)==1;session=files[0].stem
   request('/client/game/profile/create',dict(side='Usec',nickname='EmberAdminQA',headId='5cde96047d6c8b20b577f016',voiceId='5fc1223595572123ae7384a3'));request('/client/game/start');request('/client/trading/api/traderSettings');request('/client/game/logout')
   result=cli('-Action','Grant','-Profile',name,'-Item','573719762459775a626ccbc1','-Count','1');invalid_state.write_text(json.dumps(dict(username=name,session=session,commandId=result['id'])),encoding='utf-8');print('Prepared separate invalid-profile QA case; stop Development then mark-invalid.');return
  invalid=read(invalid_state);assert invalid['username'].startswith('Ember-Admin-QA-Bad-');profile=root/('SPT/user/profiles/'+invalid['session']+'.json');saved=read(profile);assert saved['info']['username']==invalid['username']
  if a.phase in ['mark-invalid','restore-invalid']:
   with socket.socket() as sock:assert sock.connect_ex(('127.0.0.1',a.port))!=0,'Stop Development before editing a QA fixture'
   if a.phase=='mark-invalid':
    assert read(root/'SPT/SPT_Data/configs/core.json')['fixes']['removeModItemsFromProfile'] is False,'Fixture requires native orphan-item rejection'
    shutil.copy2(profile,backup);invalid['orphanId']=uuid.uuid4().hex[:24];saved['characters']['pmc']['Inventory']['items'].append(dict(_id=invalid['orphanId'],_tpl='0'*24));profile.write_text(json.dumps(saved),encoding='utf-8');invalid['markedHash']=hashlib.sha256(profile.read_bytes()).hexdigest();invalid_state.write_text(json.dumps(invalid),encoding='utf-8');print('Added one unknown template only to the dedicated stopped Development fixture to trigger native validation failure.');return
   assert read(backup)['info']['username']==invalid['username'];shutil.copy2(backup,profile);print('Restored dedicated invalid-profile QA fixture.');return
  receipt=read(root/('.sptz/admin/receipts/'+invalid['commandId']+'.json'));assert receipt['status']=='Rejected' and 'invalid' in receipt['details']['error'].lower(),receipt
  assert any(i['_id']==invalid['orphanId'] and i['_tpl']=='0'*24 for i in saved['characters']['pmc']['Inventory']['items'])
  assert not any(m.get('text')=='管理补给' for d in saved.get('dialogues',{}).values() for m in d.get('messages',[]))
  assert 'codex.ember.admin.'+invalid['commandId'] not in saved.get('spt',{}).get('migrations',{})
  assert not (root/('.sptz/admin/backups/'+invalid['commandId'])).exists()
  assert hashlib.sha256(profile.read_bytes()).hexdigest()==invalid['markedHash'],'Invalid profile was modified on disk'
  a.report.parent.mkdir(parents=True,exist_ok=True);a.report.write_text(json.dumps(dict(passed=True,version=installed['version'],invalidProfileRejected=True,profileBytesPreserved=True,noGrant=True,noLedger=True,noBackup=True,noFalseAppliedReceipt=True,playerInstanceUsed=False),indent=2),encoding='utf-8')
  print('PASS invalid profile rejected before grant, backup, ledger or false Applied receipt; profile bytes preserved.');return
 if a.phase=='prepare':
  name='Ember-Admin-QA-'+uuid.uuid4().hex[:8];request('/launcher/profile/register',dict(username=name,edition='Standard'));files=[f for f in (root/'SPT/user/profiles').glob('*.json') if read(f).get('info',{}).get('username')==name];assert len(files)==1;session=files[0].stem
  request('/client/game/profile/create',dict(side='Usec',nickname='EmberAdminQA',headId='5cde96047d6c8b20b577f016',voiceId='5fc1223595572123ae7384a3'));request('/client/game/start');request('/client/trading/api/traderSettings')
  quests=request('/client/quest/list');qid=next(q['_id'] for q in quests if q['sptStatus']==1);r=request('/client/game/profile/items/moving',dict(data=[dict(Action='QuestAccept',qid=qid,type='Quest',removeExcessItems=True)],tm=0));assert not r.get('warnings');request('/client/game/logout')
  before=read(files[0]);pmc=before['characters']['pmc'];operations=[]
  for action,extra in [('Grant',['-Item','573719762459775a626ccbc1','-Count','73']),('Grant',['-Item','5448bd6b4bdc2dfc2f8b4569']),('Grant',['-Item','5755356824597772cb798962','-Count','2']),('Quest',['-Quest',qid,'-State','Ready']),('Quest',['-Quest',qid,'-State','Success'])]:
   ident='%032x'%(len(operations)+1);ident=uuid.uuid4().hex[:24]+ident[-8:]
   # Deliberately reverse lexical IDs: startup must honor queue time, not random GUID order.
   ident=('%08x'%(15-len(operations)))+ident[8:]
   out=cli('-Action',action,'-Profile',name,'-CommandId',ident,*extra);operations.append(dict(id=out['id'],action=action))
  badid='ffffffff'+uuid.uuid4().hex[8:];bad=dict(formatVersion=1,id=badid,instanceKind='Development',profileId=session,action='grant',itemTpl='0'*24,count=1)
  (root/('.sptz/admin/inbox/'+badid+'.json')).write_text(json.dumps(bad),encoding='utf-8')
  info=dict(username=name,session=session,qid=qid,operations=operations,badId=badid,initialXp=pmc['Info']['Experience'],initialMoney=sum(i.get('upd',{}).get('StackObjectsCount',1) for i in pmc['Inventory']['items'] if i['_tpl']=='5449016a4bdc2d6f028b456f'))
  statefile.write_text(json.dumps(info),encoding='utf-8');print('Prepared dedicated QA account and five local commands; restart Development to execute.');return
 state=read(statefile);session=state['session'];profile=root/('SPT/user/profiles/'+session+'.json')
 def messages(saved):return [m for d in saved.get('dialogues',{}).values() for m in d.get('messages',[]) if m.get('text')=='管理补给']
 saved=read(profile);pmc=saved['characters']['pmc'];mail=messages(saved)
 assert len(mail)==3,(len(mail),saved.get('dialogues',{}).keys())
 items=[i for m in mail for i in m['items']['data']]
 ammo=[i for i in items if i['_tpl']=='573719762459775a626ccbc1'];assert sum(i['upd']['StackObjectsCount'] for i in ammo)==73 and len(ammo)==2
 assert sum(i['_tpl']=='5755356824597772cb798962' for i in items)==2 and any(i['_tpl']=='5448bd6b4bdc2dfc2f8b4569' for i in items)
 assert all(i['upd']['MedKit']['HpResource']==100 for i in items if i['_tpl']=='5755356824597772cb798962')
 gun=next(i for i in items if i['_tpl']=='5448bd6b4bdc2dfc2f8b4569');assert gun['upd']['Repairable']['Durability']==100
 descendants={gun['_id']}
 while True:
  added={i['_id'] for i in items if i.get('parentId') in descendants}-descendants
  if not added:break
  descendants|=added
 assert len(descendants)>=5 and any(i['_tpl']=='5448c12b4bdc2d02308b456f' and i['_id'] in descendants for i in items)
 assert sorted(i['upd']['StackObjectsCount'] for i in ammo)==[23,50]

 ids={i['_id'] for i in items};assert len(ids)==len(items)
 assert all(not i.get('upd',{}).get('SpawnedInSession',False) for i in items)
 assert next(q for q in pmc['Quests'] if q['qid']==state['qid'])['status']==4
 assert pmc['Info']['Experience']==state['initialXp'] and sum(i.get('upd',{}).get('StackObjectsCount',1) for i in pmc['Inventory']['items'] if i['_tpl']=='5449016a4bdc2d6f028b456f')==state['initialMoney']
 for operation in state['operations']:
  receipt=read(root/('.sptz/admin/receipts/'+operation['id']+'.json'));assert receipt['status'] in ['Applied','AlreadyApplied']
  assert (root/('.sptz/admin/backups/'+operation['id']+'/profile-before.json')).exists()
 assert read(root/('.sptz/admin/receipts/'+state['badId']+'.json'))['status']=='Rejected'
 request('/client/game/start');native=next(x for x in request('/client/game/profile/list') if x['Info']['Side'].lower()!='savage');assert next(q for q in native['Quests'] if q['qid']==state['qid'])['status']==4;request('/client/game/logout')
 if a.phase=='verify':
  ident=state['operations'][0]['id'];shutil.copy2(root/('.sptz/admin/processed/'+ident+'.json'),root/('.sptz/admin/inbox/'+ident+'.json'));print('PASS three native attachment mails, full weapon, two stacks, quest ready/success without rewards, backups and invalid-item rejection. Restart Development for replay test.');return
 assert read(root/('.sptz/admin/receipts/'+state['operations'][0]['id']+'.json'))['status']=='AlreadyApplied'
 a.report.parent.mkdir(parents=True,exist_ok=True);a.report.write_text(json.dumps(dict(passed=True,version=installed['version'],nativeAttachmentMails=3,ammoUnits=73,fullWeaponPreset=True,nonFir=True,readyAndSuccess=True,noQuestRewards=True,backups=True,invalidItemRejected=True,restartReplayDidNotDuplicate=True,queueTimeOrderDespiteReverseIds=True,playerInstanceUsed=False,gameUiAcceptance=False),indent=2),encoding='utf-8');print('PASS native local-admin persistence and restart replay without duplicate grants.')
if __name__=='__main__':main()
