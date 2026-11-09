@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\Workspace.ps1" -Action Install -Instance Development
pause
