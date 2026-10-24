@echo off
rem Zeigt das Alarmfenster zum Ausprobieren - der PC wird dabei NICHT heruntergefahren
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -WindowStyle Hidden -File "%~dp0alarmfenster.ps1" -Sekunden 45 -Test
