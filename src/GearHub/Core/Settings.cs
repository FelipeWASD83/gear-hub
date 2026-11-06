using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GearHub;

/// <summary>The app's own settings (settings.json). Device settings live in the profiles.</summary>
internal sealed class AppSettings
{
    [JsonPropertyName("startWithWindows")] public bool StartWithWindows { get; set; } = true;
    [JsonPropertyName("language")] public string Language { get; set; } = "auto";
    [JsonPropertyName("autoProfiles")] public bool AutoProfiles { get; set; } = true;
    [JsonPropertyName("checkUpdates")] public bool CheckUpdates { get; set; } = true;
    /// <summary>On quit, give the devices back what the vendor apps had set (his request on 06/11/2026).</summary>
    [JsonPropertyName("restoreOnExit")] public bool RestoreOnExit { get; set; } = true;
    [JsonPropertyName("lastPage")] public string LastPage { get; set; } = "";
    /// <summary>1.0.0 kept the RAM choice here ("mode:#hex"); read once into Lights.</summary>
    [JsonPropertyName("ramColor")] public string RamColor { get; set; } = "";
    /// <summary>The software light effect each device runs, by device key.</summary>
    [JsonPropertyName("lights")] public System.Collections.Generic.Dictionary<string, Lighting.EffectSpec> Lights { get; set; } = new();
    /// <summary>Global hotkeys: "Ctrl+Alt+P" style, empty = off.</summary>
    [JsonPropertyName("hotkeyProfile")] public string HotkeyProfile { get; set; } = "";
    [JsonPropertyName("hotkeySnapTap")] public string HotkeySnapTap { get; set; } = "";
    [JsonPropertyName("hotkeyWindow")] public string HotkeyWindow { get; set; } = "";
    /// <summary>
    /// Razer: after a Synapse profile switch that left the keyboard on the factory values, copy the profile's stored
    /// slot into the live one (his keyboard got stuck at 1.2 mm on 06/11/2026).
    /// </summary>
    [JsonPropertyName("razerGuard")] public bool RazerGuard { get; set; } = true;
    /// <summary>Which stored keyboard slot (2..5) holds each Synapse profile, learned by the guard; by profile guid.</summary>
    [JsonPropertyName("razerSlots")] public System.Collections.Generic.Dictionary<string, RazerSlotNote> RazerSlots { get; set; } = new();

    public static AppSettings Current { get; private set; } = new();

    public static readonly object Gate = new();

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static void Load()
    {
        try
        {
            if (File.Exists(Paths.Settings))
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Paths.Settings)) ?? new AppSettings();
        }
        catch (Exception e)
        {
            Log.Error("settings", e);
            Current = new AppSettings();
        }
        Lang.Apply(Current.Language);
        // 1.0.0 → 1.1.0: the RAM's "fixed:#hex" / "breathing:#hex" / "rainbow:#hex" becomes a light effect
        if (Current.RamColor != "" && !Current.Lights.ContainsKey("corsair-dram"))
        {
            var parts = Current.RamColor.Split(':');
            Current.Lights["corsair-dram"] = new Lighting.EffectSpec
            {
                Effect = parts[0] switch { "breathing" => Lighting.Effects.Breathing, "rainbow" => Lighting.Effects.Rainbow, _ => Lighting.Effects.Static },
                A = parts.Length > 1 ? parts[1] : "#FFFFFF",
            };
            Current.RamColor = "";
            Save();
        }
    }

    public static void Save()
    {
        lock (Gate)
        try
        {
            string tmp = Paths.Settings + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Current, Options));
            File.Move(tmp, Paths.Settings, true);
        }
        catch (Exception e) { Log.Error("settings save", e); }
    }
}

internal sealed class RazerSlotNote
{
    [JsonPropertyName("slot")] public int Slot { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
}
