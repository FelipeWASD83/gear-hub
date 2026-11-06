using System;
using System.Text.Json.Nodes;
using GearHub.Ui;

namespace GearHub.Devices;

internal enum GearKind { Keyboard, Mouse, Headset, Memory, Controller, Camera, Light }

/// <summary>
/// One peripheral the hub shows. Reads and writes go to the hardware and block, so the hub calls them off the
/// interface thread; BuildPage runs on the interface thread and reaches the hardware through Page.Work.
/// </summary>
internal abstract class Gear : IDisposable
{
    /// <summary>Stable id (profiles store settings under it).</summary>
    public abstract string Key { get; }
    public abstract string Name { get; }
    public abstract GearKind Kind { get; }
    public virtual string Brand => "";

    /// <summary>Short line under the name in the sidebar (battery, mode...).</summary>
    public string Status { get; protected set; } = "";

    /// <summary>Battery 0..100 when the device reports one, and whether it is charging.</summary>
    public int? Battery { get; protected set; }
    public bool Charging { get; protected set; }

    /// <summary>The kind as the local channel spells it.</summary>
    public string KindName => Kind.ToString().ToLowerInvariant();

    /// <summary>Raised from any thread when Status or what the page shows changed.</summary>
    public event Action<Gear>? Changed;
    protected void RaiseChanged() => Changed?.Invoke(this);

    /// <summary>Settings a profile keeps for this device, read from the hardware now (null = nothing to keep).</summary>
    public virtual JsonObject? Capture() => null;

    /// <summary>Applies what Capture returned before. Missing keys are left alone.</summary>
    public virtual void Apply(JsonObject settings) { }

    /// <summary>Reads the live state (battery and the like) for the sidebar; called every few seconds while present.</summary>
    public virtual void Poll() { }

    public abstract void BuildPage(Page page);

    public virtual void Dispose() { }
}
