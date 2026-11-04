@echo off
cd /d "%~dp0"
net session >nul 2>&1
if %errorlevel% neq 0 (
    powershell -NoProfile -Command "Start-Process '%~f0' -Verb RunAs"
    exit /b
)

echo =======================================================
echo  HM Lab Z-Neo Monitor - Starting Service...
echo =======================================================

taskkill /F /IM ZNeoScreenService.exe >nul 2>&1
timeout /t 1 /nobreak >nul

if not exist "%ProgramData%\HMHubMonitor" mkdir "%ProgramData%\HMHubMonitor"

xcopy /s /y /q "%~dp0*" "%ProgramData%\HMHubMonitor\" >nul 2>&1

start "" "%ProgramData%\HMHubMonitor\ZNeoScreenService.exe"

echo.
echo [OK] Monitor service successfully launched!
timeout /t 2 /nobreak >nul
