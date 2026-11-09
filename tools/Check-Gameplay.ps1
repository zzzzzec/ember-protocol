param([string]$GameRoot,[string]$DotNet)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
if(-not $GameRoot -or -not $DotNet){
    $settings=Get-Content -Raw -LiteralPath (Join-Path $repo '.local\workspace.json') -Encoding UTF8 | ConvertFrom-Json
    if(-not $GameRoot){$GameRoot=$settings.referenceRoot}
    if(-not $DotNet){$DotNet=$settings.dotNet}
}
$project=Join-Path $repo 'tests\GameplayChecks\GameplayChecks.csproj'
$report=Join-Path $repo 'artifacts\gameplay-rule-checks.json'
& $DotNet run --project $project --configuration Release ('-p:GameRoot='+$GameRoot) -- $repo $report $GameRoot
if($LASTEXITCODE -ne 0){throw 'Gameplay rule checks failed.'}

$nativePowerShell=Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
& $nativePowerShell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Check-ClientHooks.ps1') -GameRoot $GameRoot -Report (Join-Path $repo 'artifacts\client-hook-bindings.json')
if($LASTEXITCODE -ne 0){throw 'Native client bindings failed.'}
