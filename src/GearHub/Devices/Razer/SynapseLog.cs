using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace GearHub.Devices.Razer;

/// <summary>
/// The profile Synapse has in use for a product, from its tray log (systray_systrayv*.log in
/// %LOCALAPPDATA%\Razer\RazerAppEngine\User Data\Logs: "activeProfile":"&lt;guid&gt;","productId":746,"profiles":[...]),
/// the reader the ilha uses. Synapse writes the active profile into the keyboard's live slot on every switch, so a
/// change here means whatever the hub wrote before has already been replaced by Synapse.
/// </summary>
internal sealed class SynapseLog
{
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Razer", "RazerAppEngine", "User Data", "Logs");

    private static readonly Regex Device = new(@"""activeProfile"":""(?<a>[0-9a-fA-F-]{36})"",""productId"":(?<p>\d+)", RegexOptions.Compiled);
    private static readonly Regex Name = new(@"\{""name"":""(?<n>(?:[^""\\]|\\.)*)"",""guid"":""(?<g>[0-9a-fA-F-]{36})""", RegexOptions.Compiled);

    private readonly int _product;
    private string? _file;
    private long _pos;

    public string? Active { get; private set; }
    public string? ActiveName { get; private set; }

    public SynapseLog(int product) => _product = product;

    /// <summary>Reads what the log added since last time (the first time, its last 2 MB). Returns the active profile id.</summary>
    public string? Poll()
    {
        try
        {
            var dir = new DirectoryInfo(Folder);
            if (!dir.Exists) return Active;
            var newest = dir.GetFiles("systray_systrayv*.log").Where(f => f.Length > 0).OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            if (newest == null) return Active;
            if (newest.FullName != _file)
            {
                _pos = _file == null ? Math.Max(0, newest.Length - 2_000_000) : 0;
                _file = newest.FullName;
            }
            if (newest.Length < _pos) _pos = 0;
            if (newest.Length == _pos) return Active;
            using var fs = new FileStream(newest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            fs.Seek(_pos, SeekOrigin.Begin);
            var bytes = new byte[newest.Length - _pos];
            int n = fs.Read(bytes, 0, bytes.Length);
            int end = Array.LastIndexOf(bytes, (byte)'\n', Math.Max(0, n - 1));
            if (end < 0) return Active;
            _pos += end + 1;
            foreach (var line in Encoding.UTF8.GetString(bytes, 0, end + 1).Split('\n'))
            {
                if (!line.Contains("activeProfile", StringComparison.Ordinal)) continue;
                foreach (Match m in Device.Matches(line))
                {
                    if (!int.TryParse(m.Groups["p"].Value, out int p) || p != _product) continue;
                    Active = m.Groups["a"].Value;
                    var named = Name.Matches(line).FirstOrDefault(x => x.Groups["g"].Value == Active);
                    if (named != null) ActiveName = named.Groups["n"].Value;
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return Active;
    }
}
