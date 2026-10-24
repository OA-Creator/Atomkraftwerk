# Atomkraftwerk – Alarmfenster (wird vom Programm gestartet, sobald ein Wert rot wird)
# Test ohne Herunterfahren: "Alarmfenster testen.bat"
param(
  [string]$Datei = "",
  [int]$Sekunden = 45,
  [switch]$Test
)
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

$ordner = Split-Path -Parent $MyInvocation.MyCommand.Path
$log = Join-Path $ordner "atomkraftwerk.log"
function Notiz($t) { try { Add-Content -Path $log -Value ("{0}  {1}" -f (Get-Date -Format "dd.MM.yyyy HH:mm:ss"), $t) -Encoding UTF8 } catch {} }

$werte = "Testalarm: Wasser am Flow 41,2 °C"
if ($Datei -and (Test-Path $Datei)) { $werte = (Get-Content -Path $Datei -Raw -Encoding UTF8).Trim() }

$rot   = [System.Drawing.Color]::FromArgb(255,107,112)
$grund = [System.Drawing.Color]::FromArgb(11,14,17)
$panel = [System.Drawing.Color]::FromArgb(42,20,22)
$text  = [System.Drawing.Color]::FromArgb(230,234,238)
$leise = [System.Drawing.Color]::FromArgb(160,170,180)

$f = New-Object System.Windows.Forms.Form
$f.Text = "Atomkraftwerk – ALARM"
$f.FormBorderStyle = "None"
$f.StartPosition = "CenterScreen"
$f.Size = New-Object System.Drawing.Size(620, 420)
$f.BackColor = $rot
$f.TopMost = $true
$f.ShowInTaskbar = $true
$f.KeyPreview = $true

$innen = New-Object System.Windows.Forms.Panel
$innen.Location = New-Object System.Drawing.Point(4,4)
$innen.Size = New-Object System.Drawing.Size(612, 412)
$innen.BackColor = $grund
$f.Controls.Add($innen)

$kopf = New-Object System.Windows.Forms.Label
$kopf.Text = "⚠  ATOMKRAFTWERK – ALARM" + $(if ($Test) { "  (TEST)" } else { "" })
$kopf.Font = New-Object System.Drawing.Font("Segoe UI", 20, [System.Drawing.FontStyle]::Bold)
$kopf.ForeColor = $rot
$kopf.AutoSize = $false
$kopf.Location = New-Object System.Drawing.Point(24, 18)
$kopf.Size = New-Object System.Drawing.Size(564, 44)
$innen.Controls.Add($kopf)

$liste = New-Object System.Windows.Forms.Label
$liste.Text = $werte
$liste.Font = New-Object System.Drawing.Font("Segoe UI", 15, [System.Drawing.FontStyle]::Bold)
$liste.ForeColor = $text
$liste.BackColor = $panel
$liste.Padding = New-Object System.Windows.Forms.Padding(12,8,12,8)
$liste.Location = New-Object System.Drawing.Point(24, 72)
$liste.Size = New-Object System.Drawing.Size(564, 96)
$innen.Controls.Add($liste)

$uhr = New-Object System.Windows.Forms.Label
$uhr.Font = New-Object System.Drawing.Font("Segoe UI", 16, [System.Drawing.FontStyle]::Bold)
$uhr.ForeColor = $rot
$uhr.Location = New-Object System.Drawing.Point(24, 180)
$uhr.Size = New-Object System.Drawing.Size(564, 34)
$innen.Controls.Add($uhr)

$hinweis = New-Object System.Windows.Forms.Label
$hinweis.Font = New-Object System.Drawing.Font("Segoe UI", 11)
$hinweis.ForeColor = $leise
$hinweis.Location = New-Object System.Drawing.Point(24, 216)
$hinweis.Size = New-Object System.Drawing.Size(564, 80)
$innen.Controls.Add($hinweis)
if ($Sekunden -gt 0) {
  $hinweis.Text = "Damit die Hardware keinen Schaden nimmt, fährt der PC automatisch herunter, wenn du nicht reagierst. Offene Programme werden dabei OHNE Speichern beendet – jetzt speichern oder abbrechen."
} else {
  $hinweis.Text = "Prüfe die Kühlung. Mit dem roten Knopf fährst du den PC sofort herunter."
}

$aus = New-Object System.Windows.Forms.Button
$aus.Text = "Jetzt herunterfahren"
$aus.Font = New-Object System.Drawing.Font("Segoe UI", 13, [System.Drawing.FontStyle]::Bold)
$aus.BackColor = [System.Drawing.Color]::FromArgb(194,38,44)
$aus.ForeColor = [System.Drawing.Color]::White
$aus.FlatStyle = "Flat"
$aus.FlatAppearance.BorderSize = 0
$aus.Location = New-Object System.Drawing.Point(24, 320)
$aus.Size = New-Object System.Drawing.Size(270, 60)
$innen.Controls.Add($aus)

$weg = New-Object System.Windows.Forms.Button
$weg.Text = "Abbrechen – nicht herunterfahren"
$weg.Font = New-Object System.Drawing.Font("Segoe UI", 11)
$weg.BackColor = [System.Drawing.Color]::FromArgb(18,23,28)
$weg.ForeColor = $text
$weg.FlatStyle = "Flat"
$weg.FlatAppearance.BorderColor = [System.Drawing.Color]::FromArgb(90,102,112)
$weg.Location = New-Object System.Drawing.Point(318, 320)
$weg.Size = New-Object System.Drawing.Size(270, 60)
$innen.Controls.Add($weg)

$script:rest = $Sekunden
function Uhr { if ($Sekunden -gt 0) { $uhr.Text = "Herunterfahren in $($script:rest) Sekunden" } else { $uhr.Text = "" } }
Uhr

function Herunterfahren($grund) {
  $t.Stop()
  if ($Test) { Notiz "Alarmfenster (Test): $grund – würde jetzt herunterfahren"; [System.Windows.Forms.MessageBox]::Show("TEST: Hier würde der PC jetzt herunterfahren.", "Atomkraftwerk") | Out-Null; $f.Close(); return }
  Notiz "Alarmfenster: $grund – PC fährt herunter"
  Start-Process -FilePath "shutdown.exe" -ArgumentList "/s","/f","/t","0" -WindowStyle Hidden
  $f.Close()
}

$t = New-Object System.Windows.Forms.Timer
$t.Interval = 1000
$t.Add_Tick({
  if ($Sekunden -le 0) { return }
  $script:rest--
  Uhr
  if ($script:rest -le 10) { [System.Media.SystemSounds]::Exclamation.Play() }
  if ($script:rest -le 0) { Herunterfahren "keine Reaktion nach $Sekunden s" }
})
$aus.Add_Click({ Herunterfahren "Knopf gedrückt" })
$weg.Add_Click({ $t.Stop(); Notiz "Alarmfenster: abgebrochen"; $f.Close() })
$f.Add_Shown({ $f.Activate(); [System.Media.SystemSounds]::Hand.Play(); $t.Start() })

[void]$f.ShowDialog()
