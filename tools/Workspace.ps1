[CmdletBinding()]
param(
    [ValidateSet('Build','Plan','Install','Verify','Rollback','Start','BackendCheck','Check')][string]$Action='Build',
    [ValidateSet('Player','Development')][string]$Instance='Development'
)
. (Join-Path $PSScriptRoot 'Common.ps1')
$repo=Split-Path -Parent $PSScriptRoot
$nativePowerShell=Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
function Invoke-Checked([string]$Executable,[string[]]$CommandArgs){
    & $Executable @CommandArgs
    if($LASTEXITCODE -ne 0){throw "Command failed with exit code $LASTEXITCODE : $Executable"}
}
$configPath=Join-Path $repo '.local\workspace.json'
if($Action -eq 'Check'){
    $python='python'
    if(Test-Path -LiteralPath $configPath){$python=(Read-JsonFile $configPath).python}
    Push-Location $repo
    try{
        Invoke-Checked $python @((Join-Path $PSScriptRoot 'pack.py'),'validate')
        Invoke-Checked $python @('-m','unittest','discover','-s','tests','-v')
        if(Test-Path -LiteralPath $configPath){
            $localSettings=Read-JsonFile $configPath
            Invoke-Checked $nativePowerShell @('-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $PSScriptRoot 'Check-Gameplay.ps1'),'-GameRoot',$localSettings.referenceRoot,'-DotNet',$localSettings.dotNet)
        }
    }finally{Pop-Location}
    return
}
if(-not(Test-Path -LiteralPath $configPath)){throw 'Run tools/Configure-Workspace.ps1 once to select the local instances and toolchain.'}
$config=Read-JsonFile $configPath
if($config.formatVersion -ne 1){throw 'Unsupported workspace config.'}
$player=[IO.Path]::GetFullPath($config.instances.Player.root).TrimEnd('\','/')
$development=[IO.Path]::GetFullPath($config.instances.Development.root).TrimEnd('\','/')
if($player -eq $development -or $player.StartsWith($development+'\',[StringComparison]::OrdinalIgnoreCase) -or $development.StartsWith($player+'\',[StringComparison]::OrdinalIgnoreCase) -or $config.instances.Player.port -eq $config.instances.Development.port){
    throw 'Player and Development need separate directories and ports.'
}
if($Action -eq 'BackendCheck' -and $Instance -ne 'Development'){throw 'BackendCheck is restricted to Development.'}
$target=$config.instances.$Instance
$gameRoot=(Resolve-Path -LiteralPath $target.root).Path
$meta=Read-JsonFile (Join-Path $repo 'pack.json')
$package=Join-Path $repo ('dist\ember-protocol-'+$meta.version)
function Build-CurrentPackage {
    Invoke-Checked $config.python @((Join-Path $PSScriptRoot 'pack.py'),'build','--game-root',$config.referenceRoot,'--dotnet',$config.dotNet)
}
if($Action -in @('Install','Rollback')){Assert-TargetStopped $gameRoot}
if($Action -in @('Build','Plan','Install')){Build-CurrentPackage}
if($Action -eq 'Build'){return}
if($Action -in @('Plan','Install','Verify','Rollback') -and -not(Test-Path -LiteralPath (Join-Path $package 'package.json'))){throw 'Build the current release first.'}
switch($Action){
    {$_ -in @('Plan','Install')} {
        $installArgs=@('-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $package 'Install.ps1'),'-GameRoot',$gameRoot,'-BackendPort',[string]$target.port,'-InstanceKind',$Instance)
        if(Test-Path -LiteralPath $config.dependencyArchive -PathType Leaf){$installArgs+=@('-DependencyArchive',$config.dependencyArchive)}
        else{$installArgs+=@('-DependencyRoot',$config.referenceRoot)}
        if($Action -eq 'Plan'){$installArgs+='-Plan'}
        Invoke-Checked $nativePowerShell $installArgs
        if($Action -eq 'Install'){
            Invoke-Checked $nativePowerShell @('-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $package 'Verify.ps1'),'-GameRoot',$gameRoot)
        }
    }
    'Verify' {Invoke-Checked $nativePowerShell @('-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $package 'Verify.ps1'),'-GameRoot',$gameRoot)}
    'Rollback' {Invoke-Checked $nativePowerShell @('-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $package 'Rollback.ps1'),'-GameRoot',$gameRoot)}
    'Start' {
        if($Instance -eq 'Player'){
            Write-Output 'Open SPT/SPT.Server.exe manually, then SPT/SPT.Launcher.exe. Close their windows to stop them.'
            Write-Output (Join-Path $gameRoot 'SPT\SPT.Server.exe')
            Write-Output (Join-Path $gameRoot 'SPT\SPT.Launcher.exe')
            return
        }
        $launch=Join-Path $gameRoot 'Tools\EmberProtocol\Launch.ps1'
        if(-not(Test-Path -LiteralPath $launch)){throw 'Install the pack to this instance before starting it.'}
        $launchArgs=@('-NoProfile','-ExecutionPolicy','Bypass','-File',$launch)
        if($Instance -eq 'Development'){$launchArgs+='-ServerOnly'}
        Invoke-Checked $nativePowerShell $launchArgs
    }
    'BackendCheck' {
        Invoke-Checked $config.python @((Join-Path $PSScriptRoot 'native_check.py'),'--game-root',$gameRoot,'--port',[string]$target.port,'--report',(Join-Path $repo 'artifacts\native-verification.json'))
        Invoke-Checked $config.python @((Join-Path $PSScriptRoot 'native_growth_check.py'),'--game-root',$gameRoot,'--port',[string]$target.port,'--report',(Join-Path $repo 'artifacts\native-growth-verification.json'))
        Invoke-Checked $config.python @((Join-Path $PSScriptRoot 'native_restock_check.py'),'--game-root',$gameRoot,'--port',[string]$target.port,'--report',(Join-Path $repo 'artifacts\native-restock-verification.json'))
        Invoke-Checked $config.python @((Join-Path $PSScriptRoot 'native_currency_check.py'),'--game-root',$gameRoot,'--port',[string]$target.port,'--report',(Join-Path $repo 'artifacts\native-currency-verification.json'))
        Invoke-Checked $config.python @((Join-Path $PSScriptRoot 'native_story_check.py'),'--game-root',$gameRoot,'--port',[string]$target.port,'--report',(Join-Path $repo 'artifacts\native-story-verification.json'))
        Invoke-Checked $config.python @((Join-Path $PSScriptRoot 'native_repair_check.py'),'--game-root',$gameRoot,'--port',[string]$target.port,'--report',(Join-Path $repo 'artifacts\native-repair-verification.json'))
        Invoke-Checked $config.python @((Join-Path $PSScriptRoot 'native_threat_check.py'),'--game-root',$gameRoot,'--port',[string]$target.port,'--report',(Join-Path $repo 'artifacts\native-threat-verification.json'))
    }
}
