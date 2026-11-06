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

    /// <summary>The last Snap Tap state read or written (null until known).</summary>
    public bool? SnapOn { get; private set; }

    public bool WriteSnapTap(bool enabled, IReadOnlyList<SnapPair> pairs)
    {
        RememberSnap();
        bool ok = Command(RazerProtocol.SetSnapTap, RazerProtocol.SnapTapArgs(RazerProtocol.LiveSlot, enabled, pairs)) != null;
        if (ok)
        {
            bool changed = SnapOn != enabled;
            SnapOn = enabled;
            Status = "Snap Tap " + (enabled ? Lang.T("ligado", "on") : Lang.T("desligado", "off"));
            RaiseChanged();
            if (changed) Signals.RaiseSnapTap(enabled);
        }
        return ok;
    }

    /// <summary>Turns Snap Tap on or off keeping the pairs the keyboard has (the hotkey, the tray and the ilha use this).</summary>
    public bool SetSnapTapOn(bool on)
    {
        var st = ReadSnapTap();
        return st != null && WriteSnapTap(on, st.Used);
    }

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
        RememberActuation(keys.Select(k => k.Key));
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
        RememberRapid(keys.Select(k => k.Key));
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
        // [profile, code] both ways (Synapse's setUsbHighSpeedPollingPeriod); the live profile is slot 1
        if (Command(RazerProtocol.GetHighPolling, new[] { RazerProtocol.LiveSlot }) is { Length: >= 2 } d)
            foreach (var (code, hz) in RazerProtocol.PollingRates)
                if (d[1] == code) return hz;
        return 0;
    }

    public bool WritePolling(int hz)
    {
        RememberPolling();
        foreach (var (code, rate) in RazerProtocol.PollingRates)
            if (rate == hz) return Command(RazerProtocol.SetHighPolling, new byte[] { RazerProtocol.LiveSlot, code }) != null;
        return false;
    }

    /// <summary>0..255, or -1.</summary>
    public int ReadBrightness() =>
        Command(RazerProtocol.GetBrightness, new byte[] { 1, 0 }) is { Length: >= 3 } d ? d[2] : -1;

    public bool WriteBrightness(int value)
    {
        RememberBrightness();
        return Command(RazerProtocol.SetBrightness, new byte[] { 1, 0, (byte)Math.Clamp(value, 0, 255) }) != null;
    }

    /// <summary>The keyboard's current Chroma effect: id, flags, rate, colours (null when it does not say).</summary>
    public (byte Effect, byte Flags, byte Rate, List<System.Windows.Media.Color> Colors)? ReadChroma()
    {
        if (Command(RazerProtocol.GetChromaEffect, new byte[] { RazerProtocol.LiveSlot, 0 }, 5) is not { Length: >= 6 } d) return null;
        var colors = new List<System.Windows.Media.Color>();
        for (int i = 0; i < d[5] && 8 + 3 * i < d.Length; i++) colors.Add(System.Windows.Media.Color.FromRgb(d[6 + 3 * i], d[7 + 3 * i], d[8 + 3 * i]));
        return (d[2], d[3], d[4], colors);
    }

    public bool WriteChroma(byte effect, byte flags, byte rate, IReadOnlyList<System.Windows.Media.Color> colors)
    {
        RememberChroma();
        var args = RazerProtocol.ChromaArgs(effect, flags, rate, colors.Select(c => (c.R, c.G, c.B)).ToList());
        return Command(RazerProtocol.SetChromaEffect, args, args.Length) != null;
    }

    /// <summary>The closest hardware effect to a software one ("sync all").</summary>
    public bool WriteChromaFor(GearHub.Lighting.EffectSpec s)
    {
        var a = s.ColorA;
        var b = s.ColorB;
        double sp = s.Speed;
        return s.Effect switch
        {
            GearHub.Lighting.Effects.Off => true,
            GearHub.Lighting.Effects.Static or GearHub.Lighting.Effects.Gradient => WriteChroma(1, 0, 0, new[] { a }),
            GearHub.Lighting.Effects.Breathing => WriteChroma(2, 2, 0, new[] { a, b }),
            GearHub.Lighting.Effects.Wave => WriteChroma(4, 1, RazerProtocol.ChromaRate(4, sp), Array.Empty<System.Windows.Media.Color>()),
            GearHub.Lighting.Effects.Comet => WriteChroma(6, 0, RazerProtocol.ChromaRate(6, sp), new[] { a }),
            GearHub.Lighting.Effects.Twinkle => WriteChroma(7, 0, RazerProtocol.ChromaRate(7, sp), new[] { a, b }),
            // no fire on this keyboard: a red-orange breathing is the closest
            GearHub.Lighting.Effects.Fire => WriteChroma(2, 2, 0, new[] { System.Windows.Media.Color.FromRgb(255, 40, 0), System.Windows.Media.Color.FromRgb(255, 140, 0) }),
            _ => WriteChroma(3, 0, 0, Array.Empty<System.Windows.Media.Color>()),
        };
    }

    /// <summary>
    /// How far a key is pressed right now: Synapse's "Get Multi Analog Key ADC Data" ([0, key] → word at [2..3]), on
    /// the thresholds' 0..65535 = 0..4 mm scale (06/11/2026, with him: released 0, bottomed out 65535). Null when the
    /// keyboard does not answer.
    /// </summary>
    public ushort? ReadDepth(byte key) =>
        Command(RazerProtocol.GetAnalogAdc, new byte[] { 0, key }) is { Length: >= 4 } d ? (ushort)(d[2] << 8 | d[3]) : null;


    // ---- what the keyboard had before the hub's first change (given back when the hub quits) ----
    private readonly object _baseGate = new();
    private bool _restoring;
    private string? _baseProfile;
    private SnapTapState? _baseSnap;
    private readonly Dictionary<byte, KeyActuation> _baseAct = new();
    private readonly Dictionary<byte, KeyRapidTrigger> _baseRapid = new();
    private int? _basePolling, _baseBrightness;
    private (byte Effect, byte Flags, byte Rate, List<System.Windows.Media.Color> Colors)? _baseChroma;
    private readonly SynapseLog _synapse = new(746);

    public override bool HasChanges
    {
        get
        {
            lock (_baseGate)
                return _baseSnap != null || _baseAct.Count > 0 || _baseRapid.Count > 0 || _basePolling != null || _baseBrightness != null || _baseChroma != null;
        }
    }

    /// <summary>
    /// A Synapse profile switch rewrites the live slot with Synapse's own values: what was remembered before it is
    /// no longer what to give back, so it is dropped.
    /// </summary>
    private void FollowSynapse()
    {
        string? now = _synapse.Poll();
        lock (_baseGate)
        {
            if (_baseProfile != null && now != null && now != _baseProfile && HasChanges)
            {
                Log.Write($"razer: Synapse switched to {_synapse.ActiveName ?? now}; it rewrote the keyboard, nothing of ours to give back");
                ClearBaseline();
            }
        }
    }

    private void ClearBaseline()
    {
        _baseSnap = null;
        _baseAct.Clear();
        _baseRapid.Clear();
        _basePolling = _baseBrightness = null;
        _baseChroma = null;
        _baseProfile = null;
    }

    private void Started()
    {
        if (!HasChanges) _baseProfile = _synapse.Poll();
    }

    private void RememberSnap()
    {
        if (_restoring) return;
        FollowSynapse();
        lock (_baseGate) { if (_baseSnap != null) return; Started(); }
        var s = ReadSnapTap();
        lock (_baseGate) _baseSnap ??= s;
    }

    private void RememberActuation(IEnumerable<byte> keys)
    {
        if (_restoring) return;
        FollowSynapse();
        List<byte> missing;
        lock (_baseGate) { Started(); missing = keys.Where(k => !_baseAct.ContainsKey(k)).Distinct().ToList(); }
        if (missing.Count == 0) return;
        var read = ReadActuation(missing);
        lock (_baseGate) foreach (var a in read) _baseAct.TryAdd(a.Key, a);
    }

    private void RememberRapid(IEnumerable<byte> keys)
    {
        if (_restoring) return;
        FollowSynapse();
        List<byte> missing;
        lock (_baseGate) { Started(); missing = keys.Where(k => !_baseRapid.ContainsKey(k)).Distinct().ToList(); }
        if (missing.Count == 0) return;
        var read = ReadRapid(missing);
        lock (_baseGate) foreach (var r in read) _baseRapid.TryAdd(r.Key, r);
    }

    private void RememberPolling()
    {
        if (_restoring) return;
        FollowSynapse();
        lock (_baseGate) { if (_basePolling != null) return; Started(); }
        int hz = ReadPolling();
        lock (_baseGate) if (hz > 0) _basePolling ??= hz;
    }

    private void RememberBrightness()
    {
        if (_restoring) return;
        FollowSynapse();
        lock (_baseGate) { if (_baseBrightness != null) return; Started(); }
        int b = ReadBrightness();
        lock (_baseGate) if (b >= 0) _baseBrightness ??= b;
    }

    private void RememberChroma()
    {
        if (_restoring) return;
        FollowSynapse();
        lock (_baseGate) { if (_baseChroma != null) return; Started(); }
        var c = ReadChroma();
        lock (_baseGate) _baseChroma ??= c;
    }

    public override string? RestoreOriginal()
    {
        FollowSynapse();
        SnapTapState? snap;
        List<KeyActuation> act;
        List<KeyRapidTrigger> rapid;
        int? polling, brightness;
        (byte Effect, byte Flags, byte Rate, List<System.Windows.Media.Color> Colors)? chroma;
        lock (_baseGate)
        {
            if (!HasChanges) return null;
            snap = _baseSnap;
            act = _baseAct.Values.ToList();
            rapid = _baseRapid.Values.ToList();
            polling = _basePolling;
            brightness = _baseBrightness;
            chroma = _baseChroma;
        }
        var done = new List<string>();
        _restoring = true;
        try
        {
            if (act.Count > 0 && WriteActuation(act)) done.Add(Lang.T("atuação", "actuation"));
            if (rapid.Count > 0 && WriteRapid(rapid)) done.Add("rapid trigger");
            if (snap != null && WriteSnapTap(snap.Enabled, snap.Used)) done.Add("Snap Tap");
            if (polling is int hz && WritePolling(hz)) done.Add(Lang.T("taxa", "polling"));
            if (brightness is int b && WriteBrightness(b)) done.Add(Lang.T("brilho", "brightness"));
            if (chroma is { } c && WriteChroma(c.Effect, c.Flags, c.Rate, c.Colors)) done.Add(Lang.T("luz", "lighting"));
        }
        finally { _restoring = false; }
        lock (_baseGate) ClearBaseline();
        Log.Write("razer: given back " + string.Join(", ", done));
        return done.Count > 0 ? $"{Name}: " + string.Join(", ", done) : null;
    }

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
        FollowSynapse();
        var st = ReadSnapTap();
        if (st != null) SnapOn = st.Enabled;
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
            BuildLight(p);
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
        double Gap(byte k) => s.Act.TryGetValue(k, out var a) ? Math.Max(0.05, a.PressMm - a.ReleaseMm) : 0.1;
        var first0 = s.Rapid.TryGetValue(18, out var r00) ? r00 : default;
        var sw = new SwitchView { KeyName = RazerProtocol.KeyName(18), PressMm = startAct, ReleaseMm = startAct - Gap(18), RapidOn = first0.Enabled, RapidMm = first0.DownMm > 0 ? first0.DownMm : 0.1, Margin = new Thickness(0, 6, 0, 10) };
        sec.Children.Add(sw);
        BuildLive(p, sec, sw, chips, s);
        FrameworkElement? actSlider = null;
        actSlider = p.Slider(0.1, 4.0, 0.05, startAct, v => { sw.PressMm = v; sw.ReleaseMm = Math.Max(0.05, v - Gap(First() == 0 ? (byte)18 : First())); return Page.Mm(v); }, mm =>
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

        var rtToggle = p.Toggle(rtOn, v => { rtOn = v; sw.RapidOn = v; WriteRt(v ? Lang.T("Rapid trigger ligado", "Rapid trigger on") : Lang.T("Rapid trigger desligado", "Rapid trigger off")); });
        p.Row(sec, "Rapid trigger", Lang.T("A tecla solta e volta a valer no meio do caminho.", "The key resets and fires again mid travel."), rtToggle);
        var rtSlider = p.Slider(0.1, 2.0, 0.05, rtMm, v => { sw.RapidMm = v; return Page.Mm(v); }, mm => { rtMm = mm; WriteRt(Lang.T("Sensibilidade ", "Sensitivity ") + Page.Mm(mm)); });
        p.Row(sec, Lang.T("Sensibilidade", "Sensitivity"), Lang.T("Quanto precisa mexer para soltar ou voltar a valer.", "How much movement resets or fires again."), rtSlider);

        chips.OnChange = () =>
        {
            byte k = First();
            if (k != 0) sw.KeyName = RazerProtocol.KeyName(k);
            if (k != 0 && s.Act.TryGetValue(k, out var a)) Page.SetQuiet(actSlider, a.PressMm);
            if (k != 0 && s.Rapid.TryGetValue(k, out var rr)) { sw.RapidOn = rr.Enabled; sw.RapidMm = rr.DownMm > 0 ? rr.DownMm : 0.1; }
            if (k != 0 && s.Rapid.TryGetValue(k, out var r))
            {
                rtOn = r.Enabled;
                rtMm = r.DownMm > 0 ? r.DownMm : rtMm;
                Page.SetQuiet(rtToggle, r.Enabled);
                Page.SetQuiet(rtSlider, rtMm);
            }
        };
    }

    /// <summary>
    /// "Live": reads the depth of the picked key and of W, A, S and D about 25 times a second while the page is open
    /// and the switch is on (it shares the interface with Synapse, so never in the background).
    /// </summary>
    private void BuildLive(Page p, Panel sec, SwitchView sw, KeyPicker chips, State s)
    {
        byte[] bars = { 18, 31, 32, 33 };
        var barPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 8), Visibility = Visibility.Collapsed };
        var fills = new Dictionary<byte, (Border Fill, TextBlock Text)>();
        foreach (var k in bars)
        {
            var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(new TextBlock { Text = RazerProtocol.KeyName(k), Foreground = Page.B("Ink2"), VerticalAlignment = VerticalAlignment.Center });
            var track = new Border { Background = Page.B("Line"), CornerRadius = new CornerRadius(4), Height = 8, VerticalAlignment = VerticalAlignment.Center };
            var fill = new Border { Background = Page.B("Ink"), CornerRadius = new CornerRadius(4), Height = 8, Width = 0, HorizontalAlignment = HorizontalAlignment.Left };
            var holder = new Grid();
            holder.Children.Add(track);
            holder.Children.Add(fill);
            if (s.Act.TryGetValue(k, out var a))
                holder.Children.Add(new Border { Width = 2, Height = 14, Background = Page.B("Good"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(320 * a.PressMm / SwitchView.Travel, 0, 0, 0) });
            Grid.SetColumn(holder, 1);
            g.Children.Add(holder);
            var text = new TextBlock { Foreground = Page.B("Ink2"), Margin = new Thickness(12, 0, 0, 0), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(text, 2);
            g.Children.Add(text);
            barPanel.Children.Add(g);
            fills[k] = (fill, text);
        }
        bool live = false;
        p.Row(sec, Lang.T("Ao vivo", "Live"), Lang.T("Ligue e aperte: o desenho e as barras mostram quanto cada tecla está descendo.",
            "Turn this on and press: the drawing and the bars show how far each key is going down."), p.Toggle(false, on =>
        {
            live = on;
            barPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (!on) { sw.LiveMm = null; return; }
            var ui = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            new Thread(() =>
            {
                while (live && !p.Closed)
                {
                    byte key = chips.Picked().FirstOrDefault();
                    if (key == 0) key = 18;
                    var keys = bars.Contains(key) ? bars : bars.Append(key).ToArray();
                    var mm = new Dictionary<byte, (double Mm, ushort Raw)>();
                    foreach (var k in keys)
                        // measured on 06/11/2026: released = 0, bottomed out = 65535 (the threshold scale, 0..4 mm)
                        if (ReadDepth(k) is ushort raw) mm[k] = (raw * SwitchView.Travel / 65535.0, raw);
                    ui.BeginInvoke(() =>
                    {
                        if (!live) return;
                        sw.LiveMm = mm.TryGetValue(key, out var v) ? v.Mm : null;
                        foreach (var (k, (fill, text)) in fills)
                            if (mm.TryGetValue(k, out var d))
                            {
                                fill.Width = 320 * Math.Clamp(d.Mm / SwitchView.Travel, 0, 1);
                                fill.Background = s.Act.TryGetValue(k, out var a) && d.Mm >= a.PressMm ? Page.B("Good") : Page.B("Ink");
                                text.Text = $"{Page.Mm(d.Mm)}  ({d.Raw})";
                            }
                    });
                    Thread.Sleep(40);
                }
            }) { IsBackground = true, Name = "razer-live" }.Start();
        }));
        sec.Children.Add(barPanel);
    }

    private void BuildLight(Page p)
    {
        var sec = p.Section(Lang.T("Luz do teclado", "Keyboard lighting"), Lang.T("Efeitos que o próprio teclado roda (não gastam nada do PC).", "Effects the keyboard runs by itself (no load on the PC)."));
        var loading = p.Note(sec, Lang.T("Lendo o efeito atual...", "Reading the current effect..."));
        p.Work(ReadChroma, cur =>
        {
            sec.Children.Remove(loading);
            byte effect = cur?.Effect ?? 3;
            var a = cur is { Colors.Count: > 0 } ? cur.Value.Colors[0] : System.Windows.Media.Color.FromRgb(0, 255, 120);
            var b = cur is { Colors.Count: > 1 } ? cur.Value.Colors[1] : System.Windows.Media.Color.FromRgb(0, 122, 255);
            double speed = 1;
            FrameworkElement? rowA = null, rowB = null, rowSpeed = null;
            void Rows()
            {
                var info = RazerProtocol.ChromaEffects.FirstOrDefault(e => e.Id == effect);
                if (rowA != null) rowA.Visibility = info.Colors >= 1 ? Visibility.Visible : Visibility.Collapsed;
                if (rowB != null) rowB.Visibility = info.Colors >= 2 ? Visibility.Visible : Visibility.Collapsed;
                if (rowSpeed != null) rowSpeed.Visibility = info.Speed ? Visibility.Visible : Visibility.Collapsed;
            }
            void Send()
            {
                Rows();
                var info = RazerProtocol.ChromaEffects.First(e => e.Id == effect);
                var colors = new List<System.Windows.Media.Color>();
                if (info.Colors >= 1) colors.Add(a);
                if (info.Colors >= 2) colors.Add(b);
                byte flags = effect switch { 2 => 2, 4 => 1, _ => 0 };
                byte rate = RazerProtocol.ChromaRate(effect, speed);
                byte e = effect;
                p.Work(() => WriteChroma(e, flags, rate, colors), ok => p.Say(ok ? Lang.T(info.Pt, info.En) : Lang.T("O teclado recusou", "The keyboard refused"), ok));
            }
            var chipsRow = new WrapPanel { Margin = new Thickness(0, 6, 0, 6) };
            string group = "rz" + Guid.NewGuid().ToString("N");
            foreach (var fx in RazerProtocol.ChromaEffects)
            {
                var id = fx.Id;
                var r = new RadioButton { Content = Lang.T(fx.Pt, fx.En), Style = Page.S("Chip"), GroupName = group, IsChecked = id == effect, Padding = new Thickness(12, 6, 12, 6) };
                r.Checked += (_, _) => { effect = id; Send(); };
                chipsRow.Children.Add(r);
            }
            sec.Children.Add(chipsRow);
            rowA = p.Row(sec, Lang.T("Cor", "Colour"), null, p.ColorPick(a, c => { a = c; Send(); }));
            rowB = p.Row(sec, Lang.T("Segunda cor", "Second colour"), null, p.ColorPick(b, c => { b = c; Send(); }));
            rowSpeed = p.Row(sec, Lang.T("Velocidade", "Speed"), null, p.Slider(0.25, 3, 0.25, speed, v => $"{v:0.##}x", v => { speed = v; Send(); }));
            Rows();
        });
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
