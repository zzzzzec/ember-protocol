"""Check instance routing and failure propagation with synthetic build/deployment commands."""
from pathlib import Path
import json,os,shutil,subprocess,sys,tempfile,unittest
ROOT=Path(__file__).resolve().parent.parent
PS=Path(os.environ.get('SystemRoot',r'C:\Windows'))/'System32/WindowsPowerShell/v1.0/powershell.exe'
class Workspace(unittest.TestCase):
 def setUp(self):
  self.temp=tempfile.TemporaryDirectory(prefix='ember-workspace-');self.base=Path(self.temp.name);self.repo=self.base/'Repo with spaces';self.tools=self.repo/'tools';self.tools.mkdir(parents=True)
  self.player=self.base/'Player';self.dev=self.base/'Development'
  for game in [self.player,self.dev]:
   (game/'SPT').mkdir(parents=True);(game/'SPT/SPT.Server.exe').write_bytes(b'synthetic, never executed')
  self.profile=self.player/'SPT/user/profiles/fixture.json';self.profile.parent.mkdir(parents=True);self.profile.write_bytes(b'unchanged synthetic save')
  for name in ['Workspace.ps1','Configure-Workspace.ps1','Common.ps1']:shutil.copy2(ROOT/'tools'/name,self.tools/name)
  (self.repo/'pack.json').write_text(json.dumps(dict(version='1.2.2')))
  fixture=self.repo/'fixture-package';fixture.mkdir();(fixture/'package.json').write_text('{}')
  (fixture/'Install.ps1').write_text("param([string]$GameRoot,[string]$BackendPort,[string]$InstanceKind,[string]$DependencyRoot,[string]$DependencyArchive)\n[ordered]@{port=$BackendPort;role=$InstanceKind}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $GameRoot 'installed.json')\n")
  (fixture/'Verify.ps1').write_text("param([string]$GameRoot)\n$repo=(Get-Item -LiteralPath $PSScriptRoot).Parent.Parent.FullName\nif(Test-Path -LiteralPath (Join-Path $repo '.local/fail-verify')){exit 17}\nSet-Content -LiteralPath (Join-Path $GameRoot 'verified.txt') -Value 'verified'\n")
  (self.tools/'pack.py').write_text("from pathlib import Path\nimport shutil,sys\nr=Path(__file__).resolve().parent.parent\nif (r/'.local/fail-build').exists():raise SystemExit(13)\n(r/'.local/build-called').write_text('build')\nshutil.copytree(r/'fixture-package',r/'dist/ember-protocol-1.2.2',dirs_exist_ok=True)\n")
 def tearDown(self):
  self.assertEqual(self.profile.read_bytes(),b'unchanged synthetic save');self.temp.cleanup()
 def run_ps(self,script,*args,ok=True):
  r=subprocess.run([str(PS),'-NoProfile','-ExecutionPolicy','Bypass','-File',str(self.tools/script),*args],capture_output=True,text=True,encoding='utf-8',errors='replace',timeout=90)
  if ok:self.assertEqual(r.returncode,0,r.stdout+r.stderr)
  else:self.assertNotEqual(r.returncode,0,r.stdout+r.stderr)
  return r
 def configure(self,*extra,ok=True,development=None):
  return self.run_ps('Configure-Workspace.ps1','-PlayerRoot',str(self.player),'-DevelopmentRoot',str(development or self.dev),'-DotNet',str(PS),'-Python',sys.executable,*extra,ok=ok)
 def test_default_deployment_routes_to_development_then_explicit_player(self):
  self.configure();self.run_ps('Workspace.ps1','-Action','Install')
  self.assertTrue((self.repo/'.local/build-called').exists());self.assertTrue((self.dev/'verified.txt').exists());self.assertFalse((self.player/'installed.json').exists())
  self.assertEqual(json.loads((self.dev/'installed.json').read_text(encoding='utf-8-sig')),dict(port='6974',role='Development'))
  self.run_ps('Workspace.ps1','-Action','Install','-Instance','Player');self.assertEqual(json.loads((self.player/'installed.json').read_text(encoding='utf-8-sig')),dict(port='6969',role='Player'))
 def test_failed_build_never_calls_installer(self):
  self.configure();(self.repo/'.local/fail-build').touch();r=self.run_ps('Workspace.ps1','-Action','Install',ok=False)
  self.assertIn('exit code 13',r.stderr);self.assertFalse((self.dev/'installed.json').exists());self.assertFalse((self.dev/'verified.txt').exists())
 def test_failed_verification_is_reported(self):
  self.configure();(self.repo/'.local/fail-verify').touch();r=self.run_ps('Workspace.ps1','-Action','Install',ok=False)
  self.assertIn('exit code 17',r.stderr);self.assertTrue((self.dev/'installed.json').exists());self.assertFalse((self.dev/'verified.txt').exists())
 def test_same_directory_or_port_is_rejected(self):
  r=self.configure(ok=False,development=self.player);self.assertIn('separate',r.stderr);self.assertFalse((self.repo/'.local/workspace.json').exists())
  r=self.configure('-DevelopmentPort','6969',ok=False);self.assertIn('ports must differ',r.stderr)
 def test_backend_check_cannot_target_player(self):
  self.configure();r=self.run_ps('Workspace.ps1','-Action','BackendCheck','-Instance','Player',ok=False)
  self.assertIn('restricted to Development',r.stderr);self.assertFalse((self.repo/'.local/build-called').exists());self.assertFalse((self.player/'installed.json').exists())
if __name__=='__main__':unittest.main(verbosity=2)
