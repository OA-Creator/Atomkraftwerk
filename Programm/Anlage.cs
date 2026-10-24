using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Atomkraftwerk;

// ---------------------------------------------------------------
// Die Anlage: alle Messwerte, Status, Laufzeit und Datenablage
// ---------------------------------------------------------------
public sealed class Anlage
{
    private readonly object _sperre = new();
    private readonly Dictionary<string, Messwert> _werte = new();
    private readonly List<string> _reihenfolge = new();
    private readonly Einstellungen _e;
    private readonly string _datenOrdner;
    public DateTime Start { get; } = DateTime.Now;
    public string CpuName = "", GpuName = "", BoardName = "", IcueDatei = "", IcueStand = "";
    public bool IcueLaeuft;
    public StromStand? Strom;
    public string DatenOrdner => _datenOrdner;
    // info = "Anzeige|Buchstaben|Seriennummer|Modell" – eigener Name per Buchstabe (z. B. "H:"), Seriennummer oder Modell
    public string LaufwerkName(string info)
    {
        var t = info.Split('|');
        foreach (var k in t[1].Split(' ', StringSplitOptions.RemoveEmptyEntries).Concat(new[] { t[2], t[3] }))
            if (k.Length > 0 && _e.LaufwerkNamen.TryGetValue(k, out var n) && !string.IsNullOrWhiteSpace(n)) return n;
        return t[0];
    }

    public Anlage(Einstellungen e, string programmOrdner)
    {
        _e = e;
        _datenOrdner = Path.IsPathRooted(e.DatenOrdner) ? e.DatenOrdner : Path.Combine(programmOrdner, e.DatenOrdner);
        Directory.CreateDirectory(_datenOrdner);

        // feste Werte in fester Reihenfolge
        Neu("cpu.temp", "CPU Temperatur", "Prozessor", "°C");
        Neu("cpu.watt", "CPU Leistung", "Prozessor", "W");
        Neu("cpu.last", "CPU Last", "Prozessor", "%");
        Neu("ram.belegt", "RAM belegt", "Prozessor", "GB");
        Neu("gpu.temp", "GPU Temperatur", "Grafik", "°C");
        Neu("gpu.hotspot", "GPU Hot Spot", "Grafik", "°C");
        Neu("gpu.watt", "GPU Leistung", "Grafik", "W");
        Neu("gpu.last", "GPU Last", "Grafik", "%");
        Neu("gpu.luefter", "GPU Lüfter", "Grafik", "rpm");
        Neu("board.vrm", "VRM MOS", "Board", "°C");
        Neu("board.soc", "CPU VCORE SoC", "Board", "V");
        Neu("board.pch", "Chipsatz (PCH)", "Board", "°C");
        Neu("flow.durchfluss", "Durchfluss", "Kühlung", "l/h");
        Neu("flow.wasser", "Wasser am Flow", "Kühlung", "°C");
        Neu("flow.leitwert", "Leitfähigkeit", "Kühlung", "µS/cm");
        Neu("flow.qualitaet", "Wasserqualität", "Kühlung", "%");
        Neu("strom.watt", "PC an der Steckdose", "Strom", "W");
    }

    private Messwert Neu(string id, string name, string gruppe, string einheit)
    {
        var m = new Messwert { Id = id, Name = name, Gruppe = gruppe, Einheit = einheit };
        _werte[id] = m; _reihenfolge.Add(id);
        return m;
    }

    public Messwert Wert(string id, string? name = null, string? gruppe = null, string? einheit = null)
    {
        lock (_sperre)
        {
            if (_werte.TryGetValue(id, out var m)) return m;
            return Neu(id, name ?? id, gruppe ?? "Sonstiges", einheit ?? "");
        }
    }

    // Werte entfernen, die es nicht mehr gibt (z. B. iCUE-Sensor umbenannt)
    public void Entfernen(Func<string, bool> weg)
    {
        lock (_sperre)
            foreach (var id in _reihenfolge.Where(weg).ToList()) { _reihenfolge.Remove(id); _werte.Remove(id); }
    }

    // ---------- Status nach Grenzwerten ----------
    public void Bewerten()
    {
        var g = _e.Grenzen;
        lock (_sperre)
        {
            // Wassertemperatur fuer den Durchfluss-Alarm: high flow NEXT, sonst iCUE
            double? wasser = _werte.TryGetValue("flow.wasser", out var fw) ? fw.Aktuell : null;
            wasser ??= _werte.Values.Where(x => x.Id.StartsWith("icue.") && x.Einheit == "°C" && x.Aktuell != null)
                                    .Select(x => x.Aktuell).Max();
            bool wasserWarm = g.FlussAlarmAbWasserC <= 0 || (wasser != null && wasser >= g.FlussAlarmAbWasserC);
            foreach (var m in _werte.Values)
            {
                if (m.Aktuell is null) { m.Status = "aus"; continue; }
                var v = m.Aktuell.Value;
                m.Status = m.Id switch
                {
                    "board.vrm" => v >= g.VrmAlarmC ? "alarm" : v >= g.VrmWarnC ? "warn" : "ok",
                    "board.soc" => v >= g.SocAlarmV ? "alarm" : v >= g.SocWarnV ? "warn" : "ok",
                    "cpu.temp" => v >= g.CpuAlarmC ? "alarm" : "ok",
                    "gpu.hotspot" => v >= g.GpuHotspotAlarmC ? "alarm" : "ok",
                    "flow.durchfluss" => v <= g.FlussAlarmLh && wasserWarm ? "alarm" : v <= g.FlussWarnLh ? "warn" : "ok",
                    "flow.wasser" => v >= g.WasserAlarmC ? "alarm" : v >= g.WasserWarnC ? "warn" : "ok",
                    _ when m.Id.StartsWith("lw.") => v >= g.LaufwerkAlarmC ? "alarm" : v >= g.LaufwerkWarnC ? "warn" : "ok",
                    _ when m.Id.StartsWith("icue.") && m.Einheit == "°C" =>
                        v >= g.WasserAlarmC ? "alarm" : v >= g.WasserWarnC ? "warn" : "ok",
                    _ => "ok"
                };
            }
        }
    }

    // Alle Werte, die gerade rot sind (für das Alarmfenster)
    public List<(string id, string name, string text)> RoteWerte()
    {
        lock (_sperre)
            return _reihenfolge.Select(id => _werte[id]).Where(m => m.Status == "alarm" && m.Aktuell != null)
                .Select(m => (m.Id, m.Name, m.Aktuell!.Value.ToString(m.Einheit == "V" ? "0.000" : "0.0", CultureInfo.GetCultureInfo("de-DE")) + (m.Einheit == "" ? "" : " " + m.Einheit)))
                .ToList();
    }

    public string Gesamtstatus()
    {
        lock (_sperre)
        {
            if (_werte.Values.Any(m => m.Status == "alarm")) return "alarm";
            if (_werte.Values.Any(m => m.Status == "warn")) return "warn";
            return "ok";
        }
    }

    // ---------- Laufzeit pro Tag ----------
    private DateTime _letzteLaufzeit = DateTime.Now;
    private Dictionary<string, double> _laufzeit = new();

    private string LaufzeitDatei => Path.Combine(_datenOrdner, "laufzeit.json");

    public void LaufzeitLaden()
    {
        try
        {
            if (File.Exists(LaufzeitDatei))
                _laufzeit = JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(LaufzeitDatei)) ?? new();
        }
        catch { _laufzeit = new(); }
    }

    public void LaufzeitZaehlen()
    {
        var jetzt = DateTime.Now;
        var sek = (jetzt - _letzteLaufzeit).TotalSeconds;
        _letzteLaufzeit = jetzt;
        if (sek <= 0 || sek > 120) return; // Standby/Pause nicht mitzählen
        var tag = jetzt.ToString("yyyy-MM-dd");
        lock (_sperre) { _laufzeit[tag] = (_laufzeit.TryGetValue(tag, out var s) ? s : 0) + sek; }
    }

    public double LaufzeitHeuteStunden()
    {
        lock (_sperre) return (_laufzeit.TryGetValue(DateTime.Now.ToString("yyyy-MM-dd"), out var s) ? s : 0) / 3600.0;
    }

    // ---------- Minutenwerte ablegen: daten/JJJJ-MM-TT.csv ----------
    public void MinuteSpeichern()
    {
        List<(string id, double? v)> zeile;
        lock (_sperre) zeile = _reihenfolge.Select(id => (id, _werte[id].MinutenmittelHolen())).ToList();
        var kopf = "zeit;" + string.Join(";", zeile.Select(z => z.id));
        var tag = DateTime.Now.ToString("yyyy-MM-dd");
        var datei = Path.Combine(_datenOrdner, tag + ".csv");
        try
        {
            // Kommen neue Messwerte dazu, passt die Kopfzeile nicht mehr: dann eigene Datei (Tag_2, Tag_3 ...)
            for (int n = 2; File.Exists(datei) && ErsteZeile(datei) != kopf; n++)
                datei = Path.Combine(_datenOrdner, $"{tag}_{n}.csv");
            var neu = !File.Exists(datei);
            var sb = new StringBuilder();
            if (neu) sb.AppendLine(kopf);
            sb.AppendLine(DateTime.Now.ToString("HH:mm") + ";" + string.Join(";", zeile.Select(z => Zahl.F(z.v, 2))));
            File.AppendAllText(datei, sb.ToString(), new UTF8Encoding(false));
            lock (_sperre) File.WriteAllText(LaufzeitDatei, JsonSerializer.Serialize(_laufzeit));
        }
        catch (Exception e) { Log.Schreiben("Speichern fehlgeschlagen: " + e.Message); }
    }

    private static string ErsteZeile(string datei)
    {
        try { using var sr = new StreamReader(datei); return sr.ReadLine() ?? ""; } catch { return ""; }
    }

    // ---------- Schnappschuss für die Webseite ----------
    public string AlsJson()
    {
        object daten;
        lock (_sperre)
        {
            daten = new
            {
                zeit = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
                seit = Start.ToString("yyyy-MM-ddTHH:mm:ss"),
                status = Gesamtstatus(),
                laufzeitHeuteStunden = Math.Round(LaufzeitHeuteStunden(), 2),
                hardware = new { cpu = CpuName, gpu = GpuName, board = BoardName },
                adressen = Heimnetz.Adressen(_e.Port),
                grenzen = _e.Grenzen,
                strompreisCentProKWh = _e.StrompreisCentProKWh,
                icue = new { datei = IcueDatei, stand = IcueStand, laeuft = IcueLaeuft },
                strom = Strom is null ? null : new
                {
                    eingerichtet = Strom.Eingerichtet, verbunden = Strom.Verbunden, meldung = Strom.Meldung, stand = Strom.Stand,
                    heuteKWh = Rund(Strom.HeuteKWh), monatKWh = Rund(Strom.MonatKWh), schnittTagKWh = Rund(Strom.SchnittTagKWh),
                    gesamtKWh = Rund(Strom.GesamtKWh), kostenHeuteEuro = Rund(Strom.KostenHeuteEuro), kostenMonatEuro = Rund(Strom.KostenMonatEuro)
                },
                werte = _reihenfolge.Select(id => _werte[id]).Select(m => new
                {
                    id = m.Id, name = m.Name, gruppe = m.Gruppe, einheit = m.Einheit, status = m.Status,
                    aktuell = Rund(m.Aktuell), min = Rund(m.Min), max = Rund(m.Max), schnitt = Rund(m.Schnitt)
                }).ToList()
            };
        }
        return JsonSerializer.Serialize(daten, Einstellungen.Json);
    }

    // ---------- Auswertung: gespeicherte Minutenwerte eines Tages ----------
    public string TageJson()
    {
        var tage = new SortedSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var f in Directory.GetFiles(_datenOrdner, "????-??-??*.csv"))
            {
                var n = Path.GetFileNameWithoutExtension(f);
                if (n.Length >= 10) tage.Add(n[..10]);
            }
        }
        catch { }
        return JsonSerializer.Serialize(tage.Reverse().ToList(), Einstellungen.Json);
    }

    // Minutenwerte eines Tages aus allen Dateien (Tag.csv, Tag_2.csv ...), Minute für Minute
    public (SortedDictionary<string, Dictionary<string, double>> zeilen, List<string> ids) TagLesen(string datum)
    {
        var zeilen = new SortedDictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);
        var ids = new List<string>();
        if (!System.Text.RegularExpressions.Regex.IsMatch(datum, @"^\d{4}-\d{2}-\d{2}$")) return (zeilen, ids);
        IEnumerable<string> dateien;
        try
        {
            dateien = Directory.GetFiles(_datenOrdner, datum + "*.csv")
                .Where(f => { var n = Path.GetFileNameWithoutExtension(f); return n == datum || n.StartsWith(datum + "_"); })
                .OrderBy(f => File.GetLastWriteTimeUtc(f)).ToList();
        }
        catch { return (zeilen, ids); }
        foreach (var datei in dateien)
        {
            string[] text;
            try
            {
                using var fs = new FileStream(datei, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs);
                text = sr.ReadToEnd().Split('\n');
            }
            catch { continue; }
            if (text.Length < 2) continue;
            var kopf = text[0].Trim().Split(';');
            foreach (var id in kopf.Skip(1)) if (!ids.Contains(id)) ids.Add(id);
            foreach (var z in text.Skip(1))
            {
                var t = z.Trim().Split(';');
                if (t.Length < 2 || t[0].Length != 5) continue;
                if (!zeilen.TryGetValue(t[0], out var zeile)) zeilen[t[0]] = zeile = new();
                for (int i = 1; i < t.Length && i < kopf.Length; i++)
                    if (double.TryParse(t[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) zeile[kopf[i]] = v;
            }
        }
        return (zeilen, ids);
    }

    public List<(string id, string name, string gruppe, string einheit)> WerteInfo()
    {
        lock (_sperre) return _reihenfolge.Select(id => _werte[id]).Select(m => (m.Id, m.Name, m.Gruppe, m.Einheit)).ToList();
    }

    public string TagJson(string datum)
    {
        var (zeilen, ids) = TagLesen(datum);
        object daten;
        lock (_sperre)
        {
            daten = new
            {
                datum,
                zeiten = zeilen.Keys.ToList(),
                werte = ids.Select(id =>
                {
                    _werte.TryGetValue(id, out var m);
                    return new
                    {
                        id, name = m?.Name ?? id, gruppe = m?.Gruppe ?? "", einheit = m?.Einheit ?? "",
                        reihe = zeilen.Values.Select(z => z.TryGetValue(id, out var v) ? Math.Round(v, 2) : (double?)null).ToList()
                    };
                }).ToList()
            };
        }
        return JsonSerializer.Serialize(daten, Einstellungen.Json);
    }

    public string VerlaufJson(string id)
    {
        Messwert? m;
        lock (_sperre) _werte.TryGetValue(id, out m);
        return JsonSerializer.Serialize(m?.VerlaufHolen() ?? new List<object>(), Einstellungen.Json);
    }

    private static double? Rund(double? v) => v is null || double.IsNaN(v.Value) ? null : Math.Round(v.Value, 3);
}

// Adressen dieses PCs im Heimnetz (für den QR-Code aufs Handy)
public static class Heimnetz
{
    private static List<string>? _cache;
    private static DateTime _stand;
    public static List<string> Adressen(int port)
    {
        if (_cache != null && (DateTime.Now - _stand).TotalMinutes < 5) return _cache;
        var liste = new List<string>();
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                var name = (ni.Name + " " + ni.Description).ToLowerInvariant();
                if (name.Contains("virtual") || name.Contains("vethernet") || name.Contains("wsl") || name.Contains("hyper-v") || name.Contains("vpn") || name.Contains("wireguard")) continue;
                if (ni.GetIPProperties().GatewayAddresses.Count == 0) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                    var b = ua.Address.GetAddressBytes();
                    bool privat = b[0] == 10 || (b[0] == 192 && b[1] == 168) || (b[0] == 172 && b[1] >= 16 && b[1] <= 31);
                    if (privat) liste.Add($"http://{ua.Address}:{port}/");
                }
            }
        }
        catch { }
        _cache = liste; _stand = DateTime.Now;
        return liste;
    }
}
