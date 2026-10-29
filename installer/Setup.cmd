@echo off
setlocal
set "PS=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"

rem --min: internal relaunch, keeps the console out of the way
if /i "%~1"=="--min" goto run
rem with arguments: command line mode, unchanged
if not "%~1"=="" goto cli

rem double click: web UI first (modern page in the default browser)
start "" /min cmd /c ""%~f0" --min"
exit /b 0

:run
"%PS%" -NoLogo -NoProfile -ExecutionPolicy Bypass -STA -WindowStyle Hidden -File "%~dp0Setup.Web.ps1"
if errorlevel 900 goto gui
exit /b 0

:gui
rem fallback 1: WinForms window (no browser needed)
"%PS%" -NoLogo -NoProfile -ExecutionPolicy Bypass -STA -WindowStyle Hidden -File "%~dp0Setup.Gui.ps1"
if errorlevel 900 goto cli2
exit /b 0

:cli2
rem fallback 2: terminal menu
"%PS%" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Setup.ps1"
exit /b %ERRORLEVEL%

:cli
"%PS%" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Setup.ps1" %*
set "result=%ERRORLEVEL%"
exit /b %result%
