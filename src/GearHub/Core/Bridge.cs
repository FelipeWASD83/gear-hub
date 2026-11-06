using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using GearHub.Devices;
using GearHub.Devices.Razer;
using GearHub.Profiles;

namespace GearHub;

/// <summary>Things that happened, raised by whoever did them (any thread).</summary>
internal static class Signals
{
    public static event Action<Profile, string>? ProfileApplied;
    public static event Action<bool>? SnapTap;
    public static event Action<Gear, int>? Dpi;
    public static event Action<string, IReadOnlyList<string>>? Effect;

    public static void RaiseProfile(Profile p, string reason) => ProfileApplied?.Invoke(p, reason);
    public static void RaiseSnapTap(bool on) => SnapTap?.Invoke(on);
    public static void RaiseDpi(Gear g, int dpi) => Dpi?.Invoke(g, dpi);
    public static void RaiseEffect(string name, IReadOnlyList<string> targets) => Effect?.Invoke(name, targets);
}

/// <summary>
/// Joins the app to the local channel: the hello, the events (profile, devices, connected/disconnected, battery
/// crossing 20/10/5%, Snap Tap, DPI, effect) and the commands (profile, snaptap, show).
/// </summary>
internal static class Bridge
{
    private static readonly HashSet<string> Known = new();
    private static readonly Dictionary<string, int> BatteryLevelSent = new();
    private static bool _listed;
    private static Timer? _devicesDebounce;
    public static Action<string?>? ShowWindow { get; set; }

    public static void Start()
    {
        EventChannel.Hello = Hello;
        EventChannel.Command += OnCommand;
        Signals.ProfileApplied += (p, reason) =>
        {
            var o = new JsonObject { ["type"] = "profile", ["name"] = p.Name, ["display"] = ProfileStore.Display(p) };
            if (reason.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) { o["reason"] = "app"; o["app"] = reason; }
            else o["reason"] = reason;
            EventChannel.Publish(o);
        };
        Signals.SnapTap += on => EventChannel.Publish(new JsonObject { ["type"] = "snaptap", ["on"] = on });
        Signals.Dpi += (g, v) => EventChannel.Publish(new JsonObject { ["type"] = "dpi", ["key"] = g.Key, ["name"] = g.Name, ["value"] = v });
        Signals.Effect += (name, targets) => EventChannel.Publish(new JsonObject { ["type"] = "effect", ["name"] = name, ["targets"] = new JsonArray(targets.Select(t => (JsonNode)t).ToArray()) });
        ProfileStore.Changed += () => EventChannel.Publish(new JsonObject { ["type"] = "profiles", ["profiles"] = ProfilesJson() });
        DeviceHub.Instance.ListChanged += OnList;
        DeviceHub.Instance.GearChanged += g => { CheckBattery(g); DevicesSoon(); };
        EventChannel.Start();
    }

    private static JsonArray ProfilesJson() => new(ProfileStore.All.Select(p => (JsonNode)new JsonObject
    {
        ["name"] = p.Name,
        ["display"] = ProfileStore.Display(p),
        ["apps"] = new JsonArray(p.Apps.Select(a => (JsonNode)a).ToArray()),
        ["saved"] = p.Devices.Count > 0,
    }).ToArray());

    private static JsonArray DevicesJson() => new(DeviceHub.Instance.Gears.Select(g => (JsonNode)new JsonObject
    {
        ["key"] = g.Key,
        ["name"] = g.Name,
        ["kind"] = g.KindName,
        ["status"] = g.Status,
        ["battery"] = g.Battery,
    }).ToArray());

    private static JsonObject Hello()
    {
        var active = ProfileStore.All.FirstOrDefault(p => p.Name == ProfileStore.Active) ?? ProfileStore.Default;
        var razer = DeviceHub.Instance.Gears.OfType<RazerKeyboard>().FirstOrDefault();
        return new JsonObject
        {
            ["type"] = "hello",
            ["version"] = App.Version,
            ["profile"] = active.Name,
            ["profileDisplay"] = ProfileStore.Display(active),
            ["profiles"] = ProfilesJson(),
            ["devices"] = DevicesJson(),
            ["snapTap"] = razer?.SnapOn,
        };
    }

    private static void OnList()
    {
        var now = DeviceHub.Instance.Gears.ToDictionary(g => g.Key);
        lock (Known)
        {
            if (_listed)
            {
                foreach (var g in now.Values.Where(g => !Known.Contains(g.Key)))
                    EventChannel.Publish(new JsonObject { ["type"] = "device", ["key"] = g.Key, ["name"] = g.Name, ["kind"] = g.KindName, ["connected"] = true });
                foreach (var key in Known.Where(k => !now.ContainsKey(k)).ToList())
                    EventChannel.Publish(new JsonObject { ["type"] = "device", ["key"] = key, ["name"] = Names.TryGetValue(key, out var n) ? n.Name : key, ["kind"] = Names.TryGetValue(key, out var k2) ? k2.Kind : "", ["connected"] = false });
            }
            _listed = true;
            Known.Clear();
            foreach (var g in now.Values)
            {
                Known.Add(g.Key);
                Names[g.Key] = (g.Name, g.KindName);
            }
        }
        DevicesSoon();
    }

    private static readonly Dictionary<string, (string Name, string Kind)> Names = new();

    /// <summary>One "devices" event for a burst of changes.</summary>
    private static void DevicesSoon()
    {
        _devicesDebounce ??= new Timer(_ => EventChannel.Publish(new JsonObject { ["type"] = "devices", ["devices"] = DevicesJson() }));
        _devicesDebounce.Change(600, Timeout.Infinite);
    }

    private static readonly int[] Levels = { 20, 10, 5 };

    private static void CheckBattery(Gear g)
    {
        if (g.Battery is not int pct) return;
        lock (BatteryLevelSent)
        {
            if (g.Charging || pct > 25) { BatteryLevelSent.Remove(g.Key); return; }
            int crossed = Levels.Where(l => pct <= l).DefaultIfEmpty(0).Min();
            if (crossed == 0) return;
            if (BatteryLevelSent.TryGetValue(g.Key, out int sent) && sent <= crossed) return;
            BatteryLevelSent[g.Key] = crossed;
        }
        EventChannel.Publish(new JsonObject { ["type"] = "battery", ["key"] = g.Key, ["name"] = g.Name, ["kind"] = g.KindName, ["percent"] = pct, ["charging"] = g.Charging });
    }

    private static void OnCommand(JsonObject cmd)
    {
        switch (cmd["cmd"]?.ToString())
        {
            case "profile":
                string name = cmd["name"]?.ToString() ?? "";
                var p = ProfileStore.All.FirstOrDefault(x => x.Name == name);
                if (p != null && p.Devices.Count > 0) _ = ProfileStore.Apply(p, "ilha");
                break;
            case "snaptap":
                bool on = cmd["on"]?.GetValue<bool>() ?? false;
                Task.Run(() => DeviceHub.Instance.Gears.OfType<RazerKeyboard>().FirstOrDefault()?.SetSnapTapOn(on));
                break;
            case "show":
                ShowWindow?.Invoke(cmd["page"]?.ToString());
                break;
        }
    }
}
