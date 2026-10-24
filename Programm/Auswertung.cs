using System.Text.Json;

namespace Atomkraftwerk;

// ---------------------------------------------------------------
// Auswertung über Zeiträume: Heute (Minuten), Woche (Stunden),
// Monat/Quartal (Tage), Jahr (Monate). Bereiche: Wasser und Steckdose.
// ---------------------------------------------------------------
public sealed class Auswertung
{
    private readonly Anlage _anlage;
    private readonly FritzQuelle _fritz;

    public Auswertung(Anlage anlage, FritzQuelle fritz) { _anlage = anlage; _fritz = fritz; }

    private record Wert(string Bereich, string Id, string Name, string Einheit);

    private List<Wert> Werte()
    {
        var info = _anlage.WerteInfo();
        string? Finde(Func<(string id, string name, string gruppe, string einheit), bool> f) => info.Where(f).Select(i => i.id).FirstOrDefault();
        var liste = new List<Wert>();
        void Dazu(string bereich, string? id, string? name = null)
        {
            if (id is null) return;
            var i = info.FirstOrDefault(x => x.id == id);
            liste.Add(new Wert(bereich, id, name ?? i.name ?? id, i.einheit ?? ""));
        }
        Dazu("Wasser", "flow.durchfluss");
        Dazu("Wasser", Finde(i => i.id.StartsWith("icue.") && i.einheit == "°C" && System.Text.RegularExpressions.Regex.IsMatch(i.name, "beh|wasser|coolant", System.Text.RegularExpressions.RegexOptions.IgnoreCase)), "Wasser im Behälter");
        Dazu("Wasser", "flow.wasser");
        Dazu("Wasser", Finde(i => i.id.StartsWith("icue.") && i.name.Contains("pumpe", StringComparison.OrdinalIgnoreCase)), "Pumpe");
        Dazu("Wasser", "flow.leitwert");
        Dazu("Wasser", "flow.qualitaet");
        Dazu("Steckdose", "strom.watt", "Leistung");
        foreach (var lw in info.Where(i => i.id.StartsWith("lw."))) Dazu("Laufwerke", lw.id);
        liste.Add(new Wert("Steckdose", "strom.kwh", "Verbrauch", "kWh"));
        return liste;
    }

    public string ListeJson() =>
        JsonSerializer.Serialize(Werte().Select(w => new { bereich = w.Bereich, id = w.Id, name = w.Name, einheit = w.Einheit }), Einstellungen.Json);

    public string DatenJson(string zeitraum, string id)
    {
        var w = Werte().FirstOrDefault(x => x.Id == id);
        if (w is null) return JsonSerializer.Serialize(new { punkte = Array.Empty<object>() }, Einstellungen.Json);
        var heute = DateTime.Today;
        var punkte = new List<(DateTime t, double v, double max)>();
        string art = "linie", schritt;

        if (id == "strom.kwh")
        {
            art = "balken";
            var verlauf = _fritz.VerlaufLesen();
            if (zeitraum == "jahr")
            {
                schritt = "monat";
                for (int i = 11; i >= 0; i--)
                {
                    var m = new DateTime(heute.Year, heute.Month, 1).AddMonths(-i);
                    if (verlauf.Monate.TryGetValue(m.ToString("yyyy-MM"), out var k)) punkte.Add((m, k, k));
                }
            }
            else if (zeitraum == "heute")
            {
                // Heute: aufsummierter Verbrauch aus den Minutenwerten (zählt nur, solange das Programm läuft)
                art = "linie"; schritt = "minute";
                var (zeilen, _) = _anlage.TagLesen(heute.ToString("yyyy-MM-dd"));
                double summe = 0;
                foreach (var (zeit, z) in zeilen)
                    if (z.TryGetValue("strom.watt", out var watt)) { summe += watt / 60000.0; punkte.Add((heute.Add(TimeSpan.Parse(zeit)), Math.Round(summe, 3), Math.Round(summe, 3))); }
            }
            else
            {
                schritt = "tag";
                int tage = zeitraum == "woche" ? 7 : zeitraum == "quartal" ? 90 : 30;
                for (int i = tage - 1; i >= 0; i--)
                {
                    var d = heute.AddDays(-i);
                    if (verlauf.Tage.TryGetValue(d.ToString("yyyy-MM-dd"), out var k)) punkte.Add((d, k, k));
                }
            }
        }
        else if (zeitraum == "heute")
        {
            schritt = "minute";
            var (zeilen, _) = _anlage.TagLesen(heute.ToString("yyyy-MM-dd"));
            foreach (var (zeit, z) in zeilen)
                if (z.TryGetValue(id, out var v)) punkte.Add((heute.Add(TimeSpan.Parse(zeit)), v, v));
        }
        else
        {
            // Minutenwerte der Tage einsammeln und zusammenfassen
            int tage = zeitraum switch { "woche" => 7, "monat" => 30, "quartal" => 90, _ => 366 };
            schritt = zeitraum switch { "woche" => "stunde", "jahr" => "monat", _ => "tag" };
            var eimer = new SortedDictionary<DateTime, (double summe, int n, double max)>();
            for (int i = tage - 1; i >= 0; i--)
            {
                var d = heute.AddDays(-i);
                var (zeilen, _) = _anlage.TagLesen(d.ToString("yyyy-MM-dd"));
                foreach (var (zeit, z) in zeilen)
                {
                    if (!z.TryGetValue(id, out var v)) continue;
                    var t = d.Add(TimeSpan.Parse(zeit));
                    var k = schritt switch { "stunde" => new DateTime(t.Year, t.Month, t.Day, t.Hour, 0, 0), "monat" => new DateTime(t.Year, t.Month, 1), _ => t.Date };
                    eimer.TryGetValue(k, out var e);
                    eimer[k] = (e.summe + v, e.n + 1, e.n == 0 ? v : Math.Max(e.max, v));
                }
            }
            foreach (var (k, e) in eimer) punkte.Add((k, Math.Round(e.summe / e.n, 2), Math.Round(e.max, 2)));
        }

        // Ganzen Zeitraum zeigen, auch wenn erst wenige Werte da sind
        var jetzt = DateTime.Now;
        DateTime bereichVon = zeitraum switch
        {
            "woche" => heute.AddDays(-6),
            "monat" => heute.AddDays(-29),
            "quartal" => heute.AddDays(-89),
            "jahr" => new DateTime(heute.Year, heute.Month, 1).AddMonths(-11),
            _ => punkte.Count > 0 ? punkte[0].t : heute
        };
        DateTime bereichBis = zeitraum switch
        {
            "jahr" => new DateTime(heute.Year, heute.Month, 1),
            "heute" => punkte.Count > 0 ? punkte[^1].t : jetzt,
            _ => schritt == "tag" ? heute : jetzt
        };
        return JsonSerializer.Serialize(new
        {
            name = w.Name, einheit = w.Einheit, art, schritt,
            von = bereichVon.ToString("yyyy-MM-ddTHH:mm"), bis = bereichBis.ToString("yyyy-MM-ddTHH:mm"),
            punkte = punkte.Select(p => new { t = p.t.ToString("yyyy-MM-ddTHH:mm"), v = Math.Round(p.v, 3), max = Math.Round(p.max, 3) })
        }, Einstellungen.Json);
    }
}
