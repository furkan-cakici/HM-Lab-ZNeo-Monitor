@echo off
cd /d "%~dp0"
net session >nul 2>&1
if %errorlevel% neq 0 (
    powershell -NoProfile -Command "Start-Process '%~f0' -Verb RunAs"
    exit /b
)

echo =======================================================
echo  HM Lab Z-Neo Monitor - Configuring Auto-Start...
echo =======================================================

schtasks /create /tn "HMHubScreenMonitor" /tr "\"%ProgramData%\HMHubMonitor\ZNeoScreenService.exe\"" /sc onlogon /rl HIGHEST /f

echo.
echo [OK] Service registered to start automatically upon Windows logon!
pause
