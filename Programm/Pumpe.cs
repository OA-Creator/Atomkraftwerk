using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using LibreHardwareMonitor.Hardware;

namespace Atomkraftwerk;

// ---------------------------------------------------------------
// Pumpe am Board-Anschluss (CPU_OPT = "Fan #6") selbst steuern:
//  Startschub 100 %, bis der high flow NEXT Durchfluss meldet,
//  danach Regelung auf die Zieldrehzahl. Fällt der Durchfluss weg,
//  wieder Startschub. Programmende -> Pumpe zurück ans BIOS.
// ---------------------------------------------------------------
public class PumpenEinstellungen
{
    public bool Steuern { get; set; } = true;          // false = BIOS regelt allein
    public string Anschluss { get; set; } = "Fan #6";  // Name des Board-Anschlusses (CPU_OPT)
    public double ZielRpm { get; set; } = 3140;        // alt, wird nicht mehr benutzt
    public double ZielFluss { get; set; } = 60;        // Automatik: so langsam wie möglich, aber dieser Durchfluss (l/h)
    public double MinProzent { get; set; } = 40;       // Automatik: nie weniger Ansteuerung als das
    public string Betrieb { get; set; } = "auto";      // "auto" = Regelung auf ZielFluss, "hand" = feste Ansteuerung HandProzent
    public double HandProzent { get; set; } = 60;      // Hand: 25–100 %
}

public sealed class PumpenSteuerung
{
    public const string Id = "icue.pumpe"; // gleiche Kennung wie früher, damit Leitstand, Auswertung und TURZX-Plugin weiterlaufen
    private readonly Einstellungen _e;
    private readonly string _ordner;
    private ISensor? _fan, _ctl;
    private DateTime _start = DateTime.Now, _gutSeit = DateTime.MinValue, _schlechtSeit = DateTime.MinValue, _letzteRegelung = DateTime.MinValue;
    private bool _hinweisOhneFluss;
    private double _boden; // Untergrenze, gelernt: darunter bleibt das Messrad im NEXT stehen
    private readonly Queue<double> _flussSchnitt = new(); // Durchfluss der letzten 10 s, gegen Zappeln
    public string Modus { get; private set; } = "BIOS";
    public double Prozent { get; private set; }
    public double? Rpm => _fan?.Value;
    public bool Gefunden => _fan != null;
    private bool Hand => _e.Pumpe.Betrieb.Equals("hand", StringComparison.OrdinalIgnoreCase);

    public PumpenSteuerung(Einstellungen e, string ordner) { _e = e; _ordner = ordner; }

    public void Verbinden(HardwareQuelle? hw)
    {
        (_fan, _ctl) = hw?.Anschluss(_e.Pumpe.Anschluss) ?? (null, null);
        if (_fan is null) { Log.Schreiben($"Pumpe: Anschluss \"{_e.Pumpe.Anschluss}\" nicht gefunden – Pumpe weiter aus iCUE."); return; }
        if (!_e.Pumpe.Steuern || _ctl?.Control is null) { Log.Schreiben("Pumpe: nur Anzeige, Steuerung bleibt beim BIOS."); return; }
        StartschubBeginnen("Programmstart");
    }

    private void StartschubBeginnen(string grund)
    {
        Modus = "Startschub"; _start = DateTime.Now; _gutSeit = DateTime.MinValue; _hinweisOhneFluss = false;
        Setzen(100);
        Log.Schreiben($"Pumpe: Startschub 100 % ({grund}).");
    }

    private void Setzen(double p)
    {
        var unten = Hand ? 25 : Math.Max(_e.Pumpe.MinProzent, _boden);
        Prozent = Math.Clamp(p, unten, 100);
        try { _ctl?.Control?.SetSoftware((float)Prozent); } catch (Exception ex) { Log.Schreiben("Pumpe: Setzen fehlgeschlagen: " + ex.Message); }
    }

    // jede Sekunde nach dem Lesen der Hardware
    public void Arbeiten(Anlage a)
    {
        if (_fan is null) return;
        a.Wert(Id, "Pumpe", "Kühlung", "rpm").Setzen(_fan.Value);
        if (Modus == "BIOS") return;
        var jetzt = DateTime.Now;
        var fluss = a.Wert("flow.durchfluss").Aktuell;
        var g = _e.Grenzen;

        if (Modus == "Startschub")
        {
            Setzen(100);
            if (fluss is double f && f >= g.FlussWarnLh) { if (_gutSeit == DateTime.MinValue) _gutSeit = jetzt; }
            else _gutSeit = DateTime.MinValue;
            var dauer = (jetzt - _start).TotalSeconds;
            // mindestens 3 Minuten voll laufen lassen, damit sich das Messrad freispült
            if (_gutSeit != DateTime.MinValue && (jetzt - _gutSeit).TotalSeconds >= 10 && dauer >= 180)
            {
                Modus = "Regelung"; _schlechtSeit = DateTime.MinValue;
                Log.Schreiben(Hand ? $"Pumpe: Durchfluss da ({Math.Round(fluss ?? 0)} l/h) – Hand: {_e.Pumpe.HandProzent} %."
                                   : $"Pumpe: Durchfluss da ({Math.Round(fluss ?? 0)} l/h) – regle auf {_e.Pumpe.ZielFluss} l/h.");
            }
            else if (dauer >= 120 && !_hinweisOhneFluss && _gutSeit == DateTime.MinValue)
            {
                _hinweisOhneFluss = true;
                Log.Schreiben("Pumpe: nach 2 Minuten noch kein Durchfluss gemessen – Pumpe bleibt auf 100 %.");
            }
            return;
        }

        // Hand: feste Ansteuerung; Sicherung nur, wenn zugleich das Wasser warm wird (gleiche Regel wie beim roten Alarm)
        if (Hand)
        {
            Modus = "Hand";
            Setzen(_e.Pumpe.HandProzent);
            var wasser = a.Wert("flow.wasser").Aktuell;
            var warm = g.FlussAlarmAbWasserC <= 0 || (wasser is double w && w >= g.FlussAlarmAbWasserC);
            if (fluss is double fh && fh < g.FlussAlarmLh && warm)
            {
                if (_schlechtSeit == DateTime.MinValue) _schlechtSeit = jetzt;
                if ((jetzt - _schlechtSeit).TotalSeconds >= 5) StartschubBeginnen($"Hand: Durchfluss {Math.Round(fh)} l/h bei Wasser {Math.Round(wasser ?? 0, 1)} °C");
            }
            else _schlechtSeit = DateTime.MinValue;
            return;
        }
        Modus = "Regelung";

        // Regelung
        if (fluss is double fl && fl < g.FlussAlarmLh)
        {
            if (_schlechtSeit == DateTime.MinValue) _schlechtSeit = jetzt;
            if ((jetzt - _schlechtSeit).TotalSeconds >= 5)
            {
                // darunter bricht die Messung ab: ab jetzt nicht mehr so tief gehen
                _boden = Math.Min(100, Math.Round(Prozent) + 10);
                Log.Schreiben($"Pumpe: Durchfluss-Messung bei {Math.Round(Prozent)} % weggebrochen – halte ab jetzt mindestens {_boden} %.");
                StartschubBeginnen($"Durchfluss {Math.Round(fl)} l/h");
                return;
            }
        }
        else _schlechtSeit = DateTime.MinValue;
        if ((jetzt - _letzteRegelung).TotalSeconds < 3) { if (fluss is double fq) { _flussSchnitt.Enqueue(fq); while (_flussSchnitt.Count > 10) _flussSchnitt.Dequeue(); } return; }
        _letzteRegelung = jetzt;
        // Automatik auf Durchfluss: Mittel der letzten 10 s, runter höchstens 1 %, hoch höchstens 2 % alle 3 s
        if (fluss is double fm) { _flussSchnitt.Enqueue(fm); while (_flussSchnitt.Count > 10) _flussSchnitt.Dequeue(); }
        if (_flussSchnitt.Count == 0) return;
        var mittel = _flussSchnitt.Average();
        var schritt = Math.Clamp((_e.Pumpe.ZielFluss - mittel) / _e.Pumpe.ZielFluss * 5, -1, 2);
        Setzen(Prozent + schritt);
    }

    public void Freigeben()
    {
        try { if (Modus != "BIOS") { _ctl?.Control?.SetDefault(); Log.Schreiben("Pumpe: zurück an das BIOS."); } } catch { }
        Modus = "BIOS";
    }

    // Leitstand: Zustand lesen, Zieldrehzahl ändern (wird in atomkraftwerk.json gespeichert)
    public string Json(string? ziel, string? betrieb = null, string? prozent = null, bool standard = false)
    {
        if (standard)
        {
            _e.Pumpe.Betrieb = "auto"; _e.Pumpe.ZielFluss = 60; _e.Pumpe.HandProzent = 60; _boden = 0;
            Speichern("betrieb", "\"auto\""); Speichern("zielFluss", "60"); Speichern("handProzent", "60");
            Log.Schreiben("Pumpe: im Leitstand auf Standard zurückgesetzt (Automatik, 60 l/h, Hand 60 %).");
        }
        if (betrieb is "auto" or "hand" && betrieb != _e.Pumpe.Betrieb)
        {
            _e.Pumpe.Betrieb = betrieb;
            Speichern("betrieb", "\"" + betrieb + "\"");
            Log.Schreiben("Pumpe: Betrieb im Leitstand auf " + (betrieb == "hand" ? "Hand" : "Automatik") + " gestellt.");
            if (betrieb == "auto") { _boden = 0; _letzteRegelung = DateTime.MinValue; }
        }
        if (prozent != null && double.TryParse(prozent, NumberStyles.Float, CultureInfo.InvariantCulture, out var hp))
        {
            hp = Math.Clamp(Math.Round(hp), 25, 100);
            if (hp != _e.Pumpe.HandProzent)
            {
                _e.Pumpe.HandProzent = hp;
                Speichern("handProzent", hp.ToString(CultureInfo.InvariantCulture));
                Log.Schreiben($"Pumpe: Hand-Ansteuerung im Leitstand auf {hp} % gestellt.");
            }
        }
        if (ziel != null && double.TryParse(ziel, NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
        {
            z = Math.Clamp(Math.Round(z / 5) * 5, 30, 150);
            if (z != _e.Pumpe.ZielFluss)
            {
                _e.Pumpe.ZielFluss = z;
                Speichern("zielFluss", z.ToString(CultureInfo.InvariantCulture));
                Log.Schreiben($"Pumpe: Ziel-Durchfluss im Leitstand auf {z} l/h gestellt.");
            }
        }
        return JsonSerializer.Serialize(new
        {
            gefunden = Gefunden, steuern = _e.Pumpe.Steuern, modus = Modus, boden = _boden,
            betrieb = Hand ? "hand" : "auto", hand = _e.Pumpe.HandProzent,
            prozent = Math.Round(Prozent), rpm = Rpm is double r ? Math.Round(r) : (double?)null, ziel = _e.Pumpe.ZielFluss
        }, Einstellungen.Json);
    }

    // nur die eine Zahl in der Datei ändern, alles andere (auch das Kennwort) bleibt unberührt
    private void Speichern(string schluessel, string wert)
    {
        try
        {
            var pfad = Path.Combine(_ordner, "atomkraftwerk.json");
            var text = File.ReadAllText(pfad);
            var muster = "(\"" + schluessel + "\"\\s*:\\s*)(\"[^\"]*\"|[0-9.]+)";
            var neu = Regex.Replace(text, muster, m => m.Groups[1].Value + wert);
            if (neu != text) File.WriteAllText(pfad, neu);
            else if (!Regex.IsMatch(text, muster)) Log.Schreiben($"Pumpe: \"{schluessel}\" fehlt in atomkraftwerk.json – nur bis zum Neustart gültig.");
        }
        catch (Exception ex) { Log.Schreiben("Pumpe: Einstellung nicht gespeichert: " + ex.Message); }
    }
}
