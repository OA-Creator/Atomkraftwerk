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
        => _ = SendenAsync(h, titel, text, test);

    // schickt die Meldung und gibt zurück, was geklappt hat (für den Test-Knopf im Leitstand)
    public static async Task<string> SendenAsync(HandyEinstellungen h, string titel, string text, bool test = false)
    {
        var ergebnis = new List<string>();
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
                var t = r.IsSuccessStatusCode ? "gesendet" : "Fehler " + (int)r.StatusCode;
                Log.Schreiben("Handy (ntfy): " + t); ergebnis.Add("ntfy " + t);
            }
            catch (Exception ex) { Log.Schreiben("Handy (ntfy) nicht erreichbar: " + ex.Message); ergebnis.Add("ntfy nicht erreichbar"); }
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
                var t = r.IsSuccessStatusCode ? "gesendet" : "Fehler " + (int)r.StatusCode;
                Log.Schreiben("Handy (Pushover): " + t); ergebnis.Add("Pushover " + t);
            }
            catch (Exception ex) { Log.Schreiben("Handy (Pushover) nicht erreichbar: " + ex.Message); ergebnis.Add("Pushover nicht erreichbar"); }
        }
        return ergebnis.Count == 0 ? "nichts eingetragen" : string.Join(" · ", ergebnis);
    }
}
