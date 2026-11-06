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

        // 1.1.0: Chroma packet, effects, hotkeys, the local channel
        Check("razer: pacote de efeito", RazerProtocol.ChromaArgs(2, 2, 0, new[] { ((byte)1, (byte)2, (byte)3), ((byte)4, (byte)5, (byte)6) }).SequenceEqual(new byte[] { 1, 0, 2, 2, 0, 2, 1, 2, 3, 4, 5, 6 }));
        var r = new Lighting.Effects.Renderer(Enumerable.Range(0, 20).Select(i => i / 19.0).ToArray());
        var rain = r.Frame(new Lighting.EffectSpec { Effect = Lighting.Effects.Rainbow }, 1.0, null);
        Check("efeitos: arco-íris muda pela posição", rain.Distinct().Count() > 10);
        var breathMin = Enumerable.Range(0, 100).Select(i => r.Frame(new Lighting.EffectSpec { Effect = Lighting.Effects.Breathing, A = "#FF0000", B = "#FF0000" }, i * 0.05, null)[0].R).Min();
        Check($"efeitos: respiração nunca apaga (mínimo {breathMin} de 255)", breathMin >= 80);
        var half = r.Frame(new Lighting.EffectSpec { Effect = Lighting.Effects.Static, A = "#FFFFFF", Brightness = 0.5 }, 0, null)[0];
        Check("efeitos: brilho 50%", half.R is >= 126 and <= 128);
        Check("atalhos: Ctrl+Alt+P", Hotkeys.TryParse("Ctrl+Alt+P", out uint mods, out uint vk) && mods == 3 && vk == 0x50);
        Check("atalhos: tecla sem modificador recusada", !Hotkeys.TryParse("P", out _, out _));
        Check("canal: hello e pedido", ChannelRoundTrip());

        // updater version compare
        Check("atualizador: versões", Updater.Compare("1.0.10", "1.0.9") > 0 && Updater.Compare("v2.0.0", "1.9.9") > 0 && Updater.Compare("1.0.0", "1.0.0") == 0);

        Out.AppendLine(_fail == 0 ? "tudo ok" : $"{_fail} falha(s)");
        Finish(file);
        return _fail == 0 ? 0 : 1;
    }

    /// <summary>
    /// --write-test &lt;step&gt; &lt;file&gt;: one write at a time, read back to prove the device took it, then the value from
    /// before is written again. Steps: razer-actuation, razer-rapid, razer-polling, razer-brightness, logi-dpi,
    /// logi-rate, logi-light, g733-sidetone, g733-eq, g733-light, ram, webcam.
    /// </summary>
    public static int WriteTest(string step, string? file)
    {
        var hub = DeviceHub.Instance;
        hub.Scan(force: true);
        void Line(string s) => Out.AppendLine(s);
        try
        {
            var razer = hub.Gears.OfType<RazerKeyboard>().FirstOrDefault();
            var mouse = hub.Gears.OfType<LogitechGear>().FirstOrDefault(g => g.Device.Has(HidppDevice.AdjustableDpi));
            var headset = hub.Gears.OfType<LogitechGear>().FirstOrDefault(g => g.Device.Has(HidppDevice.Sidetone));
            switch (step)
            {
                case "razer-actuation" when razer != null:
                {
                    var before = razer.ReadActuation(new byte[] { 18 }).Single();
                    var test = new KeyActuation(18, RazerProtocol.FromMm(before.ReleaseMm + 0.2), RazerProtocol.FromMm(before.PressMm + 0.2));
                    Line($"W antes: {before.PressMm} mm (solta {before.ReleaseMm})");
                    Line($"escreveu {test.PressMm}: {razer.WriteActuation(new[] { test })}");
                    var after = razer.ReadActuation(new byte[] { 18 }).Single();
                    Line($"lido de volta: {after.PressMm} mm (solta {after.ReleaseMm}) -> {(after == test ? "OK" : "DIFERENTE")}");
                    Line($"devolveu: {razer.WriteActuation(new[] { before })}, agora {razer.ReadActuation(new byte[] { 18 }).Single().PressMm} mm");
                    break;
                }
                case "razer-rapid" when razer != null:
                {
                    var before = razer.ReadRapid(new byte[] { 18 }).Single();
                    var test = before with { Enabled = !before.Enabled, Up = RazerProtocol.FromMm(0.3), Down = RazerProtocol.FromMm(0.3) };
                    Line($"W antes: {(before.Enabled ? "ligado" : "desligado")} {before.DownMm} mm");
                    Line($"escreveu {(test.Enabled ? "ligado" : "desligado")} 0,3 mm: {razer.WriteRapid(new[] { test })}");
                    var after = razer.ReadRapid(new byte[] { 18 }).Single();
                    Line($"lido de volta: {(after.Enabled ? "ligado" : "desligado")} {after.DownMm} mm -> {(after == test ? "OK" : "DIFERENTE")}");
                    Line($"devolveu: {razer.WriteRapid(new[] { before })}, agora {(razer.ReadRapid(new byte[] { 18 }).Single() == before ? "igual ao antes" : "DIFERENTE")}");
                    break;
                }
                case "restore" when razer != null:
                {
                    var syn = new SynapseLog(746);
                    syn.Poll();
                    Line($"perfil do Synapse agora: {syn.ActiveName ?? "(não achei no log)"}");
                    var before = razer.ReadActuation(new byte[] { 18 }).Single();
                    int pollBefore = razer.ReadPolling();
                    razer.WriteActuation(new[] { new KeyActuation(18, RazerProtocol.FromMm(before.ReleaseMm + 0.5), RazerProtocol.FromMm(before.PressMm + 0.5)) });
                    razer.WritePolling(pollBefore == 1000 ? 8000 : 1000);
                    Line($"mudou: W {razer.ReadActuation(new byte[] { 18 }).Single().PressMm} mm, polling {razer.ReadPolling()} Hz; mudanças guardadas: {razer.HasChanges}");
                    var done = hub.RestoreAll();
                    var after = razer.ReadActuation(new byte[] { 18 }).Single();
                    Line($"devolveu: {string.Join(" | ", done)}");
                    Line($"agora: W {after.PressMm} mm (antes {before.PressMm}), polling {razer.ReadPolling()} Hz (antes {pollBefore}) -> {(after == before && razer.ReadPolling() == pollBefore ? "IGUAL AO ANTES" : "DIFERENTE")}");
                    break;
                }
                case "depth" when razer != null:
                {
                    // announced: keys released for 3 s, then hold W all the way down, then halfway, then let go
                    for (int i = 0; i < 100; i++)
                    {
                        var w = razer.ReadDepth(18);
                        var a = razer.ReadDepth(31);
                        if (i % 5 == 0) Line($"{i / 10.0:0.0} s  W={w}  A={a}");
                        System.Threading.Thread.Sleep(100);
                    }
                    break;
                }
                case "chroma" when razer != null:
                {
                    var before = razer.ReadChroma();
                    Line($"antes: efeito {before?.Effect}");
                    foreach (var (id, name) in new[] { ((byte)4, "onda"), ((byte)7, "estrelas"), ((byte)5, "reativo") })
                    {
                        byte rate = RazerProtocol.ChromaRate(id, 1);
                        var colors = id == 7 ? new[] { System.Windows.Media.Colors.Cyan, System.Windows.Media.Colors.Magenta } : Array.Empty<System.Windows.Media.Color>();
                        Line($"{DateTime.Now:HH:mm:ss} {name}: {razer.WriteChroma(id, (byte)(id == 4 ? 1 : 0), rate, colors)}; lido: {razer.ReadChroma()?.Effect}");
                        System.Threading.Thread.Sleep(5000);
                    }
                    if (before is { } b) Line($"devolveu {b.Effect}: {razer.WriteChroma(b.Effect, b.Flags, b.Rate, b.Colors)}");
                    break;
                }
                case "razer-polling" when razer != null:
                {
                    int before = razer.ReadPolling();
                    int test = before == 1000 ? 8000 : 1000;
                    Line($"antes: {before} Hz; escreveu {test}: {razer.WritePolling(test)}; lido: {razer.ReadPolling()} Hz");
                    Line($"devolveu {before}: {razer.WritePolling(before)}; agora {razer.ReadPolling()} Hz");
                    break;
                }
                case "razer-brightness" when razer != null:
                {
                    int before = razer.ReadBrightness();
                    Line($"antes: {before}; escreveu 40: {razer.WriteBrightness(40)}; lido: {razer.ReadBrightness()}");
                    System.Threading.Thread.Sleep(2500);
                    Line($"devolveu {before}: {razer.WriteBrightness(before)}; agora {razer.ReadBrightness()}");
                    break;
                }
                case "logi-dpi" when mouse != null:
                {
                    int before = mouse.ReadDpi();
                    int test = before == 1600 ? 1200 : 1600;
                    Line($"G HUB aberto: {VendorApps.GHub}; antes: {before}; escreveu {test}: {mouse.WriteDpi(test)}");
                    System.Threading.Thread.Sleep(800);
                    Line($"lido no mouse: {mouse.ReadDpi()}");
                    Line($"devolveu {before}: {mouse.WriteDpi(before)}");
                    System.Threading.Thread.Sleep(800);
                    Line($"agora: {mouse.ReadDpi()}");
                    break;
                }
                case "logi-rate" when mouse != null:
                {
                    var (opts, before) = mouse.ReadRate();
                    int test = before == 1 ? 2 : 1;
                    mouse.WriteRate(test);
                    Line($"antes: {before} ms; escreveu {test} ms; lido {mouse.ReadRate().Current} ms");
                    mouse.WriteRate(before);
                    Line($"devolveu {before} ms; agora {mouse.ReadRate().Current} ms");
                    break;
                }
                case "logi-light" when mouse != null:
                case "g733-light" when headset != null:
                {
                    var g = step == "logi-light" ? mouse! : headset!;
                    foreach (var z in g.ReadZones())
                    {
                        g.WriteEffect(z, LogitechGear.EffectFixed, System.Windows.Media.Color.FromRgb(255, 0, 0));
                        Line($"zona {z.Index}: vermelho fixo aceito");
                    }
                    System.Threading.Thread.Sleep(3000);
                    foreach (var z in g.ReadZones())
                    {
                        // the G733 was on a 1 s cycle in G HUB (06/11/2026)
                        g.WriteEffect(z, LogitechGear.EffectCycle, System.Windows.Media.Colors.Black, step == "g733-light" ? 1000 : 5000);
                        Line($"zona {z.Index}: ciclo aceito (o G HUB e a Iluminação Dinâmica podem pôr o deles de volta)");
                    }
                    break;
                }
                case "g733-sidetone" when headset != null:
                {
                    int before = headset.ReadSidetone();
                    headset.WriteSidetone(before == 20 ? 30 : 20);
                    System.Threading.Thread.Sleep(500);
                    Line($"antes: {before}; lido depois de escrever: {headset.ReadSidetone()}");
                    headset.WriteSidetone(before);
                    System.Threading.Thread.Sleep(500);
                    Line($"devolveu {before}; agora {headset.ReadSidetone()}");
                    break;
                }
                case "g733-eq" when headset != null:
                {
                    int bands = headset.EqInfo().Bands;
                    var before = headset.ReadEq(bands);
                    var test = before.ToArray();
                    test[0] = (sbyte)(before[0] == 3 ? 4 : 3);
                    Line($"antes: {string.Join(",", before)}");
                    // the layouts public code uses: gains only, or one prefix byte before them
                    foreach (int? prefix in new int?[] { null, 0x00, 0x01, 0x02, 0x03 })
                    {
                        string name = prefix == null ? "só os ganhos" : $"prefixo {prefix:X2}";
                        try
                        {
                            headset.WriteEq(test, prefix);
                            var read = headset.ReadEq(bands);
                            Line($"{name}: aceito, lido {string.Join(",", read)} -> {(read.SequenceEqual(test) ? "OK" : "diferente")}");
                            headset.WriteEq(before, prefix);
                            Line($"   devolveu; agora {string.Join(",", headset.ReadEq(bands))}");
                            if (read.SequenceEqual(test)) break;
                        }
                        catch (HidppException e) { Line($"{name}: recusado ({e.Message})"); }
                    }
                    break;
                }
                case "logi-dpi-direct" when mouse != null:
                {
                    int before = mouse.ReadDpi();
                    int test = before == 1600 ? 1200 : 1600;
                    mouse.Device.Call(HidppDevice.AdjustableDpi, 3, 0, (byte)(test >> 8), (byte)test);
                    Line($"antes {before}; HID++ direto {test}");
                    for (int i = 1; i <= 4; i++) { System.Threading.Thread.Sleep(1000); Line($"   {i} s depois: {mouse.ReadDpi()}"); }
                    mouse.Device.Call(HidppDevice.AdjustableDpi, 3, 0, (byte)(before >> 8), (byte)before);
                    System.Threading.Thread.Sleep(500);
                    Line($"devolveu {before}; agora {mouse.ReadDpi()}");
                    break;
                }
                case "lights-slow":
                {
                    // announced to him beforehand: 10 s wait, G502 red 8 s, G733 red 8 s, RAM "ffff0000" 7 s then "ff0000ff" 7 s
                    var red = System.Windows.Media.Color.FromRgb(255, 0, 0);
                    System.Threading.Thread.Sleep(10000);
                    foreach (var (g, name, period) in new[] { (mouse, "G502", 5000), (headset, "G733", 1000) })
                    {
                        if (g == null) continue;
                        var zones = g.ReadZones();
                        foreach (var z in zones) g.WriteEffect(z, LogitechGear.EffectFixed, red);
                        Line($"{DateTime.Now:HH:mm:ss} {name} vermelho");
                        System.Threading.Thread.Sleep(8000);
                        foreach (var z in zones) g.WriteEffect(z, LogitechGear.EffectCycle, System.Windows.Media.Colors.Black, period);
                        Line($"{DateTime.Now:HH:mm:ss} {name} ciclo");
                        System.Threading.Thread.Sleep(2000);
                    }
                    var ram = hub.Gears.OfType<CorsairRam>().FirstOrDefault();
                    if (ram != null)
                    {
                        Line($"{DateTime.Now:HH:mm:ss} RAM ffff0000: {ram.PaintRaw("ffff0000") ?? "aceito"}");
                        System.Threading.Thread.Sleep(7000);
                        Line($"{DateTime.Now:HH:mm:ss} RAM ff0000ff: {ram.PaintRaw("ff0000ff") ?? "aceito"}");
                        System.Threading.Thread.Sleep(7000);
                        ram.End();
                        Line($"{DateTime.Now:HH:mm:ss} RAM solta");
                    }
                    break;
                }
                case "ram":
                case "fx":
                {
                    // the animated effects through the engine, announced to him: rainbow 8 s, comet 8 s, fire 8 s, then let go
                    var targets = Lighting.LightEngine.Targets().Where(t => step == "fx" || t is CorsairRam).ToList();
                    Line($"alvos: {string.Join(", ", targets.Select(t => t.Name))}");
                    var proc = System.Diagnostics.Process.GetCurrentProcess();
                    foreach (var fx in new[] { Lighting.Effects.Rainbow, Lighting.Effects.Comet, Lighting.Effects.Fire })
                    {
                        var spec = new Lighting.EffectSpec { Effect = fx, A = "#00C7BE", B = "#5856D6" };
                        foreach (var t in targets) Line($"{DateTime.Now:HH:mm:ss} {t.Name} {fx}: {Lighting.LightEngine.Instance.Set(t, spec, save: false, announce: false) ?? "ok"}");
                        var cpu0 = proc.TotalProcessorTime;
                        System.Threading.Thread.Sleep(8000);
                        proc.Refresh();
                        Line($"   CPU do processo em 8 s: {(proc.TotalProcessorTime - cpu0).TotalMilliseconds / 8000.0 / Environment.ProcessorCount * 100:0.00}% da máquina");
                    }
                    foreach (var t in targets) Lighting.LightEngine.Instance.Set(t, new Lighting.EffectSpec { Effect = Lighting.Effects.Off }, save: false, announce: false);
                    Line($"{DateTime.Now:HH:mm:ss} soltou (o efeito salvo de cada um volta quando o Gear Hub abrir)");
                    break;
                }
                case "webcam":
                {
                    var cam = hub.Gears.OfType<Devices.Webcam.Webcam>().FirstOrDefault();
                    var zoom = cam?.Read().FirstOrDefault(p => p.Camera && p.Id == 3);
                    if (cam == null || zoom == null) { Line("sem câmera com zoom"); break; }
                    int test = zoom.Value == 150 ? 160 : 150;
                    Line($"zoom antes {zoom.Value}; escreveu {test}: {cam.Write(true, 3, test, false)}; lido {cam.Read().First(p => p.Camera && p.Id == 3).Value}");
                    Line($"devolveu: {cam.Write(true, 3, zoom.Value, zoom.Auto)}; agora {cam.Read().First(p => p.Camera && p.Id == 3).Value}");
                    break;
                }
                default:
                    Line($"passo desconhecido ou aparelho ausente: {step}");
                    break;
            }
        }
        catch (Exception e) { Line("ERRO: " + e.GetType().Name + ": " + e.Message); }
        Finish(file);
        hub.Dispose();
        return 0;
    }

    /// <summary>--listen &lt;commands file&gt; &lt;seconds between&gt; &lt;out&gt;: a client of the running Gear Hub's channel.</summary>
    public static int Listen(string commandsFile, double gap, string? file)
    {
        try
        {
            using var client = new System.IO.Pipes.NamedPipeClientStream(".", EventChannel.PipeName, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
            client.Connect(3000);
            var reader = new System.IO.StreamReader(client);
            var writer = new System.IO.StreamWriter(client) { AutoFlush = true, NewLine = "\n" };
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var readTask = System.Threading.Tasks.Task.Run(async () =>
            {
                while (true)
                {
                    string? line = await reader.ReadLineAsync();
                    if (line == null) return;
                    lock (Out) Out.AppendLine($"{clock.Elapsed.TotalSeconds:0.0} <- {line[..Math.Min(200, line.Length)]}");
                }
            });
            System.Threading.Thread.Sleep(500);
            foreach (var cmd in File.ReadAllLines(commandsFile).Where(l => l.Trim() != ""))
            {
                lock (Out) Out.AppendLine($"{clock.Elapsed.TotalSeconds:0.0} -> {cmd}");
                writer.WriteLine(cmd);
                System.Threading.Thread.Sleep(TimeSpan.FromSeconds(gap));
            }
        }
        catch (Exception e) { Out.AppendLine("erro: " + e.Message); }
        Finish(file);
        return 0;
    }

    /// <summary>Starts the real pipe server, connects as a client, reads the hello, sends a command and sees it arrive.</summary>
    private static bool ChannelRoundTrip()
    {
        if (System.IO.Directory.GetFiles(@"\\.\pipe\").Any(p => p.EndsWith(EventChannel.PipeName, StringComparison.OrdinalIgnoreCase)))
        {
            Out.AppendLine("      (o Gear Hub está aberto e já tem o pipe: o teste do canal fala com ele)");
        }
        EventChannel.Hello = () => new JsonObject { ["type"] = "hello", ["version"] = "test" };
        JsonObject? got = null;
        using var arrived = new System.Threading.ManualResetEventSlim();
        Action<JsonObject> onCmd = c => { got = c; arrived.Set(); };
        EventChannel.Command += onCmd;
        EventChannel.Start();
        try
        {
            using var client = new System.IO.Pipes.NamedPipeClientStream(".", EventChannel.PipeName, System.IO.Pipes.PipeDirection.InOut);
            client.Connect(3000);
            var reader = new System.IO.StreamReader(client);
            var writer = new System.IO.StreamWriter(client) { AutoFlush = true, NewLine = "\n" };
            var hello = JsonNode.Parse(reader.ReadLine() ?? "{}");
            writer.WriteLine("{\"cmd\":\"profile\",\"name\":\"x\"}");
            bool ok = arrived.Wait(3000) && got?["name"]?.ToString() == "x";
            EventChannel.Publish(new JsonObject { ["type"] = "snaptap", ["on"] = true });
            var evt = JsonNode.Parse(reader.ReadLine() ?? "{}");
            return hello?["type"]?.ToString() == "hello" && ok && evt?["type"]?.ToString() == "snaptap";
        }
        catch (Exception e)
        {
            Out.AppendLine("      canal: " + e.Message);
            return false;
        }
        finally
        {
            EventChannel.Command -= onCmd;
            EventChannel.Stop();
        }
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
