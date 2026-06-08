@echo off
setlocal

set PACKAGE_ROOT=\\fileserver.example.local\software\windows-soft-inventory
set SERVER_URL=http://inventory.example.local:8080/api/v1/inventory
set INTERVAL_HOURS=6

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%PACKAGE_ROOT%\Deploy-ClientGpo.ps1" -ServerUrl "%SERVER_URL%" -IntervalHours %INTERVAL_HOURS%

exit /b %ERRORLEVEL%
