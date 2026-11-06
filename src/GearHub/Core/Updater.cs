using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading;

namespace GearHub;

internal sealed record ReleaseInfo(string Version, string Url, string? Sha256, long Size, string Page, bool IsNewer);

/// <summary>
/// Updates from the GitHub releases of FelipeWASD83/gear-hub, in the clave/ilha model:
/// 1. Check: releases/latest, asset "GearHub-&lt;version&gt;.zip".
/// 2. Install: download, check the SHA-256 against the asset's digest, extract into a staging folder (refusing paths
///    outside it), then run the NEW GearHub.exe from there with --apply-update &lt;app folder&gt; &lt;pid&gt; and quit.
/// 3. The executor waits for the old app to exit, keeps the current files in "previous", copies the new ones, opens
///    the app and waits up to 30 s for it to say it opened (--after-update writes a mark). No mark: it puts the
///    previous files back and opens the old version. Settings and profiles live in ~/.gear-hub and are never touched.
/// </summary>
internal static class Updater
{
    public const string Repository = "FelipeWASD83/gear-hub";
    public const string ApplyArg = "--apply-update", AfterArg = "--after-update";
    private static string Dir => Path.Combine(Paths.Data, "update");
    private static string Staging => Path.Combine(Dir, "staging");
    private static string Previous => Path.Combine(Dir, "previous");
    private static string OpenedMark => Path.Combine(Dir, "opened.txt");
    private static string LogFile => Path.Combine(Dir, "update.log");

    public static ReleaseInfo? Last { get; private set; }
    public static string LastText { get; private set; } = Lang.T("Ainda não procurou.", "Not checked yet.");

    private static HttpClient Http()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("GearHub/" + App.Version);
        h.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return h;
    }

    public static int Compare(string a, string b)
    {
        int[] P(string v) => v.TrimStart('v', 'V').Split('.', '-', '+').Take(3).Select(x => int.TryParse(x, out int n) ? n : 0).Concat(new[] { 0, 0, 0 }).Take(3).ToArray();
        var x = P(a);
        var y = P(b);
        for (int i = 0; i < 3; i++)
            if (x[i] != y[i]) return x[i].CompareTo(y[i]);
        return 0;
    }

    public static ReleaseInfo? Check(bool force)
    {
        try
        {
            using var h = Http();
            var r = h.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest").GetAwaiter().GetResult();
            if ((int)r.StatusCode == 404)
            {
                LastText = Lang.T("Ainda não há versão publicada.", "No version published yet.");
                return Last = null;
            }
            r.EnsureSuccessStatusCode();
            var j = JsonNode.Parse(r.Content.ReadAsStringAsync().GetAwaiter().GetResult())!;
            string tag = j["tag_name"]?.ToString().TrimStart('v') ?? "";
            var asset = (j["assets"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(a => a["name"]?.ToString() == $"GearHub-{tag}.zip");
            if (asset == null)
            {
                LastText = Lang.T($"A versão {tag} não tem o pacote GearHub-{tag}.zip.", $"Version {tag} has no GearHub-{tag}.zip package.");
                return Last = null;
            }
            string? digest = asset["digest"]?.ToString();
            string? sha = digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..].ToLowerInvariant() : null;
            bool newer = Compare(tag, App.Version) > 0;
            Last = new ReleaseInfo(tag, asset["browser_download_url"]!.ToString(), sha, asset["size"]?.GetValue<long>() ?? 0, j["html_url"]?.ToString() ?? "", newer);
            LastText = newer ? Lang.T($"Versão {tag} disponível (você tem a {App.Version}).", $"Version {tag} available (you have {App.Version}).")
                : Lang.T($"Você está na mais nova ({App.Version}).", $"You are on the newest ({App.Version}).");
            return Last;
        }
        catch (Exception e)
        {
            Log.Error("update check", e);
            LastText = Lang.T("Não deu para falar com o GitHub.", "Could not reach GitHub.");
            return null;
        }
    }

    /// <summary>Downloads, checks and starts the executor. Returns an error text, or null when the app should quit now.</summary>
    public static string? Install()
    {
        var rel = Last ?? Check(true);
        if (rel == null || !rel.IsNewer) return LastText;
        if (rel.Sha256 == null) return Lang.T("A release não traz o SHA-256; não instalo sem conferir.", "The release has no SHA-256; not installing unchecked.");
        try
        {
            Directory.CreateDirectory(Dir);
            string zip = Path.Combine(Dir, $"GearHub-{rel.Version}.zip");
            using (var h = Http())
            using (var s = h.GetStreamAsync(rel.Url).GetAwaiter().GetResult())
            using (var f = File.Create(zip))
                s.CopyTo(f);
            string sha;
            using (var f = File.OpenRead(zip)) sha = Convert.ToHexString(SHA256.HashData(f)).ToLowerInvariant();
            if (sha != rel.Sha256) return Lang.T("O SHA-256 do pacote não bateu. Nada foi trocado.", "The package SHA-256 did not match. Nothing was changed.");
            if (Directory.Exists(Staging)) Directory.Delete(Staging, true);
            Directory.CreateDirectory(Staging);
            string root = Path.GetFullPath(Staging) + Path.DirectorySeparatorChar;
            using (var a = ZipFile.OpenRead(zip))
                foreach (var e in a.Entries)
                {
                    string dest = Path.GetFullPath(Path.Combine(Staging, e.FullName.Replace('/', Path.DirectorySeparatorChar)));
                    if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return "zip: " + e.FullName;
                    if (e.FullName.EndsWith('/')) { Directory.CreateDirectory(dest); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    e.ExtractToFile(dest, true);
                }
            string exe = Path.Combine(Staging, "GearHub.exe");
            if (!File.Exists(exe)) return Lang.T("O pacote não tem o GearHub.exe.", "The package has no GearHub.exe.");
            File.Delete(OpenedMark);
            Process.Start(new ProcessStartInfo(exe, $"{ApplyArg} \"{AppContext.BaseDirectory.TrimEnd('\\')}\" {Environment.ProcessId}") { UseShellExecute = false });
            return null;
        }
        catch (Exception e)
        {
            Log.Error("update install", e);
            return Lang.T("A atualização falhou: ", "The update failed: ") + e.Message;
        }
    }

    private static void Note(string text)
    {
        try { File.AppendAllText(LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {text}\n"); } catch (Exception) { }
    }

    /// <summary>The executor: runs from the staging folder (the new version) and swaps the files.</summary>
    public static int RunExecutor(string target, int pid)
    {
        Note($"executor: target {target}, waiting for {pid}");
        try { Process.GetProcessById(pid).WaitForExit(30000); } catch (Exception) { }
        Thread.Sleep(500);
        try
        {
            if (Directory.Exists(Previous)) Directory.Delete(Previous, true);
            Copy(target, Previous);
            Copy(AppContext.BaseDirectory, target);
            Note("copied");
            Process.Start(new ProcessStartInfo(Path.Combine(target, "GearHub.exe"), AfterArg) { UseShellExecute = false, WorkingDirectory = target });
            for (int i = 0; i < 60; i++)
            {
                if (File.Exists(OpenedMark)) { Note("new version opened"); return 0; }
                Thread.Sleep(500);
            }
            Note("no sign from the new version: rolling back");
            Copy(Previous, target);
            Process.Start(new ProcessStartInfo(Path.Combine(target, "GearHub.exe")) { UseShellExecute = false, WorkingDirectory = target });
            return 2;
        }
        catch (Exception e)
        {
            Note("executor failed: " + e.Message);
            return 1;
        }
    }

    /// <summary>Called by the new version once it is up.</summary>
    public static void MarkOpened()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(OpenedMark, App.Version);
        }
        catch (Exception) { }
    }

    private static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(from, f);
            string dest = Path.Combine(to, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            for (int i = 0; ; i++)
            {
                try { File.Copy(f, dest, true); break; }
                catch (IOException) when (i < 20) { Thread.Sleep(250); }
            }
        }
    }
}
