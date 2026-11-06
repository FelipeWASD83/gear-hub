using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Media;
using GearHub.Lighting;
using GearHub.Ui;

namespace GearHub.Devices.Corsair;

/// <summary>
/// The Corsair RGB memory, through the CDCS service, as a light target of the engine: Begin opens the session (the
/// service may need starting; with iCUE open, iCUE may ask), Paint sends every LED, End closes the session and the
/// modules go back to their own hardware lighting. Tested on 06/11/2026: "ffff0000" painted the modules red
/// (aarrggbb), 1 device with 20 LEDs.
/// </summary>
internal sealed class CorsairRam : Gear, ILightTarget
{
    private readonly object _gate = new();
    private CdcsClient? _client;
    private List<(int Dev, List<(int Id, double X, double Y)> Leds)> _targets = new();
    private string _name = "";

    public override string Key => "corsair-dram";
    public override string Name => _name != "" ? _name : Lang.T("Memória RAM Corsair", "Corsair RAM");
    public override string Brand => "Corsair";
    public override GearKind Kind => GearKind.Memory;
    public int MaxFps => 30;

    public static CorsairRam? Find() => CdcsClient.Installed() ? new CorsairRam() : null;

    private CorsairRam() => Status = Describe();

    private string Describe()
    {
        var spec = LightEngine.Instance.SpecFor(Key);
        return spec == null ? Lang.T("efeito do pente", "module's own effect") : Effects.Name(spec.Effect).ToLowerInvariant();
    }

    public string? Begin()
    {
        lock (_gate)
        {
            if (_client is { Connected: true }) return null;
            _client?.Dispose();
            _client = CdcsClient.Connect();
            if (_client == null)
                return Lang.T("Não deu para falar com o serviço da Corsair (CORSAIR Device Control Service).", "Could not reach the Corsair service (CORSAIR Device Control Service).");
            _client.SetPriority(2);
            _targets = _client.Devices().Where(d => d.LedCount > 0).Select(d => (d.Id, _client.Leds(d.Id))).ToList();
            if (_name == "" && _targets.Count > 0)
            {
                string n = _client.Name(_targets[0].Dev);
                if (n != "") _name = n;
            }
            Log.Write($"cdcs: {_targets.Count} devices, {_targets.Sum(t => t.Leds.Count)} LEDs");
            if (_targets.Count == 0) return Lang.T("O serviço não listou nenhuma memória.", "The service listed no memory.");
            return null;
        }
    }

    /// <summary>LEDs of every module, left to right (position from the service's geometry, modules side by side).</summary>
    public double[] LedPositions()
    {
        lock (_gate)
        {
            var all = Ordered();
            int n = all.Count;
            return Enumerable.Range(0, n).Select(i => n <= 1 ? 0.5 : i / (double)(n - 1)).ToArray();
        }
    }

    private List<(int Dev, int Led)> Ordered() =>
        _targets.SelectMany(t => t.Leds.OrderBy(l => l.X).ThenBy(l => l.Y).Select(l => (t.Dev, l.Id))).ToList();

    public void Paint(Color[] colors)
    {
        lock (_gate)
        {
            if (_client == null) return;
            var all = Ordered();
            foreach (var g in all.Select((l, i) => (l.Dev, l.Led, C: colors[Math.Min(i, colors.Length - 1)])).GroupBy(x => x.Dev))
                _client.SetColors(g.Key, g.Select(x => (x.Led, x.C.R, x.C.G, x.C.B)));
        }
    }

    public void End()
    {
        lock (_gate)
        {
            _client?.Dispose();
            _client = null;
        }
    }

    /// <summary>Test only: holds the LEDs and paints them with a raw colour string.</summary>
    public string? PaintRaw(string color)
    {
        string? e = Begin();
        if (e != null) return e;
        lock (_gate)
        {
            bool ok = true;
            foreach (var (dev, leds) in _targets) ok &= _client!.SetColorsRaw(dev, leds.Select(l => l.Id), color);
            return ok ? null : "recusado";
        }
    }

    public override void Poll()
    {
        string s = Describe();
        if (s != Status) { Status = s; RaiseChanged(); }
    }

    public override JsonObject? Capture()
    {
        var spec = LightEngine.Instance.SpecFor(Key);
        return new JsonObject { ["light"] = System.Text.Json.JsonSerializer.SerializeToNode(spec ?? new EffectSpec { Effect = Effects.Off }) };
    }

    public override void Apply(JsonObject s)
    {
        var spec = s["light"]?.Deserialize<EffectSpec>();
        if (spec == null) return;
        var now = LightEngine.Instance.SpecFor(Key);
        if (now == null || !now.SameAs(spec)) LightEngine.Instance.Set(this, spec);
        Status = Describe();
        RaiseChanged();
    }

    public override void BuildPage(Page p)
    {
        p.Header(Name, Lang.T("Luz dos pentes de memória, pelo serviço da própria Corsair (o CORSAIR RGB DRAM Service). Sem driver novo.",
            "Memory module lighting, through Corsair's own service (the CORSAIR RGB DRAM Service). No new driver."));
        if (VendorApps.Icue)
            p.Note(p.Root, Lang.T("O iCUE está aberto. Na primeira vez ele pergunta se libera a memória para outro programa: aceite lá. Para não perguntar mais, nas configurações da memória no iCUE ponha \"CORSAIR RGB DRAM Service Access\" em On. Também dá para fechar o iCUE: o serviço continua.",
                "iCUE is open. The first time it asks whether another app may drive the memory: accept it there. To stop the question, set \"CORSAIR RGB DRAM Service Access\" to On in iCUE's memory settings. You can also close iCUE: the service keeps running."), true);
        EffectsPanel.Build(p, new ILightTarget[] { this }, Lang.T("\"Do pente\" solta a luz: os pentes voltam ao efeito gravado neles.",
            "\"Module\" lets go: the modules go back to the effect stored in them."), () => { Status = Describe(); RaiseChanged(); });
    }

    public override void Dispose() => End();
}
