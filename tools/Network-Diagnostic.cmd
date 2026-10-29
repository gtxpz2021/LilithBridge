@echo off

if not exist "%~dp0setup\LilithBridge-Installer.exe" (

  echo Extract the complete installer ZIP before opening this diagnostic.

  exit /b 1

)

start "" /D "%~dp0setup" "%~dp0setup\LilithBridge-Installer.exe" --network-diagnostic

