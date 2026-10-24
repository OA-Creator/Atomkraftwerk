using System.Net;
using System.Text;

namespace Atomkraftwerk;

// ---------------------------------------------------------------
// Kleiner Webserver (ohne Zusatzpakete): liefert Daten und die Seite
// ---------------------------------------------------------------
public sealed class Server
{
    private readonly Anlage _anlage;
    private readonly HttpListener _http = new();
    private readonly string _webOrdner;
    public string Adresse { get; private set; } = "";
    public Auswertung? Auswertung { get; set; }
    public PumpenSteuerung? Pumpe { get; set; }

    public Server(Anlage anlage, int port, string programmOrdner)
    {
        _anlage = anlage;
        _webOrdner = Path.Combine(programmOrdner, "web");
        // Erst fürs ganze Heimnetz versuchen, sonst nur auf diesem PC
        try
        {
            _http.Prefixes.Add($"http://+:{port}/");
            _http.Start();
            Adresse = $"http://localhost:{port}/ (auch im Heimnetz)";
        }
        catch (Exception e)
        {
            Log.Schreiben("Heimnetz-Freigabe nicht möglich (" + e.Message + "), nur lokal.");
            _http.Prefixes.Clear();
            _http.Prefixes.Add($"http://localhost:{port}/");
            _http.Start();
            Adresse = $"http://localhost:{port}/ (nur dieser PC)";
        }
    }

    public async Task LaufenAsync(CancellationToken stopp)
    {
        while (!stopp.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _http.GetContextAsync(); }
            catch { if (stopp.IsCancellationRequested) break; continue; }
            _ = Task.Run(() => Bearbeiten(ctx));
        }
    }

    private void Bearbeiten(HttpListenerContext ctx)
    {
        try
        {
            var pfad = ctx.Request.Url?.AbsolutePath ?? "/";
            if (pfad == "/api/verlauf")
            {
                var id = ctx.Request.QueryString["id"] ?? "";
                Senden(ctx, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(_anlage.VerlaufJson(id)));
                return;
            }
            if (pfad == "/api/auswertung/liste" && Auswertung != null)
            {
                Senden(ctx, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(Auswertung.ListeJson()));
                return;
            }
            if (pfad == "/api/auswertung" && Auswertung != null)
            {
                var zr = ctx.Request.QueryString["zeitraum"] ?? "heute";
                var id = ctx.Request.QueryString["id"] ?? "";
                Senden(ctx, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(Auswertung.DatenJson(zr, id)));
                return;
            }
            if (pfad == "/api/tage")
            {
                Senden(ctx, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(_anlage.TageJson()));
                return;
            }
            if (pfad == "/api/tag")
            {
                var datum = ctx.Request.QueryString["datum"] ?? DateTime.Now.ToString("yyyy-MM-dd");
                Senden(ctx, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(_anlage.TagJson(datum)));
                return;
            }
            if (pfad == "/api/pumpe" && Pumpe != null)
            {
                Senden(ctx, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(Pumpe.Json(ctx.Request.QueryString["ziel"], ctx.Request.QueryString["betrieb"], ctx.Request.QueryString["prozent"], ctx.Request.QueryString["standard"] == "1")));
                return;
            }
            if (pfad == "/api/jetzt")
            {
                Senden(ctx, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(_anlage.AlsJson()));
                return;
            }
            // Dateien aus dem web-Ordner (für Schritt 3), sonst die eingebaute Testseite
            var datei = pfad == "/" ? "index.html" : pfad.TrimStart('/');
            if (datei.Contains("..") || datei.Contains('\\')) { Senden(ctx, 400, "text/plain", Encoding.UTF8.GetBytes("ungueltig")); return; }
            var voll = Path.Combine(_webOrdner, datei);
            if (File.Exists(voll))
            {
                Senden(ctx, 200, Typ(voll), File.ReadAllBytes(voll));
                return;
            }
            if (pfad == "/")
            {
                Senden(ctx, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(Testseite.Html));
                return;
            }
            Senden(ctx, 404, "text/plain", Encoding.UTF8.GetBytes("nicht gefunden"));
        }
        catch (Exception e) { Log.Schreiben("Webfehler: " + e.Message); }
    }

    private static string Typ(string datei) => Path.GetExtension(datei).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".json" => "application/json; charset=utf-8",
        ".png" => "image/png",
        ".svg" => "image/svg+xml",
        ".webmanifest" => "application/manifest+json",
        _ => "application/octet-stream"
    };

    private static void Senden(HttpListenerContext ctx, int code, string typ, byte[] daten)
    {
        ctx.Response.StatusCode = code;
        ctx.Response.ContentType = typ;
        ctx.Response.Headers["Cache-Control"] = "no-store";
        ctx.Response.ContentLength64 = daten.Length;
        ctx.Response.OutputStream.Write(daten);
        ctx.Response.OutputStream.Close();
    }

    public void Stop() { try { _http.Stop(); } catch { } }
}

// Schlichte Testseite für Schritt 2 – die richtige Oberfläche kommt in Schritt 3
public static class Testseite
{
    public const string Html = """
<!doctype html>
<html lang="de"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Atomkraftwerk – Test</title>
<style>
body{margin:0;background:#0B0E11;color:#E6EAEE;font:15px system-ui,sans-serif;padding:16px}
h1{font-size:20px;letter-spacing:.08em;margin:0 0 4px}
.info{color:#8C98A4;font-size:13px;margin-bottom:14px}
.status{display:inline-block;padding:4px 10px;border-radius:999px;font-size:13px;margin-left:8px}
.ok{background:#12221A;color:#9FE2B3}.warn{background:#2A2112;color:#F2C46B}.alarm{background:#2A1416;color:#FF8F94}.aus{color:#5A6670}
table{border-collapse:collapse;width:100%;max-width:900px}
th,td{padding:7px 10px;border-bottom:1px solid #222A31;text-align:right;font-variant-numeric:tabular-nums}
th:first-child,td:first-child{text-align:left}
th{color:#8C98A4;font-weight:600;font-size:12px;letter-spacing:.1em}
tr.grp td{color:#F2A33A;font-weight:600;text-align:left;padding-top:16px;border-bottom:1px solid #333D46}
</style></head><body>
<h1>ATOMKRAFTWERK <span id="st" class="status">…</span></h1>
<div class="info" id="info">lade…</div>
<table><thead><tr><th>SENSOR</th><th>AKTUELL</th><th>MIN</th><th>MAX</th><th>Ø SEIT START</th></tr></thead><tbody id="t"></tbody></table>
<script>
const f=(v,e)=>v==null?'—':(e==='V'?v.toFixed(3):e==='rpm'?Math.round(v):v.toFixed(1))+' '+e;
async function laden(){
 try{
  const d=await (await fetch('/api/jetzt')).json();
  const st=document.getElementById('st');st.textContent={ok:'Normalbereich',warn:'Warnung',alarm:'ALARM'}[d.status];st.className='status '+d.status;
  document.getElementById('info').textContent=`${d.hardware.cpu} · ${d.hardware.gpu} · ${d.hardware.board} · Laufzeit heute ${d.laufzeitHeuteStunden.toFixed(2)} h · iCUE: ${d.icue.laeuft?d.icue.datei:'Protokoll läuft nicht'}`;
  let h='',g='';
  for(const w of d.werte){
   if(w.gruppe!==g){g=w.gruppe;h+=`<tr class="grp"><td colspan="5">${g}</td></tr>`}
   h+=`<tr class="${w.status==='aus'?'aus':''}"><td>${w.name}</td><td class="${w.status}">${f(w.aktuell,w.einheit)}</td><td>${f(w.min,w.einheit)}</td><td>${f(w.max,w.einheit)}</td><td>${f(w.schnitt,w.einheit)}</td></tr>`;
  }
  document.getElementById('t').innerHTML=h;
 }catch(e){document.getElementById('info').textContent='Keine Verbindung zum Programm'}
}
laden();setInterval(laden,2000);
</script></body></html>
""";
}
