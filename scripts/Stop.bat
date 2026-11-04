@echo off
net session >nul 2>&1
if %errorlevel% neq 0 (
    powershell -NoProfile -Command "Start-Process '%~f0' -Verb RunAs"
    exit /b
)

echo =======================================================
echo  HM Lab Z-Neo Monitor - Stopping Service...
echo =======================================================
taskkill /F /IM ZNeoScreenService.exe >nul 2>&1
echo.
echo [OK] Monitor service stopped.
timeout /t 2 >nul
