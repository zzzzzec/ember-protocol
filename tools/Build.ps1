param([Parameter(Mandatory=$true)][string]$GameRoot,[string]$DotNet='dotnet',[string]$Python='python')
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
& $Python (Join-Path $PSScriptRoot 'pack.py') build --game-root $GameRoot --dotnet $DotNet
if($LASTEXITCODE -ne 0){throw 'Build or package validation failed.'}
