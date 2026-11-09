"""Exercise the real Windows installer with synthetic game files, never a player installation."""
from pathlib import Path
import hashlib,json,os,shutil,subprocess,tempfile,unittest,zipfile
ROOT=Path(__file__).resolve().parent.parent;TOOLS=ROOT/'tools'
PS=Path(os.environ.get('SystemRoot',r'C:\Windows'))/'System32/WindowsPowerShell/v1.0/powershell.exe'
def write(p,x):p.parent.mkdir(parents=True,exist_ok=True);p.write_text(json.dumps(x,indent=2)+'\n',encoding='utf-8')
def sha(p):return hashlib.sha256(p.read_bytes()).hexdigest()
class Deployment(unittest.TestCase):
 def setUp(self):
  self.temp=tempfile.TemporaryDirectory(prefix='ember-test-');self.base=Path(self.temp.name);self.game=self.base/'Game with spaces';self.package=self.base/'Package';self.source=self.base/'Dependencies'
  for p in [self.game,self.package,self.source]:p.mkdir()
  (self.game/'EscapeFromTarkov.exe').write_bytes(b'synthetic base marker, never executed')
  write(self.game/'SPT/SPT_Data/configs/http.json',dict(ip='127.0.0.1',port=6969,backendPort=6969,logRequests=True))
  write(self.game/'SPT/user/launcher/config.json',dict(GamePath='old path',Server=dict(Url='https://old.invalid'),savedAccount='synthetic-private-value'))
  write(self.game/'SPT/user/profiles/fixture.json',dict(marker='do not touch',level=21))
  self.profile=(self.game/'SPT/user/profiles/fixture.json').read_bytes()
  self.rel='SPT/user/mods/EmberFixture/data.txt';self.dep='BepInEx/plugins/ExternalFixture.dll'
  p=self.package/'payload'/self.rel;p.parent.mkdir(parents=True);p.write_bytes(b'new content')
  d=self.source/self.dep;d.parent.mkdir(parents=True);d.write_bytes(b'locked dependency')
  previous=self.game/self.rel;previous.parent.mkdir(parents=True);previous.write_bytes(b'previous unowned file')
  self.lock=dict(formatVersion=1,components=[],files=[dict(path=self.dep,sha256=sha(d),bytes=d.stat().st_size,component='external')])
  write(self.package/'mods.lock.json',self.lock)
  self.manifest=dict(formatVersion=1,name='Synthetic fixture',version='1.2.2',sptVersion='4.0.13',gameVersion='0.16.9.40087',files=[dict(path=self.rel,sha256=sha(p),bytes=p.stat().st_size)],baseFiles=[dict(path='EscapeFromTarkov.exe',sha256=sha(self.game/'EscapeFromTarkov.exe'))],knownConflicts=['SPT/user/mods/ConflictingFixture'],dependencyLockSha256=sha(self.package/'mods.lock.json'))
  write(self.package/'package.json',self.manifest)
 def tearDown(self):
  self.assertEqual((self.game/'SPT/user/profiles/fixture.json').read_bytes(),self.profile)
  self.temp.cleanup()
 def run_ps(self,script,*extra,success=True):
  cmd=[str(PS),'-NoProfile','-ExecutionPolicy','Bypass','-File',str(TOOLS/script),'-GameRoot',str(self.game)]
  if script!='Rollback.ps1':cmd+=['-PackageRoot',str(self.package)]
  cmd+=list(extra);result=subprocess.run(cmd,capture_output=True,text=True,encoding='utf-8',errors='replace',timeout=90)
  self.assertNotIn('synthetic-private-value',result.stdout+result.stderr)
  if success:self.assertEqual(result.returncode,0,result.stdout+result.stderr)
  else:self.assertNotEqual(result.returncode,0,result.stdout+result.stderr)
  return result
 def install(self,*extra,success=True):return self.run_ps('Install.ps1','-DependencyRoot',str(self.source),*extra,success=success)
 def test_install_verify_idempotent_and_rollback(self):
  original_http=(self.game/'SPT/SPT_Data/configs/http.json').read_bytes();original_launcher=(self.game/'SPT/user/launcher/config.json').read_bytes()
  self.install('-BackendPort','6974','-InstanceKind','Development')
  self.assertEqual((self.game/self.rel).read_bytes(),b'new content');self.assertEqual((self.game/self.dep).read_bytes(),b'locked dependency')
  self.assertEqual(json.loads((self.game/'SPT/SPT_Data/configs/http.json').read_text())['port'],6974)
  self.assertEqual(json.loads((self.game/'SPT/user/launcher/config.json').read_text())['savedAccount'],'synthetic-private-value')
  self.run_ps('Verify.ps1')
  before=(self.game/'.sptz/installed.json').read_bytes();r=self.install('-BackendPort','6974','-InstanceKind','Development');self.assertIn('AlreadyCurrent',r.stdout);self.assertEqual((self.game/'.sptz/installed.json').read_bytes(),before)
  self.run_ps('Rollback.ps1');self.assertEqual((self.game/self.rel).read_bytes(),b'previous unowned file');self.assertFalse((self.game/self.dep).exists())
  self.assertEqual((self.game/'SPT/SPT_Data/configs/http.json').read_bytes(),original_http);self.assertEqual((self.game/'SPT/user/launcher/config.json').read_bytes(),original_launcher)
 def seed_config(self):
  rel='BepInEx/config/EmberFixture.cfg';p=self.package/'payload'/rel;p.parent.mkdir(parents=True,exist_ok=True);p.write_bytes(b'[General]\nValue = 1\n')
  self.manifest['files'].append(dict(path=rel,sha256=sha(p),bytes=p.stat().st_size));write(self.package/'package.json',self.manifest)
  return rel,p,self.game/rel
 def test_config_reserialization_can_verify_update_and_rollback(self):
  rel,p,current=self.seed_config();self.install();current.write_bytes('[General]\r\n# Native regenerated header\r\nValue = 1\r\n'.encode('utf-8-sig'));self.run_ps('Verify.ps1')
  p.write_bytes(b'[General]\nValue = 2\n');self.manifest['files'][-1]['sha256']=sha(p);write(self.package/'package.json',self.manifest);self.install();self.run_ps('Verify.ps1')
  current.write_bytes('[General]\r\n# Another header\r\nValue = 2\r\n'.encode('utf-8-sig'));self.run_ps('Rollback.ps1');self.assertIn('Value = 1',current.read_text(encoding='utf-8-sig'))
 def test_real_config_setting_edits_are_protected(self):
  rel,p,current=self.seed_config();self.install();current.write_bytes(b'[General]\nValue = 99\n')
  self.assertIn('Locally modified',self.install(success=False).stderr);self.assertIn('Changed or missing',self.run_ps('Verify.ps1',success=False).stderr)
  self.assertIn('Managed file changed',self.run_ps('Rollback.ps1',success=False).stderr);self.assertIn('Value = 99',current.read_text())
 def seed_svm_preset(self):
  rel='SPT/user/mods/[SVM] Server Value Modifier/Presets/sptz.json';p=self.package/'payload'/rel;p.parent.mkdir(parents=True,exist_ok=True);p.write_bytes(b'{\n  "Items": {"SMGToHolster": true}\n}\n')
  self.manifest['files'].append(dict(path=rel,sha256=sha(p),bytes=p.stat().st_size));write(self.package/'package.json',self.manifest)
  return rel,p,self.game/rel
 def test_svm_encoding_only_changes_can_verify_update_and_rollback(self):
  rel,p,current=self.seed_svm_preset();self.install()
  # A legacy manifest has no SVM signature. The desired source still safely matches.
  statepath=self.game/'.sptz/installed.json';state=json.loads(statepath.read_text())
  for entry in state['files']:entry.pop('configSha256',None)
  write(statepath,state);current.write_bytes(p.read_text().replace('\n','\r\n').rstrip('\r\n').encode('utf-8-sig'))
  self.run_ps('Verify.ps1');self.install();self.run_ps('Verify.ps1')
  p.write_bytes(p.read_bytes().replace(b'true',b'false'));self.manifest['files'][-1]['sha256']=sha(p);write(self.package/'package.json',self.manifest);self.install()
  current.write_bytes(p.read_text().replace('\n','\r\n').encode('utf-8-sig'));self.run_ps('Verify.ps1');self.run_ps('Rollback.ps1');self.assertIn('true',current.read_text(encoding='utf-8-sig'))
 def test_svm_actual_setting_edits_remain_protected(self):
  rel,p,current=self.seed_svm_preset();self.install();changed=p.read_bytes().replace(b'true',b'false');current.write_bytes(changed)
  self.assertIn('Locally modified',self.install(success=False).stderr);self.assertIn('Changed or missing',self.run_ps('Verify.ps1',success=False).stderr);self.assertIn('Managed file changed',self.run_ps('Rollback.ps1',success=False).stderr)
  self.assertEqual(current.read_bytes(),changed)
 def test_legacy_state_metadata_upgrades_without_file_changes(self):
  rel,p,current=self.seed_config();self.install();statepath=self.game/'.sptz/installed.json';state=json.loads(statepath.read_text());state['formatVersion']=1
  for entry in state['files']:entry.pop('configSha256',None)
  write(statepath,state);self.assertIn('Installed',self.install().stdout);state=json.loads(statepath.read_text());self.assertEqual(state['formatVersion'],2)
  self.assertTrue(next(f for f in state['files'] if f['path']==rel)['configSha256']);self.run_ps('Verify.ps1')
 def test_plan_does_not_mutate(self):
  self.install('-Plan');self.assertFalse((self.game/'.sptz').exists());self.assertEqual((self.game/self.rel).read_bytes(),b'previous unowned file')
 def test_release_scripts_work_without_explicit_package_root(self):
  for name in ['Common.ps1','Install.ps1','Verify.ps1','Export-Dependencies.ps1']:shutil.copy2(TOOLS/name,self.package/name)
  prefix=[str(PS),'-NoProfile','-ExecutionPolicy','Bypass','-File']
  for script,args in [('Install.ps1',['-GameRoot',str(self.game),'-DependencyRoot',str(self.source)]),('Verify.ps1',['-GameRoot',str(self.game)]),('Export-Dependencies.ps1',['-SourceRoot',str(self.source),'-Output',str(self.base/'default-export.zip')])]:
   result=subprocess.run(prefix+[str(self.package/script)]+args,cwd=self.package,capture_output=True,text=True,encoding='utf-8',errors='replace',timeout=90)
   self.assertEqual(result.returncode,0,result.stdout+result.stderr)
 def test_runtime_preferences_survive_verify_update_and_rollback(self):
  self.install('-BackendPort','6974')
  launcher=self.game/'SPT/user/launcher/config.json';http=self.game/'SPT/SPT_Data/configs/http.json'
  settings=json.loads(launcher.read_text());settings['savedAccount']='another-private-fixture';settings['windowPreference']='new';write(launcher,settings)
  settings=json.loads(http.read_text());settings['logRequests']=False;write(http,settings)
  self.run_ps('Verify.ps1')
  payload=self.package/'payload'/self.rel;payload.write_bytes(b'updated content');self.manifest['files'][0]['sha256']=sha(payload);write(self.package/'package.json',self.manifest)
  self.install('-BackendPort','6974');self.run_ps('Verify.ps1');self.run_ps('Rollback.ps1');self.run_ps('Rollback.ps1')
  self.assertEqual(json.loads(launcher.read_text())['savedAccount'],'another-private-fixture');self.assertEqual(json.loads(launcher.read_text())['windowPreference'],'new')
  self.assertEqual(json.loads(launcher.read_text())['GamePath'],'old path');self.assertFalse(json.loads(http.read_text())['logRequests']);self.assertEqual(json.loads(http.read_text())['port'],6969)
 def test_verify_compares_release_content_not_only_local_state(self):
  self.install();self.manifest['files'][0]['sha256']='0'*64;write(self.package/'package.json',self.manifest)
  result=self.run_ps('Verify.ps1',success=False);self.assertIn('Changed or missing',result.stderr)
 def test_metadata_only_update_is_recorded_and_reversible(self):
  self.install();self.manifest['version']='1.2.3';write(self.package/'package.json',self.manifest);self.install()
  self.assertEqual(json.loads((self.game/'.sptz/installed.json').read_text())['version'],'1.2.3');self.run_ps('Verify.ps1')
  self.run_ps('Rollback.ps1');self.assertEqual(json.loads((self.game/'.sptz/installed.json').read_text())['version'],'1.2.2')
 def test_corrupt_payload_is_rejected(self):
  (self.package/'payload'/self.rel).write_bytes(b'tampered');r=self.install(success=False);self.assertIn('Damaged package file',r.stderr);self.assertFalse((self.game/'.sptz/installed.json').exists());self.assertEqual((self.game/self.rel).read_bytes(),b'previous unowned file')
 def test_corrupt_dependency_leaves_existing_files_intact(self):
  (self.source/self.dep).write_bytes(b'tampered');r=self.install(success=False);self.assertIn('checksum mismatch',r.stderr);self.assertEqual((self.game/self.rel).read_bytes(),b'previous unowned file');self.assertFalse((self.game/'.sptz/installed.json').exists())
 def test_traversal_cannot_reach_profiles(self):
  self.manifest['files'][0]['path']='SPT/user/mods/../../profiles/fixture.json';write(self.package/'package.json',self.manifest);r=self.install(success=False);self.assertIn('Unsafe relative path',r.stderr)
 def test_package_cannot_manage_profile_directly(self):
  self.manifest['files'][0]['path']='SPT/user/profiles/fixture.json';write(self.package/'package.json',self.manifest);r=self.install(success=False);self.assertIn('cannot manage',r.stderr)
 def test_local_changes_are_protected(self):
  self.install();(self.game/self.rel).write_bytes(b'local change');r=self.install(success=False);self.assertIn('Locally modified',r.stderr);self.assertEqual((self.game/self.rel).read_bytes(),b'local change')
  self.install('-OverwriteLocalChanges');self.assertEqual((self.game/self.rel).read_bytes(),b'new content')
 def test_known_conflicting_mod_is_rejected(self):
  (self.game/'SPT/user/mods/ConflictingFixture').mkdir();r=self.install(success=False);self.assertIn('Conflicting mod',r.stderr)
 def test_wrong_base_is_rejected(self):
  (self.game/'EscapeFromTarkov.exe').write_bytes(b'different base');r=self.install(success=False);self.assertIn('Incompatible or incomplete',r.stderr)
 def test_incomplete_manifest_is_rejected_before_writes(self):
  del self.manifest['sptVersion'];write(self.package/'package.json',self.manifest);r=self.install(success=False);self.assertIn('missing sptVersion',r.stderr);self.assertEqual((self.game/self.rel).read_bytes(),b'previous unowned file')
 def test_archive_install_and_slip_rejection(self):
  archive=self.base/'dependencies.zip'
  with zipfile.ZipFile(archive,'w') as z:z.write(self.package/'mods.lock.json','.pack/mods.lock.json');z.write(self.source/self.dep,self.dep)
  self.run_ps('Install.ps1','-DependencyArchive',str(archive));self.run_ps('Rollback.ps1')
  with zipfile.ZipFile(archive,'a') as z:z.writestr('../escaped.txt','escape')
  self.run_ps('Install.ps1','-DependencyArchive',str(archive),success=False);self.assertFalse((self.base/'escaped.txt').exists())
 def test_dependency_export_can_be_installed(self):
  out=self.base/'export.zip'
  cmd=[str(PS),'-NoProfile','-ExecutionPolicy','Bypass','-File',str(TOOLS/'Export-Dependencies.ps1'),'-SourceRoot',str(self.source),'-Output',str(out),'-PackageRoot',str(self.package)]
  r=subprocess.run(cmd,capture_output=True,text=True,encoding='utf-8',errors='replace',timeout=90);self.assertEqual(r.returncode,0,r.stdout+r.stderr)
  self.run_ps('Install.ps1','-DependencyArchive',str(out));self.run_ps('Verify.ps1')
 def test_update_removes_only_previously_owned_files(self):
  self.install();unowned=self.game/'SPT/user/mods/EmberFixture/notes.txt';unowned.write_bytes(b'keep me')
  newrel='SPT/user/mods/EmberFixture/replacement.txt';p=self.package/'payload'/newrel;p.write_bytes(b'replacement')
  self.manifest['files']=[dict(path=newrel,sha256=sha(p),bytes=p.stat().st_size)];self.manifest['version']='1.2.3';write(self.package/'package.json',self.manifest)
  self.install();self.assertFalse((self.game/self.rel).exists());self.assertEqual(unowned.read_bytes(),b'keep me')
  self.run_ps('Rollback.ps1');self.assertEqual((self.game/self.rel).read_bytes(),b'new content');self.assertFalse((self.game/newrel).exists());self.assertEqual(unowned.read_bytes(),b'keep me')
if __name__=='__main__':unittest.main(verbosity=2)
