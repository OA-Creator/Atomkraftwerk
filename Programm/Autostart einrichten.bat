@echo off
rem Richtet den Autostart ein (einmalig) - startet sich selbst mit Administratorrechten
net session >nul 2>&1
if %errorlevel% neq 0 (
  powershell -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0autostart.ps1"
echo.
echo Fertig. Der Leitstand ist gleich unter http://localhost:8090 erreichbar.
echo Fenster mit einer Taste schliessen.
pause >nul
