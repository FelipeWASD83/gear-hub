using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using GearHub.Devices;

namespace GearHub.Profiles;

internal sealed class Profile
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    /// <summary>Executable names (cs2.exe) that switch to this profile when in front.</summary>
    [JsonPropertyName("apps")] public List<string> Apps { get; set; } = new();
    /// <summary>Per device key, what Gear.Capture returned.</summary>
    [JsonPropertyName("devices")] public Dictionary<string, JsonObject> Devices { get; set; } = new();
    [JsonPropertyName("savedAt")] public DateTime SavedAt { get; set; }

    [JsonIgnore] public bool IsDefault => Name == ProfileStore.DefaultName;
}

/// <summary>
/// The profiles (profiles.json) and the switching: when the app in front matches a profile's list its settings are
/// applied; leaving it applies the default profile (when it has anything saved). Applying runs on one background
/// thread, one profile at a time, and writes only what differs from the device's current state.
/// </summary>
internal static class ProfileStore
{
    public const string DefaultName = "default";
    private static readonly object Gate = new();
    private static readonly SemaphoreSlim Applying = new(1, 1);
    public static List<Profile> All { get; private set; } = new();
    public static string Active { get; private set; } = DefaultName;
    public static event Action? Changed;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string Display(Profile p) => p.IsDefault ? Lang.T("Padrão", "Default") : p.Name;

    public static void Load()
    {
        lock (Gate)
        {
            try
            {
                if (File.Exists(Paths.Profiles))
                    All = JsonSerializer.Deserialize<List<Profile>>(File.ReadAllText(Paths.Profiles)) ?? new();
            }
            catch (Exception e) { Log.Error("profiles", e); }
            if (!All.Any(p => p.IsDefault)) All.Insert(0, new Profile { Name = DefaultName });
        }
    }

    public static void Save()
    {
        lock (Gate)
        {
            try
            {
                string tmp = Paths.Profiles + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(All, Options));
                File.Move(tmp, Paths.Profiles, true);
            }
            catch (Exception e) { Log.Error("profiles save", e); }
        }
        Changed?.Invoke();
    }

    public static Profile? ForApp(string exe)
    {
        lock (Gate) return All.FirstOrDefault(p => !p.IsDefault && p.Apps.Any(a => string.Equals(a.Trim(), exe, StringComparison.OrdinalIgnoreCase)));
    }

    public static Profile Default
    {
        get { lock (Gate) return All.First(p => p.IsDefault); }
    }

    /// <summary>Reads every present device into the profile (blocks; off the interface thread).</summary>
    public static int Capture(Profile p)
    {
        int n = 0;
        foreach (var g in DeviceHub.Instance.Gears)
        {
            try
            {
                if (g.Capture() is { } s)
                {
                    p.Devices[g.Key] = s;
                    n++;
                }
            }
            catch (Exception e) { Log.Write($"capture {g.Name}: {e.Message}"); }
        }
        p.SavedAt = DateTime.Now;
        Save();
        return n;
    }

    public static Task Apply(Profile p, string reason)
    {
        return Task.Run(async () =>
        {
            await Applying.WaitAsync();
            try
            {
                Log.Write($"profile {p.Name} ({reason})");
                foreach (var g in DeviceHub.Instance.Gears)
                {
                    if (!p.Devices.TryGetValue(g.Key, out var s)) continue;
                    try { g.Apply((JsonObject)s.DeepClone()); }
                    catch (Exception e) { Log.Write($"apply {g.Name}: {e.Message}"); }
                }
                Active = p.Name;
                Changed?.Invoke();
            }
            finally { Applying.Release(); }
        });
    }

    /// <summary>Called on every foreground change.</summary>
    public static void OnForeground(string exe)
    {
        if (!AppSettings.Current.AutoProfiles) return;
        var p = ForApp(exe);
        if (p != null)
        {
            if (p.Name != Active) _ = Apply(p, exe);
            return;
        }
        // back on something without a profile: the default, if it holds settings
        if (Active != DefaultName && Default.Devices.Count > 0) _ = Apply(Default, exe);
        else if (Active != DefaultName) { Active = DefaultName; Changed?.Invoke(); }
    }
}
