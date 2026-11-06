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
    [JsonPropertyName("lastPage")] public string LastPage { get; set; } = "";
    /// <summary>1.0.0 kept the RAM choice here ("mode:#hex"); read once into Lights.</summary>
    [JsonPropertyName("ramColor")] public string RamColor { get; set; } = "";
    /// <summary>The software light effect each device runs, by device key.</summary>
    [JsonPropertyName("lights")] public System.Collections.Generic.Dictionary<string, Lighting.EffectSpec> Lights { get; set; } = new();
    /// <summary>Global hotkeys: "Ctrl+Alt+P" style, empty = off.</summary>
    [JsonPropertyName("hotkeyProfile")] public string HotkeyProfile { get; set; } = "";
    [JsonPropertyName("hotkeySnapTap")] public string HotkeySnapTap { get; set; } = "";
    [JsonPropertyName("hotkeyWindow")] public string HotkeyWindow { get; set; } = "";

    public static AppSettings Current { get; private set; } = new();

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
        try
        {
            string tmp = Paths.Settings + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Current, Options));
            File.Move(tmp, Paths.Settings, true);
        }
        catch (Exception e) { Log.Error("settings save", e); }
    }
}
