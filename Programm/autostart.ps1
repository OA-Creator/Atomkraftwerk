# Richtet die Aufgabe "Atomkraftwerk" ein: Start bei Anmeldung, mit hoechsten Rechten, ohne Zeitlimit
$vbs = Join-Path $PSScriptRoot "unsichtbar.vbs"
$aktion = New-ScheduledTaskAction -Execute "wscript.exe" -Argument "`"$vbs`"" -WorkingDirectory $PSScriptRoot
$ausloeser = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$ausloeser.Delay = "PT30S"
$wer = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Highest
$einst = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew
Register-ScheduledTask -TaskName "Atomkraftwerk" -Action $aktion -Trigger $ausloeser -Principal $wer -Settings $einst -Force | Out-Null
Start-ScheduledTask -TaskName "Atomkraftwerk"
Write-Host "Autostart eingerichtet und Atomkraftwerk gestartet."
