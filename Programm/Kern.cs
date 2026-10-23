using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Atomkraftwerk;

// ---------------------------------------------------------------
// Einstellungen: eine zentrale Datei atomkraftwerk.json neben dem Programm
// ---------------------------------------------------------------
public class Grenzen
{
    public double VrmWarnC { get; set; } = 80;
    public double VrmAlarmC { get; set; } = 90;
    public double SocWarnV { get; set; } = 1.28;
    public double SocAlarmV { get; set; } = 1.30;
    public double CpuAlarmC { get; set; } = 90;
    public double GpuHotspotAlarmC { get; set; } = 105;
    public double WasserWarnC { get; set; } = 35;
    public double WasserAlarmC { get; set; } = 40;
    public double FlussWarnLh { get; set; } = 25;
    public double FlussAlarmLh { get; set; } = 15;
}

public class Einstellungen
{
    public int Port { get; set; } = 8090;
    public int IntervallSekunden { get; set; } = 1;
    public string IcueOrdner { get; set; } = "";
    public string DatenOrdner { get; set; } = "daten";
    public Grenzen Grenzen { get; set; } = new();
    public double StrompreisCentProKWh { get; set; } = 0;
    public FritzEinstellungen Fritz { get; set; } = new();

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        WriteIndented = false
    };

    public static Einstellungen Laden(string ordner)
    {
        var pfad = Path.Combine(ordner, "atomkraftwerk.json");
        try
        {
            if (File.Exists(pfad))
                return JsonSerializer.Deserialize<Einstellungen>(File.ReadAllText(pfad), Json) ?? new();
        }
        catch (Exception e) { Log.Schreiben("Einstellungen nicht lesbar, nehme Standard: " + e.Message); }
        return new();
    }

    public string IcuePfad() =>
        string.IsNullOrWhiteSpace(IcueOrdner)
            ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            : Environment.ExpandEnvironmentVariables(IcueOrdner);
}

// ---------------------------------------------------------------
// Ein Messwert mit Min/Max/Durchschnitt seit Programmstart
// ---------------------------------------------------------------
public class Messwert
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Gruppe { get; init; } = "";
    public string Einheit { get; init; } = "";
    public double? Aktuell { get; private set; }
    public double? Min { get; private set; }
    public double? Max { get; private set; }
    public double? Schnitt => _anzahl == 0 ? null : _summe / _anzahl;
    public string Status { get; set; } = "ok"; // ok | warn | alarm | aus
    public DateTime? Zeit { get; private set; }

    private double _summe;
    private long _anzahl;

    // Minutenmittel für die Datenablage
    private double _minSumme;
    private long _minAnzahl;

    // Verlauf: alle 5 Sekunden ein Punkt, die letzten 30 Minuten
    private readonly Queue<(DateTime t, double v)> _verlauf = new();
    private DateTime _letzterPunkt = DateTime.MinValue;
    public List<object> VerlaufHolen()
    {
        lock (_verlauf) return _verlauf.Select(p => (object)new { t = p.t.ToString("HH:mm:ss"), v = Math.Round(p.v, 3) }).ToList();
    }

    public void Setzen(double? wert)
    {
        Aktuell = wert;
        if (wert is null || double.IsNaN(wert.Value)) { Status = "aus"; return; }
        var v = wert.Value;
        Zeit = DateTime.Now;
        if ((Zeit.Value - _letzterPunkt).TotalSeconds >= 5)
        {
            _letzterPunkt = Zeit.Value;
            lock (_verlauf)
            {
                _verlauf.Enqueue((Zeit.Value, v));
                while (_verlauf.Count > 360) _verlauf.Dequeue();
            }
        }
        Min = Min is null ? v : Math.Min(Min.Value, v);
        Max = Max is null ? v : Math.Max(Max.Value, v);
        _summe += v; _anzahl++;
        _minSumme += v; _minAnzahl++;
    }

    public double? MinutenmittelHolen()
    {
        if (_minAnzahl == 0) return null;
        var m = _minSumme / _minAnzahl;
        _minSumme = 0; _minAnzahl = 0;
        return m;
    }
}

public static class Log
{
    private static readonly object Sperre = new();
    public static string Datei = "atomkraftwerk.log";
    public static void Schreiben(string text)
    {
        var zeile = $"{DateTime.Now:dd.MM.yyyy HH:mm:ss}  {text}";
        Console.WriteLine(zeile);
        lock (Sperre)
        {
            try
            {
                if (File.Exists(Datei) && new FileInfo(Datei).Length > 2_000_000) File.Delete(Datei);
                File.AppendAllText(Datei, zeile + Environment.NewLine);
            }
            catch { }
        }
    }
}

public static class Zahl
{
    public static string F(double? v, int stellen = 1) =>
        v is null ? "" : v.Value.ToString("F" + stellen, CultureInfo.InvariantCulture);
}
