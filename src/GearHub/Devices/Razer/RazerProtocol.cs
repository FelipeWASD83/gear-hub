using System;
using System.Collections.Generic;
using System.Linq;

namespace GearHub.Devices.Razer;

/// <summary>One Snap Tap pair: two analog key ids and how the keyboard settles both pressed.</summary>
internal readonly record struct SnapPair(byte Key1, byte Key2, byte Mode)
{
    public bool Empty => Key1 == 0 && Key2 == 0 && Mode == 0;
}

internal sealed record SnapTapState(byte Slot, bool Enabled, IReadOnlyList<SnapPair> Pairs)
{
    public IReadOnlyList<SnapPair> Used => Pairs.Where(p => !p.Empty).ToList();
}

/// <summary>Actuation of one key: two thresholds 0..65535 over 0..4 mm. The deeper one ("L") is where the key fires.</summary>
internal readonly record struct KeyActuation(byte Key, ushort High, ushort Low)
{
    public double PressMm => RazerProtocol.ToMm(Math.Max(High, Low));
    public double ReleaseMm => RazerProtocol.ToMm(Math.Min(High, Low));
}

internal readonly record struct KeyRapidTrigger(byte Key, bool Enabled, ushort Up, ushort Down)
{
    public double UpMm => RazerProtocol.ToMm(Up);
    public double DownMm => RazerProtocol.ToMm(Down);
}

/// <summary>
/// The Razer HID protocol as Synapse 4.0.896 speaks it to the Huntsman V3 HE (rzDevice25, product 746), read from
/// its cached code: a 91-byte feature report (report id 0) with status, transaction id 1..31, data size, command
/// class, command id, 80 bytes of arguments and an XOR of bytes 2..87. "Get" = "set" | 0x80. Status 2 ok, 1 busy,
/// 3 failed, 4 timeout, 5 unsupported. Pure (the self test runs it).
/// </summary>
internal static class RazerProtocol
{
    public const int ReportLength = 91;
    public const byte StatusBusy = 1, StatusOk = 2, StatusFailed = 3, StatusTimeout = 4, StatusUnsupported = 5;

    public readonly record struct Cmd(byte Size, byte Class, byte Id);

    // Synapse's command table (COMMAND_*_HEADER = [size, class, id])
    public static readonly Cmd GetActiveProfile = new(1, 0x05, 0x84);
    public static readonly Cmd GetSnapTap = new(15, 0x02, 0xA7);
    public static readonly Cmd SetSnapTap = new(15, 0x02, 0x27);
    public static readonly Cmd GetActuation = new(80, 0x02, 0x99);
    public static readonly Cmd SetActuation = new(80, 0x02, 0x19);
    public static readonly Cmd GetRapidTrigger = new(80, 0x02, 0x9A);
    public static readonly Cmd SetRapidTrigger = new(80, 0x02, 0x1A);
    public static readonly Cmd GetBottomDeadZone = new(2, 0x02, 0xA8);
    public static readonly Cmd SetBottomDeadZone = new(2, 0x02, 0x28);
    public static readonly Cmd GetHighPolling = new(2, 0x00, 0xC0);
    public static readonly Cmd SetHighPolling = new(2, 0x00, 0x40);
    public static readonly Cmd GetBrightness = new(3, 0x0F, 0x84);
    public static readonly Cmd SetBrightness = new(3, 0x0F, 0x04);

    /// <summary>Synapse writes the active profile to slot 1 (the live one).</summary>
    public const byte LiveSlot = 1;
    public const int ActuationPerCall = 15, RapidPerCall = 13;
    public const double FullTravelMm = 4.0;

    public static double ToMm(ushort raw) => Math.Round(raw * FullTravelMm / 65535.0, 2);
    public static ushort FromMm(double mm) => (ushort)Math.Clamp(Math.Round(mm * 65535.0 / FullTravelMm), 0, 65535);

    public static byte[] Build(byte transaction, Cmd cmd, ReadOnlySpan<byte> args, int? size = null)
    {
        var r = new byte[ReportLength];
        r[2] = transaction;
        r[6] = (byte)(size ?? cmd.Size);
        r[7] = cmd.Class;
        r[8] = cmd.Id;
        for (int i = 0; i < args.Length && i < 80; i++) r[9 + i] = args[i];
        r[89] = Checksum(r.AsSpan(1));
        return r;
    }

    public static byte Checksum(ReadOnlySpan<byte> packet)
    {
        byte c = 0;
        for (int i = 2; i < 88; i++) c ^= packet[i];
        return c;
    }

    public readonly record struct Reply(byte Status, byte Transaction, byte Size, byte Class, byte Id, byte[] Data);

    public static Reply Parse(ReadOnlySpan<byte> report)
    {
        var p = report[1..];
        int size = Math.Min((int)p[5], 80);
        return new Reply(p[0], p[1], p[5], p[6], p[7], p.Slice(8, size).ToArray());
    }

    public static bool IsOurs(Reply r, byte transaction, Cmd cmd) =>
        r.Status != StatusBusy && r.Transaction == transaction && r.Class == cmd.Class && r.Id == cmd.Id;

    public static SnapTapState ParseSnapTap(byte[] d)
    {
        var pairs = new List<SnapPair>();
        for (int i = 3; i + 2 < d.Length; i += 3) pairs.Add(new SnapPair(d[i], d[i + 1], d[i + 2]));
        return new SnapTapState(d.Length > 0 ? d[0] : (byte)0, d.Length > 2 && d[2] != 0, pairs);
    }

    /// <summary>Slot, number of pairs, on/off, then 3 bytes per pair in use (Synapse's way).</summary>
    public static byte[] SnapTapArgs(byte slot, bool enabled, IReadOnlyList<SnapPair> used)
    {
        var a = new byte[3 + 3 * used.Count];
        a[0] = slot;
        a[1] = (byte)used.Count;
        a[2] = (byte)(enabled ? 1 : 0);
        for (int i = 0; i < used.Count; i++)
        {
            a[3 + 3 * i] = used[i].Key1;
            a[4 + 3 * i] = used[i].Key2;
            a[5 + 3 * i] = used[i].Mode;
        }
        return a;
    }

    // setMultiAnalogKeyActuationPoint(dataType 0, profileId, [{keyId, thresholdH, thresholdL}]): 2 + 5 per key, words big-endian
    public static byte[] ActuationQuery(byte slot, IReadOnlyList<byte> keys)
    {
        var a = new byte[2 + 5 * keys.Count];
        a[1] = slot;
        for (int i = 0; i < keys.Count; i++) a[2 + 5 * i] = keys[i];
        return a;
    }

    public static byte[] ActuationArgs(byte slot, IReadOnlyList<KeyActuation> keys)
    {
        var a = new byte[2 + 5 * keys.Count];
        a[1] = slot;
        for (int i = 0; i < keys.Count; i++)
        {
            int o = 2 + 5 * i;
            a[o] = keys[i].Key;
            a[o + 1] = (byte)(keys[i].High >> 8); a[o + 2] = (byte)keys[i].High;
            a[o + 3] = (byte)(keys[i].Low >> 8); a[o + 4] = (byte)keys[i].Low;
        }
        return a;
    }

    public static List<KeyActuation> ParseActuation(byte[] d)
    {
        var list = new List<KeyActuation>();
        for (int n = 2; n + 4 < d.Length; n += 5)
            if (d[n] != 0) list.Add(new KeyActuation(d[n], (ushort)(d[n + 1] << 8 | d[n + 2]), (ushort)(d[n + 3] << 8 | d[n + 4])));
        return list;
    }

    // setMultiAnalogKeyRapidTrigger(0, profileId, [{keyId, enabled, up, down}]): 2 + 6 per key
    public static byte[] RapidQuery(byte slot, IReadOnlyList<byte> keys)
    {
        var a = new byte[2 + 6 * keys.Count];
        a[1] = slot;
        for (int i = 0; i < keys.Count; i++) a[2 + 6 * i] = keys[i];
        return a;
    }

    public static byte[] RapidArgs(byte slot, IReadOnlyList<KeyRapidTrigger> keys)
    {
        var a = new byte[2 + 6 * keys.Count];
        a[1] = slot;
        for (int i = 0; i < keys.Count; i++)
        {
            int o = 2 + 6 * i;
            a[o] = keys[i].Key;
            a[o + 1] = (byte)(keys[i].Enabled ? 1 : 0);
            a[o + 2] = (byte)(keys[i].Up >> 8); a[o + 3] = (byte)keys[i].Up;
            a[o + 4] = (byte)(keys[i].Down >> 8); a[o + 5] = (byte)keys[i].Down;
        }
        return a;
    }

    public static List<KeyRapidTrigger> ParseRapid(byte[] d)
    {
        var list = new List<KeyRapidTrigger>();
        for (int n = 2; n + 5 < d.Length; n += 6)
            if (d[n] != 0) list.Add(new KeyRapidTrigger(d[n], d[n + 1] != 0, (ushort)(d[n + 2] << 8 | d[n + 3]), (ushort)(d[n + 4] << 8 | d[n + 5])));
        return list;
    }

    /// <summary>Synapse's USB_HIGH_SPEED_POLLING_PERIOD_VALUE: code → Hz.</summary>
    public static readonly (byte Code, int Hz)[] PollingRates = { (0x01, 8000), (0x02, 4000), (0x04, 2000), (0x08, 1000), (0x10, 500), (0x20, 250), (0x40, 125) };

    // Synapse's analog key ids (its layout table)
    public static readonly (byte Id, string Name)[] Keys =
    {
        (2, "1"), (3, "2"), (4, "3"), (5, "4"), (6, "5"), (7, "6"), (8, "7"), (9, "8"), (10, "9"), (11, "0"),
        (16, "Tab"), (17, "Q"), (18, "W"), (19, "E"), (20, "R"), (21, "T"), (22, "Y"), (23, "U"), (24, "I"), (25, "O"), (26, "P"),
        (30, "Caps"), (31, "A"), (32, "S"), (33, "D"), (34, "F"), (35, "G"), (36, "H"), (37, "J"), (38, "K"), (39, "L"),
        (44, "Shift"), (46, "Z"), (47, "X"), (48, "C"), (49, "V"), (50, "B"), (51, "N"), (52, "M"),
        (58, "Ctrl"), (60, "Alt"), (61, "Space"),
        (79, "←"), (83, "↑"), (84, "↓"), (89, "→"),
    };

    public static string KeyName(byte id)
    {
        foreach (var k in Keys)
            if (k.Id == id) return k.Name == "Space" ? Lang.T("Espaço", "Space") : k.Name;
        return "#" + id;
    }

    // LAST_INPUT, PRIO_LEFT, PRIO_RIGHT, NEUTRAL, COMPARE_LEVEL in Synapse
    public static string ModeName(byte mode) => mode switch
    {
        0 => Lang.T("última tecla vale", "last input wins"),
        1 => Lang.T("a primeira tem prioridade", "first key wins"),
        2 => Lang.T("a segunda tem prioridade", "second key wins"),
        3 => Lang.T("as duas anulam", "both cancel"),
        4 => Lang.T("a mais apertada vale", "deeper press wins"),
        _ => Lang.T("modo ", "mode ") + mode,
    };
}
