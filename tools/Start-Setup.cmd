@echo off

if not exist "%~dp0setup\LilithBridge-Installer.exe" (

  echo Installer not found. Extract the complete ZIP before opening Start-Setup.cmd.

  exit /b 1

)

start "" /D "%~dp0setup" "%~dp0setup\LilithBridge-Installer.exe"

