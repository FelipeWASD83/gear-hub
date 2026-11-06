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
    [JsonPropertyName("ramColor")] public string RamColor { get; set; } = "";

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
