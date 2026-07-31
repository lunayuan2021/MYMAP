@echo off
cd /d "%~dp0"
set "URL=http://127.0.0.1:9091/litchi_delivery_replay/"
where powershell >nul 2>nul
if %errorlevel%==0 (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0start.ps1"
  goto :end
)
where py >nul 2>nul
if %errorlevel%==0 (
  start "" "%URL%"
  py -3 lychee_server.py --port 9091 --host 0.0.0.0
  goto :end
)
where python >nul 2>nul
if %errorlevel%==0 (
  start "" "%URL%"
  python lychee_server.py --port 9091 --host 0.0.0.0
  goto :end
)
echo Could not find PowerShell, py, or python. Please run this folder through any local HTTP server.
:end
pause
