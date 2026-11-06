using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;

namespace GearHub.Devices.Corsair;

/// <summary>
/// Client of the CORSAIR Device Control Service ("CORSAIR RGB DRAM Service"), the LocalSystem service that writes the
/// DRAM LEDs over SMBus with Corsair's own signed driver. Read statically from CorsairDeviceControlServiceAPI.dll and
/// the service on 06/11/2026 (nothing of ours touches the SMBus or any driver):
/// - frames are uint32 little-endian length + UTF-8 JSON ending in \0, both ways;
/// - rendezvous: write ":pid:&lt;pid&gt;" to \\.\pipe\CorsairUtilityEngine\{A78E0942-...}_in, read the base path of a
///   dedicated trio (_in, _out, _callback) from ..._out;
/// - requests are {"method":..., fields at the root}; CDCSConnect uses "params"; a non-zero "errorCode" is an error;
/// - the client with the highest priority (CDCSSetLightingPriority) holds the LEDs; iCUE registers 0 then 1;
/// - when the session ends the service turns the modules' own hardware lighting back on.
/// Never called here: CDCSUpdateFw, CDCSSetPerfProfile, CDCSClearPerfProfile (firmware and XMP/voltage profiles).
/// </summary>
internal sealed class CdcsClient : IDisposable
{
    public const string ServiceName = "CorsairDeviceControlService";
    private const string Rendezvous = @"CorsairUtilityEngine\{A78E0942-12BB-4E77-B359-3B14722A80EA}";
    private NamedPipeClientStream? _in, _out, _callback;
    private readonly object _gate = new();
    private Thread? _callbackThread;
    public bool Allowed { get; private set; } = true;
    public event Action<bool>? AllowedChanged;
    public bool Connected => _in?.IsConnected == true && _out?.IsConnected == true;

    public static bool Installed()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{ServiceName}");
            return k != null;
        }
        catch (Exception) { return false; }
    }

    public static bool ServicePipeOpen()
    {
        try
        {
            string want = @"\\.\pipe\" + Rendezvous + "_in";
            return Directory.GetFiles(@"\\.\pipe\").Any(p => string.Equals(p, want, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception) { return false; }
    }

    /// <summary>Starts the service if it is stopped (it is Manual; Authenticated Users may start it).</summary>
    public static bool EnsureService()
    {
        if (ServicePipeOpen()) return true;
        IntPtr scm = OpenSCManagerW(null, null, 0x0001);
        if (scm == IntPtr.Zero) return false;
        try
        {
            IntPtr svc = OpenServiceW(scm, ServiceName, 0x0010 | 0x0004);
            if (svc == IntPtr.Zero) return false;
            try { StartServiceW(svc, 0, IntPtr.Zero); }
            finally { CloseServiceHandle(svc); }
        }
        finally { CloseServiceHandle(scm); }
        for (int i = 0; i < 40 && !ServicePipeOpen(); i++) Thread.Sleep(250);
        return ServicePipeOpen();
    }

    public static CdcsClient? Connect()
    {
        if (!EnsureService()) { Log.Write("cdcs: service not running"); return null; }
        var c = new CdcsClient();
        try
        {
            string basePath;
            using (var rin = new NamedPipeClientStream(".", Rendezvous + "_in", PipeDirection.Out))
            using (var rout = new NamedPipeClientStream(".", Rendezvous + "_out", PipeDirection.In))
            {
                rin.Connect(2000);
                rout.Connect(2000);
                WriteFrame(rin, ":pid:" + Environment.ProcessId);
                basePath = ReadFrame(rout) ?? throw new IOException("no rendezvous answer");
            }
            string name = basePath.StartsWith(@"\\.\pipe\", StringComparison.OrdinalIgnoreCase) ? basePath[9..] : basePath;
            c._out = new NamedPipeClientStream(".", name + "_out", PipeDirection.In);
            c._out.Connect(2000);
            c._in = new NamedPipeClientStream(".", name + "_in", PipeDirection.Out);
            c._in.Connect(2000);
            c._callback = new NamedPipeClientStream(".", name + "_callback", PipeDirection.In);
            c._callback.Connect(2000);
            c._callbackThread = new Thread(c.DrainCallbacks) { IsBackground = true, Name = "cdcs-callback" };
            c._callbackThread.Start();
            var hello = c.Call(new JsonObject { ["method"] = "CDCSConnect", ["params"] = new JsonObject { ["clientProtocolVersion"] = 1 } });
            if (hello?["breakingChanges"]?.GetValue<bool>() == true) throw new IOException("protocol changed");
            Log.Write($"cdcs: connected, server protocol {hello?["serverProtocolVersion"]}");
            return c;
        }
        catch (Exception e)
        {
            Log.Error("cdcs connect", e);
            c.Dispose();
            return null;
        }
    }

    private static void WriteFrame(Stream s, string json)
    {
        var body = Encoding.UTF8.GetBytes(json + "\0");
        s.Write(BitConverter.GetBytes((uint)body.Length));
        s.Write(body);
        s.Flush();
    }

    private static string? ReadFrame(Stream s)
    {
        var len = new byte[4];
        if (!ReadExact(s, len)) return null;
        uint n = BitConverter.ToUInt32(len);
        if (n > 16 * 1024 * 1024) throw new IOException("frame too big");
        var body = new byte[n];
        if (!ReadExact(s, body)) return null;
        string t = Encoding.UTF8.GetString(body);
        int z = t.IndexOf('\0');
        return z >= 0 ? t[..z] : t;
    }

    private static bool ReadExact(Stream s, byte[] buf)
    {
        int got = 0;
        while (got < buf.Length)
        {
            int r = s.Read(buf, got, buf.Length - got);
            if (r <= 0) return false;
            got += r;
        }
        return true;
    }

    /// <summary>One request and its answer; null when the service said errorCode != 0 or the pipe broke.</summary>
    public JsonObject? Call(JsonObject request)
    {
        lock (_gate)
        {
            if (_in == null || _out == null) return null;
            WriteFrame(_in, request.ToJsonString());
            string? answer = ReadFrame(_out);
            if (answer == null) return null;
            var o = JsonNode.Parse(answer) as JsonObject;
            if (o?["errorCode"] is JsonValue err && err.GetValue<int>() != 0)
            {
                Log.Write($"cdcs {request["method"]}: errorCode {err}");
                return null;
            }
            if (o?["error"] != null)
            {
                Log.Write($"cdcs {request["method"]}: {o["error"]!.ToJsonString()}");
                return null;
            }
            return o;
        }
    }

    private void DrainCallbacks()
    {
        try
        {
            while (_callback != null)
            {
                string? m = ReadFrame(_callback);
                if (m == null) return;
                var o = JsonNode.Parse(m) as JsonObject;
                string method = o?["method"]?.ToString() ?? "";
                Log.Write("cdcs callback: " + method + " " + (o?["isAllowed"]?.ToString() ?? ""));
                if (method == "CDCSControlAllowedChanged" && o?["isAllowed"] is JsonValue v)
                {
                    Allowed = v.GetValue<bool>();
                    AllowedChanged?.Invoke(Allowed);
                }
            }
        }
        catch (Exception e) { Log.Write("cdcs callback ended: " + e.Message); }
    }

    public sealed record DeviceInfo(int Id, int LedCount);

    public List<DeviceInfo> Devices()
    {
        var r = Call(new JsonObject { ["method"] = "CDCSGetDeviceInfos" });
        return (r?["deviceInfos"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
            .Select(d => new DeviceInfo(d["id"]?.GetValue<int>() ?? 0, d["ledCount"]?.GetValue<int>() ?? 0)).ToList();
    }

    public string Name(int id) => Call(new JsonObject { ["method"] = "CDCSGetDeviceName", ["id"] = id })?["deviceModelName"]?.ToString() ?? "";

    /// <summary>LED ids with their position (x left to right), from CDCSGetGeometryInfo.</summary>
    public List<(int Id, double X, double Y)> Leds(int id)
    {
        var r = Call(new JsonObject { ["method"] = "CDCSGetGeometryInfo", ["id"] = id });
        return (r?["LEDs"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
            .Select(l => (l["id"]?.GetValue<int>() ?? 0, l["x"]?.GetValue<double>() ?? 0, l["y"]?.GetValue<double>() ?? 0)).ToList();
    }

    public List<JsonObject> Modules(int id) =>
        (Call(new JsonObject { ["method"] = "CDCSGetDramModuleData", ["id"] = id })?["moduleData"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Select(m => (JsonObject)m.DeepClone()).ToList();

    public bool SetPriority(int priority) => Call(new JsonObject { ["method"] = "CDCSSetLightingPriority", ["priority"] = priority }) != null;

    /// <summary>Colours as "aarrggbb" strings (the official client's format, alpha first).</summary>
    public bool SetColors(int id, IEnumerable<(int Led, byte R, byte G, byte B)> colors)
    {
        var list = new JsonArray(colors.Select(c => (JsonNode)new JsonObject { ["id"] = c.Led, ["color"] = $"ff{c.R:x2}{c.G:x2}{c.B:x2}" }).ToArray());
        return Call(new JsonObject { ["method"] = "CDCSSetLedColors", ["id"] = id, ["colors"] = list }) != null;
    }

    public void Dispose()
    {
        var cb = _callback;
        _callback = null;
        try { _in?.Dispose(); } catch (Exception) { }
        try { _out?.Dispose(); } catch (Exception) { }
        try { cb?.Dispose(); } catch (Exception) { }
        _in = _out = null;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenSCManagerW(string? machine, string? db, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenServiceW(IntPtr scm, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool StartServiceW(IntPtr svc, int argc, IntPtr argv);
    [DllImport("advapi32.dll")] private static extern bool CloseServiceHandle(IntPtr h);
}
