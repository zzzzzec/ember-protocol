[CmdletBinding()]
param(
 [ValidateSet('Help','Profiles','Items','Quests','Grant','Quest','Status')][string]$Action='Help',
 [string]$GameRoot,[string]$Profile,[string]$Item,[int]$Count=1,[string]$Quest,
 [ValidateSet('Ready','Success')][string]$State='Ready',[string]$CommandId,
 [string]$Note='管理补给',[string]$Search,[switch]$Plan
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
function Value($Object,[string]$Key){if($null -eq $Object){return $null};$property=$Object.PSObject.Properties[$Key];if($property){return $property.Value};return $null}
function Read-LocalJson([string]$Path){Get-Content -LiteralPath $Path -Raw -Encoding UTF8|ConvertFrom-Json}
if($Action -eq 'Help'){
 Write-Output 'Ember local admin: Profiles / Items -Search text / Quests -Profile username / Grant -Profile username -Item tpl -Count n / Quest -Profile username -Quest id -State Ready|Success / Status -CommandId id'
 Write-Output 'Specify -GameRoot. Grant/Quest queue one command for the NEXT server startup; -Plan previews without writing. Success marks only, without quest rewards. Mail grants are non-FIR.'
 return
}
if(-not $GameRoot){$GameRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))}
$GameRoot=(Resolve-Path -LiteralPath $GameRoot).Path
$installed=Read-LocalJson (Join-Path $GameRoot '.sptz\installed.json')
if($installed.instanceKind -notin @('Player','Development')){throw 'Unsupported instance role.'}
$mod=Join-Path $GameRoot 'SPT\user\mods\Codex-ZombieSurvival'
$admin=Join-Path $GameRoot '.sptz\admin'
if($Action -eq 'Status'){
 if($CommandId -notmatch '^[a-f0-9]{32}$'){throw 'CommandId must be a lowercase 32-digit GUID.'}
 $receipt=Join-Path $admin ('receipts\'+$CommandId+'.json')
 if(Test-Path -LiteralPath $receipt){Get-Content -LiteralPath $receipt -Raw -Encoding UTF8;return}
 if(Test-Path -LiteralPath (Join-Path $admin ('inbox\'+$CommandId+'.json'))){[ordered]@{id=$CommandId;status='PendingNextServerStartup'}|ConvertTo-Json;return}
 throw 'Command not found.'
}
if($Action -eq 'Items'){
 $handbook=Read-LocalJson (Join-Path $GameRoot 'SPT\SPT_Data\database\templates\handbook.json')
 $localePath=Join-Path $GameRoot 'SPT\SPT_Data\database\locales\global\ch.json'
 $null=[Reflection.Assembly]::LoadFrom((Join-Path $GameRoot 'EscapeFromTarkov_Data\Managed\Newtonsoft.Json.dll'))
 $locale=$null
 if(Test-Path -LiteralPath $localePath){$locale=[Newtonsoft.Json.Linq.JObject]::Parse([IO.File]::ReadAllText($localePath))}
 ConvertTo-Json -Depth 5 -InputObject @($handbook.Items|ForEach-Object {
  $tpl=$_.Id;$label=$null;if($null -ne $locale){$label=$locale.GetValue(($tpl+' Name'),[StringComparison]::Ordinal)};$name=if($null -ne $label){$label.ToString()}else{$tpl}
  if(-not $Search -or $name.IndexOf($Search,[StringComparison]::OrdinalIgnoreCase) -ge 0 -or $tpl -eq $Search){[ordered]@{tpl=$tpl;name=$name}}
 }|Select-Object -First 100)
 return
}
$profiles=@(Get-ChildItem -LiteralPath (Join-Path $GameRoot 'SPT\user\profiles') -Filter '*.json' -File|ForEach-Object {
 $data=Read-LocalJson $_.FullName;$info=Value $data 'info';$pmc=Value (Value $data 'characters') 'pmc';$details=Value $pmc 'Info'
 [pscustomobject]@{id=$_.BaseName;username=(Value $info 'username');nickname=(Value $details 'Nickname');level=(Value $details 'Level');pmc=$pmc}
})
if($Action -eq 'Profiles'){ConvertTo-Json -Depth 4 -InputObject @($profiles|Select-Object username,nickname,level);return}
if(-not $Profile){throw 'Specify one exact profile username or nickname.'}
$selectedProfiles=@($profiles|Where-Object {$_.username -ceq $Profile -or $_.nickname -ceq $Profile -or $_.id -ceq $Profile})
if($selectedProfiles.Count -ne 1 -or $null -eq $selectedProfiles[0].pmc){throw 'Profile must uniquely identify a created character.'}
$target=$selectedProfiles[0]
$quests=Read-LocalJson (Join-Path $mod 'data\quests.json')
$locales=Read-LocalJson (Join-Path $mod 'data\locales.json')
if($Action -eq 'Quests'){
 ConvertTo-Json -Depth 5 -InputObject @($quests.PSObject.Properties|ForEach-Object {
  $qid=$_.Name;$name=Value $locales.ch ($qid+' name');$current=@((Value $target.pmc 'Quests')|Where-Object {$null -ne $_ -and $_.qid -eq $qid})
  if(-not $Search -or $qid -eq $Search -or ($name -and $name.IndexOf($Search,[StringComparison]::OrdinalIgnoreCase) -ge 0)){
   [ordered]@{questId=$qid;name=$name;status=if($current.Count){$current[0].status}else{'NotAccepted'}}
  }
 })
 return
}
if(-not $CommandId){$CommandId=[guid]::NewGuid().ToString('N')}
if($CommandId -notmatch '^[a-f0-9]{32}$'){throw 'CommandId must be a lowercase 32-digit GUID.'}
if($Note.Length -gt 500){throw 'Note exceeds 500 characters.'}
$command=[ordered]@{formatVersion=1;id=$CommandId;createdUtc=[DateTimeOffset]::UtcNow.ToString('o');instanceKind=$installed.instanceKind;profileId=$target.id;action=$Action.ToLowerInvariant()}
if($Action -eq 'Grant'){
 if($Item -notmatch '^[a-f0-9]{24}$' -or $Count -lt 1 -or $Count -gt 1000000){throw 'Specify a valid template and count 1..1000000.'}
 $command.itemTpl=$Item;$command.count=$Count;$command.note=$Note
}else{
 if($Quest -notmatch '^[a-f0-9]{24}$' -or $null -eq (Value $quests $Quest)){throw 'Specify an authored campaign quest ID.'}
 $current=@((Value $target.pmc 'Quests')|Where-Object {$null -ne $_ -and $_.qid -eq $Quest})
 if($current.Count -ne 1 -or $current[0].status -notin @(2,3,'Started','AvailableForFinish')){throw 'Quest must already be accepted and unfinished.'}
 $command.questId=$Quest;$command.state=$State.ToLowerInvariant()
}
if($Plan){[ordered]@{status='PreviewOnly';command=$command;appliesAt='NextServerStartup';questRewardsGranted=$false}|ConvertTo-Json -Depth 8;return}
$inbox=Join-Path $admin 'inbox';New-Item -ItemType Directory -Path $inbox -Force|Out-Null
$file=Join-Path $inbox ($CommandId+'.json')
if((Test-Path -LiteralPath $file) -or (Test-Path -LiteralPath (Join-Path $admin ('receipts\'+$CommandId+'.json')))){throw 'Command ID already exists; choose a new ID.'}
$temp=Join-Path $inbox ($CommandId+'.tmp')
[IO.File]::WriteAllText($temp,($command|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
Move-Item -LiteralPath $temp -Destination $file
[ordered]@{id=$CommandId;status='Queued';profile=$target.username;action=$Action;appliesAt='NextServerStartup'}|ConvertTo-Json
