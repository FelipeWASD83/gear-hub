using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Windows.Media;
using GearHub.Hid;
using GearHub.Ui;

namespace GearHub.Devices.Controllers;

/// <summary>
/// DualSense and DualSense Edge straight over HID, used only while DSX is closed (DSX holds the controller otherwise).
/// Report layout from the Linux driver (hid-playstation.c), the public reference: USB output report 0x02 (48 bytes,
/// common block at byte 1); Bluetooth 0x31 (78 bytes, common block at byte 3, CRC32 of 0xA2 + bytes 0..73 at the end).
/// Common block: valid_flag0 (0x04 right trigger, 0x08 left trigger), valid_flag1 (0x01 mic LED, 0x04 light bar,
/// 0x10 player LEDs), motors, mic LED at +8, right trigger at +10 (11 bytes), left at +21, valid_flag2 at +38
/// (0x02 light bar setup), light bar setup at +41, LED brightness +42, player LEDs +43, red/green/blue +44..46.
/// Input: battery in the status byte (low nibble 0..10, high nibble 1 = charging, 2 = full).
/// </summary>
internal sealed class DualSense : Gear
{
    public static readonly ushort[] Products = { 0x0CE6, 0x0DF2 };
    private readonly HidInfo _info;
    private HidStream? _stream;
    private readonly bool _bluetooth;
    private byte _seq;
    private Color _light = Color.FromRgb(0, 122, 255);
    private int _left, _right, _players = 0b00100;

    private DualSense(HidInfo info)
    {
        _info = info;
        _bluetooth = info.OutputLength != 48;
    }

    public override string Key => "dualsense-" + (_info.Product == 0x0DF2 ? "edge" : "std");
    public override string Name => _info.Product == 0x0DF2 ? "DualSense Edge" : "DualSense";
    public override string Brand => "PlayStation";
    public override GearKind Kind => GearKind.Controller;
    public string Path => _info.Path;

    public static List<DualSense> Find(IEnumerable<HidInfo> all)
    {
        if (VendorApps.Dsx) return new List<DualSense>();
        return all.Where(h => h.Vendor == 0x054C && Products.Contains(h.Product) && h.UsagePage == 0x0001 && h.Usage == 0x0005 && h.OutputLength > 0)
            .Select(h => new DualSense(h)).ToList();
    }

    // trigger effect blocks (mode + params), the "legacy" modes every DualSense firmware accepts
    public static readonly (string Pt, string En, byte[] Effect)[] Triggers =
    {
        ("Normal", "Normal", new byte[] { 0x05 }),
        ("Resistência", "Resistance", new byte[] { 0x01, 0x30, 0xA0 }),
        ("Seção", "Section", new byte[] { 0x02, 0x50, 0xA0 }),
        ("Rígido", "Rigid", new byte[] { 0x01, 0x00, 0xFF }),
    };

    private bool Open()
    {
        if (_stream is { Alive: true }) return true;
        _stream?.Dispose();
        _stream = HidStream.Open(_info);
        return _stream != null;
    }

    public static byte[] BuildReport(bool bluetooth, byte seq, Color light, int players, byte[] left, byte[] right, bool lightbarSetup)
    {
        var r = new byte[bluetooth ? 78 : 48];
        int c;
        if (bluetooth)
        {
            r[0] = 0x31;
            r[1] = (byte)(seq << 4);
            r[2] = 0x10;
            c = 3;
        }
        else
        {
            r[0] = 0x02;
            c = 1;
        }
        r[c + 0] = 0x04 | 0x08;
        r[c + 1] = 0x04 | 0x10;
        right.CopyTo(r, c + 10);
        left.CopyTo(r, c + 21);
        if (lightbarSetup)
        {
            r[c + 38] = 0x02;
            r[c + 41] = 0x02;
        }
        r[c + 42] = 0x00;
        r[c + 43] = (byte)(players & 0x1F);
        r[c + 44] = light.R;
        r[c + 45] = light.G;
        r[c + 46] = light.B;
        if (bluetooth)
        {
            uint crc = Crc32(new byte[] { 0xA2 }.Concat(r.Take(74)).ToArray());
            BitConverter.GetBytes(crc).CopyTo(r, 74);
        }
        return r;
    }

    public static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++) crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
        }
        return ~crc;
    }

    private bool Send(bool setup = false)
    {
        if (!Open()) return false;
        var report = BuildReport(_bluetooth, _seq++, _light, _players, Triggers[_left].Effect, Triggers[_right].Effect, setup);
        if (_seq > 15) _seq = 0;
        return _stream!.Write(report);
    }

    public override void Poll()
    {
        if (!Open()) return;
        byte[]? last = null;
        while (_stream!.Reports.TryTake(out var r)) last = r;
        last ??= _stream.Reports.TryTake(out var one, 200) ? one : null;
        if (last == null) return;
        int status = last[0] == 0x31 && last.Length > 54 ? last[54] : last[0] == 0x01 && last.Length > 53 ? last[53] : -1;
        if (status < 0) return;
        int level = Math.Min(100, (status & 0x0F) * 10 + 5);
        int state = (status & 0xF0) >> 4;
        string s = state == 2 ? Lang.T("carregado", "full") : $"{level}%" + (state == 1 ? Lang.T(" carregando", " charging") : "");
        if (s != Status) { Status = s; RaiseChanged(); }
    }

    public override JsonObject? Capture() => new() { ["left"] = _left, ["right"] = _right, ["light"] = Page.Hex(_light), ["players"] = _players };

    public override void Apply(JsonObject s)
    {
        _left = Math.Clamp(s["left"]?.GetValue<int>() ?? 0, 0, Triggers.Length - 1);
        _right = Math.Clamp(s["right"]?.GetValue<int>() ?? 0, 0, Triggers.Length - 1);
        if (Page.TryParse(s["light"]?.ToString() ?? "", out var c)) _light = c;
        _players = s["players"]?.GetValue<int>() ?? _players;
        Send(true);
    }

    public override void BuildPage(Page p)
    {
        p.Header(Name, Lang.T("Direto pelo " + (_bluetooth ? "Bluetooth" : "USB") + ", sem o DSX. Com o DSX aberto, quem manda é ele.",
            "Straight over " + (_bluetooth ? "Bluetooth" : "USB") + ", without DSX. With DSX open, DSX is in charge."));
        string fail = Lang.T("O controle não aceitou", "The controller did not take it");
        var names = Triggers.Select(t => Lang.T(t.Pt, t.En)).ToList();
        var sec = p.Section(Lang.T("Gatilhos adaptáveis", "Adaptive triggers"));
        p.Row(sec, "L2", null, p.Segments(names, _left, i => { _left = i; p.Work(() => Send(), ok => p.Say(ok ? "L2: " + names[i] : fail, ok)); }));
        p.Row(sec, "R2", null, p.Segments(names, _right, i => { _right = i; p.Work(() => Send(), ok => p.Say(ok ? "R2: " + names[i] : fail, ok)); }));
        var light = p.Section(Lang.T("Luz", "Light"));
        p.Row(light, Lang.T("Barra de luz", "Light bar"), null, p.ColorPick(_light, c => { _light = c; p.Work(() => Send(true), ok => p.Say(ok ? Lang.T("Luz trocada", "Light changed") : fail, ok)); }));
        var leds = new[] { "1", "2", "3", "4", Lang.T("Nenhum", "None") };
        int[] masks = { 0b00100, 0b01010, 0b10101, 0b11011, 0 };
        p.Row(light, Lang.T("LEDs do jogador", "Player LEDs"), null, p.Segments(leds, Array.IndexOf(masks, _players), i => { _players = masks[i]; p.Work(() => Send(), ok => p.Say(ok ? leds[i] : fail, ok)); }));
    }

    public override void Dispose() => _stream?.Dispose();
}
