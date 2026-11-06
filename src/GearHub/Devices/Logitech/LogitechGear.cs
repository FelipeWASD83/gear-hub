using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GearHub.Hid;
using GearHub.Ui;
using Page = GearHub.Ui.Page;

namespace GearHub.Devices.Logitech;

/// <summary>
/// Any Logitech device that speaks HID++ 2.0: the page shows a section for each feature it has (DPI 0x2201, report
/// rate 0x8060, onboard profiles 0x8100, lighting 0x8071 or 0x8070, sidetone 0x8300, equalizer 0x8310, battery).
/// Measured on the G502 X PLUS (C095) and the G733 (0AFE) on 06/11/2026; the write formats follow Solaar and
/// libratbag. With G HUB open, DPI, sidetone and onboard mode go through G HUB's agent instead, because G HUB
/// re-applies its own values on every profile event.
/// </summary>
internal sealed class LogitechGear : Gear, GearHub.Lighting.ILightTarget
{
    // ---- light target: each zone is one "LED", painted as a fixed colour 15 times a second ----
    private List<Zone>? _lightZones;

    public bool LightsAvailable => _d.Has(HidppDevice.RgbEffects) || _d.Has(HidppDevice.ColorLedEffects);
    public int MaxFps => 15;

    public string? Begin()
    {
        try { _lightZones = ReadZones().Where(z => z.Effects.Contains(EffectFixed)).ToList(); }
        catch (HidppException e) { return e.Message; }
        return _lightZones.Count == 0 ? Lang.T("Sem zona de luz com cor fixa.", "No light zone takes a fixed colour.") : null;
    }

    public double[] LedPositions()
    {
        int n = _lightZones?.Count ?? 1;
        return Enumerable.Range(0, n).Select(i => n <= 1 ? 0.5 : i / (double)(n - 1)).ToArray();
    }

    private Color[]? _lastPainted;

    public void Paint(Color[] colors)
    {
        if (_lightZones == null) return;
        for (int i = 0; i < _lightZones.Count && i < colors.Length; i++)
        {
            if (_lastPainted != null && i < _lastPainted.Length && _lastPainted[i] == colors[i]) continue;
            WriteEffect(_lightZones[i], EffectFixed, colors[i]);
        }
        _lastPainted = colors;
    }

    /// <summary>Back to a slow colour cycle (G HUB puts its own back on its next profile event).</summary>
    public void End()
    {
        if (_lightZones == null) return;
        try
        {
            foreach (var z in _lightZones.Where(z => z.Effects.Contains(EffectCycle))) WriteEffect(z, EffectCycle, Colors.Black, 5000);
        }
        catch (HidppException) { }
        _lastPainted = null;
    }

    private readonly HidppDevice _d;
    private readonly ushort _product;

    public LogitechGear(HidppDevice d, ushort product)
    {
        _d = d;
        _product = product;
    }

    public HidppDevice Device => _d;
    // the G733 answers ffffffff as its unit id: fall back to the product id there
    public override string Key => "logi-" + (_d.UnitId is "" or "ffffffff" or "00000000" ? _product.ToString("x4") : _d.UnitId);
    public override string Name => _d.Name != "" ? _d.Name : "Logitech " + _product.ToString("X4");
    public override string Brand => "Logitech";

    public override GearKind Kind => _d.Type switch
    {
        0 => GearKind.Keyboard,
        3 or 4 => GearKind.Mouse,
        8 => GearKind.Headset,
        _ => _d.Has(HidppDevice.AdjustableDpi) ? GearKind.Mouse : _d.Has(HidppDevice.Sidetone) ? GearKind.Headset : GearKind.Mouse,
    };

    public static readonly ushort[] Receivers = { 0xC547, 0xC539, 0xC53A, 0xC53F, 0xC545, 0xC541, 0xC548, 0xC52B, 0xC534 };

    /// <summary>Opens every HID++ interface of Logitech and returns the devices answering there (wired first).</summary>
    public static List<LogitechGear> Find(IEnumerable<HidInfo> all, ISet<string> skipPaths)
    {
        var found = new List<LogitechGear>();
        var groups = all.Where(h => h.Vendor == 0x046D && (h.UsagePage == 0xFF00 || h.UsagePage == 0xFF43))
            .GroupBy(h => (h.Product, h.Interface))
            .OrderBy(g => Receivers.Contains(g.Key.Product) ? 1 : 0);
        foreach (var g in groups)
        {
            var list = g.ToList();
            if (list.Any(h => skipPaths.Contains(h.Path))) continue;
            var link = HidppLink.Open(list);
            if (Environment.GetEnvironmentVariable("GEARHUB_TRACE") == "1")
                Log.Write($"logitech: {g.Key.Product:X4} mi_{g.Key.Interface:X2}, {list.Count} collections ({string.Join(", ", list.Select(h => $"{h.UsagePage:X4}/{h.Usage:X} in {h.InputLength} out {h.OutputLength}"))}), link {(link == null ? "not opened" : "open")}");
            if (link == null) continue;
            bool used = false;
            var wired = HidppDevice.Probe(link, 0xFF, g.Key.Product);
            if (wired != null && wired.Features.Count > 3)
            {
                var gear = new LogitechGear(wired, g.Key.Product);
                if (found.All(f => f.Key != gear.Key)) { found.Add(gear); used = true; }
            }
            else if (Receivers.Contains(g.Key.Product))
            {
                for (byte slot = 1; slot <= 6; slot++)
                {
                    var d = HidppDevice.Probe(link, slot, g.Key.Product);
                    if (d == null || d.Features.Count <= 3) continue;
                    var gear = new LogitechGear(d, g.Key.Product);
                    if (found.Any(f => f.Key == gear.Key)) continue;
                    found.Add(gear);
                    used = true;
                }
            }
            if (!used) link.Dispose();
        }
        return found;
    }

    /// <summary>A quick ping on each slot of the given receivers (nothing else): does anyone answer now?</summary>
    public static bool ReceiverHasDevice(IEnumerable<HidInfo> receiverCollections)
    {
        foreach (var g in receiverCollections.GroupBy(h => (h.Product, h.Interface)))
        {
            using var link = HidppLink.Open(g.ToList());
            if (link == null) continue;
            for (byte slot = 1; slot <= 6; slot++)
            {
                try
                {
                    if (link.Request(slot, 0x00, 1, new byte[] { 0, 0, 0x5A }, 250)[0] >= 2) return true;
                }
                catch (HidppException) { }
            }
        }
        return false;
    }

    public string LinkPath => _d.Link.Path;
    public bool Alive => _d.Link.Alive;

    public override void Dispose()
    {
        // links are shared by every device behind a receiver; the hub disposes each link once
    }

    // ---- hardware ----

    public (int Min, int Max, int Step)? DpiRange()
    {
        var r = _d.Call(HidppDevice.AdjustableDpi, 1, 0);
        var values = new List<int>();
        int step = 50;
        for (int i = 1; i + 1 < 16; i += 2)
        {
            int v = r[i] << 8 | r[i + 1];
            if (v == 0) break;
            if ((v & 0xE000) == 0xE000) step = v & 0x1FFF;
            else values.Add(v);
        }
        return values.Count == 0 ? null : (values.Min(), values.Max(), step);
    }

    public int ReadDpi()
    {
        var r = _d.Call(HidppDevice.AdjustableDpi, 2, 0);
        return r[1] << 8 | r[2];
    }

    /// <summary>
    /// Straight to the sensor, also with G HUB open (tested on 06/11/2026: it held; G HUB puts its own back on a profile
    /// switch). Editing G HUB's MOUSE_SETTINGS card instead only changed G HUB's table, not the mouse.
    /// </summary>
    public bool WriteDpi(int dpi)
    {
        if (!_restoring && _baseDpi == null) _baseDpi = ReadDpi();
        _d.Call(HidppDevice.AdjustableDpi, 3, 0, (byte)(dpi >> 8), (byte)dpi);
        Signals.RaiseDpi(this, dpi);
        return true;
    }

    /// <summary>Report intervals in ms the device takes (bit n = n+1 ms), and the current one.</summary>
    public (List<int> Options, int Current) ReadRate()
    {
        int mask = _d.Call(HidppDevice.ReportRate, 0)[0];
        var opts = Enumerable.Range(0, 8).Where(b => (mask & (1 << b)) != 0).Select(b => b + 1).ToList();
        int cur = _d.Call(HidppDevice.ReportRate, 1)[0];
        return (opts, cur);
    }

    public void WriteRate(int ms)
    {
        if (!_restoring && _baseRate == null) _baseRate = ReadRate().Current;
        _d.Call(HidppDevice.ReportRate, 2, (byte)ms);
    }

    /// <summary>True = onboard memory, false = host (software) mode.</summary>
    public bool ReadOnboard() => _d.Call(HidppDevice.OnboardProfiles, 2)[0] == 1;

    public void WriteOnboard(bool onboard)
    {
        if (!_restoring && _baseOnboard == null) _baseOnboard = ReadOnboard();
        if (VendorApps.GHub)
        {
            bool? ok = GHubClient.With(c => GHubId(c) is { } id && c.SetOnboard(id, onboard));
            if (ok == true) return;
        }
        _d.Call(HidppDevice.OnboardProfiles, 1, (byte)(onboard ? 1 : 2));
    }

    /// <summary>G HUB's id for this device, by the USB id it is on now or any model id it reports (receiver).</summary>
    private string? GHubId(GHubClient c) =>
        new[] { _product }.Concat(_d.ModelIds).Select(pid => c.DeviceId(pid, out _)).FirstOrDefault(id => id != null);


    // ---- what the device had before the hub's first change (given back when the hub quits) ----
    private bool _restoring;
    private int? _baseDpi, _baseRate, _baseSidetone;
    private bool? _baseOnboard;

    public override bool HasChanges => _baseDpi != null || _baseRate != null || _baseSidetone != null || _baseOnboard != null;

    public override string? RestoreOriginal()
    {
        if (!HasChanges) return null;
        var done = new List<string>();
        _restoring = true;
        try
        {
            void Try(string what, Action a)
            {
                try { a(); done.Add(what); }
                catch (HidppException e) { Log.Write($"{Name} restore {what}: {e.Message}"); }
            }
            if (_baseOnboard is bool ob) Try(Lang.T("memória", "onboard"), () => WriteOnboard(ob));
            if (_baseDpi is int dpi) Try("DPI", () => WriteDpi(dpi));
            if (_baseRate is int ms) Try(Lang.T("taxa", "rate"), () => WriteRate(ms));
            if (_baseSidetone is int st) Try("sidetone", () => WriteSidetone(st));
        }
        finally { _restoring = false; }
        _baseDpi = _baseRate = _baseSidetone = null;
        _baseOnboard = null;
        return done.Count > 0 ? $"{Name}: " + string.Join(", ", done) : null;
    }

    public int ReadSidetone() => _d.Call(HidppDevice.Sidetone, 0)[0];

    public void WriteSidetone(int level)
    {
        if (!_restoring && _baseSidetone == null) _baseSidetone = ReadSidetone();
        if (VendorApps.GHub && GHubClient.With(c => GHubId(c) is { } id && c.SetSidetone(id, level)) == true) return;
        _d.Call(HidppDevice.Sidetone, 1, (byte)Math.Clamp(level, 0, 100));
    }

    public (int Bands, int Range) EqInfo()
    {
        var r = _d.Call(HidppDevice.Equalizer, 0);
        return (r[0], r[1]);
    }

    public sbyte[] ReadEq(int bands)
    {
        var r = _d.Call(HidppDevice.Equalizer, 2, 0);
        return r.Take(bands).Select(b => unchecked((sbyte)b)).ToArray();
    }

    /// <summary>SetGains layout: null = gains only, else the prefix byte before them (EqPrefix, found on the device).</summary>
    public static int? EqPrefix = 0x02;

    public void WriteEq(sbyte[] gains) => WriteEq(gains, EqPrefix);

    public void WriteEq(sbyte[] gains, int? prefix)
    {
        int o = prefix == null ? 0 : 1;
        var p = new byte[o + gains.Length];
        if (prefix is int pre) p[0] = (byte)pre;
        for (int i = 0; i < gains.Length; i++) p[o + i] = unchecked((byte)gains[i]);
        _d.Call(HidppDevice.Equalizer, 3, p);
    }

    /// <summary>A lighting zone (0x8070) or cluster (0x8071) and the effect ids it offers, by index.</summary>
    public sealed record Zone(byte Index, int Location, List<int> Effects);

    public List<Zone> ReadZones()
    {
        var zones = new List<Zone>();
        if (_d.Has(HidppDevice.RgbEffects))
        {
            int clusters = _d.Call(HidppDevice.RgbEffects, 0, 0xFF, 0xFF, 0)[2];
            for (byte c = 0; c < clusters; c++)
            {
                var info = _d.Call(HidppDevice.RgbEffects, 0, c, 0xFF, 0);
                int count = info[4];
                var effects = new List<int>();
                for (byte e = 0; e < count; e++)
                {
                    var ef = _d.Call(HidppDevice.RgbEffects, 0, c, e, 0);
                    effects.Add(ef[2] << 8 | ef[3]);
                }
                zones.Add(new Zone(c, info[2] << 8 | info[3], effects));
            }
        }
        else if (_d.Has(HidppDevice.ColorLedEffects))
        {
            int count = _d.Call(HidppDevice.ColorLedEffects, 0)[0];
            for (byte z = 0; z < count; z++)
            {
                var info = _d.Call(HidppDevice.ColorLedEffects, 1, z);
                var effects = new List<int>();
                for (byte e = 0; e < info[3]; e++)
                {
                    var ef = _d.Call(HidppDevice.ColorLedEffects, 2, z, e);
                    effects.Add(ef[2] << 8 | ef[3]);
                }
                zones.Add(new Zone(z, info[1] << 8 | info[2], effects));
            }
        }
        return zones;
    }

    public const int EffectOff = 0x0000, EffectFixed = 0x0001, EffectCycle = 0x0003, EffectBreathing = 0x000A;

    /// <summary>The 10 effect parameters Solaar documents for each effect.</summary>
    public static byte[] EffectParams(int effect, Color c, int periodMs = 4000)
    {
        var p = new byte[10];
        switch (effect)
        {
            case EffectFixed:
                p[0] = c.R; p[1] = c.G; p[2] = c.B;
                break;
            case EffectBreathing:
                p[0] = c.R; p[1] = c.G; p[2] = c.B;
                p[3] = (byte)(periodMs >> 8); p[4] = (byte)periodMs; p[5] = 0; p[6] = 100;
                break;
            case EffectCycle:
                p[5] = (byte)(periodMs >> 8); p[6] = (byte)periodMs; p[7] = 100;
                break;
        }
        return p;
    }

    public void WriteEffect(Zone z, int effect, Color c, int periodMs = 4000)
    {
        int index = z.Effects.IndexOf(effect);
        if (index < 0) throw new HidppException("effect not offered");
        var pars = EffectParams(effect, c, periodMs);
        var p = new byte[2 + 10 + 1];
        p[0] = z.Index;
        p[1] = (byte)index;
        pars.CopyTo(p, 2);
        p[12] = 0x01;
        if (_d.Has(HidppDevice.RgbEffects)) _d.Call(HidppDevice.RgbEffects, 1, p);
        else _d.Call(HidppDevice.ColorLedEffects, 3, p);
    }

    private static string ZoneName(Zone z, int count) => z.Location switch
    {
        1 => count > 1 ? Lang.T("Principal", "Primary") : Lang.T("Luz", "Light"),
        2 => "Logo",
        _ => Lang.T("Zona ", "Zone ") + (z.Index + 1),
    };

    private static string EffectName(int e) => e switch
    {
        EffectOff => Lang.T("Apagado", "Off"),
        EffectFixed => Lang.T("Fixo", "Fixed"),
        EffectBreathing => Lang.T("Respiração", "Breathing"),
        EffectCycle => Lang.T("Ciclo", "Cycle"),
        _ => "#" + e.ToString("X4"),
    };

    // ---- profiles ----

    public override JsonObject? Capture()
    {
        var o = new JsonObject();
        try
        {
            if (_d.Has(HidppDevice.AdjustableDpi)) o["dpi"] = ReadDpi();
            if (_d.Has(HidppDevice.ReportRate)) o["reportMs"] = ReadRate().Current;
            if (_d.Has(HidppDevice.Sidetone)) o["sidetone"] = ReadSidetone();
            if (_d.Has(HidppDevice.Equalizer)) o["eq"] = new JsonArray(ReadEq(EqInfo().Bands).Select(g => (JsonNode)(int)g).ToArray());
        }
        catch (HidppException e) { Log.Write($"{Name} capture: {e.Message}"); }
        return o.Count > 0 ? o : null;
    }

    public override void Apply(JsonObject s)
    {
        try
        {
            if (s["dpi"] is JsonValue dpi && _d.Has(HidppDevice.AdjustableDpi) && ReadDpi() != (int)dpi) WriteDpi((int)dpi);
            if (s["reportMs"] is JsonValue ms && _d.Has(HidppDevice.ReportRate) && ReadRate().Current != (int)ms) WriteRate((int)ms);
            if (s["sidetone"] is JsonValue st && _d.Has(HidppDevice.Sidetone) && ReadSidetone() != (int)st) WriteSidetone((int)st);
            if (s["eq"] is JsonArray eq && _d.Has(HidppDevice.Equalizer))
            {
                var want = eq.Select(n => (sbyte)(int)n!).ToArray();
                if (!ReadEq(want.Length).SequenceEqual(want)) WriteEq(want);
            }
        }
        catch (HidppException e) { Log.Write($"{Name} apply: {e.Message}"); }
    }

    public override void Poll()
    {
        var b = _d.ReadBattery();
        if (b is { } bb) { Battery = bb.Percent; Charging = bb.Charging; }
        string s = b is { } v ?$"{(v.Estimated ? "~" : "")}{v.Percent}%" + (v.Charging ? Lang.T(" carregando", " charging") : "") : "";
        if (s != Status) { Status = s; RaiseChanged(); }
    }

    // ---- page ----

    private sealed class State
    {
        public (int Percent, bool Charging, bool Estimated)? Battery;
        public (int Min, int Max, int Step)? DpiRange;
        public int Dpi;
        public (List<int> Options, int Current)? Rate;
        public bool? Onboard;
        public List<Zone> Zones = new();
        public int Sidetone = -1;
        public (int Bands, int Range)? Eq;
        public sbyte[] Gains = Array.Empty<sbyte>();
        public bool EqWritable;
        public bool LampArray;
    }

    public override void BuildPage(Page p)
    {
        string kind = Kind switch { GearKind.Headset => Lang.T("Headset", "Headset"), GearKind.Keyboard => Lang.T("Teclado", "Keyboard"), _ => "Mouse" };
        p.Header(Name, kind + " Logitech, " + Lang.T("falando direto com o aparelho (HID++).", "talking straight to the device (HID++)."));
        if (VendorApps.GHub)
            p.Note(p.Root, Lang.T("O G HUB está aberto: sidetone e memória passam por ele. O resto vai direto ao aparelho, e o G HUB pode pôr o dele de volta quando trocar de perfil.",
                "G HUB is open: sidetone and memory go through it. The rest goes straight to the device, and G HUB may put its own back on a profile switch."), true);
        var loading = p.Note(p.Root, Lang.T("Lendo o aparelho...", "Reading the device..."));
        p.Work(() =>
        {
            var s = new State { Battery = _d.ReadBattery() };
            T? Try<T>(Func<T> f) where T : struct { try { return f(); } catch (HidppException) { return null; } }
            if (_d.Has(HidppDevice.AdjustableDpi)) { s.DpiRange = Try(() => DpiRange() ?? (100, 25600, 50)); s.Dpi = Try(ReadDpi) ?? 0; }
            if (_d.Has(HidppDevice.ReportRate)) s.Rate = Try(ReadRate);
            if (_d.Has(HidppDevice.OnboardProfiles)) s.Onboard = Try(ReadOnboard);
            try { s.Zones = ReadZones(); } catch (HidppException) { }
            if (_d.Has(HidppDevice.Sidetone)) s.Sidetone = Try(ReadSidetone) ?? -1;
            if (_d.Has(HidppDevice.Equalizer))
            {
                s.Eq = Try(EqInfo);
                if (s.Eq is { } eq)
                {
                    try
                    {
                        s.Gains = ReadEq(eq.Bands);
                        // writing the same gains changes nothing and says whether the device takes EQ writes at all:
                        // the G733 refuses every layout (error 2) and G HUB does its EQ on the PC instead (06/11/2026)
                        WriteEq(s.Gains);
                        s.EqWritable = true;
                    }
                    catch (HidppException) { }
                }
            }
            // Windows lists a lamp array for both the G502 and the G733, but only drives the one G HUB hands it
            // ("control": "LAMPARRAY"); the G733 on "FIRMWARE" took our colour. Without G HUB there is no telling.
            var lights = DeviceHub.Instance.Gears.OfType<Lighting.DynamicLights>().FirstOrDefault();
            if (s.Zones.Count > 0 && lights != null && new[] { _product }.Concat(_d.ModelIds).Any(lights.Covers))
                s.LampArray = GHubClient.With(c => GHubId(c) is { } id && c.LightingControl(id) == "LAMPARRAY") == true;
            return s;
        }, s =>
        {
            p.Root.Children.Remove(loading);
            Build(p, s);
        });
    }

    private void Build(Page p, State s)
    {
        string fail = Lang.T("O aparelho recusou", "The device refused");
        if (s.Battery is { } b)
            p.Note(p.Root, Lang.T("Bateria: ", "Battery: ") + $"{(b.Estimated ? "~" : "")}{b.Percent}%" + (b.Charging ? Lang.T(", carregando", ", charging") : ""));

        if (s.DpiRange != null || s.Rate != null || s.Onboard != null)
        {
            var sec = p.Section(Lang.T("Sensor", "Sensor"));
            if (s.DpiRange is { } r)
            {
                FrameworkElement? slider = null;
                var presets = new[] { 400, 800, 1200, 1600, 2400, 3200 }.Where(v => v >= r.Min && v <= r.Max).ToList();
                Border? quick = null;
                slider = p.Slider(r.Min, r.Max, r.Step, s.Dpi, v => $"{v:0}", v =>
                {
                    int dpi = (int)v;
                    if (quick != null) Page.SetQuiet(quick, presets.IndexOf(dpi));
                    p.Work(() => WriteDpi(dpi), ok => p.Say(ok ? $"DPI {dpi}" : fail, ok), fail);
                }, 360);
                p.Row(sec, "DPI", Lang.T($"De {r.Min} a {r.Max}, de {r.Step} em {r.Step}.", $"From {r.Min} to {r.Max}, in steps of {r.Step}."), slider);
                quick = p.Segments(presets.Select(v => v.ToString()).ToList(), presets.IndexOf(s.Dpi), i =>
                {
                    int dpi = presets[i];
                    Page.SetQuiet(slider, dpi);
                    p.Work(() => WriteDpi(dpi), ok => p.Say(ok ? $"DPI {dpi}" : fail, ok), fail);
                });
                p.Row(sec, Lang.T("Atalhos de DPI", "DPI presets"), null, quick);
            }
            if (s.Rate is { } rate && rate.Options.Count > 0)
            {
                var opts = rate.Options.OrderBy(ms => ms).ToList();
                p.Row(sec, Lang.T("Taxa de resposta", "Report rate"), null, p.Segments(opts.Select(ms => $"{1000 / ms} Hz").ToList(), opts.IndexOf(rate.Current), i =>
                {
                    int ms = opts[i];
                    p.Work(() => WriteRate(ms), () => p.Say(Lang.T("Taxa em ", "Rate at ") + $"{1000 / ms} Hz"), fail);
                }));
            }
            if (Kind == GearKind.Mouse)
            {
                var result = new TextBlock { Text = "", Foreground = Page.B("Ink2"), FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
                var measure = p.Button(Lang.T("Medir (3 s)", "Measure (3 s)"), () =>
                {
                    if (App.Window == null) return;
                    result.Text = Lang.T("mexa o mouse em círculos...", "move the mouse in circles...");
                    Ui.MouseRate.Measure(App.Window, hz =>
                    {
                        result.Text = hz is int v ? $"{v} Hz" : Lang.T("mexeu pouco, tente de novo", "too little movement, try again");
                        if (hz is int v2) p.Say(Lang.T("Taxa real: ", "Real rate: ") + $"{v2} Hz");
                    });
                });
                p.Row(sec, Lang.T("Taxa real", "Real rate"), Lang.T("Conta os relatórios que chegam enquanto você mexe o mouse.", "Counts the reports that arrive while you move the mouse."), p.Line(result, measure));
            }
            if (s.Onboard is bool onboard)
                p.Row(sec, Lang.T("Usar a memória do mouse", "Use the mouse's memory"),
                    Lang.T("Ligado, o mouse segue o perfil gravado nele (sem programa nenhum). Desligado, quem manda é o programa.",
                        "On, the mouse follows the profile stored in it (no software needed). Off, software is in charge."),
                    p.Toggle(onboard, v => p.Work(() => WriteOnboard(v), () => p.Say(v ? Lang.T("Memória do mouse", "Onboard memory") : Lang.T("Modo programa", "Software mode")), fail)));
        }

        if (s.Zones.Count > 0)
        {
            string hint = Lang.T("Os efeitos animados são desenhados pelo Gear Hub, uma cor por zona. \"Do aparelho\" devolve o ciclo de cores dele.",
                "Animated effects are drawn by Gear Hub, one colour per zone. \"Device's own\" puts its colour cycle back.");
            if (VendorApps.GHub) hint += " " + Lang.T("Se o G HUB trocar de perfil, ele pode pôr a luz dele de volta.", "If G HUB switches profile, it may put its own light back.");
            if (s.LampArray)
                p.Note(p.Root, Lang.T("A Iluminação Dinâmica do Windows está mandando na luz deste aparelho e regrava a cor o tempo todo, então o que for escolhido aqui não aparece. Para o Gear Hub mandar, desligue o aparelho em Configurações > Personalização > Iluminação dinâmica (ou mude a cor na página Iluminação Dinâmica, com esta janela na frente).",
                    "Windows Dynamic Lighting is driving this device's light and keeps writing over it, so what you pick here will not show. To drive it from Gear Hub, turn the device off in Settings > Personalization > Dynamic Lighting (or change the colour on the Dynamic Lighting page, with this window in front)."), true);
            Ui.EffectsPanel.Build(p, new GearHub.Lighting.ILightTarget[] { this }, hint, title: Lang.T("Luz", "Lighting"));
        }

        if (s.Sidetone >= 0 || s.Eq != null)
        {
            var sec = p.Section(Lang.T("Áudio", "Audio"));
            if (s.Sidetone >= 0)
                p.Row(sec, "Sidetone", Lang.T("Quanto da sua voz volta no fone.", "How much of your voice comes back in the headset."),
                    p.Slider(0, 100, 1, s.Sidetone, v => $"{v:0}%", v => { int l = (int)v; p.Work(() => WriteSidetone(l), () => p.Say($"Sidetone {l}%"), fail); }));
            if (s.Eq is { } && !s.EqWritable)
                p.Note(sec, Lang.T("Equalizador: este aparelho não aceita EQ gravado nele. O do G HUB é feito no PC (pelo driver de áudio da Logitech), não no fone.",
                    "Equalizer: this device does not take a stored EQ. G HUB's EQ runs on the PC (Logitech's audio driver), not in the headset."));
            if (s.Eq is { } eq && eq.Bands > 0 && s.EqWritable)
            {
                p.Note(sec, Lang.T("Equalizador gravado no fone (vale em qualquer programa).", "Equalizer stored in the headset (works in any app)."));
                string[] freqs = eq.Bands == 10 ? new[] { "32", "64", "125", "250", "500", "1k", "2k", "4k", "8k", "16k" } : Enumerable.Range(1, eq.Bands).Select(i => i.ToString()).ToArray();
                var gains = s.Gains.Length == eq.Bands ? s.Gains.ToArray() : new sbyte[eq.Bands];
                for (int i = 0; i < eq.Bands; i++)
                {
                    int band = i;
                    p.Row(sec, freqs[i] + " Hz", null, p.Slider(-eq.Range, eq.Range, 1, gains[i], v => $"{v:+0;-0;0} dB", v =>
                    {
                        gains[band] = (sbyte)v;
                        var copy = gains.ToArray();
                        p.Work(() => WriteEq(copy), () => p.Say(Lang.T("Equalizador gravado", "Equalizer saved")), fail);
                    }, 260));
                }
                sec.Children.Add(p.Line(p.Button(Lang.T("Zerar", "Flat"), () =>
                {
                    var zero = new sbyte[eq.Bands];
                    p.Work(() => WriteEq(zero), () => p.Say(Lang.T("Equalizador zerado (reabra a página)", "Equalizer flat (reopen the page)")), fail);
                })));
            }
        }
    }
}
