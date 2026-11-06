using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace GearHub;

/// <summary>Portuguese or English, following Windows unless the settings force one.</summary>
internal static class Lang
{
    public static bool Pt { get; set; } = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "pt";

    public static string T(string pt, string en) => Pt ? pt : en;

    public static void Apply(string? choice)
    {
        Pt = choice switch
        {
            "pt" => true,
            "en" => false,
            _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "pt",
        };
    }
}

/// <summary>
/// Where things live. Data goes to %USERPROFILE%\.gear-hub, not AppData: a process started from inside an MSIX app
/// (the Claude app) sees a virtual AppData, the same trap the ilha documents.
/// </summary>
internal static class Paths
{
    public static string Data
    {
        get
        {
            string? env = Environment.GetEnvironmentVariable("GEARHUB_HOME");
            string dir = !string.IsNullOrWhiteSpace(env) ? env : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gear-hub");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string Settings => Path.Combine(Data, "settings.json");
    public static string Profiles => Path.Combine(Data, "profiles.json");
    public static string LogFile => Path.Combine(Data, "gear-hub.log");

    /// <summary>The installed copy (the only one that writes the Run entry), the folder the Desktop Glow setup uses.</summary>
    public static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "GearHub");

    public static string Exe => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "GearHub.exe");

    public static bool IsInstalledCopy =>
        string.Equals(Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\'), Path.GetFullPath(InstallDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
}

/// <summary>A small rotating log (1 MB, then .1).</summary>
internal static class Log
{
    private static readonly object Gate = new();

    public static void Write(string text)
    {
        try
        {
            lock (Gate)
            {
                string f = Paths.LogFile;
                var info = new FileInfo(f);
                if (info.Exists && info.Length > 1_000_000)
                {
                    File.Copy(f, f + ".1", true);
                    File.Delete(f);
                }
                File.AppendAllText(f, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Thread.CurrentThread.ManagedThreadId}] {text}\n", Encoding.UTF8);
            }
        }
        catch (Exception) { }
    }

    public static void Error(string where, Exception e) => Write($"{where}: {e.GetType().Name}: {e.Message}");
}
