using System.Diagnostics;
using System.Text;

namespace Atomkraftwerk;

// ---------------------------------------------------------------
// Alarmfenster: Wird ein Wert rot, springt ein Fenster über allem auf
// (alarmfenster.ps1, Windows PowerShell) mit Countdown und Knopf
// "Jetzt herunterfahren". Nach "Abbrechen" meldet sich derselbe Wert
// erst wieder, wenn er zwischendurch einmal nicht rot war.
// ---------------------------------------------------------------
public sealed class AlarmMelder
{
    private readonly Einstellungen _e;
    private readonly string _ordner;
    private readonly HashSet<string> _gemeldet = new();
    private Process? _fenster;

    public AlarmMelder(Einstellungen e, string ordner) { _e = e; _ordner = ordner; }

    public void Pruefen(Anlage anlage)
    {
        var rot = anlage.RoteWerte();
        // Test fürs Handy: "Handy-Alarm testen.bat" legt diese Datei an
        var testFlagge = Path.Combine(_ordner, "handytest.flag");
        if (File.Exists(testFlagge))
        {
            try { File.Delete(testFlagge); } catch { }
            Log.Schreiben("Handy-Test angefordert.");
            Handy.Senden(_e.Handy, "Atomkraftwerk – TEST", "Testmeldung vom PC. Wenn das ankommt, klappt der Alarm aufs Handy.", test: true);
        }
        // Wieder normal -> darf sich beim nächsten Mal erneut melden
        _gemeldet.RemoveWhere(id => !rot.Any(r => r.id == id));
        if (_fenster != null && !_fenster.HasExited) return;   // Fenster ist schon offen
        if (!rot.Any(r => !_gemeldet.Contains(r.id))) return;  // nichts Neues

        foreach (var r in rot) _gemeldet.Add(r.id);
        var text = string.Join("\n", rot.Select(r => $"{r.name}: {r.text}"));
        var abschalten = _e.AlarmFenster && _e.AlarmAbschaltenSekunden > 0
            ? $"\nDer PC fährt in {_e.AlarmAbschaltenSekunden} s herunter, wenn am PC niemand reagiert." : "";
        Handy.Senden(_e.Handy, "ALARM – Atomkraftwerk", text + abschalten);
        if (!_e.AlarmFenster) return;
        try
        {
            File.WriteAllText(Path.Combine(_ordner, "alarm.txt"), text, new UTF8Encoding(true));
            var psi = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = _ordner
            };
            foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-STA", "-WindowStyle", "Hidden",
                                      "-File", Path.Combine(_ordner, "alarmfenster.ps1"),
                                      "-Datei", Path.Combine(_ordner, "alarm.txt"),
                                      "-Sekunden", Math.Max(0, _e.AlarmAbschaltenSekunden).ToString() })
                psi.ArgumentList.Add(a);
            _fenster = Process.Start(psi);
            Log.Schreiben("ALARM – Fenster geöffnet: " + text.Replace("\n", " | "));
        }
        catch (Exception ex) { Log.Schreiben("Alarmfenster startet nicht: " + ex.Message); }
    }
}
