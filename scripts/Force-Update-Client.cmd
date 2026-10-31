@echo off
setlocal
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Force-Update-Client.ps1"
if errorlevel 1 (
  echo.
  echo Update failed. See the message above and %%ProgramData%%\RemotePrint\install.log.
  pause
  exit /b 1
)
endlocal
