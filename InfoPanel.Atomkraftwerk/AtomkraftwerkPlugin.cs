using System.Globalization;
using System.Text.Json;
using InfoPanel.Plugins;

namespace InfoPanel.Atomkraftwerk;

// Holt Durchfluss (high flow NEXT) und Strom (FRITZ!DECT) vom Atomkraftwerk-Leitstand
// und stellt sie als fertige Texte für die Anzeige bereit.
public class AtomkraftwerkPlugin : BasePlugin
{
    private const string Adresse = "http://localhost:8090/api/jetzt";
    private static readonly CultureInfo Punkt = CultureInfo.InvariantCulture;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(1.5) };

    private readonly PluginText _fluss = new("fluss", "Durchfluss (Anzeige)", "--- l/h");
    private readonly PluginText _flussSchnitt = new("fluss-avg", "Durchfluss Ø (Anzeige)", "(Ø --- l/h)");
    private readonly PluginText _pcWatt = new("pc-watt", "PC Leistung (Anzeige)", "PC --- W");
    private readonly PluginSensor _flussWert = new("fluss-wert", "Durchfluss", 0, "l/h");
    private readonly PluginSensor _flussSchnittWert = new("fluss-avg-wert", "Durchfluss Ø", 0, "l/h");
    private readonly PluginSensor _wattWert = new("pc-watt-wert", "PC Leistung", 0, "W");
    private readonly PluginText _status = new("status", "Verbindung", "-");

    public AtomkraftwerkPlugin() : base("atomkraftwerk", "Atomkraftwerk", "Durchfluss und Strom vom Atomkraftwerk-Leitstand") { }

    public override string? ConfigFilePath => null;
    public override TimeSpan UpdateInterval => TimeSpan.FromSeconds(2);

    public override void Initialize() { }

    public override void Load(List<IPluginContainer> containers)
    {
        var c = new PluginContainer("akw", "Atomkraftwerk");
        c.Entries.Add(_fluss); c.Entries.Add(_flussSchnitt); c.Entries.Add(_pcWatt);
        c.Entries.Add(_flussWert); c.Entries.Add(_flussSchnittWert); c.Entries.Add(_wattWert);
        c.Entries.Add(_status);
        containers.Add(c);
    }

    public override async Task UpdateAsync(CancellationToken cancellationToken)
    {
        try
        {
            var json = await _http.GetStringAsync(Adresse, cancellationToken);
            using var doc = JsonDocument.Parse(json);
            double? fluss = null, schnitt = null, watt = null;
            foreach (var w in doc.RootElement.GetProperty("werte").EnumerateArray())
            {
                var id = w.GetProperty("id").GetString();
                if (id == "flow.durchfluss") { fluss = Zahl(w, "aktuell"); schnitt = Zahl(w, "schnitt"); }
                else if (id == "strom.watt") watt = Zahl(w, "aktuell");
            }
            _fluss.Value = fluss is null ? "--- l/h" : $"{Math.Round(fluss.Value).ToString("0", Punkt)} l/h";
            _flussSchnitt.Value = schnitt is null ? "(Ø --- l/h)" : $"(Ø {schnitt.Value.ToString("0.0", Punkt)} l/h)";
            _pcWatt.Value = watt is null ? "PC --- W" : $"PC {Math.Round(watt.Value).ToString("0", Punkt)} W";
            _flussWert.Value = (float)(fluss ?? 0);
            _flussSchnittWert.Value = (float)(schnitt ?? 0);
            _wattWert.Value = (float)(watt ?? 0);
            _status.Value = "verbunden";
        }
        catch
        {
            _fluss.Value = "--- l/h"; _flussSchnitt.Value = "(Ø --- l/h)"; _pcWatt.Value = "PC --- W";
            _status.Value = "Atomkraftwerk nicht erreichbar";
        }
    }

    public override void Update() { }

    public override void Close() => _http.Dispose();

    private static double? Zahl(JsonElement w, string feld) =>
        w.TryGetProperty(feld, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
}
