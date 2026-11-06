using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows.Media;
using GearHub.Ui;
using Microsoft.Win32;

namespace GearHub.Devices.Controllers;

/// <summary>
/// DSX's mod server (DSX 3.1.7, Steam 1812620): JSON over UDP to 127.0.0.1 (6969 unless its app_settings.json says
/// otherwise). Read with REA from DSX.dll's metadata for the ilha on 06/11/2026 (DSX.ModificationSystem: Packet,
/// Instruction, InstructionType, UDPResponse, Device; trigger modes are TriggerType_v2_Legacy). DSX answers every
/// packet with its state. DSX's license and Steam checks are not touched.
/// </summary>
internal static class DsxApi
{
    public const int GetStatus = 0, TriggerUpdate = 1, RgbUpdate = 2, MicLed = 5, ResetToUser = 7;
    public const int Left = 1, Right = 2;
    public const int MicOn = 0, MicPulse = 1, MicOff = 2;

    /// <summary>Trigger presets: name and the TriggerType_v2_Legacy mode with its parameters.</summary>
    public static readonly (string Pt, string En, int[] Mode)[] TriggerPresets =
    {
        ("Normal", "Normal", new[] { 0 }),
        ("Resistência", "Resistance", new[] { 13, 0, 6 }),
        ("Arma", "Gun", new[] { 16, 2, 7, 8 }),
        ("Vibração", "Vibration", new[] { 8, 30 }),
        ("Rígido", "Rigid", new[] { 7 }),
    };

    public sealed record Pad(int Index, string Mac, int Type, int Connection, int Battery)
    {
        public string Name => Type switch { 0 => "DualSense", 1 => "DualSense Edge", 2 or 3 or 4 => "DualShock 4", _ => Lang.T("Controle", "Controller") };
        public string Link => Connection switch { 0 => "USB", 1 => "Bluetooth", 2 => "Dongle", _ => "" };
    }

    public static string Packet(params (int Type, int[] Parameters)[] instructions)
    {
        var list = new JsonArray();
        foreach (var (type, ps) in instructions)
            list.Add(new JsonObject { ["type"] = type, ["parameters"] = new JsonArray(ps.Select(x => (JsonNode)x).ToArray()) });
        return new JsonObject { ["instructions"] = list }.ToJsonString();
    }

    public static List<Pad>? ParseStatus(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject n) return null;
            JsonNode? Get(JsonObject o, string k) => o.FirstOrDefault(p => string.Equals(p.Key, k, StringComparison.OrdinalIgnoreCase)).Value;
            int I(JsonObject o, string k) => Get(o, k) is JsonValue v && v.TryGetValue<int>(out int x) ? x : -1;
            var pads = new List<Pad>();
            if (Get(n, "Devices") is JsonArray arr)
                foreach (var d in arr.OfType<JsonObject>())
                    pads.Add(new Pad(I(d, "Index"), Get(d, "MacAddress")?.ToString() ?? "", I(d, "DeviceType"), I(d, "ConnectionType"), I(d, "BatteryLevel")));
            return pads;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Steam library folder that holds the app, from libraryfolders.vdf.</summary>
    public static string? SteamApp(string folderName)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            string? steam = k?.GetValue("SteamPath") as string;
            if (steam == null) return null;
            var libs = new List<string> { steam.Replace('/', '\\') };
            string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf))
                foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                    libs.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
            foreach (var lib in libs.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string dir = Path.Combine(lib, "steamapps", "common", folderName);
                if (Directory.Exists(dir)) return dir;
            }
        }
        catch (Exception) { }
        return null;
    }

    /// <summary>Where DSX listens, or null when its UDP server is off.</summary>
    public static int? Port()
    {
        try
        {
            if (SteamApp("DSX") is { } dir)
            {
                string f = Path.Combine(dir, "Main_v3_Beta", "DSX_Savefile", "app_settings.json");
                if (File.Exists(f))
                {
                    using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var doc = JsonDocument.Parse(fs);
                    var r = doc.RootElement;
                    if (r.TryGetProperty("use_incoming_udp", out var on) && on.ValueKind == JsonValueKind.False) return null;
                    if (r.TryGetProperty("incoming_udp_port", out var p) && p.TryGetInt32(out int port) && port > 0) return port;
                }
            }
        }
        catch (Exception) { }
        return 6969;
    }

    public static List<Pad>? Send(int port, string packet, int timeoutMs = 600)
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        udp.Client.ReceiveTimeout = timeoutMs;
        var bytes = Encoding.UTF8.GetBytes(packet);
        udp.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Loopback, port));
        try
        {
            var from = new IPEndPoint(IPAddress.Any, 0);
            return ParseStatus(Encoding.UTF8.GetString(udp.Receive(ref from)));
        }
        catch (SocketException) { return null; }
    }
}

/// <summary>The controllers DSX holds, driven through its mod server (only while DSX.exe runs).</summary>
internal sealed class DsxGear : Gear
{
    private int? _port;
    private List<DsxApi.Pad> _pads = new();
    private int _left, _right;
    private Color _light = Color.FromRgb(0, 122, 255);

    public override string Key => "dsx";
    public override string Name => "DSX";
    public override string Brand => "DSX";
    public override GearKind Kind => GearKind.Controller;
    public IReadOnlyList<DsxApi.Pad> Pads => _pads;

    public static DsxGear? Find()
    {
        if (!VendorApps.Dsx) return null;
        var g = new DsxGear { _port = DsxApi.Port() };
        if (g._port == null) return null;
        g.Poll();
        return g;
    }

    private bool _changed;

    private List<DsxApi.Pad>? Send(params (int, int[])[] ins)
    {
        if (ins.Any(i => i.Item1 != DsxApi.GetStatus && i.Item1 != DsxApi.ResetToUser)) _changed = true;
        return _port is int p ? DsxApi.Send(p, DsxApi.Packet(ins)) : null;
    }

    public override bool HasChanges => _changed;

    /// <summary>DSX's own "back to the user's settings".</summary>
    public override string? RestoreOriginal()
    {
        if (!_changed) return null;
        _changed = false;
        return Send((DsxApi.ResetToUser, new[] { 0 })) != null ? Lang.T("DSX: perfil dele", "DSX: its profile") : null;
    }

    public override void Poll()
    {
        var pads = Send((DsxApi.GetStatus, Array.Empty<int>()));
        if (pads != null) _pads = pads;
        Battery = _pads.FirstOrDefault(d => d.Battery >= 0)?.Battery;
        string s = pads == null ? Lang.T("sem resposta", "no answer")
            : pads.Count == 0 ? Lang.T("nenhum controle", "no controller")
            : string.Join(", ", pads.Select(d => d.Name + (d.Battery >= 0 ? $" {d.Battery}%" : "")));
        if (s != Status) { Status = s; RaiseChanged(); }
    }

    private bool SetTriggers(int left, int right)
    {
        _left = left;
        _right = right;
        int[] L = new[] { 0, DsxApi.Left }.Concat(DsxApi.TriggerPresets[left].Mode).ToArray();
        int[] R = new[] { 0, DsxApi.Right }.Concat(DsxApi.TriggerPresets[right].Mode).ToArray();
        return Send((DsxApi.TriggerUpdate, L), (DsxApi.TriggerUpdate, R)) != null;
    }

    private bool SetLight(Color c)
    {
        _light = c;
        return Send((DsxApi.RgbUpdate, new[] { 0, (int)c.R, c.G, c.B })) != null;
    }

    public override JsonObject? Capture() => new() { ["left"] = _left, ["right"] = _right, ["light"] = Page.Hex(_light) };

    public override void Apply(JsonObject s)
    {
        int l = s["left"]?.GetValue<int>() ?? 0, r = s["right"]?.GetValue<int>() ?? 0;
        if (l != _left || r != _right) SetTriggers(Math.Clamp(l, 0, DsxApi.TriggerPresets.Length - 1), Math.Clamp(r, 0, DsxApi.TriggerPresets.Length - 1));
        if (Page.TryParse(s["light"]?.ToString() ?? "", out var c) && c != _light) SetLight(c);
    }

    public override void BuildPage(Page p)
    {
        p.Header("DSX", Lang.T("Controles de PlayStation pelo DSX (o servidor de mods dele). As mudanças valem até você trocar o perfil no DSX ou mandar voltar.",
            "PlayStation controllers through DSX (its mod server). Changes last until you switch the profile in DSX or reset."));
        string fail = Lang.T("O DSX não respondeu", "DSX did not answer");
        if (_pads.Count == 0) p.Note(p.Root, Lang.T("O DSX está aberto mas não tem controle conectado.", "DSX is open but no controller is connected."), true);
        foreach (var pad in _pads)
            p.Note(p.Root, $"{pad.Name} · {pad.Link}" + (pad.Battery >= 0 ? Lang.T($" · bateria {pad.Battery}%", $" · battery {pad.Battery}%") : ""));
        var names = DsxApi.TriggerPresets.Select(t => Lang.T(t.Pt, t.En)).ToList();
        var sec = p.Section(Lang.T("Gatilhos adaptáveis", "Adaptive triggers"));
        p.Row(sec, "L2", null, p.Segments(names, _left, i => p.Work(() => SetTriggers(i, _right), ok => p.Say(ok ? "L2: " + names[i] : fail, ok))));
        p.Row(sec, "R2", null, p.Segments(names, _right, i => p.Work(() => SetTriggers(_left, i), ok => p.Say(ok ? "R2: " + names[i] : fail, ok))));
        var light = p.Section(Lang.T("Luz", "Light"));
        p.Row(light, Lang.T("Barra de luz", "Light bar"), null, p.ColorPick(_light, c => p.Work(() => SetLight(c), ok => p.Say(ok ? Lang.T("Luz trocada", "Light changed") : fail, ok))));
        var mic = new[] { Lang.T("Acesa", "On"), Lang.T("Piscando", "Pulse"), Lang.T("Apagada", "Off") };
        p.Row(light, Lang.T("Luz do microfone", "Mic light"), null, p.Segments(mic, -1, i => p.Work(() => Send((DsxApi.MicLed, new[] { 0, i })) != null, ok => p.Say(ok ? mic[i] : fail, ok))));
        p.Root.Children.Add(p.Line(p.Button(Lang.T("Voltar ao que está no DSX", "Back to DSX's settings"), () =>
            p.Work(() => Send((DsxApi.ResetToUser, new[] { 0 })) != null, ok => p.Say(ok ? Lang.T("Voltou ao perfil do DSX", "Back to DSX's profile") : fail, ok)))));
    }
}
