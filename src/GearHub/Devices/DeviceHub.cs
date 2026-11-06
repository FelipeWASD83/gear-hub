using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GearHub.Devices.Controllers;
using GearHub.Devices.Corsair;
using GearHub.Devices.Lighting;
using GearHub.Devices.Logitech;
using GearHub.Devices.Razer;
using GearHub.Hid;

namespace GearHub.Devices;

/// <summary>
/// Finds the peripherals and keeps the list current: every 4 s it compares the set of HID paths (cheap) and only
/// probes again what changed; battery and status are read every 20 s. Everything here runs on one background thread;
/// the interface listens to ListChanged and GearChanged.
/// </summary>
internal sealed class DeviceHub : IDisposable
{
    private readonly object _gate = new();
    private readonly List<Gear> _gears = new();
    private readonly List<HidppLink> _links = new();
    private readonly Dictionary<string, bool> _cameraHasControls = new();
    private readonly Timer _timer;
    private string _hidSignature = "";
    private bool _dsxWas;
    private int _ticks;
    private bool _scanning;
    public event Action? ListChanged;
    public event Action<Gear>? GearChanged;

    public static DeviceHub Instance { get; } = new();

    private DeviceHub()
    {
        _timer = new Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public IReadOnlyList<Gear> Gears
    {
        get { lock (_gate) return _gears.ToList(); }
    }

    public Gear? ByKey(string key)
    {
        lock (_gate) return _gears.FirstOrDefault(g => g.Key == key);
    }

    public void Start()
    {
        Task.Run(() =>
        {
            Scan(force: true);
            GearHub.Lighting.LightEngine.Instance.Restore();
            _timer.Change(4000, 4000);
        });
    }

    private void Tick()
    {
        if (_scanning) return;
        _scanning = true;
        try
        {
            bool every20s = ++_ticks % 5 == 0;
            // a wireless mouse asleep answers "unknown device" behind its receiver and waking it changes no HID path:
            // while a receiver has nobody, ask it again every 20 s
            Scan(force: false, retryReceivers: every20s);
            if (every20s) PollAll();
        }
        catch (Exception e) { Log.Error("hub tick", e); }
        finally { _scanning = false; }
    }

    /// <summary>
    /// Gives every device back what it had before the hub's first change (the vendor apps' settings), and lets go of
    /// the lights (the RAM goes back to its own effect). Blocks; returns what was given back, one line per device.
    /// </summary>
    public List<string> RestoreAll()
    {
        var done = new List<string>();
        foreach (var g in Gears)
        {
            try
            {
                if (g.RestoreOriginal() is string s) done.Add(s);
            }
            catch (Exception e) { Log.Write($"restore {g.Name}: {e.Message}"); }
        }
        Log.Write("restore: " + (done.Count == 0 ? "nothing to give back" : string.Join(" | ", done)));
        return done;
    }

    public void PollAll()
    {
        foreach (var g in Gears)
        {
            try { g.Poll(); }
            catch (Exception e) { Log.Write($"poll {g.Name}: {e.Message}"); }
        }
    }

    /// <summary>Rescans (force) or rescans only when the HID set or DSX changed.</summary>
    public void Scan(bool force, bool retryReceivers = false)
    {
        var hid = HidApi.Enumerate();
        string sig = string.Join("|", hid.Select(h => h.Path).OrderBy(p => p, StringComparer.Ordinal));
        bool dsx = VendorApps.Dsx;
        bool woke = false;
        if (retryReceivers && !force && sig == _hidSignature)
        {
            HashSet<ushort> usedProducts;
            lock (_gate) usedProducts = _gears.OfType<LogitechGear>().Select(l => l.Device.Product).ToHashSet();
            var empty = hid.Where(h => h.Vendor == 0x046D && h.UsagePage == 0xFF00 && LogitechGear.Receivers.Contains(h.Product) && !usedProducts.Contains(h.Product)).ToList();
            woke = empty.Count > 0 && LogitechGear.ReceiverHasDevice(empty);
            if (woke) Log.Write("scan: a device answered behind a receiver");
        }
        if (!force && !woke && sig == _hidSignature && dsx == _dsxWas) return;
        bool first = _hidSignature == "";
        _hidSignature = sig;
        _dsxWas = dsx;
        Log.Write($"scan: {hid.Count} hid collections, dsx {(dsx ? "on" : "off")}");

        var found = new List<Gear>();
        lock (_gate)
        {
            // Razer
            var razer = RazerKeyboard.Find(hid);
            var oldRazer = _gears.OfType<RazerKeyboard>().FirstOrDefault();
            if (razer != null) found.Add(oldRazer != null && oldRazer.Key == razer.Key ? oldRazer : razer);

            // Logitech: keep devices whose link is still alive and still listed; probe the rest
            var keepLogi = _gears.OfType<LogitechGear>().Where(l => l.Alive && hid.Any(h => string.Equals(h.Path, l.LinkPath, StringComparison.OrdinalIgnoreCase))).ToList();
            var usedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var l in keepLogi)
            {
                var link = l.Device.Link;
                foreach (var h in hid.Where(h => h.Vendor == 0x046D && SameInterface(h.Path, link.Path))) usedPaths.Add(h.Path);
            }
            found.AddRange(keepLogi);
            foreach (var dead in _links.Where(k => !keepLogi.Any(l => l.Device.Link == k)).ToList())
            {
                dead.Dispose();
                _links.Remove(dead);
            }
            foreach (var l in LogitechGear.Find(hid, usedPaths))
            {
                if (found.OfType<LogitechGear>().Any(f => f.Key == l.Key)) continue;
                found.Add(l);
                if (!_links.Contains(l.Device.Link)) _links.Add(l.Device.Link);
            }

            // Corsair memory (the service exists)
            found.Add(_gears.OfType<CorsairRam>().FirstOrDefault() ?? (Gear?)CorsairRam.Find() ?? new NullGear());

            // PlayStation: DSX when it runs, else the pads straight
            if (dsx) found.Add(_gears.OfType<DsxGear>().FirstOrDefault() ?? (Gear?)DsxGear.Find() ?? new NullGear());
            foreach (var pad in DualSense.Find(hid))
                found.Add(_gears.OfType<DualSense>().FirstOrDefault(o => o.Path == pad.Path) ?? pad);

            // cameras, Windows controllers, dynamic lighting
            // virtual cameras (OBS, Meta Quest) list no controls: leave them out (asked once per camera)
            foreach (var c in Devices.Webcam.Webcam.Find())
            {
                if (!_cameraHasControls.TryGetValue(c.Key, out bool has))
                {
                    try { has = c.Read().Count > 0; } catch (Exception) { has = false; }
                    _cameraHasControls[c.Key] = has;
                }
                if (has) found.Add(_gears.OfType<Devices.Webcam.Webcam>().FirstOrDefault(o => o.Key == c.Key) ?? c);
            }
            try { if (GameControllers.Find() is { } gc) found.Add(_gears.OfType<GameControllers>().FirstOrDefault() ?? gc); }
            catch (Exception e) { Log.Write("controllers: " + e.Message); }
            try
            {
                var lights = DynamicLights.FindAsync().GetAwaiter().GetResult();
                if (lights != null) found.Add(lights);
            }
            catch (Exception e) { Log.Write("lights: " + e.Message); }

            found.RemoveAll(g => g is NullGear);
            foreach (var gone in _gears.Where(g => !found.Contains(g)))
            {
                gone.Changed -= OnGearChanged;
                GearHub.Lighting.LightEngine.Instance.Drop(gone.Key);
                gone.Dispose();
            }
            foreach (var g in found.Where(g => !_gears.Contains(g))) g.Changed += OnGearChanged;
            _gears.Clear();
            _gears.AddRange(found.OrderBy(g => g.Kind).ThenBy(g => g.Name));
        }
        if (first || force) PollAll();
        ListChanged?.Invoke();
        // a device that came back takes its light effect again
        if (!first) GearHub.Lighting.LightEngine.Instance.Restore();
    }

    private static bool SameInterface(string a, string b)
    {
        string Cut(string p)
        {
            p = p.ToLowerInvariant();
            int i = p.IndexOf("&col", StringComparison.Ordinal);
            int j = p.IndexOf('#', p.IndexOf('#') + 1);
            return i > 0 && i < j ? p[..i] : p[..Math.Max(0, j)];
        }
        return Cut(a) == Cut(b);
    }

    private void OnGearChanged(Gear g) => GearChanged?.Invoke(g);

    public void Dispose()
    {
        _timer.Dispose();
        lock (_gate)
        {
            foreach (var g in _gears) g.Dispose();
            foreach (var l in _links) l.Dispose();
            _gears.Clear();
            _links.Clear();
        }
    }

    private sealed class NullGear : Gear
    {
        public override string Key => "";
        public override string Name => "";
        public override GearKind Kind => GearKind.Light;
        public override void BuildPage(Ui.Page page) { }
    }
}
