"""Local admin CLI queues commands without editing any profile."""
from pathlib import Path
import json,os,subprocess,tempfile,unittest
ROOT=Path(__file__).resolve().parent.parent
PS=Path(os.environ.get('SystemRoot',r'C:\Windows'))/'System32/WindowsPowerShell/v1.0/powershell.exe'
class Admin(unittest.TestCase):
 def setUp(self):
  self.tmp=tempfile.TemporaryDirectory(prefix='ember-admin-');self.game=Path(self.tmp.name)/'Game with spaces';self.game.mkdir()
  self.pid='a'*24;self.qid='b'*24
  self.profile=self.game/('SPT/user/profiles/'+self.pid+'.json');self.profile.parent.mkdir(parents=True)
  self.original=json.dumps(dict(info=dict(username='AdminFixture'),characters=dict(pmc=dict(Info=dict(Nickname='Fixture',Level=1),Quests=[dict(qid=self.qid,status=2)])))).encode();self.profile.write_bytes(self.original)
  self.put('.sptz/installed.json',dict(instanceKind='Development',port=6974))
  self.put('SPT/user/mods/Codex-ZombieSurvival/data/quests.json',{self.qid:{}})
  self.put('SPT/user/mods/Codex-ZombieSurvival/data/locales.json',dict(ch={self.qid+' name':'Fixture quest'}))
 def put(self,rel,data):
  p=self.game/rel;p.parent.mkdir(parents=True,exist_ok=True);p.write_text(json.dumps(data),encoding='utf-8')
 def tearDown(self):
  self.assertEqual(self.profile.read_bytes(),self.original);self.tmp.cleanup()
 def run_cli(self,*args,ok=True):
  r=subprocess.run([str(PS),'-NoProfile','-ExecutionPolicy','Bypass','-File',str(ROOT/'tools/Admin.ps1'),'-GameRoot',str(self.game),*args],capture_output=True,encoding='utf-8',errors='replace',timeout=20)
  self.assertEqual(r.returncode==0,ok,r.stdout+r.stderr);return r
 def test_profiles_and_quests_read_only(self):
  self.assertEqual(json.loads(self.run_cli('-Action','Profiles').stdout)[0]['username'],'AdminFixture')
  self.assertEqual(json.loads(self.run_cli('-Action','Quests','-Profile','AdminFixture').stdout)[0]['status'],2)
 def test_grant_queue_and_status(self):
  r=json.loads(self.run_cli('-Action','Grant','-Profile','Fixture','-Item','c'*24,'-Count','73').stdout)
  command=json.loads((self.game/('.sptz/admin/inbox/'+r['id']+'.json')).read_text(encoding='utf-8-sig'))
  self.assertEqual(command['profileId'],self.pid);self.assertEqual(command['count'],73)
  self.assertEqual(json.loads(self.run_cli('-Action','Status','-CommandId',r['id']).stdout)['status'],'PendingNextServerStartup')
 def test_preview_never_writes_queue(self):
  result=json.loads(self.run_cli('-Action','Grant','-Profile','AdminFixture','-Item','c'*24,'-Plan').stdout)
  self.assertEqual(result['status'],'PreviewOnly');self.assertFalse((self.game/'.sptz/admin').exists())
 def test_quest_success_is_explicit_and_no_rewards(self):
  result=json.loads(self.run_cli('-Action','Quest','-Profile','AdminFixture','-Quest',self.qid,'-State','Success','-Plan').stdout)
  self.assertFalse(result['questRewardsGranted']);self.assertEqual(result['command']['state'],'success')
 def test_completed_and_unaccepted_quests_rejected(self):
  self.run_cli('-Action','Quest','-Profile','AdminFixture','-Quest','d'*24,ok=False)
  self.original=self.original.replace(b'"status": 2',b'"status": 4');self.profile.write_bytes(self.original)
  self.run_cli('-Action','Quest','-Profile','AdminFixture','-Quest',self.qid,ok=False)
 def test_duplicate_and_traversal_ids_rejected(self):
  args=['-Action','Grant','-Profile','AdminFixture','-Item','c'*24,'-CommandId','e'*32]
  self.run_cli(*args);self.run_cli(*args,ok=False)
  self.run_cli('-Action','Status','-CommandId','../../escape',ok=False)
 def test_unknown_profile_and_invalid_quantity_rejected(self):
  self.run_cli('-Action','Grant','-Profile','Missing','-Item','c'*24,ok=False)
  self.run_cli('-Action','Grant','-Profile','Fixture','-Item','c'*24,'-Count','0',ok=False)
if __name__=='__main__':unittest.main()
