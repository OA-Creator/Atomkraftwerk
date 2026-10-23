@echo off
rem ATOMKRAFTWERK - (neu) starten; eine Verknuepfung davon auf den Desktop legen
net session >nul 2>&1
if %errorlevel% neq 0 (
  powershell -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)
set ORDNER=%~dp0
echo Atomkraftwerk wird neu gestartet ...
rem laufendes Atomkraftwerk sauber beenden, danach liegengebliebene Flagge entfernen
echo. > "%ORDNER%beenden.flag"
timeout /t 5 >nul
del "%ORDNER%beenden.flag" 2>nul
powershell -NoProfile -ExecutionPolicy Bypass -File "%ORDNER%autostart.ps1"
echo Fertig.
timeout /t 3 >nul
