@echo off
rem Schickt eine Testmeldung aufs Handy (ntfy und/oder Pushover, je nach atomkraftwerk.json)
cd /d "%~dp0"
echo. > "%~dp0handytest.flag"
echo Testmeldung angefordert - sie sollte in ein paar Sekunden auf dem Handy sein.
echo Ergebnis steht im atomkraftwerk.log ("Handy (ntfy): gesendet").
timeout /t 5 >nul
