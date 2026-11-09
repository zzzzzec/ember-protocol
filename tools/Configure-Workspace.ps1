[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$PlayerRoot,
    [Parameter(Mandatory=$true)][string]$DevelopmentRoot,
    [string]$ReferenceRoot,
    [string]$DotNet='dotnet',
    [string]$Python='python',
    [string]$DependencyArchive,
    [ValidateRange(1,65535)][int]$PlayerPort=6969,
    [ValidateRange(1,65535)][int]$DevelopmentPort=6974
)
. (Join-Path $PSScriptRoot 'Common.ps1')
$repo=Split-Path -Parent $PSScriptRoot
$player=(Resolve-Path -LiteralPath $PlayerRoot).Path.TrimEnd('\','/')
$development=(Resolve-Path -LiteralPath $DevelopmentRoot).Path.TrimEnd('\','/')
if($player -eq $development -or $player.StartsWith($development+'\',[StringComparison]::OrdinalIgnoreCase) -or $development.StartsWith($player+'\',[StringComparison]::OrdinalIgnoreCase)){
    throw 'Player and Development must use separate, non-nested directories.'
}
if($PlayerPort -eq $DevelopmentPort){throw 'Player and Development ports must differ.'}
if(-not $ReferenceRoot){$ReferenceRoot=$player}
$reference=(Resolve-Path -LiteralPath $ReferenceRoot).Path
foreach($game in @($player,$development,$reference)){
    if(-not(Test-Path -LiteralPath (Join-Path $game 'SPT\SPT.Server.exe'))){throw "Not an SPT instance: $game"}
}
$dotnetPath=(Get-Command -Name $DotNet -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$pythonPath=(Get-Command -Name $Python -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
if(-not $DependencyArchive){$DependencyArchive=Join-Path $repo 'artifacts\ember-dependencies.zip'}
$settings=[ordered]@{
    formatVersion=1
    referenceRoot=$reference
    dotNet=$dotnetPath
    python=$pythonPath
    dependencyArchive=[IO.Path]::GetFullPath($DependencyArchive)
    instances=[ordered]@{
        Player=[ordered]@{root=$player;port=$PlayerPort}
        Development=[ordered]@{root=$development;port=$DevelopmentPort}
    }
}
$path=Join-Path $repo '.local\workspace.json'
Write-AtomicText $path (($settings|ConvertTo-Json -Depth 6)+"`n")
[PSCustomObject]@{status='Configured';config=$path;playerPort=$PlayerPort;developmentPort=$DevelopmentPort}|ConvertTo-Json
