using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace GearHub;

/// <summary>
/// The local channel other apps (the ilha) use: \\.\pipe\GearHub.Events, current user only, one JSON object per line
/// both ways (docs/canal-local.md). Each connection gets a "hello" with the whole state, then every event; commands
/// coming back are handed to the app through Command.
/// </summary>
internal static class EventChannel
{
    public const string PipeName = "GearHub.Events";
    private static readonly List<StreamWriter> Clients = new();
    private static readonly object Gate = new();
    private static CancellationTokenSource? _stop;

    /// <summary>Builds the hello (whole state).</summary>
    public static Func<JsonObject>? Hello { get; set; }

    /// <summary>A command from a client (cmd and its fields); called on a background thread.</summary>
    public static event Action<JsonObject>? Command;

    public static int ClientCount
    {
        get { lock (Gate) return Clients.Count; }
    }

    public static void Start()
    {
        _stop = new CancellationTokenSource();
        var token = _stop.Token;
        Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                NamedPipeServerStream? server = null;
                try
                {
                    server = Create();
                    await server.WaitForConnectionAsync(token);
                    var s = server;
                    server = null;
                    _ = Task.Run(() => Serve(s, token));
                }
                catch (OperationCanceledException) { break; }
                catch (Exception e)
                {
                    Log.Error("channel", e);
                    server?.Dispose();
                    await Task.Delay(2000, token).ContinueWith(_ => { });
                }
            }
        });
    }

    private static NamedPipeServerStream Create()
    {
        var sec = new PipeSecurity();
        var me = WindowsIdentity.GetCurrent().User!;
        sec.AddAccessRule(new PipeAccessRule(me, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, sec);
    }

    private static async Task Serve(NamedPipeServerStream s, CancellationToken token)
    {
        var writer = new StreamWriter(s, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        var reader = new StreamReader(s, Encoding.UTF8);
        try
        {
            lock (Gate) Clients.Add(writer);
            Send(writer, HelloNow());
            while (!token.IsCancellationRequested && s.IsConnected)
            {
                string? line = await reader.ReadLineAsync(token);
                if (line == null) break;
                try
                {
                    if (JsonNode.Parse(line) is JsonObject cmd && cmd["cmd"] is JsonValue)
                    {
                        if (cmd["cmd"]!.ToString() == "state") Send(writer, HelloNow());
                        else Command?.Invoke(cmd);
                    }
                }
                catch (Exception e) { Log.Write("channel: bad line: " + e.Message); }
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { }
        finally
        {
            lock (Gate) Clients.Remove(writer);
            try { s.Dispose(); } catch (Exception) { }
        }
    }

    private static JsonObject HelloNow()
    {
        try { return Hello?.Invoke() ?? new JsonObject { ["type"] = "hello" }; }
        catch (Exception e)
        {
            Log.Error("channel hello", e);
            return new JsonObject { ["type"] = "hello" };
        }
    }

    private static void Send(StreamWriter w, JsonObject o)
    {
        lock (w) w.WriteLine(o.ToJsonString());
    }

    /// <summary>Sends an event to every connected client (from any thread; a dead client is dropped).</summary>
    public static void Publish(JsonObject evt)
    {
        List<StreamWriter> list;
        lock (Gate) list = Clients.ToList();
        if (list.Count == 0) return;
        string text = evt.ToJsonString();
        foreach (var w in list)
        {
            try { lock (w) w.WriteLine(text); }
            catch (Exception)
            {
                lock (Gate) Clients.Remove(w);
            }
        }
    }

    public static void Stop() => _stop?.Cancel();
}
