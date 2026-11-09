[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$GameRoot,[string]$BackupId)
. (Join-Path $PSScriptRoot 'Common.ps1')
$root=(Resolve-Path -LiteralPath $GameRoot).Path
Assert-TargetStopped $root
$statePath=Get-ContainedPath $root '.sptz/installed.json'
if(-not(Test-Path -LiteralPath $statePath)){throw 'This target has no managed installation to roll back.'}
$state=Read-JsonFile $statePath
if(-not $BackupId){$BackupId=$state.installId}
if($BackupId -notmatch '^\d{8}-\d{6}-[a-f0-9]{8}$' -or $BackupId -ne $state.installId){throw 'Only the latest managed transaction can be rolled back.'}
$backup=Get-ContainedPath $root ('.sptz/backups/'+$BackupId)
$transaction=Read-JsonFile (Join-Path $backup 'transaction.json')
if($transaction.id -ne $BackupId){throw 'Backup transaction mismatch.'}
foreach($record in $transaction.files){
 $rel=Get-SafeRelative $record.path
 if($rel -notin @('SPT/SPT_Data/configs/http.json','SPT/user/launcher/config.json','SPT/user/mods/Codex-ZombieSurvival/runtime.json')){Assert-ManagedPath $rel}
 $dest=Get-ContainedPath $root $rel
 $sameConfig=$false
 if($null -ne $record.PSObject.Properties['afterConfigSha256'] -and $record.afterConfigSha256 -and (Test-Path -LiteralPath $dest -PathType Leaf)){
  $sameConfig=(Get-ConfigSignature $dest $rel) -eq $record.afterConfigSha256
 }
 if($record.afterSha256){
  if(-not(Test-Path -LiteralPath $dest -PathType Leaf) -or ((Get-Sha $dest) -ne $record.afterSha256 -and -not $sameConfig -and -not (Test-MutableRuntimeConfig $rel))){throw "Managed file changed after installation: $rel"}
 }elseif(Test-Path -LiteralPath $dest){throw "A removed path was recreated: $rel"}
 if($record.existed){
  $copy=Get-StoredPath (Join-Path $backup 'files') $rel
  if(-not(Test-Path -LiteralPath $copy -PathType Leaf) -or (Get-Sha $copy) -ne $record.beforeSha256){throw "Missing or damaged backup: $rel"}
 }
}
for($index=@($transaction.files).Count-1;$index -ge 0;$index--){
 $record=$transaction.files[$index];$dest=Get-ContainedPath $root $record.path
 if($record.existed){
  $copy=Get-StoredPath (Join-Path $backup 'files') $record.path
  if((Test-MutableRuntimeConfig $record.path) -and (Get-Sha $dest) -ne $record.afterSha256){Restore-RuntimeConfig $copy $dest $record.path}
  else{Write-AtomicFile $copy $dest}
 }
 elseif(Test-Path -LiteralPath $dest){Remove-Item -LiteralPath $dest -Force}
}
if($transaction.hadPreviousInstall){Write-AtomicFile (Join-Path $backup 'previous-install.json') $statePath}else{Remove-Item -LiteralPath $statePath -Force}
[PSCustomObject]@{status='RolledBack';transaction=$BackupId;filesRestored=@($transaction.files).Count;profilesChanged=$false}|ConvertTo-Json
