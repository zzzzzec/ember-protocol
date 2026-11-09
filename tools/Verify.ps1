param([Parameter(Mandatory=$true)][string]$GameRoot,[string]$PackageRoot)
if(-not $PackageRoot){$PackageRoot=$PSScriptRoot}
. (Join-Path $PSScriptRoot 'Common.ps1')
$root=(Resolve-Path -LiteralPath $GameRoot).Path
$manifest=Read-JsonFile (Join-Path $PackageRoot 'package.json')
Assert-BaseFiles $root $manifest
$lockPath=Join-Path $PackageRoot 'mods.lock.json'
if((Get-Sha $lockPath) -ne $manifest.dependencyLockSha256){throw 'Dependency lock mismatch.'}
$lock=Read-JsonFile $lockPath
$state=Read-JsonFile (Get-ContainedPath $root '.sptz/installed.json')
if($state.version -ne $manifest.version -or $state.dependencyLockSha256 -ne $manifest.dependencyLockSha256){throw 'A different package version is installed.'}
$expected=@{}
foreach($f in @($manifest.files)+@($lock.files)){
 Assert-ManagedPath $f.path
 if($expected.ContainsKey($f.path)){throw 'Duplicate expected file.'}
 $expected[$f.path]=$f.sha256
}
$seen=@{}
$mismatches=@()
foreach($f in $state.files){
 if($seen.ContainsKey($f.path)){throw 'Duplicate installed file.'};$seen[$f.path]=$true
 $path=Get-ContainedPath $root $f.path
 if($expected.ContainsKey($f.path)){
  $valid=$false
  if($f.sha256 -eq $expected[$f.path] -and (Test-Path -LiteralPath $path -PathType Leaf)){
   $valid=(Get-Sha $path) -eq $expected[$f.path]
   if(-not $valid){
    $releaseConfig=Get-ContainedPath (Join-Path $PackageRoot 'payload') $f.path
    if((Test-Path -LiteralPath $releaseConfig -PathType Leaf) -and (Get-Sha $releaseConfig) -eq $expected[$f.path]){
     $signature=Get-ConfigSignature $releaseConfig $f.path
     $valid=$signature -and (Get-ConfigSignature $path $f.path) -eq $signature
    }
   }
  }
  if(-not $valid){$mismatches+=$f.path}
 }elseif(Test-MutableRuntimeConfig $f.path){
  $settings=Read-JsonFile $path
  if($f.path -eq 'SPT/SPT_Data/configs/http.json'){
   if($settings.port -ne $state.port -or $settings.backendPort -ne $state.port){$mismatches+=$f.path}
  }elseif($settings.GamePath -ne $root -or $settings.Server.Url.TrimEnd('/') -ne "https://127.0.0.1:$($state.port)"){$mismatches+=$f.path}
 }elseif($f.path -eq 'SPT/user/mods/Codex-ZombieSurvival/runtime.json'){
  if(-not(Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Sha $path) -ne $f.sha256 -or (Read-JsonFile $path).instanceKind -ne $state.instanceKind){$mismatches+=$f.path}
 }else{throw "Unexpected installed path: $($f.path)"}
}
foreach($rel in @($expected.Keys)+@('SPT/SPT_Data/configs/http.json','SPT/user/mods/Codex-ZombieSurvival/runtime.json')){if(-not $seen.ContainsKey($rel)){$mismatches+=$rel}}
if($mismatches.Count){throw ('Changed or missing managed files: '+($mismatches -join ', '))}
[PSCustomObject]@{status='Verified';version=$state.version;files=@($state.files).Count;port=$state.port;instanceKind=$state.instanceKind;inRaidAcceptance=$false}|ConvertTo-Json
