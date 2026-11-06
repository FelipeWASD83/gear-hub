using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using GearHub.Devices;
using GearHub.Devices.Controllers;
using GearHub.Devices.Corsair;
using GearHub.Devices.Logitech;
using GearHub.Devices.Razer;

namespace GearHub;

/// <summary>
/// --self-test: the pure parts (packet building and parsing, CRC, conversions) against known values, no hardware.
/// --probe: a read-only tour of every device present (only "get" requests), to check the backends on this PC.
/// </summary>
internal static class SelfTest
{
    private static readonly StringBuilder Out = new();
    private static int _fail;

    private static void Check(string what, bool ok)
    {
        Out.AppendLine((ok ? "ok    " : "FALHA ") + what);
        if (!ok) _fail++;
    }

    private static void Finish(string? file)
    {
        string text = Out.ToString();
        if (file != null) File.WriteAllText(file, text, Encoding.UTF8);
        Console.Out.Write(text);
    }

    public static int Run(string? file)
    {
        // Razer: the packet Synapse builds for Get Snap Tap slot 1 (transaction 1)
        var pkt = RazerProtocol.Build(1, RazerProtocol.GetSnapTap, new byte[] { 1 });
        Check("razer: tamanho 91", pkt.Length == 91);
        Check("razer: cabeçalho", pkt[2] == 1 && pkt[6] == 15 && pkt[7] == 0x02 && pkt[8] == 0xA7 && pkt[9] == 1);
        byte x = 0;
        for (int i = 3; i < 89; i++) x ^= pkt[i];
        Check("razer: checksum", pkt[89] == x);
        // reply read on 06/11/2026: Snap Tap on with A/D
        var st = RazerProtocol.ParseSnapTap(new byte[] { 1, 4, 1, 31, 33, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
        Check("razer: snap tap lido", st.Enabled && st.Used.Count == 1 && st.Used[0] == new SnapPair(31, 33, 0));
        Check("razer: args do snap tap", RazerProtocol.SnapTapArgs(1, false, st.Used).SequenceEqual(new byte[] { 1, 1, 0, 31, 33, 0 }));
        // actuation read on 06/11/2026: W 0x4662/0x4CC8 (1.1 / 1.2 mm)
        var act = RazerProtocol.ParseActuation(new byte[] { 0, 1, 18, 0x46, 0x62, 0x4C, 0xC8, 31, 0x46, 0x62, 0x4C, 0xC8 });
        Check("razer: atuação lida", act.Count == 2 && act[0].Key == 18 && Math.Abs(act[0].PressMm - 1.2) < 0.01 && Math.Abs(act[0].ReleaseMm - 1.1) < 0.01);
        Check("razer: args da atuação", RazerProtocol.ActuationArgs(1, act.Take(1).ToList()).SequenceEqual(new byte[] { 0, 1, 18, 0x46, 0x62, 0x4C, 0xC8 }));
        Check("razer: mm ida e volta", RazerProtocol.FromMm(0.1) == 1638 && Math.Abs(RazerProtocol.ToMm(1638) - 0.1) < 0.001);
        var rt = RazerProtocol.ParseRapid(new byte[] { 0, 1, 18, 0, 0x06, 0x66, 0x06, 0x66 });
        Check("razer: rapid trigger lido", rt.Count == 1 && !rt[0].Enabled && Math.Abs(rt[0].UpMm - 0.1) < 0.001);
        Check("razer: args do rapid trigger", RazerProtocol.RapidArgs(1, new[] { rt[0] with { Enabled = true } }).SequenceEqual(new byte[] { 0, 1, 18, 1, 0x06, 0x66, 0x06, 0x66 }));

        // Logitech: effect parameters and the G733 battery
        var fixedP = LogitechGear.EffectParams(LogitechGear.EffectFixed, System.Windows.Media.Color.FromRgb(1, 2, 3));
        Check("logitech: efeito fixo", fixedP.Length == 10 && fixedP[0] == 1 && fixedP[1] == 2 && fixedP[2] == 3 && fixedP[3] == 0);
        var cyc = LogitechGear.EffectParams(LogitechGear.EffectCycle, System.Windows.Media.Colors.Black, 5000);
        Check("logitech: ciclo", cyc[5] == 0x13 && cyc[6] == 0x88 && cyc[7] == 100);
        Check("logitech: bateria por tensão (tabela)", Math.Abs(BatteryCurve.Percent(3.65) - 6) <= 1 && BatteryCurve.Percent(3.9) >= 98 && BatteryCurve.Percent(3.3) == 0);
        int ghub = BatteryCurve.Percent(3.625, 0x0AFE);
        Check($"logitech: 3,625 V no G733 = {ghub}% (o G HUB mostrou 5%)", ghub is >= 3 and <= 7);

        // DualSense: CRC32 of a known string and the report layout
        Check("dualsense: crc32", DualSense.Crc32(Encoding.ASCII.GetBytes("123456789")) == 0xCBF43926);
        var usb = DualSense.BuildReport(false, 0, System.Windows.Media.Color.FromRgb(10, 20, 30), 0b00100, new byte[] { 5 }, new byte[] { 1, 2, 3 }, true);
        Check("dualsense: usb", usb.Length == 48 && usb[0] == 2 && usb[1] == 0x0C && usb[2] == 0x14 && usb[11] == 1 && usb[22] == 5 && usb[44] == 4 && usb[45] == 10 && usb[47] == 30);
        var bt = DualSense.BuildReport(true, 3, System.Windows.Media.Colors.Red, 0, new byte[] { 5 }, new byte[] { 5 }, false);
        uint crc = DualSense.Crc32(new byte[] { 0xA2 }.Concat(bt.Take(74)).ToArray());
        Check("dualsense: bluetooth", bt.Length == 78 && bt[0] == 0x31 && bt[1] == 0x30 && BitConverter.ToUInt32(bt, 74) == crc);

        // DSX packet shape
        var dsx = JsonNode.Parse(DsxApi.Packet((DsxApi.RgbUpdate, new[] { 0, 255, 0, 0 })))!;
        Check("dsx: pacote", dsx["instructions"]![0]!["type"]!.GetValue<int>() == 2 && dsx["instructions"]![0]!["parameters"]!.AsArray().Count == 4);

        // updater version compare
        Check("atualizador: versões", Updater.Compare("1.0.10", "1.0.9") > 0 && Updater.Compare("v2.0.0", "1.9.9") > 0 && Updater.Compare("1.0.0", "1.0.0") == 0);

        Out.AppendLine(_fail == 0 ? "tudo ok" : $"{_fail} falha(s)");
        Finish(file);
        return _fail == 0 ? 0 : 1;
    }

    /// <summary>Read-only: lists what the hub finds and what each device answers to "get".</summary>
    public static int Probe(string? file)
    {
        var hub = DeviceHub.Instance;
        hub.Scan(force: true);
        foreach (var g in hub.Gears)
        {
            Out.AppendLine($"== {g.Name} [{g.Key}] {g.Kind} status=\"{g.Status}\"");
            try
            {
                switch (g)
                {
                    case RazerKeyboard r:
                        var st = r.ReadSnapTap();
                        Out.AppendLine($"   snap tap: {(st == null ? "sem resposta" : (st.Enabled ? "on " : "off ") + string.Join(", ", st.Used.Select(p => $"{RazerProtocol.KeyName(p.Key1)}/{RazerProtocol.KeyName(p.Key2)} modo {p.Mode}")))}");
                        foreach (var a in r.ReadActuation(new byte[] { 18, 31, 32, 33, 61 })) Out.AppendLine($"   atuação {RazerProtocol.KeyName(a.Key)}: {a.PressMm} mm (solta {a.ReleaseMm})");
                        foreach (var k in r.ReadRapid(new byte[] { 18, 31, 32, 33 })) Out.AppendLine($"   rapid {RazerProtocol.KeyName(k.Key)}: {(k.Enabled ? "on" : "off")} {k.DownMm}/{k.UpMm} mm");
                        Out.AppendLine($"   polling {r.ReadPolling()} Hz, brilho {r.ReadBrightness()}");
                        break;
                    case LogitechGear l:
                        var d = l.Device;
                        Out.AppendLine($"   índice {d.Index:X2}, unit {d.UnitId}, tipo {d.Type}, features {string.Join(" ", d.Features.Keys.OrderBy(k => k).Select(k => k.ToString("X4")))}");
                        if (d.Has(HidppDevice.AdjustableDpi)) Out.AppendLine($"   dpi {l.ReadDpi()} faixa {l.DpiRange()}");
                        if (d.Has(HidppDevice.ReportRate)) { var rr = l.ReadRate(); Out.AppendLine($"   taxa {rr.Current} ms de [{string.Join(",", rr.Options)}]"); }
                        if (d.Has(HidppDevice.OnboardProfiles)) Out.AppendLine($"   memória onboard: {l.ReadOnboard()}");
                        foreach (var z in l.ReadZones()) Out.AppendLine($"   luz zona {z.Index} local {z.Location}: efeitos {string.Join(",", z.Effects.Select(e => e.ToString("X4")))}");
                        if (d.Has(HidppDevice.Sidetone)) Out.AppendLine($"   sidetone {l.ReadSidetone()}");
                        if (d.Has(HidppDevice.Equalizer)) { var eq = l.EqInfo(); Out.AppendLine($"   eq {eq.Bands} bandas ±{eq.Range}: {string.Join(",", l.ReadEq(eq.Bands))}"); }
                        Out.AppendLine($"   bateria {d.ReadBattery()}");
                        break;
                    case CorsairRam:
                        Out.AppendLine($"   serviço instalado: {CdcsClient.Installed()}, pipe aberto: {CdcsClient.ServicePipeOpen()} (sem conectar: conectar avisa o iCUE)");
                        break;
                    case Devices.Webcam.Webcam w:
                        foreach (var p in w.Read()) Out.AppendLine($"   {p.Label}: {p.Value} [{p.Min}..{p.Max}] auto={p.Auto}");
                        break;
                    default:
                        g.Poll();
                        Out.AppendLine($"   {g.Status}");
                        break;
                }
            }
            catch (Exception e) { Out.AppendLine("   erro: " + e.Message); }
        }
        Finish(file);
        hub.Dispose();
        return 0;
    }
}
