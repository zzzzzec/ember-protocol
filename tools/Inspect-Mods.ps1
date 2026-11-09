param([Parameter(Mandatory=$true)][string]$GameRoot,[Parameter(Mandatory=$true)][string]$Output)
$ErrorActionPreference='Stop'
$root=(Resolve-Path -LiteralPath $GameRoot).Path
$null=[Reflection.Assembly]::LoadFrom((Join-Path $root 'SPT\Mono.Cecil.dll'))
$records=@()
foreach($base in @('SPT\user\mods','BepInEx\plugins')){
 foreach($file in Get-ChildItem -LiteralPath (Join-Path $root $base) -Recurse -Filter '*.dll' -File){
  try {
   $module=[Mono.Cecil.ModuleDefinition]::ReadModule($file.FullName)
   $info=[Diagnostics.FileVersionInfo]::GetVersionInfo($file.FullName)
   $row=[ordered]@{path=$file.FullName.Substring($root.Length+1).Replace('\','/');assemblyVersion=$module.Assembly.Name.Version.ToString();fileVersion=$info.FileVersion;productVersion=$info.ProductVersion;plugins=@()}
   foreach($type in $module.Types){
    foreach($attr in $type.CustomAttributes | Where-Object {$_.AttributeType.Name -eq 'BepInPlugin'}){
     $row.plugins+=@{guid=[string]$attr.ConstructorArguments[0].Value;name=[string]$attr.ConstructorArguments[1].Value;version=[string]$attr.ConstructorArguments[2].Value}
    }
   }
   $records+=$row
   $module.Dispose()
  } catch {
   if($_.Exception.InnerException -is [BadImageFormatException]){
    $records+=@{path=$file.FullName.Substring($root.Length+1).Replace('\','/');native=$true;plugins=@()}
   } else {throw "Could not inspect $($file.Name): $($_.Exception.Message)"}
  }
 }
}
$parent=Split-Path -Parent ([IO.Path]::GetFullPath($Output))
if(-not(Test-Path -LiteralPath $parent)){New-Item -ItemType Directory -Path $parent -Force|Out-Null}
$records | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $Output -Encoding UTF8
