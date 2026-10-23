@echo off
rem Entfernt den Autostart wieder
net session >nul 2>&1
if %errorlevel% neq 0 (
  powershell -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)
powershell -NoProfile -Command "Unregister-ScheduledTask -TaskName 'Atomkraftwerk' -Confirm:$false"
echo Autostart entfernt. Fenster mit einer Taste schliessen.
pause >nul
