using System.Globalization;
using System.Text;
using LibreHardwareMonitor.Hardware;

namespace Atomkraftwerk;

// ---------------------------------------------------------------
// CPU, GPU, RAM und Board über LibreHardwareMonitor
// ---------------------------------------------------------------
public sealed class HardwareQuelle : IDisposable
{
    private readonly Computer _pc = new()
    {
        IsCpuEnabled = true, IsGpuEnabled = true, IsMotherboardEnabled = true, IsMemoryEnabled = true,
        IsControllerEnabled = true, // Aquacomputer high flow NEXT (USB)
        IsStorageEnabled = true     // Laufwerkstemperaturen
    };

    private ISensor? _cpuTemp, _cpuWatt, _cpuLast, _gpuTemp, _gpuHot, _gpuWatt, _gpuLast, _gpuLuefter, _ram, _vrm, _soc, _pch;
    private ISensor? _fluss, _flussWasser, _leitwert, _qualitaet;
    private readonly List<(string id, string modell, ISensor temp)> _laufwerke = new();
    // Lüfter- und Pumpenanschlüsse am Board (Drehzahl + Ansteuerung in %), nur lesen
    private readonly List<(string id, string name, string einheit, ISensor s)> _boardLuefter = new();
    public bool Offen { get; private set; }

    public void Oeffnen()
    {
        _pc.Open();
        Offen = true;
        AllesAktualisieren();
        SensorenSuchen();
    }

    private static IEnumerable<IHardware> Alle(IHardware h)
    {
        yield return h;
        foreach (var s in h.SubHardware) foreach (var x in Alle(s)) yield return x;
    }

    private IEnumerable<IHardware> AlleHardware => _pc.Hardware.SelectMany(Alle);

    private static ISensor? Finde(IEnumerable<IHardware> hw, SensorType typ, string name) =>
        hw.SelectMany(h => h.Sensors).FirstOrDefault(s => s.SensorType == typ && s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private void SensorenSuchen()
    {
        var cpu = AlleHardware.Where(h => h.HardwareType == HardwareType.Cpu).ToList();
        _cpuTemp = Finde(cpu, SensorType.Temperature, "Core (Tctl/Tdie)") ?? cpu.SelectMany(h => h.Sensors).FirstOrDefault(s => s.SensorType == SensorType.Temperature);
        _cpuWatt = Finde(cpu, SensorType.Power, "Package");
        _cpuLast = Finde(cpu, SensorType.Load, "CPU Total");

        // Grafikkarte: die mit dem meisten Speicher (nicht die Grafik im Prozessor)
        var gpus = AlleHardware.Where(h => h.HardwareType is HardwareType.GpuAmd or HardwareType.GpuNvidia or HardwareType.GpuIntel).ToList();
        var gpu = gpus.OrderByDescending(g => g.Sensors.FirstOrDefault(s => s.SensorType == SensorType.SmallData && s.Name == "GPU Memory Total")?.Value ?? 0).FirstOrDefault();
        var g1 = gpu is null ? new List<IHardware>() : new List<IHardware> { gpu };
        _gpuTemp = Finde(g1, SensorType.Temperature, "GPU Core");
        _gpuHot = Finde(g1, SensorType.Temperature, "GPU Hot Spot");
        _gpuWatt = Finde(g1, SensorType.Power, "GPU Package") ?? Finde(g1, SensorType.Power, "GPU Core");
        _gpuLast = Finde(g1, SensorType.Load, "GPU Core");
        _gpuLuefter = Finde(g1, SensorType.Fan, "GPU Fan");
        GpuName = gpu?.Name ?? "";

        var mem = AlleHardware.Where(h => h.HardwareType == HardwareType.Memory).ToList();
        _ram = Finde(mem, SensorType.Data, "Memory Used");

        // Board-Chip ITE IT8696E (Gigabyte X870E Aorus Elite): Zuordnung per Vergleich mit HWiNFO am 23.10.2026
        var sio = AlleHardware.Where(h => h.HardwareType == HardwareType.SuperIO).ToList();
        _vrm = Finde(sio, SensorType.Temperature, "Temperature #5"); // VRM MOS
        _soc = Finde(sio, SensorType.Voltage, "Voltage #5");         // CPU VCORE SoC
        _pch = Finde(sio, SensorType.Temperature, "Temperature #2"); // PCH
        // Durchflusssensor Aquacomputer high flow NEXT
        var flow = AlleHardware.Where(h => h.Name.Contains("high flow", StringComparison.OrdinalIgnoreCase)).ToList();
        _fluss = flow.SelectMany(h => h.Sensors).FirstOrDefault(s => s.SensorType == SensorType.Flow);
        _flussWasser = Finde(flow, SensorType.Temperature, "Water Temperature")
                       ?? flow.SelectMany(h => h.Sensors).FirstOrDefault(s => s.SensorType == SensorType.Temperature && !s.Name.Contains("External"));
        _leitwert = flow.SelectMany(h => h.Sensors).FirstOrDefault(s => s.Name.Equals("Conductivity", StringComparison.OrdinalIgnoreCase));
        _qualitaet = flow.SelectMany(h => h.Sensors).FirstOrDefault(s => s.Name.Equals("Water Quality", StringComparison.OrdinalIgnoreCase));
        Log.Schreiben("high flow NEXT: " + (flow.Count == 0 ? "nicht gefunden" :
            string.Join(", ", flow.SelectMany(h => h.Sensors).Select(s => $"{s.SensorType}/{s.Name}"))));

        // Laufwerke: je Laufwerk die erste Temperatur (bei NVMe "Composite")
        _laufwerke.Clear();
        foreach (var lw in AlleHardware.Where(h => h.HardwareType == HardwareType.Storage))
        {
            var t = lw.Sensors.Where(s => s.SensorType == SensorType.Temperature).OrderBy(s => s.Index).FirstOrDefault();
            if (t is null) continue;
            var (seriennr, buchstaben) = LaufwerkInfo(lw);
            var schluessel = string.IsNullOrEmpty(seriennr) ? lw.Name : seriennr;
            var id = "lw." + new string(schluessel.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
            while (_laufwerke.Any(x => x.id == id)) id += "-2";
            // Kennung für die Namenszuordnung in atomkraftwerk.json: Laufwerksbuchstabe(n), sonst Seriennummer, sonst Modell
            var kurz = lw.Name.Replace("Samsung SSD ", "").Trim();
            var anzeige = buchstaben.Length > 0 ? $"{kurz} ({buchstaben})" : kurz;
            _laufwerke.Add((id, anzeige + "|" + buchstaben + "|" + seriennr + "|" + lw.Name.Trim(), t));
        }
        Log.Schreiben("Laufwerke: " + (_laufwerke.Count == 0 ? "keine Temperatur gefunden" : string.Join(" | ", _laufwerke.Select(l => l.modell))));

        CpuName = cpu.FirstOrDefault()?.Name ?? "";
        BoardName = AlleHardware.FirstOrDefault(h => h.HardwareType == HardwareType.Motherboard)?.Name ?? "";

        Log.Schreiben($"Hardware: {CpuName} | {GpuName} | {BoardName} | Board-Chip: {sio.FirstOrDefault()?.Name ?? "nicht gefunden"}");

        // Lüfter-/Pumpenanschlüsse am Board: alle Chips (IT8696E und IT87952E), nur lesen – es wird nichts verstellt
        _boardLuefter.Clear();
        for (int c = 0; c < sio.Count; c++)
        {
            var chip = sio[c];
            var kurz = new string(chip.Name.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
            foreach (var s in chip.Sensors.Where(s => s.SensorType is SensorType.Fan or SensorType.Control).OrderBy(s => s.SensorType).ThenBy(s => s.Index))
            {
                var art = s.SensorType == SensorType.Fan ? "rpm" : "%";
                var id = $"bl.{kurz}.{(s.SensorType == SensorType.Fan ? "fan" : "ctl")}{s.Index + 1}";
                _boardLuefter.Add((id, $"{chip.Name} {s.Name}", art, s));
            }
            var zeilen = chip.Sensors.Where(s => s.SensorType is SensorType.Fan or SensorType.Control).OrderBy(s => s.Index).ThenBy(s => s.SensorType)
                .Select(s => s.SensorType == SensorType.Fan
                    ? $"{s.Name} = {(s.Value is float f ? Math.Round(f) + " rpm" : "—")}"
                    : $"{s.Name} = {(s.Value is float p ? Math.Round(p) + " %" : "—")}, steuerbar: {(s.Control is null ? "NEIN" : "ja (" + s.Control.ControlMode + ")")}");
            Log.Schreiben($"Board-Anschlüsse {chip.Name}: " + (zeilen.Any() ? string.Join(" | ", zeilen) : "keine gefunden"));
        }
    }

    // Laufwerksbuchstaben und Seriennummer über die DiskInfoToolkit-Daten von LibreHardwareMonitor (per Reflection, ohne feste Abhängigkeit)
    private static (string serie, string buchstaben) LaufwerkInfo(IHardware hw)
    {
        try
        {
            const System.Reflection.BindingFlags alle = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var st = hw.GetType().GetProperty("Storage", alle)?.GetValue(hw);
            if (st is null) Log.Schreiben("Laufwerk " + hw.Name + ": keine Detaildaten (" + hw.GetType().FullName + ")");
            if (st is null) return ("", "");
            var serie = (st.GetType().GetProperty("SerialNumber", alle)?.GetValue(st)?.ToString() ?? "").Trim();
            var liste = new List<string>();
            if (st.GetType().GetProperty("Partitions", alle)?.GetValue(st) is System.Collections.IEnumerable teile)
                foreach (var t in teile)
                {
                    var b = t?.GetType().GetProperty("DriveLetter", alle)?.GetValue(t)?.ToString()?.Trim().TrimEnd(':', '\\');
                    if (!string.IsNullOrEmpty(b) && b != "\0" && char.IsLetter(b[0])) liste.Add(b[0] + ":");
                }
            return (serie, string.Join(" ", liste.Distinct().OrderBy(x => x)));
        }
        catch (Exception ex) { Log.Schreiben("Laufwerk " + hw.Name + ": " + ex.Message); return ("", ""); }
    }

    // Board-Anschluss (z. B. "Fan #6") mit Drehzahl- und Steuer-Sensor
    public (ISensor? fan, ISensor? ctl) Anschluss(string name)
    {
        var sio = AlleHardware.Where(h => h.HardwareType == HardwareType.SuperIO).SelectMany(h => h.Sensors).ToList();
        return (sio.FirstOrDefault(s => s.SensorType == SensorType.Fan && s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)),
                sio.FirstOrDefault(s => s.SensorType == SensorType.Control && s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)));
    }

    public string CpuName { get; private set; } = "";
    public string GpuName { get; private set; } = "";
    public string BoardName { get; private set; } = "";

    private void AllesAktualisieren()
    {
        foreach (var h in _pc.Hardware) Aktualisieren(h);
    }

    private static void Aktualisieren(IHardware h)
    {
        h.Update();
        foreach (var s in h.SubHardware) Aktualisieren(s);
    }

    public void Lesen(Anlage a)
    {
        if (!Offen) return;
        AllesAktualisieren();
        a.Wert("cpu.temp").Setzen(_cpuTemp?.Value);
        a.Wert("cpu.watt").Setzen(_cpuWatt?.Value);
        a.Wert("cpu.last").Setzen(_cpuLast?.Value);
        a.Wert("gpu.temp").Setzen(_gpuTemp?.Value);
        a.Wert("gpu.hotspot").Setzen(_gpuHot?.Value);
        a.Wert("gpu.watt").Setzen(_gpuWatt?.Value);
        a.Wert("gpu.last").Setzen(_gpuLast?.Value);
        a.Wert("gpu.luefter").Setzen(_gpuLuefter?.Value);
        a.Wert("ram.belegt").Setzen(_ram?.Value);
        a.Wert("board.vrm").Setzen(_vrm?.Value);
        a.Wert("board.soc").Setzen(_soc?.Value);
        a.Wert("board.pch").Setzen(_pch?.Value);
        a.Wert("flow.durchfluss").Setzen(_fluss?.Value);
        a.Wert("flow.wasser").Setzen(_flussWasser?.Value);
        a.Wert("flow.leitwert").Setzen(_leitwert?.Value);
        a.Wert("flow.qualitaet").Setzen(_qualitaet?.Value);
        foreach (var (id, info, temp) in _laufwerke)
            a.Wert(id, a.LaufwerkName(info), "Laufwerke", "°C").Setzen(temp.Value);
        foreach (var (id, name, einheit, s) in _boardLuefter)
            a.Wert(id, name, "Board-Lüfter", einheit).Setzen(s.Value);
    }

    public void Dispose()
    {
        try { if (Offen) _pc.Close(); } catch { }
    }
}

// ---------------------------------------------------------------
// Wasser, Pumpe, Lüfter (und später Durchfluss) aus der iCUE-Sensorprotokollierung
// ---------------------------------------------------------------
public sealed class IcueQuelle
{
    private readonly Einstellungen _e;
    public IcueQuelle(Einstellungen e) { _e = e; }

    public string Datei { get; private set; } = "";
    public string Stand { get; private set; } = "";
    public bool Laeuft { get; private set; }
    // hängt die Pumpe am Board, kommt ihre Drehzahl nicht mehr aus iCUE
    public bool PumpeVomBoard { get; set; }

    public void Lesen(Anlage a)
    {
        Laeuft = false;
        string? datei = null;
        try
        {
            var ordner = _e.IcuePfad();
            if (Directory.Exists(ordner))
                datei = new DirectoryInfo(ordner).GetFiles("corsair_cue_*.csv").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()?.FullName;
        }
        catch { }
        if (datei is null) { Datei = "keine iCUE-Protokolldatei"; return; }
        Datei = Path.GetFileName(datei);
        Laeuft = (DateTime.UtcNow - File.GetLastWriteTimeUtc(datei)).TotalSeconds < 180;

        var (kopf, letzte) = KopfUndLetzte(datei);
        if (kopf is null || letzte is null) return;
        var namen = Spalten(kopf);
        var werte = Spalten(letzte);
        if (werte.Count > 0) Stand = werte[0].Trim();
        // Sensoren, die in der aktuellen iCUE-Datei nicht mehr vorkommen (umbenannt), nicht weiter führen
        var aktuelle = namen.Skip(1).Select(n => "icue." + IdAus(n.Trim())).ToHashSet();
        a.Entfernen(id => id.StartsWith("icue.") && !aktuelle.Contains(id) && !(PumpeVomBoard && id == PumpenSteuerung.Id));
        for (int i = 1; i < namen.Count && i < werte.Count; i++)
        {
            var name = namen[i].Trim();
            var id = "icue." + IdAus(name);
            if (PumpeVomBoard && (id == PumpenSteuerung.Id || name.Contains("pump", StringComparison.OrdinalIgnoreCase))) continue;
            var mw = a.Wert(id, name, "Kühlung", EinheitAus(name));
            mw.Setzen(Laeuft && ZahlAus(werte[i], out var v) ? v : null);
        }
    }

    private static string IdAus(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name.ToLowerInvariant())
            sb.Append(char.IsLetterOrDigit(c) ? c : '-');
        return sb.ToString().Trim('-');
    }

    private static string EinheitAus(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("rpm") || n.Contains("lüfter") || n.Contains("pumpe") || n.Contains("fan") || n.Contains("pump")) return "rpm";
        if (n.Contains("load") || n.Contains("auslastung")) return "%";
        if (n.Contains("volt")) return "V";
        return "°C";
    }

    private static (string?, string?) KopfUndLetzte(string datei)
    {
        using var fs = new FileStream(datei, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        string? kopf;
        using (var sr = new StreamReader(fs, Encoding.UTF8, true, 4096, leaveOpen: true)) kopf = sr.ReadLine();
        long start = Math.Max(0, fs.Length - 8192);
        fs.Seek(start, SeekOrigin.Begin);
        using var sr2 = new StreamReader(fs, Encoding.UTF8, false);
        var zeilen = sr2.ReadToEnd().Split('\n').Select(z => z.Trim('\r', ' ', '﻿')).Where(z => z.Length > 0).ToList();
        if (start > 0 && zeilen.Count > 0) zeilen.RemoveAt(0);
        var letzte = zeilen.LastOrDefault();
        if (letzte is not null && kopf is not null && letzte.Trim('﻿') == kopf.Trim('﻿')) letzte = null;
        return (kopf?.Trim('﻿'), letzte);
    }

    private static List<string> Spalten(string zeile)
    {
        var liste = new List<string>(); var sb = new StringBuilder(); bool inAnf = false;
        foreach (var c in zeile)
        {
            if (c == '"') { inAnf = !inAnf; continue; }
            if (c == ',' && !inAnf) { liste.Add(sb.ToString()); sb.Clear(); continue; }
            sb.Append(c);
        }
        liste.Add(sb.ToString());
        return liste;
    }

    private static bool ZahlAus(string text, out double wert)
    {
        var sb = new StringBuilder();
        foreach (var c in text.Trim()) if (char.IsDigit(c) || c is '.' or ',' or '-') sb.Append(c);
        var s = sb.ToString();
        s = s.Contains(',') && !s.Contains('.') ? s.Replace(',', '.') : s.Replace(",", "");
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out wert);
    }
}
