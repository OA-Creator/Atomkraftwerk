using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace Atomkraftwerk;

// ---------------------------------------------------------------
// Strom: FRITZ!DECT-Steckdose über die FRITZ!Box (AHA-Schnittstelle)
// ---------------------------------------------------------------
public class FritzEinstellungen
{
    public string Adresse { get; set; } = "http://fritz.box";
    public string Benutzer { get; set; } = "atomkraftwerk";
    public string Kennwort { get; set; } = "";
    public string Ain { get; set; } = "";
    public int AbfrageSekunden { get; set; } = 10;
}

public sealed class StromStand
{
    public bool Eingerichtet { get; set; }
    public bool Verbunden { get; set; }
    public string Meldung { get; set; } = "";
    public double? HeuteKWh { get; set; }
    public double? MonatKWh { get; set; }
    public double? SchnittTagKWh { get; set; }
    public double? GesamtKWh { get; set; }
    public double? KostenHeuteEuro { get; set; }
    public double? KostenMonatEuro { get; set; }
    public string Stand { get; set; } = "";
    // Standby: Verbrauch in der Zeit, in der der PC zuletzt aus war
    public double? StandbyWatt { get; set; }
    public double? StandbyKWh { get; set; }
    public string StandbyVon { get; set; } = "";
    public string StandbyBis { get; set; } = "";
}

// Merkzettel für die Standby-Rechnung (wird jede Minute geschrieben, überlebt das Herunterfahren)
public sealed class StandbyMerker
{
    public double ZaehlerWh { get; set; }          // Zählerstand der Steckdose beim letzten Auslesen
    public DateTime ZaehlerZeit { get; set; }
    public double LaufWhSeitZaehler { get; set; }  // vom PC verbrauchte Wh seit diesem Auslesen (aus den 10-s-Wattwerten)
    public DateTime ZuletztAn { get; set; }        // letztes Lebenszeichen des Programms
}

public sealed class FritzQuelle
{
    private readonly FritzEinstellungen _f;
    private readonly double _preisCent;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private string _sid = "0000000000000000";
    private DateTime _naechsterLoginVersuch = DateTime.MinValue;
    private DateTime _letzteStatistik = DateTime.MinValue;
    private double? _watt;
    public StromStand Stand { get; } = new();

    private readonly string _beginnDatei;
    private DateTime? _messbeginn;

    // Standby-Rechnung
    private readonly DateTime _programmStart = DateTime.Now;
    private StandbyMerker? _alterMerker;           // vom letzten Programmlauf
    private double? _zaehlerWh; private DateTime _zaehlerZeit;
    private double _laufWhSeitZaehler, _laufWhSeitStart;
    private DateTime _letzteWattZeit = DateTime.MinValue, _letzterMerker = DateTime.MinValue;
    private bool _standbyGerechnet;
    private string MerkerDatei => Path.Combine(Path.GetDirectoryName(_beginnDatei)!, "standby-merker.json");
    private string StandbyDatei => Path.Combine(Path.GetDirectoryName(_beginnDatei)!, "standby.json");

    public FritzQuelle(Einstellungen e, string datenOrdner)
    {
        // Tag, an dem die Strommessung begann (für einen ehrlichen Ø pro Tag im ersten Monat)
        _beginnDatei = Path.Combine(datenOrdner, "strom-beginn.txt");
        try
        {
            if (File.Exists(_beginnDatei) && DateTime.TryParse(File.ReadAllText(_beginnDatei).Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var b))
                _messbeginn = b.Date;
        }
        catch { }
        try { if (File.Exists(MerkerDatei)) _alterMerker = System.Text.Json.JsonSerializer.Deserialize<StandbyMerker>(File.ReadAllText(MerkerDatei), Einstellungen.Json); } catch { }
        try
        {
            if (File.Exists(StandbyDatei))
            {
                var alt = System.Text.Json.JsonSerializer.Deserialize<StromStand>(File.ReadAllText(StandbyDatei), Einstellungen.Json);
                if (alt != null) { Stand.StandbyWatt = alt.StandbyWatt; Stand.StandbyKWh = alt.StandbyKWh; Stand.StandbyVon = alt.StandbyVon; Stand.StandbyBis = alt.StandbyBis; }
            }
        }
        catch { }
        _f = e.Fritz;
        _preisCent = e.StrompreisCentProKWh;
        Stand.Eingerichtet = !string.IsNullOrWhiteSpace(_f.Kennwort);
        Stand.Meldung = Stand.Eingerichtet ? "verbinde …" : "Kennwort in atomkraftwerk.json fehlt";
    }

    // Läuft im Hintergrund, damit eine langsame FRITZ!Box den Messlauf nicht aufhält
    public async Task LaufenAsync(CancellationToken stopp)
    {
        if (!Stand.Eingerichtet) { Log.Schreiben("Strom: kein FRITZ!Box-Kennwort eingetragen."); return; }
        while (!stopp.IsCancellationRequested)
        {
            try { await EinmalAsync(); }
            catch (Exception ex)
            {
                Stand.Verbunden = false; _watt = null;
                Stand.Meldung = "FRITZ!Box nicht erreichbar";
                Log.Schreiben("Strom: " + ex.Message);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, _f.AbfrageSekunden)), stopp); } catch { break; }
        }
    }

    public double? Watt => _watt;

    private async Task EinmalAsync()
    {
        if (!await AngemeldetAsync()) return;

        var mw = await BefehlAsync("getswitchpower");
        if (mw is null) return; // Sitzung abgelaufen -> nächster Durchlauf meldet neu an
        _watt = double.TryParse(mw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) ? m / 1000.0 : null;

        // Verbrauch des laufenden PCs mitzählen (Watt × Zeit), für die Standby-Rechnung
        var jetzt = DateTime.Now;
        if (_watt is double w && _letzteWattZeit != DateTime.MinValue)
        {
            var wh = w * Math.Min(60, (jetzt - _letzteWattZeit).TotalSeconds) / 3600.0;
            _laufWhSeitZaehler += wh; _laufWhSeitStart += wh;
        }
        _letzteWattZeit = jetzt;

        // Verbrauchsverlauf der FRITZ!Box alle 5 Minuten (zählt auch Nacht und Standby)
        if ((DateTime.Now - _letzteStatistik).TotalMinutes >= 5)
        {
            var gesamt = await BefehlAsync("getswitchenergy");
            if (gesamt is not null && double.TryParse(gesamt.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var wh))
            {
                Stand.GesamtKWh = wh / 1000.0;
                StandbyRechnen(wh);
                _zaehlerWh = wh; _zaehlerZeit = DateTime.Now; _laufWhSeitZaehler = 0;
            }

            var xml = await BefehlAsync("getbasicdevicestats");
            if (xml is not null) StatistikAuswerten(xml);
            _letzteStatistik = DateTime.Now;
        }
        // Merkzettel jede Minute schreiben, damit er beim Herunterfahren aktuell ist
        if (_zaehlerWh is double zw && (DateTime.Now - _letzterMerker).TotalSeconds >= 60)
        {
            _letzterMerker = DateTime.Now;
            var merker = new StandbyMerker { ZaehlerWh = zw, ZaehlerZeit = _zaehlerZeit, LaufWhSeitZaehler = _laufWhSeitZaehler, ZuletztAn = DateTime.Now };
            try { File.WriteAllText(MerkerDatei, System.Text.Json.JsonSerializer.Serialize(merker, Einstellungen.Json)); } catch { }
        }
        Stand.Verbunden = _watt is not null;
        Stand.Meldung = Stand.Verbunden ? "" : "Steckdose meldet keinen Wert";
        Stand.Stand = DateTime.Now.ToString("HH:mm:ss");
    }

    // Beim ersten Zählerstand nach dem Start: was hat die Steckdose verbraucht, während der PC aus war?
    // Zählerdifferenz minus das, was der PC vor dem Herunterfahren und seit dem Programmstart selbst verbraucht hat.
    private void StandbyRechnen(double whJetzt)
    {
        if (_standbyGerechnet) return;
        _standbyGerechnet = true;
        var m = _alterMerker;
        if (m is null) return;
        var stunden = (_programmStart - m.ZuletztAn).TotalHours;
        if (stunden < 10.0 / 60) return; // nur Programm neu gestartet, PC war nicht aus
        var standbyWh = whJetzt - m.ZaehlerWh - m.LaufWhSeitZaehler - _laufWhSeitStart;
        if (standbyWh < 0) standbyWh = 0;
        Stand.StandbyKWh = Math.Round(standbyWh / 1000.0, 3);
        Stand.StandbyWatt = Math.Round(standbyWh / stunden, 1);
        Stand.StandbyVon = m.ZuletztAn.ToString("dd.MM. HH:mm");
        Stand.StandbyBis = _programmStart.ToString("dd.MM. HH:mm");
        Log.Schreiben($"Strom: Standby {Stand.StandbyVon} – {Stand.StandbyBis}: {Math.Round(standbyWh)} Wh, Ø {Stand.StandbyWatt} W.");
        try { File.WriteAllText(StandbyDatei, System.Text.Json.JsonSerializer.Serialize(Stand, Einstellungen.Json)); } catch { }
    }

    private void StatistikAuswerten(string xml)
    {
        var doc = XDocument.Parse(xml);
        var energie = doc.Root?.Element("energy");
        if (energie is null) return;
        List<double?>? tagesWerte = null, monatsWerte = null;
        foreach (var st in energie.Elements("stats"))
        {
            var grid = (int?)st.Attribute("grid") ?? 0;
            var werte = st.Value.Split(',').Select(w => double.TryParse(w.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : (double?)null).ToList();
            if (werte.Count == 0) continue;
            if (grid == 86400) { Stand.HeuteKWh = werte[0] / 1000.0; tagesWerte = werte; }        // Tageswerte, neuester zuerst
            else if (grid >= 2419200) { Stand.MonatKWh = werte[0] / 1000.0; monatsWerte = werte; } // Monatswerte, neuester zuerst
        }
        if (_messbeginn is null)
        {
            _messbeginn = DateTime.Today;
            try { File.WriteAllText(_beginnDatei, DateTime.Today.ToString("yyyy-MM-dd")); } catch { }
        }
        var monatsAnfang = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        var ab = _messbeginn.Value > monatsAnfang ? _messbeginn.Value : monatsAnfang;
        var tage = (DateTime.Today - ab).Days + 1;
        if (Stand.MonatKWh is not null) Stand.SchnittTagKWh = Stand.MonatKWh / Math.Max(1, tage);
        Stand.KostenHeuteEuro = Stand.HeuteKWh * _preisCent / 100.0;
        Stand.KostenMonatEuro = Stand.MonatKWh * _preisCent / 100.0;
        VerlaufSpeichern(tagesWerte, monatsWerte);
    }

    // Verbrauch pro Tag und Monat dauerhaft ablegen (FRITZ!Box hält nur 31 Tage / 24 Monate)
    public sealed class StromVerlauf
    {
        public SortedDictionary<string, double> Tage { get; set; } = new();
        public SortedDictionary<string, double> Monate { get; set; } = new();
    }
    public string VerlaufDatei => Path.Combine(Path.GetDirectoryName(_beginnDatei)!, "strom-verlauf.json");
    private readonly object _verlaufSperre = new();

    public StromVerlauf VerlaufLesen()
    {
        lock (_verlaufSperre)
        {
            try { if (File.Exists(VerlaufDatei)) return System.Text.Json.JsonSerializer.Deserialize<StromVerlauf>(File.ReadAllText(VerlaufDatei), Einstellungen.Json) ?? new(); }
            catch { }
            return new();
        }
    }

    private void VerlaufSpeichern(List<double?>? tage, List<double?>? monate)
    {
        var v = VerlaufLesen();
        var beginn = _messbeginn ?? DateTime.Today;
        if (tage != null)
            for (int i = 0; i < tage.Count; i++)
            {
                var d = DateTime.Today.AddDays(-i);
                if (d < beginn || tage[i] is null) continue;
                v.Tage[d.ToString("yyyy-MM-dd")] = Math.Round(tage[i]!.Value / 1000.0, 3);
            }
        if (monate != null)
            for (int i = 0; i < monate.Count; i++)
            {
                var d = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(-i);
                if (d < new DateTime(beginn.Year, beginn.Month, 1) || monate[i] is null) continue;
                v.Monate[d.ToString("yyyy-MM")] = Math.Round(monate[i]!.Value / 1000.0, 3);
            }
        lock (_verlaufSperre)
        {
            try { File.WriteAllText(VerlaufDatei, System.Text.Json.JsonSerializer.Serialize(v, Einstellungen.Json)); } catch { }
        }
    }

    private async Task<string?> BefehlAsync(string befehl)
    {
        var url = $"{_f.Adresse.TrimEnd('/')}/webservices/homeautoswitch.lua?ain={Uri.EscapeDataString(_f.Ain.Replace(" ", ""))}&switchcmd={befehl}&sid={_sid}";
        using var antwort = await _http.GetAsync(url);
        if ((int)antwort.StatusCode == 403) { _sid = "0000000000000000"; return null; }
        antwort.EnsureSuccessStatusCode();
        return await antwort.Content.ReadAsStringAsync();
    }

    // ---------- Anmeldung (FRITZ!OS: PBKDF2, ältere Boxen: MD5) ----------
    private async Task<bool> AngemeldetAsync()
    {
        if (_sid != "0000000000000000") return true;
        if (DateTime.Now < _naechsterLoginVersuch) return false;

        var basis = _f.Adresse.TrimEnd('/') + "/login_sid.lua?version=2";
        var info = XDocument.Parse(await _http.GetStringAsync(basis)).Root!;
        var challenge = info.Element("Challenge")?.Value ?? "";
        var antwortText = challenge.StartsWith("2$") ? Pbkdf2Antwort(challenge, _f.Kennwort) : Md5Antwort(challenge, _f.Kennwort);

        using var inhalt = new FormUrlEncodedContent(new Dictionary<string, string> { ["username"] = _f.Benutzer, ["response"] = antwortText });
        var ergebnis = XDocument.Parse(await (await _http.PostAsync(basis, inhalt)).Content.ReadAsStringAsync()).Root!;
        _sid = ergebnis.Element("SID")?.Value ?? "0000000000000000";
        if (_sid == "0000000000000000")
        {
            // Falsches Kennwort: nicht dauernd probieren, sonst sperrt die FRITZ!Box
            _naechsterLoginVersuch = DateTime.Now.AddMinutes(5);
            Stand.Verbunden = false; _watt = null;
            Stand.Meldung = "Anmeldung an der FRITZ!Box abgelehnt (Benutzer/Kennwort prüfen)";
            Log.Schreiben("Strom: " + Stand.Meldung);
            return false;
        }
        Log.Schreiben("Strom: an der FRITZ!Box angemeldet.");
        return true;
    }

    private static string Pbkdf2Antwort(string challenge, string kennwort)
    {
        var t = challenge.Split('$');
        int iter1 = int.Parse(t[1]), iter2 = int.Parse(t[3]);
        var salt1 = Convert.FromHexString(t[2]);
        var salt2 = Convert.FromHexString(t[4]);
        var hash1 = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(kennwort), salt1, iter1, HashAlgorithmName.SHA256, 32);
        var hash2 = Rfc2898DeriveBytes.Pbkdf2(hash1, salt2, iter2, HashAlgorithmName.SHA256, 32);
        return t[4] + "$" + Convert.ToHexString(hash2).ToLowerInvariant();
    }

    private static string Md5Antwort(string challenge, string kennwort)
    {
        var bytes = Encoding.Unicode.GetBytes(challenge + "-" + kennwort);
        return challenge + "-" + Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant();
    }
}
