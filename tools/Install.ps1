[CmdletBinding()]
param(
 [Parameter(Mandatory=$true)][string]$GameRoot,
 [string]$PackageRoot,
 [string]$DependencyRoot,
 [string]$DependencyArchive,
 [ValidateRange(1,65535)][int]$BackendPort=6969,
 [ValidateSet('Player','Development')][string]$InstanceKind='Player',
 [switch]$Plan,
 [switch]$OverwriteLocalChanges
)
if(-not $PackageRoot){$PackageRoot=$PSScriptRoot}
. (Join-Path $PSScriptRoot 'Common.ps1')
$root=(Resolve-Path -LiteralPath $GameRoot).Path
$package=(Resolve-Path -LiteralPath $PackageRoot).Path
$manifest=Read-JsonFile (Join-Path $package 'package.json')
foreach($required in @('formatVersion','version','sptVersion','gameVersion','files','baseFiles','knownConflicts','dependencyLockSha256')){
 if($null -eq $manifest.PSObject.Properties[$required]){throw "Invalid package manifest: missing $required"}
}
if($manifest.formatVersion -ne 1){throw 'Unsupported package format.'}
$lockPath=Join-Path $package 'mods.lock.json'
if((Get-Sha $lockPath) -ne $manifest.dependencyLockSha256){throw 'Dependency lock does not match this package.'}
$lock=Read-JsonFile $lockPath
Assert-TargetStopped $root
Assert-BaseFiles $root $manifest
foreach($conflict in $manifest.knownConflicts){if(Test-Path -LiteralPath (Get-ContainedPath $root $conflict)){throw "Conflicting mod is installed: $conflict"}}
$statePath=Get-ContainedPath $root '.sptz/installed.json'
$old=$null;$oldRaw=$null
if(Test-Path -LiteralPath $statePath){$oldRaw=Get-Content -Raw -LiteralPath $statePath -Encoding UTF8;$old=$oldRaw|ConvertFrom-Json}
$oldConfigSignatures=@{}
if($null -ne $old){foreach($f in $old.files){if($null -ne $f.PSObject.Properties['configSha256'] -and $f.configSha256){$oldConfigSignatures[$f.path]=$f.configSha256}}}
$oldFiles=@{}
if($null -ne $old){foreach($f in $old.files){$oldFiles[$f.path]=$f.sha256}}
$all=New-Object 'Collections.Generic.Dictionary[string,object]' ([StringComparer]::OrdinalIgnoreCase)
$archive=$null
try {
 if($DependencyRoot -and $DependencyArchive){throw 'Use either DependencyRoot or DependencyArchive.'}
 if($DependencyArchive){$archive=Read-DependencyArchive $DependencyArchive $manifest.dependencyLockSha256}
 $dependencySource=$null
 if($DependencyRoot){$dependencySource=(Resolve-Path -LiteralPath $DependencyRoot).Path}
 foreach($f in $manifest.files){
  $rel=Get-SafeRelative $f.path;Assert-ManagedPath $rel
  if($all.ContainsKey($rel)){throw 'Duplicate managed file.'}
  $source=Get-ContainedPath (Join-Path $package 'payload') $rel
  if(-not (Test-Path -LiteralPath $source -PathType Leaf) -or (Get-Sha $source) -ne $f.sha256){throw "Damaged package file: $rel"}
  $all.Add($rel,[PSCustomObject]@{path=$rel;sha256=$f.sha256;kind='file';source=$source;text=$null})
 }
 foreach($f in $lock.files){
  $rel=Get-SafeRelative $f.path;Assert-ManagedPath $rel
  if($all.ContainsKey($rel)){throw 'A dependency overlaps a first-party file or setting.'}
  if($null -ne $archive){
   if(-not $archive.Entries.ContainsKey($rel) -or $archive.Entries[$rel].Length -ne $f.bytes){throw "Incomplete dependency archive: $rel"}
   $row=[PSCustomObject]@{path=$rel;sha256=$f.sha256;kind='archive';source=$archive.Entries[$rel];text=$null}
  }else{
   $sourceRoot=$root;if($dependencySource){$sourceRoot=$dependencySource}
   $source=Get-ContainedPath $sourceRoot $rel
   if(-not (Test-Path -LiteralPath $source -PathType Leaf)){throw "Missing dependency: $rel. Supply DependencyRoot or DependencyArchive."}
   $row=[PSCustomObject]@{path=$rel;sha256=$f.sha256;kind='file';source=$source;text=$null}
  }
  $all.Add($rel,$row)
 }
 if($null -ne $archive -and $archive.Entries.Count -ne @($lock.files).Count){throw 'Unexpected files in dependency archive.'}
 # Machine-specific state is generated at install time and is never included in Git or release payloads.
 $generated=[ordered]@{}
 $generated['SPT/user/mods/Codex-ZombieSurvival/runtime.json']=([ordered]@{instanceKind=$InstanceKind}|ConvertTo-Json)
 $httpPath=Get-ContainedPath $root 'SPT/SPT_Data/configs/http.json'
 if(-not(Test-Path -LiteralPath $httpPath)){throw 'Target has no native HTTP config.'}
 $http=Read-JsonFile $httpPath;$http.port=$BackendPort;$http.backendPort=$BackendPort
 $generated['SPT/SPT_Data/configs/http.json']=$http|ConvertTo-Json -Depth 30
 $launcherPath=Get-ContainedPath $root 'SPT/user/launcher/config.json'
 if(Test-Path -LiteralPath $launcherPath){
  $launcher=Read-JsonFile $launcherPath;$launcher.GamePath=$root;$launcher.Server.Url="https://127.0.0.1:$BackendPort"
  $generated['SPT/user/launcher/config.json']=$launcher|ConvertTo-Json -Depth 50
 }
 foreach($rel in $generated.Keys){
  $text=$generated[$rel]+"`n"
  $all.Add($rel,[PSCustomObject]@{path=$rel;sha256=(Get-TextSha $text);kind='generated';source=$null;text=$text})
 }
 $changes=New-Object 'Collections.Generic.List[object]'
 foreach($row in $all.Values){
  $signature=$null
  if($row.kind -eq 'file'){$signature=Get-ConfigSignature $row.source $row.path}
  $row|Add-Member -MemberType NoteProperty -Name configSha256 -Value $signature
  $dest=Get-ContainedPath $root $row.path
  if(Test-Path -LiteralPath $dest -PathType Container){throw "Expected a file: $($row.path)"}
  $current=$null;if(Test-Path -LiteralPath $dest -PathType Leaf){$current=Get-Sha $dest}
  $sameConfig=$false
  if($current -and $signature){
   $currentSignature=Get-ConfigSignature $dest $row.path
   $sameConfig=($currentSignature -eq $signature -or ($oldConfigSignatures.ContainsKey($row.path) -and $currentSignature -eq $oldConfigSignatures[$row.path]))
  }
  if($oldFiles.ContainsKey($row.path) -and $current -and $current -ne $oldFiles[$row.path] -and $current -ne $row.sha256 -and -not $sameConfig -and -not (Test-MutableRuntimeConfig $row.path) -and -not $OverwriteLocalChanges){throw "Locally modified managed file: $($row.path). Import the setting into Git or use OverwriteLocalChanges after review."}
  if($current -ne $row.sha256){$changes.Add([PSCustomObject]@{path=$row.path;before=$current;operation='write';row=$row})}
 }
 foreach($rel in $oldFiles.Keys){
  if($all.ContainsKey($rel)){continue}
  Assert-ManagedPath $rel;$dest=Get-ContainedPath $root $rel
  if(Test-Path -LiteralPath $dest -PathType Leaf){
   $current=Get-Sha $dest
   $sameConfig=$oldConfigSignatures.ContainsKey($rel) -and (Get-ConfigSignature $dest $rel) -eq $oldConfigSignatures[$rel]
   if($current -ne $oldFiles[$rel] -and -not $sameConfig -and -not $OverwriteLocalChanges){throw "Locally modified obsolete file: $rel"}
   $changes.Add([PSCustomObject]@{path=$rel;before=$current;operation='remove';row=$null})
  }
 }
 if($Plan){[PSCustomObject]@{version=$manifest.version;target=$root;filesToChange=$changes.Count;changes=@($changes|Select-Object path,operation)}|ConvertTo-Json -Depth 8;return}
 if($changes.Count -eq 0 -and $null -ne $old -and $old.formatVersion -eq 2 -and $old.version -eq $manifest.version -and $old.dependencyLockSha256 -eq $manifest.dependencyLockSha256 -and $old.port -eq $BackendPort -and $old.instanceKind -eq $InstanceKind){[PSCustomObject]@{status='AlreadyCurrent';version=$manifest.version;filesChanged=0}|ConvertTo-Json;return}
 $id=(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8)
 $stage=Get-ContainedPath $root ('.sptz/staging/'+$id)
 $backup=Get-ContainedPath $root ('.sptz/backups/'+$id)
 New-Item -ItemType Directory -Path $stage -Force|Out-Null
 $applied=New-Object 'Collections.Generic.List[object]'
 $records=New-Object 'Collections.Generic.List[object]'
 $transactionStarted=$false
 try {
  # Stage and verify every changed file before touching existing installation files.
  foreach($change in $changes){
   if($change.operation -eq 'remove'){continue}
   $row=$change.row;$target=Get-StoredPath $stage $row.path
   New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force|Out-Null
   if($row.kind -eq 'generated'){[IO.File]::WriteAllText($target,$row.text,(New-Object Text.UTF8Encoding($false)))}
   elseif($row.kind -eq 'archive'){
    $inputStream=$row.source.Open();$outputStream=[IO.File]::Create($target)
    try{$inputStream.CopyTo($outputStream)}finally{$inputStream.Dispose();$outputStream.Dispose()}
   }else{[IO.File]::Copy($row.source,$target,$true)}
   if((Get-Sha $target) -ne $row.sha256){throw "Dependency or payload checksum mismatch: $($row.path)"}
  }
  New-Item -ItemType Directory -Path $backup -Force|Out-Null
  if($null -ne $oldRaw){[IO.File]::WriteAllText((Join-Path $backup 'previous-install.json'),$oldRaw,(New-Object Text.UTF8Encoding($false)))}
  foreach($change in $changes){
   $dest=Get-ContainedPath $root $change.path
   $exists=Test-Path -LiteralPath $dest -PathType Leaf
   if($exists){
    if((Get-Sha $dest) -ne $change.before){throw 'A target file changed during installation.'}
    $copy=Get-StoredPath (Join-Path $backup 'files') $change.path;New-Item -ItemType Directory -Path (Split-Path -Parent $copy) -Force|Out-Null;[IO.File]::Copy($dest,$copy,$false)
   }
   $records.Add([PSCustomObject]@{path=$change.path;existed=$exists;beforeSha256=$change.before;afterSha256=$(if($change.operation -eq 'write'){$change.row.sha256}else{$null});afterConfigSha256=$(if($change.operation -eq 'write'){$change.row.configSha256}else{$null});operation=$change.operation})
  }
  $transaction=[ordered]@{id=$id;version=$manifest.version;hadPreviousInstall=($null -ne $oldRaw);files=$records.ToArray()}
  Write-AtomicText (Join-Path $backup 'transaction.json') (($transaction|ConvertTo-Json -Depth 10)+"`n")
  $transactionStarted=$true
  foreach($change in $changes){
   $dest=Get-ContainedPath $root $change.path
   if($change.operation -eq 'remove'){Remove-Item -LiteralPath $dest -Force}else{Move-StagedFile (Get-StoredPath $stage $change.path) $dest}
   $applied.Add($change)
  }
  $state=[ordered]@{formatVersion=2;installId=$id;version=$manifest.version;sptVersion=$manifest.sptVersion;gameVersion=$manifest.gameVersion;port=$BackendPort;instanceKind=$InstanceKind;dependencyLockSha256=$manifest.dependencyLockSha256;files=@($all.Values|Select-Object path,sha256,configSha256)}
  Write-AtomicText $statePath (($state|ConvertTo-Json -Depth 10)+"`n")
  [PSCustomObject]@{status='Installed';version=$manifest.version;filesChanged=$changes.Count;backupId=$id;port=$BackendPort;instanceKind=$InstanceKind}|ConvertTo-Json
 }catch{
  for($index=$applied.Count-1;$index -ge 0;$index--){
   $change=$applied[$index]
   $dest=Get-ContainedPath $root $change.path;$copy=Get-StoredPath (Join-Path $backup 'files') $change.path
   if(Test-Path -LiteralPath $copy){Write-AtomicFile $copy $dest}elseif(Test-Path -LiteralPath $dest){Remove-Item -LiteralPath $dest -Force}
  }
  if($transactionStarted){
   if($null -ne $oldRaw){Write-AtomicText $statePath $oldRaw}elseif(Test-Path -LiteralPath $statePath){Remove-Item -LiteralPath $statePath -Force}
  }
  throw
 }finally{if(Test-Path -LiteralPath $stage){Remove-SafeStage $root $stage}}
}finally{if($null -ne $archive){$archive.Zip.Dispose()}}
