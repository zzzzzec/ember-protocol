param([switch]$ServerOnly)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$server=Join-Path $root 'SPT\SPT.Server.exe';$launcher=Join-Path $root 'SPT\SPT.Launcher.exe'
if(-not(Test-Path -LiteralPath (Join-Path $root 'EscapeFromTarkov.exe')) -or -not(Test-Path -LiteralPath $server)){throw 'This helper must be installed in an SPT game directory.'}
if(-not $ServerOnly){
 Write-Output '请手动依次打开服务器和启动器，关闭时直接关闭各自窗口：'
 Write-Output $server
 Write-Output $launcher
 return
}
$state=Get-Content -Raw -LiteralPath (Join-Path $root '.sptz\installed.json') -Encoding UTF8|ConvertFrom-Json
if($state.instanceKind -ne 'Development'){throw 'Background ServerOnly startup is restricted to the Development instance. Open SPT.Server.exe manually for Player.'}
$http=Get-Content -Raw -LiteralPath (Join-Path $root 'SPT\SPT_Data\configs\http.json') -Encoding UTF8|ConvertFrom-Json
$port=[int]$http.port
$running=@(Get-CimInstance Win32_Process -Filter "Name='SPT.Server.exe'"|Where-Object {$_.ExecutablePath -eq $server})
if(-not $running.Count){
 $tcp=New-Object Net.Sockets.TcpClient
 try{$tcp.Connect('127.0.0.1',$port);throw 'The selected port belongs to another process. Choose a free port for this instance.'}catch [Net.Sockets.SocketException]{}finally{$tcp.Dispose()}
 $launchUtc=[DateTime]::UtcNow
 $process=Start-Process -FilePath $server -WorkingDirectory (Split-Path -Parent $server) -WindowStyle Hidden -PassThru
}
$startupErrors=Join-Path $root 'SPT\user\mods\Codex-ZombieSurvival\startup-errors.jsonl'
$ready=$false
for($i=0;$i -lt 120;$i++){
 $tcp=New-Object Net.Sockets.TcpClient
 try{$connection=$tcp.ConnectAsync('127.0.0.1',$port);if($connection.Wait(250) -and $tcp.Connected){$ready=$true;break}}catch{}finally{$tcp.Dispose()}
 if($process -and (Test-Path -LiteralPath $startupErrors) -and (Get-Item -LiteralPath $startupErrors).LastWriteTimeUtc -ge $launchUtc){Stop-Process -Id $process.Id -ErrorAction SilentlyContinue;throw ('后端启动失败，详细错误已保存在：'+$startupErrors)}
 if($process -and $process.HasExited){throw 'Backend exited; inspect its server log.'}
 Start-Sleep -Milliseconds 500
}
if(-not $ready){throw 'Backend did not become ready.'}