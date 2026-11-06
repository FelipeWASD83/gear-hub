using System;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace GearHub.Devices.Logitech;

/// <summary>
/// G HUB's local agent (ws://localhost:9010, subprotocol "json", Origin file://), used only while G HUB runs, for
/// what it would otherwise undo: with the mouse in host mode G HUB re-applies its own DPI card on every profile event.
/// Envelope seen on 06/11/2026: {"msgId","verb","path","payload"}; the agent first sends OPTIONS "/" (ready); answers
/// carry the same msgId and result.code SUCCESS. Write formats come from G HUB's own UI code (app.asar), not tested
/// against the agent before this app.
/// </summary>
internal sealed class GHubClient : IDisposable
{
    private readonly ClientWebSocket _ws = new();
    private int _next = 1;

    private GHubClient() { }

    public static GHubClient? Connect(int timeoutMs = 2500)
    {
        var c = new GHubClient();
        try
        {
            c._ws.Options.AddSubProtocol("json");
            c._ws.Options.SetRequestHeader("Origin", "file://");
            using var cts = new CancellationTokenSource(timeoutMs);
            c._ws.ConnectAsync(new Uri("ws://localhost:9010"), cts.Token).GetAwaiter().GetResult();
            // the agent says it is ready with OPTIONS "/"
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                var m = c.Receive(timeoutMs);
                if (m?["verb"]?.ToString() == "OPTIONS") return c;
            }
            return c;
        }
        catch (Exception e)
        {
            Log.Write("ghub: " + e.Message);
            c.Dispose();
            return null;
        }
    }

    private JsonObject? Receive(int timeoutMs)
    {
        var buf = new byte[64 * 1024];
        using var ms = new MemoryStream();
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            while (true)
            {
                var r = _ws.ReceiveAsync(new ArraySegment<byte>(buf), cts.Token).GetAwaiter().GetResult();
                if (r.MessageType == WebSocketMessageType.Close) return null;
                ms.Write(buf, 0, r.Count);
                if (r.EndOfMessage) break;
            }
            return JsonNode.Parse(Encoding.UTF8.GetString(ms.ToArray())) as JsonObject;
        }
        catch (OperationCanceledException) { return null; }
    }

    /// <summary>Sends a request and returns its payload when the agent says SUCCESS, else null.</summary>
    public JsonObject? Call(string verb, string path, JsonObject? payload = null, int timeoutMs = 3000)
    {
        string id = (_next++).ToString();
        var msg = new JsonObject { ["msgId"] = id, ["verb"] = verb, ["path"] = path };
        if (payload != null) msg["payload"] = payload;
        var bytes = Encoding.UTF8.GetBytes(msg.ToJsonString());
        using (var cts = new CancellationTokenSource(timeoutMs))
            _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cts.Token).GetAwaiter().GetResult();
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var m = Receive((int)Math.Max(50, (deadline - DateTime.UtcNow).TotalMilliseconds));
            if (m == null) break;
            if (m["msgId"]?.ToString() != id) continue;
            string code = m["result"]?["code"]?.ToString() ?? "";
            if (code != "SUCCESS")
            {
                Log.Write($"ghub {verb} {path}: {code} {m["result"]?["what"]}");
                return null;
            }
            return m["payload"] as JsonObject ?? new JsonObject();
        }
        Log.Write($"ghub {verb} {path}: no answer");
        return null;
    }

    /// <summary>G HUB's id for a device ("dev00000001"), matched by USB product id (wired or wireless model id).</summary>
    public string? DeviceId(ushort product, out string model)
    {
        model = "";
        var list = Call("GET", "/devices/list");
        if (list?["deviceInfos"] is not JsonArray infos) return null;
        string hex = product.ToString("x4");
        foreach (var d in infos.OfType<JsonObject>())
        {
            // "pid" is the decimal USB product id (49301 for the G502 X PLUS's C095); the HID paths carry pid_xxxx
            string pid = d["pid"]?.ToString() ?? "";
            if (pid == product.ToString() || d["activeInterfaces"]?.ToJsonString().ToLowerInvariant().Contains($"pid_{hex}") == true)
            {
                model = d["deviceModel"]?.ToString() ?? "";
                return d["id"]?.ToString();
            }
        }
        return null;
    }

    /// <summary>
    /// Sets the active DPI in G HUB's current profile: reads the MOUSE_SETTINGS card assigned to the device's slot,
    /// swaps the active level for the new value and assigns it back (the payload G HUB's UI builds).
    /// </summary>
    public bool SetDpi(string model, int dpi)
    {
        string? profile = Call("GET", "/profile/active")?["id"]?.ToString();
        if (profile == null) return false;
        var profiles = Call("GET", "/profiles");
        string slotPrefix = model.Replace('_', '-');
        JsonObject? assignment = null;
        foreach (var p in (profiles?["profiles"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
        {
            if (p["id"]?.ToString() != profile) continue;
            assignment = (p["assignments"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                .FirstOrDefault(a => a["slotId"]?.ToString() is string s && s.StartsWith(slotPrefix, StringComparison.OrdinalIgnoreCase) && s.EndsWith("_mouse_settings", StringComparison.OrdinalIgnoreCase));
        }
        string? slot = assignment?["slotId"]?.ToString();
        string? cardId = assignment?["cardId"]?.ToString();
        if (slot == null || cardId == null) return false;
        var card = Call("GET", "/card", new JsonObject { ["id"] = cardId });
        if (card == null || card["readOnly"]?.GetValue<bool>() == true) return false;
        card.Remove("@type");
        if (card["mouseSettings"]?["dpiTable"] is not JsonObject table) return false;
        int old = table["activeDpi"]?.GetValue<int>() ?? 0;
        if (table["levels"] is JsonArray levels)
        {
            for (int i = 0; i < levels.Count; i++)
                if (levels[i]?.GetValue<int>() == old) { levels[i] = dpi; break; }
        }
        table["activeDpi"] = dpi;
        var r = Call("SET", "/assignment", new JsonObject
        {
            ["profile"] = profile,
            ["slotId"] = slot,
            ["card"] = card,
            ["updateCard"] = true,
            ["returnCard"] = true,
        });
        return r != null;
    }

    public bool SetSidetone(string deviceId, int level) =>
        Call("SET", $"/audio/{deviceId}/sidetone", new JsonObject { ["muted"] = level == 0, ["gain"] = level / 100.0, ["channel"] = "SIDETONE" }) != null;

    public bool SetOnboard(string deviceId, bool onboard) =>
        Call("SET", $"/onboard_profiles/{deviceId}/onboard_mode", new JsonObject { ["mode"] = onboard ? "ONBOARD" : "HOST" }) != null;

    public bool? ReadOnboard(string deviceId)
    {
        var r = Call("GET", $"/onboard_profiles/{deviceId}/onboard_mode");
        if (r == null) return null;
        return r["mode"]?.ToString() == "ONBOARD";
    }

    public void Dispose()
    {
        try
        {
            if (_ws.State == WebSocketState.Open)
                _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", new CancellationTokenSource(500).Token).Wait(600);
        }
        catch (Exception) { }
        _ws.Dispose();
    }

    /// <summary>Runs `f` with a fresh connection when G HUB is open; null when it is closed or did not answer.</summary>
    public static T? With<T>(Func<GHubClient, T> f) where T : struct
    {
        if (!VendorApps.GHub) return null;
        using var c = Connect();
        return c == null ? null : f(c);
    }

    public static Task<bool> Ready() => Task.Run(() => VendorApps.GHub);
}
