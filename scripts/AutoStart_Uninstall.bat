@echo off
net session >nul 2>&1
if %errorlevel% neq 0 (
    powershell -NoProfile -Command "Start-Process '%~f0' -Verb RunAs"
    exit /b
)

schtasks /delete /tn "HMHubScreenMonitor" /f
echo [OK] Auto-start scheduled task removed.
pause
