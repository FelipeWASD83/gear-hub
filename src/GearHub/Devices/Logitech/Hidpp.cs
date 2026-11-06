using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using GearHub.Hid;

namespace GearHub.Devices.Logitech;

/// <summary>An HID++ 2.0 error or a missing answer.</summary>
internal sealed class HidppException : Exception
{
    public int Code { get; }
    public HidppException(string message, int code = -1) : base(message) => Code = code;
}

/// <summary>
/// HID++ over the vendor collections of one Logitech USB device (measured on 06/11/2026): short report 0x10 (7 bytes,
/// usage FF00/1), long 0x11 (20 bytes, usage FF00/2 or FF43/0202 on headsets, which have only the long one).
/// Requests carry our own software id (0xB): every answer reaches every open handle, G HUB's included (it uses 0xF),
/// so we take only answers with our id. One request at a time per device.
/// </summary>
internal sealed class HidppLink : IDisposable
{
    public const byte SoftwareId = 0x0B;
    private readonly HidStream? _short;
    private readonly HidStream _long;
    private readonly object _gate = new();
    public string Path => _long.Info.Path;

    private HidppLink(HidStream? s, HidStream l)
    {
        _short = s;
        _long = l;
    }

    /// <summary>Opens the HID++ collections of one interface (paths that share VID, PID and "mi_xx").</summary>
    public static HidppLink? Open(IReadOnlyList<HidInfo> sameInterface)
    {
        var longInfo = sameInterface.FirstOrDefault(h => h.OutputLength == 20 && ((h.UsagePage == 0xFF00 && h.Usage == 2) || (h.UsagePage == 0xFF43 && h.Usage == 0x0202)));
        if (longInfo == null) return null;
        var shortInfo = sameInterface.FirstOrDefault(h => h.OutputLength == 7 && h.UsagePage == 0xFF00 && h.Usage == 1);
        var l = HidStream.Open(longInfo);
        if (l == null) return null;
        var s = shortInfo != null ? HidStream.Open(shortInfo) : null;
        return new HidppLink(s, l);
    }

    public bool Alive => _long.Alive;

    /// <summary>Sends feature/function with parameters and returns the 16 bytes of the answer (params from byte 4).</summary>
    public byte[] Request(byte device, byte feature, byte function, ReadOnlySpan<byte> parameters, int timeoutMs = 600)
    {
        byte fnSw = (byte)(function << 4 | SoftwareId);
        bool useShort = _short != null && parameters.Length <= 3;
        var report = new byte[useShort ? 7 : 20];
        report[0] = (byte)(useShort ? 0x10 : 0x11);
        report[1] = device;
        report[2] = feature;
        report[3] = fnSw;
        parameters.CopyTo(report.AsSpan(4));
        lock (_gate)
        {
            _long.Drain();
            _short?.Drain();
            if (!(useShort ? _short!.Write(report) : _long.Write(report))) throw new HidppException("write failed");
            var deadline = Environment.TickCount64 + timeoutMs;
            while (Environment.TickCount64 < deadline)
            {
                byte[]? r = Take(_long, 15) ?? (_short != null ? Take(_short, 15) : null);
                if (r == null || r.Length < 7 || r[1] != device) continue;
                // HID++ 2.0 error: 11 dev FF feat fnSw err
                if (r[2] == 0xFF && r.Length >= 6 && r[3] == feature && r[4] == fnSw) throw new HidppException($"error {r[5]}", r[5]);
                // HID++ 1.0 error: 10 dev 8F subId addr err
                if (r[2] == 0x8F && r[3] == feature) throw new HidppException($"hid++1 error {r[5]}", r[5]);
                if (r[2] == feature && r[3] == fnSw)
                {
                    var data = new byte[16];
                    Array.Copy(r, 4, data, 0, Math.Min(16, r.Length - 4));
                    return data;
                }
            }
            throw new HidppException("timeout");
        }
    }

    private static byte[]? Take(HidStream s, int ms) => s.Reports.TryTake(out var r, ms) ? r : null;

    public void Dispose()
    {
        _short?.Dispose();
        _long.Dispose();
    }
}

/// <summary>One HID++ 2.0 device behind a link (device index 0xFF wired/headset, 1..6 through a receiver).</summary>
internal sealed class HidppDevice
{
    public HidppLink Link { get; }
    public byte Index { get; }
    public Dictionary<ushort, byte> Features { get; } = new();
    public string Name { get; private set; } = "";
    public int Type { get; private set; } = -1;
    public string UnitId { get; private set; } = "";
    public ushort Product { get; private set; }
    public List<ushort> ModelIds { get; } = new();

    // feature ids
    public const ushort Root = 0x0000, FeatureSet = 0x0001, DeviceInfo = 0x0003, DeviceName = 0x0005;
    public const ushort Battery = 0x1000, BatteryVoltage = 0x1001, UnifiedBattery = 0x1004, Adc = 0x1F20;
    public const ushort AdjustableDpi = 0x2201, ReportRate = 0x8060, ColorLedEffects = 0x8070, RgbEffects = 0x8071, OnboardProfiles = 0x8100;
    public const ushort Sidetone = 0x8300, Equalizer = 0x8310;

    private HidppDevice(HidppLink link, byte index)
    {
        Link = link;
        Index = index;
    }

    public bool Has(ushort feature) => Features.ContainsKey(feature);

    public byte[] Call(ushort feature, byte function, params byte[] parameters)
    {
        if (!Features.TryGetValue(feature, out byte idx)) throw new HidppException($"no feature {feature:X4}");
        return Link.Request(Index, idx, function, parameters);
    }

    /// <summary>Ping, then the feature table (about 35 requests, well under a second). Null = nothing answers there.</summary>
    public static HidppDevice? Probe(HidppLink link, byte index, ushort product)
    {
        try
        {
            var ping = link.Request(index, 0x00, 1, new byte[] { 0, 0, 0x5A }, 400);
            if (ping[0] < 2) return null; // HID++ 1.0 (a receiver itself)
        }
        catch (HidppException e)
        {
            if (Environment.GetEnvironmentVariable("GEARHUB_TRACE") == "1") Log.Write($"hid++ ping {index:X2} on {product:X4}: {e.Message}");
            return null;
        }

        var d = new HidppDevice(link, index) { Product = product };
        d.Features[Root] = 0;
        try
        {
            var fs = link.Request(index, 0x00, 0, new byte[] { 0x00, 0x01 });
            if (fs[0] == 0) return null;
            d.Features[FeatureSet] = fs[0];
            int count = link.Request(index, fs[0], 0, ReadOnlySpan<byte>.Empty)[0];
            for (int i = 1; i <= count; i++)
            {
                var f = link.Request(index, fs[0], 1, new[] { (byte)i });
                ushort id = (ushort)(f[0] << 8 | f[1]);
                d.Features.TryAdd(id, (byte)i);
            }
            if (d.Has(DeviceName))
            {
                int len = d.Call(DeviceName, 0)[0];
                var sb = new StringBuilder();
                while (sb.Length < len)
                {
                    var part = d.Call(DeviceName, 1, (byte)sb.Length);
                    int take = Math.Min(16, len - sb.Length);
                    sb.Append(Encoding.ASCII.GetString(part, 0, take).TrimEnd('\0'));
                    if (part[0] == 0) break;
                }
                d.Name = sb.ToString().Trim();
                try { d.Type = d.Call(DeviceName, 2)[0]; } catch (HidppException) { }
            }
            if (d.Has(DeviceInfo))
            {
                var info = d.Call(DeviceInfo, 0);
                d.UnitId = Convert.ToHexString(info, 1, 4).ToLowerInvariant();
                // model ids after the transport: 4099 (wireless) and c095 (USB) on the G502 X PLUS
                for (int i = 7; i + 1 < 13; i += 2)
                {
                    ushort m = (ushort)(info[i] << 8 | info[i + 1]);
                    if (m != 0) d.ModelIds.Add(m);
                }
            }
        }
        catch (HidppException e)
        {
            Log.Write($"hid++ probe {index:X2}: {e.Message}");
            return d.Features.Count > 2 ? d : null;
        }
        return d;
    }

    /// <summary>Battery as (percent, charging, estimated) from whichever feature the device has; null when none answers.</summary>
    public (int Percent, bool Charging, bool Estimated)? ReadBattery()
    {
        try
        {
            if (Has(UnifiedBattery))
            {
                var r = Call(UnifiedBattery, 1);
                return (r[0], r[2] is 1 or 2, false);
            }
            if (Has(Battery))
            {
                var r = Call(Battery, 0);
                return (r[0], r[2] is 1 or 2 or 3, false);
            }
            if (Has(Adc) || Has(BatteryVoltage))
            {
                var r = Call(Has(Adc) ? Adc : BatteryVoltage, 0);
                int mv = r[0] << 8 | r[1];
                if (mv == 0) return null;
                bool charging = (r[2] & 0x02) != 0;
                return (BatteryCurve.Percent(mv / 1000.0, Product), charging, true);
            }
        }
        catch (HidppException) { }
        return null;
    }
}

/// <summary>
/// Volts to percent for headsets that only report voltage (the G733's 0x1F20). With G HUB installed its own discharge
/// curve is read (depots\...\battery_&lt;pid&gt;.xml: minutes, volts, mWh discharged; percent = 1 - mWh/total; 3.625 V
/// gave 4.9% and G HUB showed 5% on 06/11/2026). Without it, points taken from that same curve.
/// </summary>
internal static class BatteryCurve
{
    private static readonly (double V, double Pct)[] Points =
    {
        (3.46, 0), (3.5, 0.5), (3.65, 6), (3.7, 23), (3.75, 42), (3.8, 72), (3.9, 99), (3.95, 100),
    };

    private static readonly Dictionary<ushort, List<(double V, double Pct)>?> Loaded = new();

    /// <summary>G HUB's discharge curve for a product, highest voltage first, or null.</summary>
    private static List<(double V, double Pct)>? Load(ushort product)
    {
        lock (Loaded)
        {
            if (Loaded.TryGetValue(product, out var c)) return c;
            List<(double, double)>? curve = null;
            try
            {
                string root = @"C:\ProgramData\LGHUB\depots";
                var file = System.IO.Directory.Exists(root)
                    ? System.IO.Directory.EnumerateFiles(root, $"battery_{product:x4}.xml", System.IO.SearchOption.AllDirectories).FirstOrDefault()
                    : null;
                if (file != null)
                {
                    string t = System.IO.File.ReadAllText(file);
                    int a = t.IndexOf("<discharge", StringComparison.Ordinal);
                    int s = t.IndexOf("<![CDATA[", a, StringComparison.Ordinal) + 9;
                    int e = t.IndexOf("]]>", s, StringComparison.Ordinal);
                    var rows = new List<(double V, double Mwh)>();
                    foreach (var line in t[s..e].Split('\n'))
                    {
                        var parts = line.Trim().Split(',');
                        if (parts.Length == 3 && double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v)
                            && double.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double mwh))
                            rows.Add((v, mwh));
                    }
                    if (rows.Count > 10)
                    {
                        double total = rows[^1].Mwh;
                        curve = rows.Select(r => (r.V, 100.0 * (1 - r.Mwh / total))).ToList();
                    }
                }
            }
            catch (Exception ex) { Log.Error("battery curve", ex); }
            Loaded[product] = curve;
            return curve;
        }
    }

    public static int Percent(double volts, ushort product)
    {
        var curve = Load(product);
        if (curve == null) return Percent(volts);
        foreach (var (v, pct) in curve)
            if (volts >= v) return (int)Math.Round(Math.Clamp(pct, 0, 100));
        return 0;
    }

    public static int Percent(double volts)
    {
        if (volts <= Points[0].V) return 0;
        for (int i = 1; i < Points.Length; i++)
        {
            if (volts > Points[i].V) continue;
            var (v0, p0) = Points[i - 1];
            var (v1, p1) = Points[i];
            return (int)Math.Round(p0 + (p1 - p0) * (volts - v0) / (v1 - v0));
        }
        return 100;
    }
}
