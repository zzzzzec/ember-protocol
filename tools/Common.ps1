Set-StrictMode -Version 2.0
$ErrorActionPreference='Stop'
function Read-JsonFile([string]$Path){Get-Content -Raw -LiteralPath $Path -Encoding UTF8 | ConvertFrom-Json}
function Get-Sha([string]$Path){
 $hash=[Security.Cryptography.SHA256]::Create();$stream=[IO.File]::OpenRead($Path)
 try{([BitConverter]::ToString($hash.ComputeHash($stream))).Replace('-','').ToLowerInvariant()}finally{$stream.Dispose();$hash.Dispose()}
}
function Get-TextSha([string]$Text){
 $hash=[Security.Cryptography.SHA256]::Create()
 try{([BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($Text)))).Replace('-','').ToLowerInvariant()}finally{$hash.Dispose()}
}
function Get-StoredPath([string]$Root,[string]$Relative){
 $key=Get-TextSha ((Get-SafeRelative $Relative).ToLowerInvariant())
 return Get-ContainedPath $Root ($key+'.bin')
}
function Get-SafeRelative([string]$Path){
 $p=$Path.Replace('\','/')
 if([string]::IsNullOrWhiteSpace($p) -or $p.StartsWith('/') -or $p.Contains(':') -or $p.IndexOfAny([char[]]'*?') -ge 0){throw "Unsafe relative path: $Path"}
 foreach($part in $p.Split('/')){if($part -eq '' -or $part -eq '.' -or $part -eq '..' -or $part.TrimEnd(' ','.') -ne $part -or $part -match '[\x00-\x1f]'){throw "Unsafe relative path: $Path"}}
 return $p
}
function Get-ContainedPath([string]$Root,[string]$Relative){
 $rel=Get-SafeRelative $Relative
 $base=[IO.Path]::GetFullPath($Root).TrimEnd('\','/')
 $path=[IO.Path]::GetFullPath((Join-Path $base $rel))
 if(-not $path.StartsWith($base+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Path escapes target root.'}
 $probe=$path
 while($probe.Length -gt $base.Length){
  if(Test-Path -LiteralPath $probe){if((Get-Item -LiteralPath $probe -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw "Reparse point is not a managed target: $rel"}}
  $probe=Split-Path -Parent $probe
 }
 return $path
}
function Assert-ManagedPath([string]$Path){
 $p=Get-SafeRelative $Path
 if($p -match '^(SPT/user/mods/|BepInEx/plugins/|BepInEx/config/|Tools/EmberProtocol/)' -or $p -in @('Greed.exe','启动余烬协议.cmd','Tools/ZombieSurvival/collect-diagnostics.py','Tools/ZombieSurvival/Collect-Diagnostics.cmd')){return}
 throw "Package cannot manage this path: $p"
}
function Get-ConfigSignature([string]$Path,[string]$Relative){
 # Greed rewrites preset encoding and line endings without changing settings.
 # Keep all JSON characters/order significant; only BOM, CRLF and final newlines normalize.
 if($Relative -match '^SPT/user/mods/\[SVM\] Server Value Modifier/Presets/[^/]+\.json$'){
  $preset=[IO.File]::ReadAllText($Path,[Text.Encoding]::UTF8).Replace("`r`n","`n").TrimEnd([char[]]"`r`n")
  return Get-TextSha $preset
 }
 if($Relative -notmatch '^BepInEx/config/[^/]+\.cfg$'){return $null}
 $section='';$seen=@{};$entries=New-Object 'Collections.Generic.List[string]'
 foreach($raw in (Get-Content -LiteralPath $Path -Encoding UTF8)){
  $line=$raw.Trim()
  if(-not $line -or $line.StartsWith('#') -or $line.StartsWith(';')){continue}
  if($line.StartsWith('[') -and $line.EndsWith(']')){$section=$line;continue}
  $separator=$line.IndexOf('=')
  if($separator -lt 0){return $null}
  $key=$section+[char]0+$line.Substring(0,$separator).Trim()
  if($seen.ContainsKey($key)){return $null};$seen[$key]=$true
  $entries.Add($key+'='+$line.Substring($separator+1).Trim())
 }
 $sorted=$entries.ToArray();[Array]::Sort($sorted,[StringComparer]::Ordinal)
 return Get-TextSha ($sorted -join "`n")
}
function Test-MutableRuntimeConfig([string]$Path){
 return $Path -in @('SPT/SPT_Data/configs/http.json','SPT/user/launcher/config.json')
}
function Restore-RuntimeConfig([string]$Backup,[string]$Destination,[string]$Relative){
 $before=Read-JsonFile $Backup;$current=Read-JsonFile $Destination
 if($Relative -eq 'SPT/SPT_Data/configs/http.json'){$current.port=$before.port;$current.backendPort=$before.backendPort}
 elseif($Relative -eq 'SPT/user/launcher/config.json'){$current.GamePath=$before.GamePath;$current.Server.Url=$before.Server.Url}
 else{throw 'Not a mutable runtime config.'}
 Write-AtomicText $Destination (($current|ConvertTo-Json -Depth 50)+"`n")
}
function Assert-TargetStopped([string]$Root){
 $names=@('EscapeFromTarkov.exe','SPT.Server.exe','SPT.Launcher.exe')
 try{$running=@(Get-CimInstance Win32_Process | Where-Object {$_.Name -in $names})}catch{throw 'Cannot check running game processes; run in a normal PowerShell session.'}
 $paths=@((Join-Path $Root 'EscapeFromTarkov.exe'),(Join-Path $Root 'SPT\SPT.Server.exe'),(Join-Path $Root 'SPT\SPT.Launcher.exe'))
 foreach($p in $running){if($p.ExecutablePath -in $paths){throw ("请先关闭目标目录的游戏、启动器和服务器窗口。仍在运行：{0}（PID {1}）。" -f $p.Name,$p.ProcessId)}}
}
function Assert-BaseFiles([string]$Root,$Manifest){
 foreach($f in $Manifest.baseFiles){
  $path=Get-ContainedPath $Root $f.path
  if(-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Sha $path) -ne $f.sha256){throw "Incompatible or incomplete SPT/game base: $($f.path)"}
 }
}
function Write-AtomicText([string]$Path,[string]$Text){
 $parent=Split-Path -Parent $Path
 if(-not (Test-Path -LiteralPath $parent)){New-Item -ItemType Directory -Path $parent -Force | Out-Null}
 $temp=Join-Path $parent ('.ember-'+[Guid]::NewGuid().ToString('N')+'.tmp')
 [IO.File]::WriteAllText($temp,$Text,(New-Object Text.UTF8Encoding($false)))
 if(Test-Path -LiteralPath $Path){[IO.File]::Replace($temp,$Path,[NullString]::Value)}else{[IO.File]::Move($temp,$Path)}
}
function Write-AtomicFile([string]$Source,[string]$Path){
 $parent=Split-Path -Parent $Path
 if(-not (Test-Path -LiteralPath $parent)){New-Item -ItemType Directory -Path $parent -Force | Out-Null}
 $temp=Join-Path $parent ('.ember-'+[Guid]::NewGuid().ToString('N')+'.tmp')
 [IO.File]::Copy($Source,$temp,$false)
 if(Test-Path -LiteralPath $Path){[IO.File]::Replace($temp,$Path,[NullString]::Value)}else{[IO.File]::Move($temp,$Path)}
}
function Move-StagedFile([string]$Source,[string]$Path){
 $parent=Split-Path -Parent $Path
 if(-not (Test-Path -LiteralPath $parent)){New-Item -ItemType Directory -Path $parent -Force | Out-Null}
 if(Test-Path -LiteralPath $Path){[IO.File]::Replace($Source,$Path,[NullString]::Value)}else{[IO.File]::Move($Source,$Path)}
}
function Remove-SafeStage([string]$Root,[string]$Stage){
 $base=Get-ContainedPath $Root '.sptz/staging'
 $full=[IO.Path]::GetFullPath($Stage)
 if(-not $full.StartsWith($base+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Refusing to remove a path outside staging.'}
 $rel=$full.Substring(([IO.Path]::GetFullPath($Root).TrimEnd('\','/')+'\').Length)
 $null=Get-ContainedPath $Root $rel
 if(Test-Path -LiteralPath $full){Remove-Item -LiteralPath $full -Recurse -Force}
}
function Read-DependencyArchive([string]$Path,[string]$ExpectedLockHash){
 Add-Type -AssemblyName System.IO.Compression
 Add-Type -AssemblyName System.IO.Compression.FileSystem
 $zip=[IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($Path))
 try{
  $index=$zip.GetEntry('.pack/mods.lock.json')
  if($null -eq $index){throw 'Dependency archive has no lock manifest.'}
  $reader=New-Object IO.StreamReader($index.Open(),[Text.Encoding]::UTF8)
  try{$text=$reader.ReadToEnd()}finally{$reader.Dispose()}
  if((Get-TextSha $text) -ne $ExpectedLockHash){throw 'Dependency archive belongs to a different locked mod combination.'}
  $entries=New-Object 'Collections.Generic.Dictionary[string,object]' ([StringComparer]::OrdinalIgnoreCase)
  foreach($entry in $zip.Entries){
   if($entry.FullName -eq '.pack/mods.lock.json'){continue}
   $rel=Get-SafeRelative $entry.FullName
   Assert-ManagedPath $rel
   if($entries.ContainsKey($rel)){throw 'Dependency archive contains duplicate file names.'}
   $entries.Add($rel,$entry)
  }
  return [PSCustomObject]@{Zip=$zip;Entries=$entries}
 }catch{$zip.Dispose();throw}
}
