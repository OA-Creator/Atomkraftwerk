@echo off
rem Beendet das laufende Atomkraftwerk sauber (letzte Minute wird noch gespeichert)
cd /d "%~dp0"
echo. > beenden.flag
echo Atomkraftwerk wird beendet...
timeout /t 3 >nul
