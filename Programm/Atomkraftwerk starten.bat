@echo off
rem ATOMKRAFTWERK - startet sich selbst mit Administratorrechten (noetig fuer die Board-Sensoren)
net session >nul 2>&1
if %errorlevel% neq 0 (
  powershell -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)
cd /d "%~dp0"
title Atomkraftwerk
start "" cmd /c "timeout /t 4 >nul & start http://localhost:8090"
"C:\Program Files\dotnet\dotnet.exe" Atomkraftwerk.dll
echo.
echo Atomkraftwerk beendet - Fenster mit einer Taste schliessen.
pause >nul
