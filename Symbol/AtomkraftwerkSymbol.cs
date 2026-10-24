// =====================================================================
// ATOMKRAFTWERK.EXE – ein Symbol für alles
//  Doppelklick: startet das Atomkraftwerk (falls es nicht läuft) und
//  öffnet den Leitstand als eigenes Fenster (Edge im App-Modus, ohne Adressleiste).
//  Fenster schließen: Das Symbol bleibt unten rechts bei den ausgeblendeten Symbolen.
//  Klick aufs Symbol: Leitstand wieder öffnen. Rechtsklick: Menü.
// =====================================================================
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

static class AtomkraftwerkApp
{
    const string Url = "http://localhost:8090/";
    const string FensterTitel = "Atomkraftwerk Leitstand";
    static NotifyIcon symbol;
    static string ordner;
    static Control ui;

    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern IntPtr FindWindow(string cls, string titel);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr l);

    [STAThread]
    static void Main(string[] args)
    {
        ordner = AppDomain.CurrentDomain.BaseDirectory;
        bool autostart = args.Length > 0 && args[0].Equals("/autostart", StringComparison.OrdinalIgnoreCase);

        bool erstes;
        var einmal = new Mutex(true, "Atomkraftwerk_Symbol_OAPC", out erstes);
        if (!erstes)
        {
            // Symbol läuft schon: Programm anwerfen falls aus, dann Leitstand holen
            if (autostart) return;
            if (!Erreichbar()) { ProgrammStarten(); for (int i = 0; i < 90 && !Erreichbar(); i++) Thread.Sleep(1000); }
            LeitstandZeigen();
            return;
        }

        Application.EnableVisualStyles();
        ui = new Control(); ui.CreateControl();

        symbol = new NotifyIcon();
        try { symbol.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { symbol.Icon = SystemIcons.Application; }
        symbol.Text = "Atomkraftwerk – starte …";
        var menu = new ContextMenuStrip();
        var oeffnen = menu.Items.Add("Leitstand öffnen", null, (s, e) => Oeffnen());
        oeffnen.Font = new Font(oeffnen.Font, FontStyle.Bold);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Atomkraftwerk neu starten", null, (s, e) => new Thread(NeuStarten) { IsBackground = true }.Start());
        menu.Items.Add("Atomkraftwerk beenden", null, (s, e) => ProgrammBeenden());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Symbol schließen", null, (s, e) => { symbol.Visible = false; Application.Exit(); });
        symbol.ContextMenuStrip = menu;
        symbol.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) Oeffnen(); };
        symbol.Visible = true;

        Verknuepfungen();

        // Start im Hintergrund: Programm anwerfen, warten bis erreichbar, Fenster öffnen
        new Thread(() =>
        {
            if (!Erreichbar())
            {
                try { File.Delete(Path.Combine(ordner, "beenden.flag")); } catch { }
                ProgrammStarten();
            }
            for (int i = 0; i < 90 && !Erreichbar(); i++) Thread.Sleep(1000);
            if (!autostart) Ui(LeitstandZeigen);
        }) { IsBackground = true }.Start();

        var takt = new System.Windows.Forms.Timer { Interval = 5000 };
        takt.Tick += (s, e) => ThreadPool.QueueUserWorkItem(_ => Status());
        takt.Start();
        ThreadPool.QueueUserWorkItem(_ => Status());

        Application.Run();
        symbol.Visible = false;
        GC.KeepAlive(einmal);
    }

    // Leitstand öffnen; läuft das Atomkraftwerk nicht, wird es vorher gestartet
    static void Oeffnen()
    {
        new Thread(() =>
        {
            if (!Erreichbar())
            {
                Ui(() => symbol.Text = "Atomkraftwerk – starte …");
                try { File.Delete(Path.Combine(ordner, "beenden.flag")); } catch { }
                ProgrammStarten();
                for (int i = 0; i < 90 && !Erreichbar(); i++) Thread.Sleep(1000);
            }
            Ui(LeitstandZeigen);
        }) { IsBackground = true }.Start();
    }

    static void Protokoll(string text)
    {
        try { File.AppendAllText(Path.Combine(ordner, "symbol.log"), DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss") + "  " + text + Environment.NewLine); } catch { }
    }

    static void Ui(Action a) { try { if (ui.InvokeRequired) ui.BeginInvoke(a); else a(); } catch { } }

    // ---------- Programm ----------
    static string Holen(string pfad, int ms)
    {
        try
        {
            var r = (HttpWebRequest)WebRequest.Create(Url + pfad);
            r.Timeout = ms; r.ReadWriteTimeout = ms; r.Proxy = null;
            using (var a = r.GetResponse())
            using (var s = new StreamReader(a.GetResponseStream(), Encoding.UTF8)) return s.ReadToEnd();
        }
        catch { return null; }
    }

    static bool Erreichbar() { return Holen("api/handy", 1500) != null; }

    static void ProgrammStarten()
    {
        // Die Aufgabe "Atomkraftwerk" (Autostart einrichten.bat) startet mit Admin-Rechten, ohne Rückfrage
        try
        {
            var p = Process.Start(new ProcessStartInfo("schtasks.exe", "/run /tn \"Atomkraftwerk\"") { CreateNoWindow = true, UseShellExecute = false });
            p.WaitForExit(10000);
            Protokoll("Start über Aufgabe \"Atomkraftwerk\": Rückgabe " + p.ExitCode);
            if (p.ExitCode == 0) return;
        }
        catch (Exception ex) { Protokoll("schtasks: " + ex.Message); }
        // keine Aufgabe eingerichtet: mit Rückfrage (Admin) unsichtbar starten
        try
        {
            Process.Start(new ProcessStartInfo("wscript.exe", "\"" + Path.Combine(ordner, "unsichtbar.vbs") + "\"")
            { UseShellExecute = true, Verb = "runas", WorkingDirectory = ordner });
        }
        catch { }
    }

    static void ProgrammBeenden()
    {
        try { File.WriteAllText(Path.Combine(ordner, "beenden.flag"), "1"); } catch { }
    }

    static void NeuStarten()
    {
        Ui(() => symbol.Text = "Atomkraftwerk – starte neu …");
        ProgrammBeenden();
        for (int i = 0; i < 20 && Erreichbar(); i++) Thread.Sleep(1000);
        Thread.Sleep(2000);
        try { File.Delete(Path.Combine(ordner, "beenden.flag")); } catch { }
        ProgrammStarten();
    }

    // Tooltip am Symbol: Zustand und Wasser
    static void Status()
    {
        var j = Holen("api/jetzt", 3000);
        string text;
        if (j == null) text = "Atomkraftwerk – aus";
        else
        {
            var st = Regex.Match(j, "\"status\"\\s*:\\s*\"(\\w+)\"").Groups[1].Value;
            var stText = st == "alarm" ? "ALARM" : st == "warn" ? "Warnung" : "läuft";
            text = "Atomkraftwerk – " + stText;
            var w = Wert(j, "flow.wasser") ?? Wert(j, "icue.behälter");
            var f = Wert(j, "flow.durchfluss");
            if (w != null) text += "\nWasser " + w + " °C";
            if (f != null) text += (w != null ? " · " : "\n") + f + " l/h";
        }
        if (text.Length > 63) text = text.Substring(0, 63);
        Ui(() => symbol.Text = text);
    }

    static string Wert(string json, string id)
    {
        var m = Regex.Match(json, "\"id\"\\s*:\\s*\"" + Regex.Escape(id) + "\"[^{}]*?\"aktuell\"\\s*:\\s*(-?[0-9.]+)");
        if (!m.Success) return null;
        double d;
        if (!double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d)) return null;
        return d.ToString("0.0", System.Globalization.CultureInfo.GetCultureInfo("de-DE"));
    }

    // ---------- Fenster ----------
    static IntPtr Finden()
    {
        IntPtr gefunden = IntPtr.Zero;
        EnumWindows((h, l) =>
        {
            if (!IsWindowVisible(h)) return true;
            var sb = new StringBuilder(256);
            GetWindowText(h, sb, 256);
            if (sb.ToString().StartsWith(FensterTitel)) { gefunden = h; return false; }
            return true;
        }, IntPtr.Zero);
        return gefunden;
    }

    static void LeitstandZeigen()
    {
        var h = Finden();
        if (h != IntPtr.Zero)
        {
            if (IsIconic(h)) ShowWindow(h, 9);
            SetForegroundWindow(h);
            return;
        }
        var edge = EdgePfad();
        try
        {
            if (edge != null)
                Process.Start(new ProcessStartInfo(edge, "--app=" + Url + " --window-size=1500,950") { UseShellExecute = false });
            else
                Process.Start(Url);
        }
        catch { try { Process.Start(Url); } catch { } }
    }

    static string EdgePfad()
    {
        foreach (var wurzel in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            try
            {
                using (var k = wurzel.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe"))
                {
                    var p = k == null ? null : k.GetValue("") as string;
                    if (!string.IsNullOrEmpty(p) && File.Exists(p)) return p;
                }
            }
            catch { }
        }
        foreach (var p in new[] {
            Environment.ExpandEnvironmentVariables(@"%ProgramFiles(x86)%\Microsoft\Edge\Application\msedge.exe"),
            Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\Microsoft\Edge\Application\msedge.exe") })
            if (File.Exists(p)) return p;
        return null;
    }

    // ---------- Verknüpfungen: Desktop + Autostart (einmalig, wenn sie fehlen) ----------
    static void Verknuepfungen()
    {
        var exe = Application.ExecutablePath;
        Anlegen(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Atomkraftwerk.lnk"), exe, "");
        Anlegen(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Atomkraftwerk.lnk"), exe, "/autostart");
    }

    static void Anlegen(string lnk, string ziel, string argumente)
    {
        try
        {
            if (File.Exists(lnk)) return;
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            dynamic v = shell.CreateShortcut(lnk);
            v.TargetPath = ziel;
            v.Arguments = argumente;
            v.WorkingDirectory = Path.GetDirectoryName(ziel);
            v.IconLocation = ziel + ",0";
            v.Description = "Atomkraftwerk Leitstand";
            v.Save();
            Protokoll("Verknüpfung angelegt: " + lnk);
        }
        catch (Exception ex) { Protokoll("Verknüpfung nicht angelegt (" + lnk + "): " + ex.Message); }
    }
}
