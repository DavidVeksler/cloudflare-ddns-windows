@echo off
REM Double-click launcher for the Cloudflare DDNS control panel.
REM Builds (if needed) and starts the WPF GUI via run-gui.ps1.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-gui.ps1"
if errorlevel 1 (
    echo.
    echo Something went wrong launching the control panel. See the messages above.
    pause
)
