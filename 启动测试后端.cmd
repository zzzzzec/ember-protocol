@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\Workspace.ps1" -Action Start -Instance Development
if errorlevel 1 pause
