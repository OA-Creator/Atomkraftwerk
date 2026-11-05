using Atomkraftwerk;

// =====================================================================
// ATOMKRAFTWERK – Sammel-Programm (Schritt 2)
// Liest jede Sekunde CPU, GPU, RAM, Board (LibreHardwareMonitor) und die
// iCUE-Protokollierung, führt Min/Max/Ø seit Start, zählt die Laufzeit pro
// Tag, legt Minutenwerte in daten\JJJJ-MM-TT.csv ab und stellt alles
// unter http://localhost:8090 bereit.
// =====================================================================

var ordner = AppContext.BaseDirectory;
Directory.SetCurrentDirectory(ordner);
Log.Datei = Path.Combine(ordner, "atomkraftwerk.log");

using var einmal = new Mutex(true, "Global\\Atomkraftwerk_OAPC", out var ersteInstanz);
if (!ersteInstanz) { Console.WriteLine("Das Atomkraftwerk läuft schon."); Thread.Sleep(3000); return; }

var e = Einstellungen.Laden(ordner);
var anlage = new Anlage(e, ordner);
anlage.LaufzeitLaden();
Log.Schreiben("Atomkraftwerk startet.");

HardwareQuelle? hw = null;
try
{
    hw = new HardwareQuelle();
    hw.Oeffnen();
    anlage.CpuName = hw.CpuName; anlage.GpuName = hw.GpuName; anlage.BoardName = hw.BoardName;
}
catch (Exception ex) { Log.Schreiben("Hardware nicht lesbar (Admin-Rechte?): " + ex.Message); }

var icue = new IcueQuelle(e);
var pumpe = new PumpenSteuerung(e, ordner);
pumpe.Verbinden(hw);
icue.PumpeVomBoard = pumpe.Gefunden;
var stopp = new CancellationTokenSource();
var fritz = new FritzQuelle(e, anlage.DatenOrdner);
anlage.Strom = fritz.Stand;
_ = Task.Run(() => fritz.LaufenAsync(stopp.Token));
Console.CancelKeyPress += (_, a) => { a.Cancel = true; stopp.Cancel(); };

// Windows fährt herunter / meldet ab: letzten Stand dezent aufs Handy, dann sauber beenden.
// Sicher klappt das beim Herunterfahren über den Leitstand-Knopf; über das Startmenü nur, wenn Windows dem Programm noch Zeit lässt.
var letzterStandGesendet = 0;
async Task LetztenStandSendenAsync(string grund)
{
    if (Interlocked.Exchange(ref letzterStandGesendet, 1) == 1) return;
    try
    {
        anlage.LaufzeitZaehlen();
        var text = anlage.LetzterStandText();
        Log.Schreiben($"Letzter Stand ({grund}): {text}");
        await Handy.HinweisAsync(e.Handy, "PC aus " + DateTime.Now.ToString("HH:mm"), text);
    }
    catch (Exception ex) { Log.Schreiben("Letzter Stand: " + ex.Message); }
}
void BeimHerunterfahren(System.Runtime.InteropServices.PosixSignalContext k)
{
    k.Cancel = true;
    LetztenStandSendenAsync("Windows fährt herunter").Wait(TimeSpan.FromSeconds(5));
    stopp.Cancel();
}
using var sigTerm = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, BeimHerunterfahren);
using var sigQuit = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGQUIT, BeimHerunterfahren); // Abmelden

Server? server = null;
try
{
    server = new Server(anlage, e.Port, ordner);
    server.Auswertung = new Auswertung(anlage, fritz);
    server.Pumpe = pumpe;
    server.Einst = e;
    server.VorDemHerunterfahren = () => LetztenStandSendenAsync("im Leitstand heruntergefahren");
    _ = server.LaufenAsync(stopp.Token);
    Log.Schreiben("Leitstand erreichbar: " + server.Adresse);
}
catch (Exception ex) { Log.Schreiben("Webserver startet nicht: " + ex.Message); }

var alarm = new AlarmMelder(e, ordner);

var letzteMinute = DateTime.Now.Minute;
var intervall = TimeSpan.FromSeconds(Math.Max(1, e.IntervallSekunden));

// Nach Ruhezustand/Energiesparen setzt das BIOS den Pumpenanschluss auf seine eigene Einstellung (Full Speed),
// und die Board-Steuerung antwortet nicht mehr. Dann Hardware neu öffnen und die Pumpe neu übernehmen (seit 05.11.2026).
var letzterLauf = DateTime.Now;
var steuerungWegSeit = DateTime.MinValue;
var letzteUebernahme = DateTime.MinValue;
void HardwareNeuUebernehmen(string grund)
{
    letzteUebernahme = DateTime.Now; steuerungWegSeit = DateTime.MinValue;
    Log.Schreiben($"{grund} – öffne die Hardware neu und übernehme die Pumpe wieder.");
    try { hw?.Dispose(); } catch { }
    try
    {
        hw = new HardwareQuelle();
        hw.Oeffnen();
        anlage.CpuName = hw.CpuName; anlage.GpuName = hw.GpuName; anlage.BoardName = hw.BoardName;
    }
    catch (Exception ex) { hw = null; Log.Schreiben("Hardware nach dem Aufwachen nicht lesbar: " + ex.Message); }
    pumpe.Verbinden(hw);
}

while (!stopp.IsCancellationRequested)
{
    var pause = DateTime.Now - letzterLauf;
    letzterLauf = DateTime.Now;
    if (pause.TotalSeconds > 30)
        HardwareNeuUebernehmen($"Aufgewacht nach {Math.Round(pause.TotalMinutes, 1)} min Pause (Ruhezustand)");
    else if (pumpe.SteuerungWeg)
    {
        if (steuerungWegSeit == DateTime.MinValue) steuerungWegSeit = DateTime.Now;
        if ((DateTime.Now - steuerungWegSeit).TotalSeconds >= 15 && (DateTime.Now - letzteUebernahme).TotalMinutes >= 5)
            HardwareNeuUebernehmen("Pumpensteuerung antwortet seit 15 s nicht");
    }
    else steuerungWegSeit = DateTime.MinValue;

    // Sanft beenden: "Atomkraftwerk beenden.bat" legt diese Datei an
    var flagge = Path.Combine(ordner, "beenden.flag");
    if (File.Exists(flagge)) { try { File.Delete(flagge); } catch { } Log.Schreiben("Beenden angefordert."); break; }
    try
    {
        hw?.Lesen(anlage);
        icue.Lesen(anlage);
        pumpe.Arbeiten(anlage);
        anlage.Wert("strom.watt").Setzen(fritz.Watt);
        anlage.Wert("strom.temp").Setzen(fritz.Temp);
        anlage.IcueDatei = icue.Datei; anlage.IcueStand = icue.Stand; anlage.IcueLaeuft = icue.Laeuft;
        anlage.Bewerten();
        alarm.Pruefen(anlage);
        anlage.LaufzeitZaehlen();
        if (DateTime.Now.Minute != letzteMinute)
        {
            letzteMinute = DateTime.Now.Minute;
            anlage.MinuteSpeichern();
        }
    }
    catch (Exception ex) { Log.Schreiben("Fehler im Messlauf: " + ex.Message); }
    try { await Task.Delay(intervall, stopp.Token); } catch { break; }
}

anlage.MinuteSpeichern();
pumpe.Freigeben();
server?.Stop();
hw?.Dispose();
Log.Schreiben("Atomkraftwerk beendet.");
