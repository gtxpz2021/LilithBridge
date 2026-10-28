@echo off
setlocal DisableDelayedExpansion
if "%~3"=="" goto usage
if not "%~4"=="" goto usage
if not exist "%~1" (echo Python executable not found. 1>&2 & exit /b 2)
if not exist "%~2\config.json" (echo Local model directory is incomplete. 1>&2 & exit /b 3)
"%~1" -I -X utf8 -B -u "%~dp0semantic_worker.py" --model "%~2" --manifest "%~dp0model-manifest.json" --index "%~3"
exit /b %errorlevel%
:usage
echo Usage: Start-Worker.cmd "venv-python.exe" "local-model-directory" "independent-index-directory" 1>&2
echo Production integration must select semantic_worker.py, not this CMD. 1>&2
exit /b 2
