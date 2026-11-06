using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Windows.Media;
using GearHub.Ui;

namespace GearHub.Devices.Corsair;

/// <summary>
/// The Corsair RGB memory, through the CDCS service. While the hub holds the LEDs it keeps one session open (fixed
/// colour once, breathing and rainbow drawn here at 25 frames a second); letting go closes the session and the
/// modules go back to their own hardware lighting. The choice is kept in settings and taken again at start.
/// </summary>
internal sealed class CorsairRam : Gear
{
    public const string Release = "module", Fixed = "fixed", Breathing = "breathing", Rainbow = "rainbow";
    private readonly object _gate = new();
    private CdcsClient? _client;
    private List<(int Dev, List<(int Id, double X, double Y)> Leds)> _targets = new();
    private Thread? _animator;
    private volatile bool _stop;
    private string _mode = Release;
    private Color _color = Colors.White;
    private string _name = "";

    public override string Key => "corsair-dram";
    public override string Name => _name != "" ? _name : Lang.T("Memória RAM Corsair", "Corsair RAM");
    public override string Brand => "Corsair";
    public override GearKind Kind => GearKind.Memory;
    public string Mode => _mode;

    public static CorsairRam? Find() => CdcsClient.Installed() ? new CorsairRam() : null;

    private CorsairRam()
    {
        Parse(AppSettings.Current.RamColor, out _mode, out _color);
        Status = Describe();
    }

    private static void Parse(string text, out string mode, out Color color)
    {
        mode = Release;
        color = Colors.White;
        if (string.IsNullOrEmpty(text)) return;
        var parts = text.Split(':');
        mode = parts[0] is Fixed or Breathing or Rainbow ? parts[0] : Release;
        if (parts.Length > 1 && Page.TryParse(parts[1], out var c)) color = c;
    }

    private string Describe() => _mode switch
    {
        Fixed => Lang.T("cor fixa", "fixed colour"),
        Breathing => Lang.T("respiração", "breathing"),
        Rainbow => Lang.T("arco-íris", "rainbow"),
        _ => Lang.T("efeito do pente", "module's own effect"),
    };

    /// <summary>Takes the choice saved last time (called once at start, off the interface thread).</summary>
    public void Restore()
    {
        if (_mode != Release) Set(_mode, _color, save: false);
    }

    /// <summary>Switches mode; Release lets go of the LEDs. Blocks (pipe I/O). Returns an error text or null.</summary>
    public string? Set(string mode, Color color, bool save = true)
    {
        lock (_gate)
        {
            StopAnimation();
            _mode = mode;
            _color = color;
            if (save)
            {
                AppSettings.Current.RamColor = mode == Release ? "" : $"{mode}:{Page.Hex(color)}";
                AppSettings.Save();
            }
            Status = Describe();
            RaiseChanged();
            if (mode == Release)
            {
                _client?.Dispose();
                _client = null;
                return null;
            }
            if (!EnsureClient(out string? error)) return error;
            if (mode == Fixed) return Paint(_ => color) ? null : Lang.T("O serviço recusou a cor", "The service refused the colour");
            _stop = false;
            _animator = new Thread(Animate) { IsBackground = true, Name = "ram-effect", Priority = ThreadPriority.BelowNormal };
            _animator.Start();
            return null;
        }
    }

    private bool EnsureClient(out string? error)
    {
        error = null;
        if (_client is { Connected: true }) return true;
        _client?.Dispose();
        _client = CdcsClient.Connect();
        if (_client == null)
        {
            error = Lang.T("Não deu para falar com o serviço da Corsair (CORSAIR Device Control Service).", "Could not reach the Corsair service (CORSAIR Device Control Service).");
            return false;
        }
        _client.SetPriority(2);
        _targets = _client.Devices().Where(d => d.LedCount > 0).Select(d => (d.Id, _client.Leds(d.Id))).ToList();
        foreach (var t in _targets)
            if (t.Leds.Count == 0) Log.Write($"cdcs: device {t.Dev} has no geometry");
        if (_name == "" && _targets.Count > 0)
        {
            string n = _client.Name(_targets[0].Dev);
            if (n != "") { _name = n; RaiseChanged(); }
        }
        Log.Write($"cdcs: {_targets.Count} devices, {_targets.Sum(t => t.Leds.Count)} LEDs");
        if (_targets.Count == 0) { error = Lang.T("O serviço não listou nenhuma memória.", "The service listed no memory."); return false; }
        return true;
    }

    /// <summary>Paints every LED with `f(position 0..1 from left to right)`.</summary>
    private bool Paint(Func<double, Color> f)
    {
        if (_client == null) return false;
        bool ok = true;
        foreach (var (dev, leds) in _targets)
        {
            if (leds.Count == 0) continue;
            double min = leds.Min(l => l.X), max = leds.Max(l => l.X);
            double span = Math.Max(1e-6, max - min);
            ok &= _client.SetColors(dev, leds.Select(l =>
            {
                var c = f((l.X - min) / span);
                return (l.Id, c.R, c.G, c.B);
            }));
        }
        return ok;
    }

    private void Animate()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!_stop)
        {
            double t = clock.Elapsed.TotalSeconds;
            try
            {
                if (_mode == Breathing)
                {
                    double k = 0.08 + 0.92 * (0.5 - 0.5 * Math.Cos(t * Math.PI * 2 / 4.0));
                    var c = Color.FromRgb((byte)(_color.R * k), (byte)(_color.G * k), (byte)(_color.B * k));
                    Paint(_ => c);
                }
                else Paint(x => Hue((x * 0.6 + t / 6.0) % 1.0));
            }
            catch (Exception e)
            {
                Log.Error("ram effect", e);
                return;
            }
            Thread.Sleep(40);
        }
    }

    private void StopAnimation()
    {
        _stop = true;
        _animator?.Join(500);
        _animator = null;
    }

    private static Color Hue(double h)
    {
        double r = Math.Clamp(Math.Abs(h * 6 - 3) - 1, 0, 1);
        double g = Math.Clamp(2 - Math.Abs(h * 6 - 2), 0, 1);
        double b = Math.Clamp(2 - Math.Abs(h * 6 - 4), 0, 1);
        return Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }

    public override JsonObject? Capture() => new() { ["mode"] = _mode, ["color"] = Page.Hex(_color) };

    public override void Apply(JsonObject s)
    {
        string mode = s["mode"]?.ToString() ?? Release;
        Color c = Page.TryParse(s["color"]?.ToString() ?? "", out var parsed) ? parsed : _color;
        if (mode != _mode || c != _color) Set(mode, c);
    }

    public override void BuildPage(Page p)
    {
        p.Header(Name, Lang.T("Luz dos pentes de memória, pelo serviço da própria Corsair (o CORSAIR RGB DRAM Service). Sem driver novo.",
            "Memory module lighting, through Corsair's own service (the CORSAIR RGB DRAM Service). No new driver."));
        if (VendorApps.Icue)
            p.Note(p.Root, Lang.T("O iCUE está aberto. Na primeira vez ele pergunta se libera a memória para outro programa: aceite lá. Para não perguntar mais, nas configurações da memória no iCUE ponha \"CORSAIR RGB DRAM Service Access\" em On. Também dá para fechar o iCUE: o serviço continua.",
                "iCUE is open. The first time it asks whether another app may drive the memory: accept it there. To stop the question, set \"CORSAIR RGB DRAM Service Access\" to On in iCUE's memory settings. You can also close iCUE: the service keeps running."), true);
        var sec = p.Section(Lang.T("Luz", "Lighting"), Lang.T("\"Do pente\" solta a luz: os pentes voltam ao efeito gravado neles.",
            "\"Module\" lets go: the modules go back to the effect stored in them."));
        string[] modes = { Release, Fixed, Breathing, Rainbow };
        string[] labels = { Lang.T("Do pente", "Module"), Lang.T("Fixa", "Fixed"), Lang.T("Respiração", "Breathing"), Lang.T("Arco-íris", "Rainbow") };
        Color color = _color;
        string mode = _mode;
        void Go(string m, Color c)
        {
            p.Say(Lang.T("Falando com o serviço...", "Talking to the service..."));
            p.Work(() => Set(m, c), err => p.Say(err ?? labels[Array.IndexOf(modes, m)], err == null));
        }
        p.Row(sec, Lang.T("Efeito", "Effect"), null, p.Segments(labels, Array.IndexOf(modes, mode), i => { mode = modes[i]; Go(mode, color); }));
        p.Row(sec, Lang.T("Cor", "Colour"), Lang.T("Vale para fixa e respiração.", "For fixed and breathing."), p.ColorPick(color, c =>
        {
            color = c;
            if (mode is Release or Rainbow) mode = Fixed;
            Go(mode, color);
        }));
    }

    public override void Dispose()
    {
        lock (_gate)
        {
            StopAnimation();
            _client?.Dispose();
            _client = null;
        }
    }
}
