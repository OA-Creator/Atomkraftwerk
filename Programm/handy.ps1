# Gibt den Leitstand (Port 8090) in der Windows-Firewall frei - nur fuer das Heimnetz (Profil "Privat")
Remove-NetFirewallRule -DisplayName "Atomkraftwerk Leitstand" -ErrorAction SilentlyContinue
New-NetFirewallRule -DisplayName "Atomkraftwerk Leitstand" -Direction Inbound -Protocol TCP -LocalPort 8090 -Action Allow -Profile Private | Out-Null
Write-Host ""
Write-Host "Firewall-Freigabe fuer das Heimnetz ist eingerichtet."
Write-Host ""
$profil = Get-NetConnectionProfile | Select-Object -First 1
if ($profil -and $profil.NetworkCategory -ne "Private") {
  Write-Host "ACHTUNG: Dein Netzwerk '$($profil.Name)' steht auf '$($profil.NetworkCategory)'." -ForegroundColor Yellow
  Write-Host "Damit das Handy zugreifen kann, muss es auf 'Privat' stehen:" -ForegroundColor Yellow
  Write-Host "Einstellungen > Netzwerk und Internet > Ethernet > Netzwerkprofiltyp: Privates Netzwerk" -ForegroundColor Yellow
  Write-Host ""
}
Write-Host "Am Handy (im WLAN zu Hause) diese Adresse im Browser oeffnen:" -ForegroundColor Cyan
Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.IPAddress -like "192.168.*" -or $_.IPAddress -like "10.*" -or $_.IPAddress -like "172.*" } |
  Where-Object { $_.PrefixOrigin -ne "WellKnown" } |
  ForEach-Object { Write-Host ("   http://" + $_.IPAddress + ":8090") -ForegroundColor Cyan }
Write-Host ""
Write-Host "Dann im Browser-Menue 'Zum Startbildschirm hinzufuegen' waehlen."
