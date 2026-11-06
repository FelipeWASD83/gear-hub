using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using GearHub.Hid;
using GearHub.Ui;
using Page = GearHub.Ui.Page;
using Microsoft.Win32.SafeHandles;

namespace GearHub.Devices.Razer;

/// <summary>
/// Razer Huntsman V3 HE TKL (1532:02EA): the control interface 3 with 91-byte feature reports. Synapse talks to the
/// same interface, so a reply of another transaction is skipped and read again and a busy keyboard is asked again.
/// Everything is written to slot 1, the live one Synapse fills with the active profile: it lasts until Synapse
/// switches profile (or forever with Synapse closed).
/// </summary>
internal sealed class RazerKeyboard : Gear
{
    public static readonly ushort[] Products = { 0x02EA };
    private readonly string _path;
    private readonly ushort _product;
    private readonly object _gate = new();
    private byte _transaction;

    private RazerKeyboard(string path, ushort product)
    {
        _path = path;
        _product = product;
    }

    public override string Key => $"razer-{_product:x4}";
    public override string Name => "Razer Huntsman V3 HE TKL";
    public override string Brand => "Razer";
    public override GearKind Kind => GearKind.Keyboard;

    public static RazerKeyboard? Find(IEnumerable<HidInfo> all)
    {
        foreach (var h in all)
            if (h.Vendor == 0x1532 && Products.Contains(h.Product) && h.Interface == 3 && h.FeatureLength == RazerProtocol.ReportLength)
                return new RazerKeyboard(h.Path, h.Product);
        return null;
    }

    public bool SamePath(string path) => string.Equals(path, _path, StringComparison.OrdinalIgnoreCase);

    // ---- hardware ----

    public byte[]? Command(RazerProtocol.Cmd cmd, ReadOnlySpan<byte> args, int? size = null)
    {
        byte[] a = args.ToArray();
        lock (_gate)
        {
            using SafeFileHandle? h = HidApi.Open(_path, false);
            if (h == null) return null;
            // 1 ms sleeps while we talk (the default tick is 15.6 ms); the keyboard answers within a few ms
            timeBeginPeriod(1);
            try
            {
                return Exchange(h, cmd, a, size);
            }
            finally { timeEndPeriod(1); }
        }
    }

    private byte[]? Exchange(SafeFileHandle h, RazerProtocol.Cmd cmd, byte[] a, int? size)
    {
        {
            // Synapse talks to the same interface and its own command overwrites the reply buffer: when our reply
            // does not show up within ~40 ms the command is sent again (a profile switch or Synapse being edited)
            for (int attempt = 0; attempt < 10; attempt++)
            {
                if (attempt > 0) Thread.Sleep(2 + Random.Shared.Next(12));
                _transaction = (byte)(_transaction % 31 + 1);
                byte t = _transaction;
                var report = RazerProtocol.Build(t, cmd, a, size);
                if (!HidApi.SetFeature(h, report)) { Thread.Sleep(20); continue; }
                var back = new byte[RazerProtocol.ReportLength];
                for (int i = 0; i < 14; i++)
                {
                    Thread.Sleep(i < 4 ? 1 : 3);
                    Array.Clear(back);
                    if (!HidApi.GetFeature(h, back)) break;
                    var r = RazerProtocol.Parse(back);
                    if (!RazerProtocol.IsOurs(r, t, cmd)) continue;
                    if (Environment.GetEnvironmentVariable("GEARHUB_TRACE") == "1")
                        Log.Write($"razer: {cmd.Class:X2} {cmd.Id:X2} attempt {attempt} poll {i} status {r.Status}");
                    if (r.Status == RazerProtocol.StatusOk) return r.Data;
                    Log.Write($"razer: {cmd.Class:X2} {cmd.Id:X2} status {r.Status}");
                    return null;
                }
            }
            Log.Write($"razer: {cmd.Class:X2} {cmd.Id:X2} no answer");
            return null;
        }
    }

    [System.Runtime.InteropServices.DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
    [System.Runtime.InteropServices.DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);

    public SnapTapState? ReadSnapTap() =>
        Command(RazerProtocol.GetSnapTap, new[] { RazerProtocol.LiveSlot }) is { } d ? RazerProtocol.ParseSnapTap(d) : null;

    public bool WriteSnapTap(bool enabled, IReadOnlyList<SnapPair> pairs) =>
        Command(RazerProtocol.SetSnapTap, RazerProtocol.SnapTapArgs(RazerProtocol.LiveSlot, enabled, pairs)) != null;

    public List<KeyActuation> ReadActuation(IReadOnlyList<byte> keys)
    {
        var all = new List<KeyActuation>();
        foreach (var chunk in keys.Chunk(RazerProtocol.ActuationPerCall))
        {
            var q = RazerProtocol.ActuationQuery(RazerProtocol.LiveSlot, chunk);
            if (Command(RazerProtocol.GetActuation, q, q.Length) is { } d) all.AddRange(RazerProtocol.ParseActuation(d));
        }
        return all;
    }

    public bool WriteActuation(IReadOnlyList<KeyActuation> keys)
    {
        bool ok = true;
        foreach (var chunk in keys.Chunk(RazerProtocol.ActuationPerCall))
        {
            var a = RazerProtocol.ActuationArgs(RazerProtocol.LiveSlot, chunk);
            ok &= Command(RazerProtocol.SetActuation, a, a.Length) != null;
        }
        return ok;
    }

    public List<KeyRapidTrigger> ReadRapid(IReadOnlyList<byte> keys)
    {
        var all = new List<KeyRapidTrigger>();
        foreach (var chunk in keys.Chunk(RazerProtocol.RapidPerCall))
        {
            var q = RazerProtocol.RapidQuery(RazerProtocol.LiveSlot, chunk);
            if (Command(RazerProtocol.GetRapidTrigger, q, q.Length) is { } d) all.AddRange(RazerProtocol.ParseRapid(d));
        }
        return all;
    }

    public bool WriteRapid(IReadOnlyList<KeyRapidTrigger> keys)
    {
        bool ok = true;
        foreach (var chunk in keys.Chunk(RazerProtocol.RapidPerCall))
        {
            var a = RazerProtocol.RapidArgs(RazerProtocol.LiveSlot, chunk);
            ok &= Command(RazerProtocol.SetRapidTrigger, a, a.Length) != null;
        }
        return ok;
    }

    /// <summary>Polling rate in Hz, or 0 when the keyboard does not say.</summary>
    public int ReadPolling()
    {
        if (Command(RazerProtocol.GetHighPolling, ReadOnlySpan<byte>.Empty) is { Length: >= 2 } d)
            foreach (var (code, hz) in RazerProtocol.PollingRates)
                if (d[1] == code) return hz;
        return 0;
    }

    public bool WritePolling(int hz)
    {
        foreach (var (code, rate) in RazerProtocol.PollingRates)
            if (rate == hz) return Command(RazerProtocol.SetHighPolling, new byte[] { 0, code }) != null;
        return false;
    }

    /// <summary>0..255, or -1.</summary>
    public int ReadBrightness() =>
        Command(RazerProtocol.GetBrightness, new byte[] { 1, 0 }) is { Length: >= 3 } d ? d[2] : -1;

    public bool WriteBrightness(int value) =>
        Command(RazerProtocol.SetBrightness, new byte[] { 1, 0, (byte)Math.Clamp(value, 0, 255) }) != null;

    private static IReadOnlyList<byte> AllKeys => RazerProtocol.Keys.Select(k => k.Id).ToList();

    // ---- profiles ----

    public override JsonObject? Capture()
    {
        var o = new JsonObject();
        if (ReadSnapTap() is { } st)
            o["snapTap"] = new JsonObject
            {
                ["enabled"] = st.Enabled,
                ["pairs"] = new JsonArray(st.Used.Select(p => (JsonNode)new JsonArray(p.Key1, p.Key2, p.Mode)).ToArray()),
            };
        var act = ReadActuation(AllKeys);
        if (act.Count > 0) o["actuation"] = new JsonObject(act.Select(k => new KeyValuePair<string, JsonNode?>(k.Key.ToString(), new JsonArray(k.High, k.Low))));
        var rt = ReadRapid(AllKeys);
        if (rt.Count > 0) o["rapidTrigger"] = new JsonObject(rt.Select(k => new KeyValuePair<string, JsonNode?>(k.Key.ToString(), new JsonArray(k.Enabled, k.Up, k.Down))));
        int hz = ReadPolling();
        if (hz > 0) o["pollingHz"] = hz;
        int br = ReadBrightness();
        if (br >= 0) o["brightness"] = br;
        return o;
    }

    public override void Apply(JsonObject s)
    {
        if (s["snapTap"] is JsonObject st)
        {
            var pairs = (st["pairs"] as JsonArray ?? new JsonArray()).OfType<JsonArray>()
                .Select(p => new SnapPair((byte)(int)p[0]!, (byte)(int)p[1]!, (byte)(int)p[2]!)).ToList();
            bool on = st["enabled"]?.GetValue<bool>() ?? false;
            var now = ReadSnapTap();
            if (now == null || now.Enabled != on || !now.Used.SequenceEqual(pairs)) WriteSnapTap(on, pairs);
        }
        if (s["actuation"] is JsonObject act)
        {
            var want = act.Select(kv => new KeyActuation(byte.Parse(kv.Key), (ushort)(int)kv.Value![0]!, (ushort)(int)kv.Value![1]!)).ToList();
            var now = ReadActuation(want.Select(w => w.Key).ToList()).ToDictionary(k => k.Key);
            var diff = want.Where(w => !now.TryGetValue(w.Key, out var n) || n != w).ToList();
            if (diff.Count > 0) WriteActuation(diff);
        }
        if (s["rapidTrigger"] is JsonObject rt)
        {
            var want = rt.Select(kv => new KeyRapidTrigger(byte.Parse(kv.Key), kv.Value![0]!.GetValue<bool>(), (ushort)(int)kv.Value![1]!, (ushort)(int)kv.Value![2]!)).ToList();
            var now = ReadRapid(want.Select(w => w.Key).ToList()).ToDictionary(k => k.Key);
            var diff = want.Where(w => !now.TryGetValue(w.Key, out var n) || n != w).ToList();
            if (diff.Count > 0) WriteRapid(diff);
        }
        if (s["pollingHz"] is JsonValue hz && hz.GetValue<int>() is int rate && rate > 0 && ReadPolling() != rate) WritePolling(rate);
        if (s["brightness"] is JsonValue b && b.GetValue<int>() is int br && ReadBrightness() != br) WriteBrightness(br);
    }

    public override void Poll()
    {
        var st = ReadSnapTap();
        string s = st == null ? "" : "Snap Tap " + (st.Enabled ? Lang.T("ligado", "on") : Lang.T("desligado", "off"));
        if (s != Status) { Status = s; RaiseChanged(); }
    }

    // ---- page ----

    private sealed class State
    {
        public SnapTapState? Snap;
        public Dictionary<byte, KeyActuation> Act = new();
        public Dictionary<byte, KeyRapidTrigger> Rapid = new();
        public int Polling;
        public int Brightness;
    }

    public override void BuildPage(Page p)
    {
        p.Header(Name, Lang.T("Teclado magnético. Tudo é gravado direto no teclado, no perfil que está ativo.",
            "Magnetic keyboard. Everything is written straight to the keyboard, in the active profile."));
        if (VendorApps.Synapse)
            p.Note(p.Root, Lang.T("O Synapse está aberto: quando ele trocar de perfil, regrava o que estiver no perfil dele.",
                "Synapse is open: when it switches profile it writes its own profile back."), true);
        var loading = p.Note(p.Root, Lang.T("Lendo o teclado...", "Reading the keyboard..."));
        p.Work(() =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var s = new State { Snap = ReadSnapTap(), Polling = ReadPolling(), Brightness = ReadBrightness() };
            foreach (var k in ReadActuation(AllKeys)) s.Act[k.Key] = k;
            foreach (var k in ReadRapid(AllKeys)) s.Rapid[k.Key] = k;
            Log.Write($"razer: page read in {sw.ElapsedMilliseconds} ms ({s.Act.Count} actuation, {s.Rapid.Count} rapid)");
            return s;
        }, s =>
        {
            p.Root.Children.Remove(loading);
            BuildSnapTap(p, s);
            BuildAnalog(p, s);
            BuildOther(p, s);
        });
    }

    private void BuildSnapTap(Page p, State s)
    {
        var sec = p.Section("Snap Tap", Lang.T("Quando as duas teclas do par estão apertadas, só uma vale (o modo diz qual).",
            "When both keys of a pair are held, only one counts (the mode says which)."));
        if (s.Snap == null) { p.Note(sec, Lang.T("O teclado não respondeu.", "The keyboard did not answer."), true); return; }
        var pairs = s.Snap.Used.ToList();
        bool enabled = s.Snap.Enabled;
        var list = new StackPanel();

        void Save(string ok)
        {
            var copy = pairs.ToList();
            bool on = enabled;
            p.Work(() => WriteSnapTap(on, copy), done =>
            {
                p.Say(done ? ok : Lang.T("O teclado recusou", "The keyboard refused"), done);
                if (done) { Status = "Snap Tap " + (on ? Lang.T("ligado", "on") : Lang.T("desligado", "off")); RaiseChanged(); }
            });
        }

        p.Row(sec, Lang.T("Ligado", "On"), null, p.Toggle(enabled, v =>
        {
            enabled = v;
            Save(v ? Lang.T("Snap Tap ligado", "Snap Tap on") : Lang.T("Snap Tap desligado", "Snap Tap off"));
        }));
        sec.Children.Add(list);

        void Draw()
        {
            list.Children.Clear();
            if (pairs.Count == 0) p.Note(list, Lang.T("Nenhum par. Escolha duas teclas abaixo e adicione.", "No pairs. Pick two keys below and add."));
            for (int i = 0; i < pairs.Count; i++)
            {
                int idx = i;
                var modes = Enumerable.Range(0, 5).Select(m => RazerProtocol.ModeName((byte)m)).ToList();
                var seg = p.Segments(new[] { Lang.T("Última", "Last"), "1ª", "2ª", Lang.T("Anula", "Cancel"), Lang.T("Mais funda", "Deeper") },
                    pairs[i].Mode, m =>
                    {
                        pairs[idx] = pairs[idx] with { Mode = (byte)m };
                        Save(Lang.T("Modo: ", "Mode: ") + modes[m]);
                    });
                var remove = p.Button(Lang.T("Tirar", "Remove"), () =>
                {
                    pairs.RemoveAt(idx);
                    Save(Lang.T("Par tirado", "Pair removed"));
                    Draw();
                });
                p.Row(list, $"{RazerProtocol.KeyName(pairs[i].Key1)} ⇄ {RazerProtocol.KeyName(pairs[i].Key2)}", RazerProtocol.ModeName(pairs[i].Mode), p.Line(seg, remove));
            }
        }
        Draw();

        var chips = KeyChips(p, sec, new HashSet<byte>());
        var add = p.Button(Lang.T("Adicionar par", "Add pair"), () =>
        {
            var picked = chips.Picked();
            if (picked.Count != 2) { p.Say(Lang.T("Escolha exatamente duas teclas", "Pick exactly two keys"), false); return; }
            if (pairs.Count >= 4) { p.Say(Lang.T("O teclado guarda até 4 pares", "The keyboard keeps up to 4 pairs"), false); return; }
            pairs.Add(new SnapPair(picked[0], picked[1], 0));
            chips.Clear();
            Save(Lang.T("Par adicionado", "Pair added"));
            Draw();
        });
        sec.Children.Add(p.Line(add));
    }

    private void BuildAnalog(Page p, State s)
    {
        var sec = p.Section(Lang.T("Atuação e rapid trigger", "Actuation and rapid trigger"),
            Lang.T("Escolha as teclas e mexa nos controles. O valor mostrado é o da primeira tecla escolhida.",
                "Pick keys and move the controls. The value shown is the first picked key's."));
        var chips = KeyChips(p, sec, new HashSet<byte> { 18, 31, 32, 33 });
        byte First() => chips.Picked().FirstOrDefault(k => s.Act.ContainsKey(k));

        double startAct = s.Act.TryGetValue(18, out var w) ? w.PressMm : 1.5;
        FrameworkElement? actSlider = null;
        actSlider = p.Slider(0.1, 4.0, 0.05, startAct, Page.Mm, mm =>
        {
            var keys = chips.Picked();
            if (keys.Count == 0) { p.Say(Lang.T("Escolha alguma tecla", "Pick a key"), false); return; }
            var list = keys.Select(k =>
            {
                // keep each key's gap between release and press (0.1 mm when unknown)
                double gap = s.Act.TryGetValue(k, out var a) ? Math.Max(0.05, a.PressMm - a.ReleaseMm) : 0.1;
                ushort press = RazerProtocol.FromMm(mm), release = RazerProtocol.FromMm(Math.Max(0.05, mm - gap));
                return new KeyActuation(k, release, press);
            }).ToList();
            p.Work(() => WriteActuation(list), ok =>
            {
                if (ok) foreach (var k in list) s.Act[k.Key] = k;
                p.Say(ok ? Lang.T("Atuação em ", "Actuation at ") + Page.Mm(mm) : Lang.T("O teclado recusou", "The keyboard refused"), ok);
            });
        });
        p.Row(sec, Lang.T("Ponto de atuação", "Actuation point"), Lang.T("Quanto a tecla desce até valer.", "How far the key travels before it counts."), actSlider);

        var first = s.Rapid.TryGetValue(18, out var r0) ? r0 : new KeyRapidTrigger(18, false, RazerProtocol.FromMm(0.1), RazerProtocol.FromMm(0.1));
        bool rtOn = first.Enabled;
        double rtMm = first.DownMm > 0 ? first.DownMm : 0.1;

        void WriteRt(string ok)
        {
            var keys = chips.Picked();
            if (keys.Count == 0) { p.Say(Lang.T("Escolha alguma tecla", "Pick a key"), false); return; }
            ushort v = RazerProtocol.FromMm(rtMm);
            var list = keys.Select(k => new KeyRapidTrigger(k, rtOn, v, v)).ToList();
            p.Work(() => WriteRapid(list), done =>
            {
                if (done) foreach (var k in list) s.Rapid[k.Key] = k;
                p.Say(done ? ok : Lang.T("O teclado recusou", "The keyboard refused"), done);
            });
        }

        var rtToggle = p.Toggle(rtOn, v => { rtOn = v; WriteRt(v ? Lang.T("Rapid trigger ligado", "Rapid trigger on") : Lang.T("Rapid trigger desligado", "Rapid trigger off")); });
        p.Row(sec, "Rapid trigger", Lang.T("A tecla solta e volta a valer no meio do caminho.", "The key resets and fires again mid travel."), rtToggle);
        var rtSlider = p.Slider(0.1, 2.0, 0.05, rtMm, Page.Mm, mm => { rtMm = mm; WriteRt(Lang.T("Sensibilidade ", "Sensitivity ") + Page.Mm(mm)); });
        p.Row(sec, Lang.T("Sensibilidade", "Sensitivity"), Lang.T("Quanto precisa mexer para soltar ou voltar a valer.", "How much movement resets or fires again."), rtSlider);

        chips.OnChange = () =>
        {
            byte k = First();
            if (k != 0 && s.Act.TryGetValue(k, out var a)) Page.SetQuiet(actSlider, a.PressMm);
            if (k != 0 && s.Rapid.TryGetValue(k, out var r))
            {
                rtOn = r.Enabled;
                rtMm = r.DownMm > 0 ? r.DownMm : rtMm;
                Page.SetQuiet(rtToggle, r.Enabled);
                Page.SetQuiet(rtSlider, rtMm);
            }
        };
    }

    private void BuildOther(Page p, State s)
    {
        var sec = p.Section(Lang.T("Teclado", "Keyboard"));
        var rates = RazerProtocol.PollingRates.Select(r => r.Hz >= 1000 ? $"{r.Hz / 1000} kHz" : $"{r.Hz} Hz").ToList();
        int sel = Array.FindIndex(RazerProtocol.PollingRates, r => r.Hz == s.Polling);
        p.Row(sec, Lang.T("Taxa de resposta", "Polling rate"), Lang.T("8 kHz gasta mais CPU.", "8 kHz uses more CPU."), p.Segments(rates, sel, i =>
        {
            int hz = RazerProtocol.PollingRates[i].Hz;
            p.Work(() => WritePolling(hz), ok => p.Say(ok ? Lang.T("Taxa em ", "Polling at ") + rates[i] : Lang.T("O teclado recusou", "The keyboard refused"), ok));
        }));
        if (s.Brightness >= 0)
            p.Row(sec, Lang.T("Brilho", "Brightness"), null, p.Slider(0, 100, 1, Math.Round(s.Brightness / 2.55), v => $"{v:0}%", v =>
            {
                int raw = (int)Math.Round(v * 2.55);
                p.Work(() => WriteBrightness(raw), ok => p.Say(ok ? Lang.T("Brilho ", "Brightness ") + $"{v:0}%" : Lang.T("O teclado recusou", "The keyboard refused"), ok));
            }));
    }

    /// <summary>The grid of analog keys as toggle chips.</summary>
    private static KeyPicker KeyChips(Page p, Panel parent, HashSet<byte> start)
    {
        var picker = new KeyPicker();
        var rows = new StackPanel { Margin = new Thickness(0, 8, 0, 8) };
        WrapPanel? wrap = null;
        int lastRow = -1;
        foreach (var (id, _) in RazerProtocol.Keys)
        {
            // keyboard rows by Synapse's ids: digits, Q row, A row, Z row, bottom row and arrows
            int row = id <= 11 ? 0 : id <= 26 ? 1 : id <= 39 ? 2 : id <= 52 ? 3 : 4;
            if (row != lastRow)
            {
                wrap = new WrapPanel { Margin = new Thickness(row * 14, 0, 0, 0) };
                rows.Children.Add(wrap);
                lastRow = row;
            }
            var chip = new ToggleButton { Content = RazerProtocol.KeyName(id), Style = Page.S("Chip"), IsChecked = start.Contains(id), Tag = id };
            chip.Checked += (_, _) => { picker.Order.Remove(id); picker.Order.Add(id); picker.OnChange?.Invoke(); };
            chip.Unchecked += (_, _) => { picker.Order.Remove(id); picker.OnChange?.Invoke(); };
            if (chip.IsChecked == true) picker.Order.Add(id);
            picker.Chips.Add(chip);
            wrap!.Children.Add(chip);
        }
        parent.Children.Add(rows);
        return picker;
    }

    private sealed class KeyPicker
    {
        public readonly List<ToggleButton> Chips = new();
        public readonly List<byte> Order = new();
        public Action? OnChange;
        public List<byte> Picked() => Order.ToList();

        public void Clear()
        {
            foreach (var c in Chips) c.IsChecked = false;
            Order.Clear();
        }
    }
}
