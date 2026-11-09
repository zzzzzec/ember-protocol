param([Parameter(Mandatory=$true)][string]$SourceRoot,[Parameter(Mandatory=$true)][string]$Output,[string]$PackageRoot)
if(-not $PackageRoot){$PackageRoot=$PSScriptRoot}
. (Join-Path $PSScriptRoot 'Common.ps1')
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root=(Resolve-Path -LiteralPath $SourceRoot).Path
$manifest=Read-JsonFile (Join-Path $PackageRoot 'package.json');$lockPath=Join-Path $PackageRoot 'mods.lock.json'
if((Get-Sha $lockPath) -ne $manifest.dependencyLockSha256){throw 'Dependency lock mismatch.'}
$lock=Read-JsonFile $lockPath
$out=[IO.Path]::GetFullPath($Output)
if(Test-Path -LiteralPath $out){throw 'Output archive already exists; choose another name.'}
$parent=Split-Path -Parent $out
if(-not(Test-Path -LiteralPath $parent)){New-Item -ItemType Directory -Path $parent -Force|Out-Null}
$temp=$out+'.partial'
if(Test-Path -LiteralPath $temp){throw 'A previous partial export exists; inspect it first.'}
try{
 $zip=[IO.Compression.ZipFile]::Open($temp,[IO.Compression.ZipArchiveMode]::Create)
 try{
  $null=[IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,$lockPath,'.pack/mods.lock.json',[IO.Compression.CompressionLevel]::Optimal)
  $component='';$count=0
  foreach($f in $lock.files){
   Assert-ManagedPath $f.path;$source=Get-ContainedPath $root $f.path
   if(-not(Test-Path -LiteralPath $source -PathType Leaf) -or (Get-Sha $source) -ne $f.sha256){throw "Dependency source changed: $($f.path)"}
   if($f.component -ne $component){$component=$f.component;Write-Host "Exporting $component"}
   $compression=[IO.Compression.CompressionLevel]::Fastest
   if([IO.Path]::GetExtension($source) -in @('.bundle','.png','.jpg','.ogg')){$compression=[IO.Compression.CompressionLevel]::NoCompression}
   $null=[IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,$source,$f.path,$compression);$count++
  }
 }finally{$zip.Dispose()}
 [IO.File]::Move($temp,$out)
 [PSCustomObject]@{status='Exported';files=$count;bytes=(Get-Item -LiteralPath $out).Length;output=$out;sha256=(Get-Sha $out)}|ConvertTo-Json
}catch{if(Test-Path -LiteralPath $temp){Remove-Item -LiteralPath $temp -Force};throw}
