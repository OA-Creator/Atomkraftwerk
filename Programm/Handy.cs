using System.Net.Http.Json;

namespace Atomkraftwerk;

// ---------------------------------------------------------------
// Alarm aufs Handy – nur ausgehend, kein offener Port:
//  Android: App "ntfy" (Thema abonnieren, höchste Stufe darf "Nicht stören" übergehen)
//  iPhone:  App "Pushover" (Notfall-Priorität, kritischer Hinweis, wiederholt bis bestätigt)
// ---------------------------------------------------------------
public class HandyEinstellungen
{
    public string NtfyServer { get; set; } = "https://ntfy.sh";
    public string NtfyThema { get; set; } = "";          // geheim halten – wer das Thema kennt, liest mit
    public string PushoverToken { get; set; } = "";      // "API Token" der eigenen Pushover-Anwendung
    public string PushoverBenutzer { get; set; } = "";   // "User Key" aus dem Pushover-Konto
}

public static class Handy
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public static void Senden(HandyEinstellungen h, string titel, string text, bool test = false)
    {
        _ = Task.Run(async () =>
        {
            if (!string.IsNullOrWhiteSpace(h.NtfyThema))
            {
                try
                {
                    var r = await Http.PostAsJsonAsync(h.NtfyServer.TrimEnd('/') + "/", new
                    {
                        topic = h.NtfyThema,
                        title = titel,
                        message = text,
                        priority = test ? 4 : 5,
                        tags = new[] { test ? "test_tube" : "rotating_light" }
                    });
                    Log.Schreiben("Handy (ntfy): " + (r.IsSuccessStatusCode ? "gesendet" : "Fehler " + (int)r.StatusCode));
                }
                catch (Exception ex) { Log.Schreiben("Handy (ntfy) nicht erreichbar: " + ex.Message); }
            }
            if (!string.IsNullOrWhiteSpace(h.PushoverToken) && !string.IsNullOrWhiteSpace(h.PushoverBenutzer))
            {
                try
                {
                    var felder = new Dictionary<string, string>
                    {
                        ["token"] = h.PushoverToken, ["user"] = h.PushoverBenutzer,
                        ["title"] = titel, ["message"] = text,
                        ["priority"] = test ? "1" : "2", ["sound"] = "siren"
                    };
                    if (!test) { felder["retry"] = "30"; felder["expire"] = "600"; }
                    var r = await Http.PostAsync("https://api.pushover.net/1/messages.json", new FormUrlEncodedContent(felder));
                    Log.Schreiben("Handy (Pushover): " + (r.IsSuccessStatusCode ? "gesendet" : "Fehler " + (int)r.StatusCode));
                }
                catch (Exception ex) { Log.Schreiben("Handy (Pushover) nicht erreichbar: " + ex.Message); }
            }
        });
    }
}
