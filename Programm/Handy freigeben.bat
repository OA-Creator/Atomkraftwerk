@echo off
rem Gibt den Leitstand fuer das Handy im Heimnetz frei - startet sich selbst mit Administratorrechten
net session >nul 2>&1
if %errorlevel% neq 0 (
  powershell -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0handy.ps1"
echo.
echo Fenster mit einer Taste schliessen.
pause >nul
