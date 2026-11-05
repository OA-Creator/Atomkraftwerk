using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using LibreHardwareMonitor.Hardware;

namespace Atomkraftwerk;

// ---------------------------------------------------------------
// Pumpe am Board-Anschluss (FAN4_PUMP = "Fan #6") selbst steuern:
//  Startschub 100 %, bis der high flow NEXT Durchfluss meldet,
//  danach Regelung auf die Zieldrehzahl. Fällt der Durchfluss weg,
//  wieder Startschub. Programmende -> Pumpe zurück ans BIOS.
// ---------------------------------------------------------------
public class PumpenEinstellungen
{
    public bool Steuern { get; set; } = true;          // false = BIOS regelt allein
    public string Anschluss { get; set; } = "Fan #6";  // Name des Board-Anschlusses (FAN4_PUMP)
    public double ZielRpm { get; set; } = 3140;        // alt, wird nicht mehr benutzt
    public double ZielFluss { get; set; } = 60;        // Automatik: so langsam wie möglich, aber dieser Durchfluss (l/h)
    public double ZielFlussMax { get; set; } = 70;     // Automatik: bei warmem Wasser steigt das Ziel bis hierhin (l/h)
    public double ZielTempAb { get; set; } = 30;       // ab dieser Wassertemperatur steigt das Ziel (°C)
    public double ZielTempBis { get; set; } = 35;      // ab dieser Wassertemperatur gilt ZielFlussMax (°C)
    public double MinProzent { get; set; } = 20;       // Automatik: nie weniger Ansteuerung als das
    public string Betrieb { get; set; } = "auto";      // "auto" = Regelung auf ZielFluss, "hand" = feste Ansteuerung HandProzent
    public double HandProzent { get; set; } = 60;      // Hand: 25–100 %
    public double AnlaufMin { get; set; } = 3;         // Startschub: so lange mindestens (1, 2, 3, 5 oder 10 Minuten)
    public double AnlaufProzent { get; set; } = 100;   // Startschub: mit dieser Stärke (80, 90 oder 100 %)
}

public sealed class PumpenSteuerung
{
    public const string Id = "icue.pumpe"; // gleiche Kennung wie früher, damit Leitstand, Auswertung und TURZX-Plugin weiterlaufen
    private readonly Einstellungen _e;
    private readonly string _ordner;
    private ISensor? _fan, _ctl;
    private DateTime _start = DateTime.Now, _gutSeit = DateTime.MinValue, _schlechtSeit = DateTime.MinValue, _letzteRegelung = DateTime.MinValue;
    private bool _hinweisOhneFluss;
    private DateTime _ungeregeltSeit = DateTime.MinValue;
    public string? Hinweis { get; private set; } // gesetzt, wenn die Pumpe seit 1 Stunde ungeregelt läuft
    private double _boden; // Untergrenze, gelernt: darunter bleibt das Messrad im NEXT stehen
    private readonly Queue<double> _flussSchnitt = new(); // Durchfluss der letzten 10 s, gegen Zappeln
    public string Modus { get; private set; } = "BIOS";
    public double ZielJetzt { get; private set; }   // Ziel-Durchfluss gerade jetzt (nach Wassertemperatur)
    public double? WasserJetzt { get; private set; }
    public double Prozent { get; private set; }
    public double? Rpm => _fan?.Value;
    public bool Gefunden => _fan != null;
    // Board-Steuerung gibt keinen Wert mehr zurück (z. B. nach Ruhezustand), obwohl wir steuern
    public bool SteuerungWeg => Modus != "BIOS" && _ctl != null && _ctl.Value is null;
    private bool Hand => _e.Pumpe.Betrieb.Equals("hand", StringComparison.OrdinalIgnoreCase);

    public PumpenSteuerung(Einstellungen e, string ordner) { _e = e; _ordner = ordner; }

    public void Verbinden(HardwareQuelle? hw)
    {
        Modus = "BIOS"; // bis feststeht, dass wir steuern können (auch beim erneuten Verbinden nach dem Aufwachen)
        (_fan, _ctl) = hw?.Anschluss(_e.Pumpe.Anschluss) ?? (null, null);
        if (_fan is null) { Log.Schreiben($"Pumpe: Anschluss \"{_e.Pumpe.Anschluss}\" nicht gefunden – Pumpe weiter aus iCUE."); return; }
        if (!_e.Pumpe.Steuern || _ctl?.Control is null) { Log.Schreiben("Pumpe: nur Anzeige, Steuerung bleibt beim BIOS."); return; }
        StartschubBeginnen("Programmstart");
    }

    private void StartschubBeginnen(string grund)
    {
        Modus = "Startschub"; _start = DateTime.Now; _gutSeit = DateTime.MinValue; _hinweisOhneFluss = false;
        Setzen(_e.Pumpe.AnlaufProzent);
        Log.Schreiben($"Pumpe: Startschub {_e.Pumpe.AnlaufProzent} % für {_e.Pumpe.AnlaufMin} min ({grund}).");
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
        WasserJetzt = a.Wert("flow.wasser").Aktuell;
        ZielJetzt = ZielNachTemperatur(WasserJetzt);
        UngeregeltPruefen(jetzt, fluss);

        if (Modus == "Startschub")
        {
            Setzen(_e.Pumpe.AnlaufProzent);
            if (fluss is double f && f >= g.FlussWarnLh) { if (_gutSeit == DateTime.MinValue) _gutSeit = jetzt; }
            else _gutSeit = DateTime.MinValue;
            var dauer = (jetzt - _start).TotalSeconds;
            // mindestens die Anlaufzeit voll laufen lassen, damit sich das Messrad freispült
            if (_gutSeit != DateTime.MinValue && (jetzt - _gutSeit).TotalSeconds >= 10 && dauer >= _e.Pumpe.AnlaufMin * 60)
            {
                Modus = "Regelung"; _schlechtSeit = DateTime.MinValue;
                Log.Schreiben(Hand ? $"Pumpe: Durchfluss da ({Math.Round(fluss ?? 0)} l/h) – Hand: {_e.Pumpe.HandProzent} %."
                                   : $"Pumpe: Durchfluss da ({Math.Round(fluss ?? 0)} l/h) – regle auf {_e.Pumpe.ZielFluss} l/h (bei warmem Wasser bis {_e.Pumpe.ZielFlussMax} l/h).");
            }
            else if (dauer >= 120 && !_hinweisOhneFluss && _gutSeit == DateTime.MinValue)
            {
                _hinweisOhneFluss = true;
                Log.Schreiben($"Pumpe: nach 2 Minuten noch kein Durchfluss gemessen – Pumpe bleibt auf {_e.Pumpe.AnlaufProzent} %.");
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
        var schritt = Math.Clamp((ZielJetzt - mittel) / ZielJetzt * 5, -1, 2);
        Setzen(Prozent + schritt);
    }

    // Ziel-Durchfluss nach Wassertemperatur: bis ZielTempAb = ZielFluss, bis ZielTempBis gleichmäßig auf ZielFlussMax
    private double ZielNachTemperatur(double? wasser)
    {
        var p = _e.Pumpe;
        var max = Math.Max(p.ZielFluss, p.ZielFlussMax);
        if (wasser is not double w || w < 5 || w > 80 || p.ZielTempBis <= p.ZielTempAb) return p.ZielFluss;
        var anteil = Math.Clamp((w - p.ZielTempAb) / (p.ZielTempBis - p.ZielTempAb), 0, 1);
        return p.ZielFluss + (max - p.ZielFluss) * anteil;
    }

    // Pumpe läuft ungeregelt (Steuerstecker ab): keine Drehzahl trotz Durchfluss,
    // oder Vollgas obwohl wenig vorgegeben. Steht das 1 Stunde an -> Hinweis (nur Bildschirm).
    private void UngeregeltPruefen(DateTime jetzt, double? fluss)
    {
        var rpm = _fan?.Value ?? 0;
        var ohneSignal = rpm < 1 && fluss is double f && f > 30;
        var vollgas = rpm > 4000 && Prozent <= 60 && Modus != "Startschub";
        if (ohneSignal || vollgas)
        {
            if (_ungeregeltSeit == DateTime.MinValue) _ungeregeltSeit = jetzt;
            if (Hinweis is null && (jetzt - _ungeregeltSeit).TotalMinutes >= 60)
            {
                Hinweis = ohneSignal
                    ? "Die Pumpe meldet seit 1 Stunde keine Drehzahl und läuft ungeregelt auf Vollgas. Stecker am Board-Anschluss FAN4_PUMP prüfen."
                    : "Die Pumpe läuft seit 1 Stunde auf Vollgas, obwohl weniger vorgegeben ist. Stecker am Board-Anschluss FAN4_PUMP prüfen.";
                Log.Schreiben("Pumpe: " + Hinweis);
            }
        }
        else
        {
            if (Hinweis != null) Log.Schreiben("Pumpe: läuft wieder geregelt.");
            _ungeregeltSeit = DateTime.MinValue; Hinweis = null;
        }
    }

    public void Freigeben()
    {
        try { if (Modus != "BIOS") { _ctl?.Control?.SetDefault(); Log.Schreiben("Pumpe: zurück an das BIOS."); } } catch { }
        Modus = "BIOS";
    }

    // Leitstand: Zustand lesen, Zieldrehzahl ändern (wird in atomkraftwerk.json gespeichert)
    public string Json(string? ziel, string? betrieb = null, string? prozent = null, bool standard = false, string? anlaufMin = null, string? anlaufProzent = null)
    {
        if (standard)
        {
            _e.Pumpe.Betrieb = "auto"; _e.Pumpe.ZielFluss = 60; _e.Pumpe.HandProzent = 60; _boden = 0;
            _e.Pumpe.AnlaufMin = 3; _e.Pumpe.AnlaufProzent = 100;
            Speichern("betrieb", "\"auto\""); Speichern("zielFluss", "60"); Speichern("handProzent", "60");
            Speichern("anlaufMin", "3"); Speichern("anlaufProzent", "100");
            Log.Schreiben("Pumpe: im Leitstand auf Standard zurückgesetzt (Automatik, 60 l/h, Hand 60 %, Anlauf 3 min mit 100 %).");
        }
        if (anlaufMin != null && double.TryParse(anlaufMin, NumberStyles.Float, CultureInfo.InvariantCulture, out var am)
            && am is 1 or 2 or 3 or 5 or 10 && am != _e.Pumpe.AnlaufMin)
        {
            _e.Pumpe.AnlaufMin = am;
            Speichern("anlaufMin", am.ToString(CultureInfo.InvariantCulture));
            Log.Schreiben($"Pumpe: Anlaufzeit im Leitstand auf {am} min gestellt.");
        }
        if (anlaufProzent != null && double.TryParse(anlaufProzent, NumberStyles.Float, CultureInfo.InvariantCulture, out var ap)
            && ap is 80 or 90 or 100 && ap != _e.Pumpe.AnlaufProzent)
        {
            _e.Pumpe.AnlaufProzent = ap;
            Speichern("anlaufProzent", ap.ToString(CultureInfo.InvariantCulture));
            Log.Schreiben($"Pumpe: Anlauf-Stärke im Leitstand auf {ap} % gestellt.");
            if (Modus == "Startschub") Setzen(ap);
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
            prozent = Math.Round(Prozent), rpm = Rpm is double r ? Math.Round(r) : (double?)null, ziel = _e.Pumpe.ZielFluss, zielJetzt = Math.Round(ZielJetzt > 0 ? ZielJetzt : _e.Pumpe.ZielFluss), zielMax = _e.Pumpe.ZielFlussMax,
            tempAb = _e.Pumpe.ZielTempAb, tempBis = _e.Pumpe.ZielTempBis, wasser = WasserJetzt is double wj ? Math.Round(wj, 1) : (double?)null, anlaufMin = _e.Pumpe.AnlaufMin, anlaufProzent = _e.Pumpe.AnlaufProzent, hinweis = Hinweis
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
            if (!Regex.IsMatch(text, muster))
            {
                // Schlüssel fehlt noch: im Abschnitt "pumpe" ergänzen, sonst nichts anfassen
                var abschnitt = new Regex("(\"pumpe\"\\s*:\\s*\\{)(\\s*)", RegexOptions.IgnoreCase);
                var mp = abschnitt.Match(text);
                if (!mp.Success) { Log.Schreiben($"Pumpe: \"{schluessel}\" fehlt in atomkraftwerk.json – nur bis zum Neustart gültig."); return; }
                var einzug = mp.Groups[2].Value.Length > 0 ? mp.Groups[2].Value : " ";
                neu = text.Substring(0, mp.Index + mp.Groups[1].Length) + einzug + "\"" + schluessel + "\": " + wert + "," + text.Substring(mp.Index + mp.Groups[1].Length);
            }
            if (neu != text) File.WriteAllText(pfad, neu);
        }
        catch (Exception ex) { Log.Schreiben("Pumpe: Einstellung nicht gespeichert: " + ex.Message); }
    }
}
