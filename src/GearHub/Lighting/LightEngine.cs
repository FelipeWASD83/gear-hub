using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Media;
using GearHub.Devices;
using GearHub.Devices.Razer;

namespace GearHub.Lighting;

/// <summary>
/// Runs the software effects: one thread at up to 30 frames a second, each target at its own pace (the RAM 30, the
/// Logitech zones 15), a still effect painted once and refreshed every 10 s. What each target runs is kept in
/// settings ("lights") and taken again at start. The Razer keyboard has its effects in hardware; "sync all" maps the
/// software effect to the closest one it has.
/// </summary>
internal sealed class LightEngine
{
    public static LightEngine Instance { get; } = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, Running> _running = new();
    private Thread? _thread;
    private volatile bool _stop;
    private bool _audio;

    private sealed class Running
    {
        public required ILightTarget Target;
        public required EffectSpec Spec;
        public required Effects.Renderer Renderer;
        public long NextFrame;
        public long LastPaint;
        public Color[]? Last;
    }

    public event Action? Changed;

    public EffectSpec? SpecFor(string key)
    {
        lock (_gate) return _running.TryGetValue(key, out var r) ? r.Spec.Clone() : AppSettings.Current.Lights.TryGetValue(key, out var s) ? s.Clone() : null;
    }

    public static IEnumerable<ILightTarget> Targets() => DeviceHub.Instance.Gears.OfType<ILightTarget>().Where(t => t.LightsAvailable);

    /// <summary>Starts (or changes) a target's effect. Off lets the device go. Blocks briefly; returns an error text or null.</summary>
    public string? Set(ILightTarget target, EffectSpec spec, bool save = true, bool announce = true)
    {
        string? error = null;
        lock (_gate)
        {
            if (spec.Effect == Effects.Off)
            {
                if (_running.Remove(target.Key, out var old)) Safe(() => old.Target.End());
            }
            else
            {
                if (!_running.ContainsKey(target.Key))
                {
                    error = target.Begin();
                    if (error != null) return error;
                }
                var pos = target.LedPositions();
                _running[target.Key] = new Running { Target = target, Spec = spec.Clone(), Renderer = new Effects.Renderer(pos) };
                EnsureThread();
            }
            UpdateAudio();
            if (save)
            {
                if (spec.Effect == Effects.Off) AppSettings.Current.Lights.Remove(target.Key);
                else AppSettings.Current.Lights[target.Key] = spec.Clone();
                AppSettings.Save();
            }
        }
        if (announce && spec.Effect != Effects.Off) Signals.RaiseEffect(Effects.Name(spec.Effect), new[] { target.Name });
        Changed?.Invoke();
        return error;
    }

    /// <summary>The same effect everywhere: every software target, and the Razer keyboard's closest hardware effect.</summary>
    public List<string> SyncAll(EffectSpec spec)
    {
        var problems = new List<string>();
        var names = new List<string>();
        foreach (var t in Targets().ToList())
        {
            string? e = Set(t, spec, announce: false);
            if (e != null) problems.Add($"{t.Name}: {e}");
            else names.Add(t.Name);
        }
        foreach (var k in DeviceHub.Instance.Gears.OfType<RazerKeyboard>())
        {
            if (k.WriteChromaFor(spec)) names.Add(k.Name);
            else problems.Add($"{k.Name}: " + Lang.T("o teclado recusou", "the keyboard refused"));
        }
        if (names.Count > 0) Signals.RaiseEffect(Effects.Name(spec.Effect), names);
        return problems;
    }

    /// <summary>Takes back what settings say each present target should run (start and when a device arrives).</summary>
    public void Restore()
    {
        foreach (var t in Targets().ToList())
        {
            bool running;
            lock (_gate) running = _running.ContainsKey(t.Key);
            if (!running && AppSettings.Current.Lights.TryGetValue(t.Key, out var spec))
            {
                string? e = Set(t, spec, save: false, announce: false);
                if (e != null) Log.Write($"lights: {t.Name}: {e}");
            }
        }
    }

    /// <summary>A device went away: forget its runner (its settings stay).</summary>
    public void Drop(string key)
    {
        lock (_gate) _running.Remove(key);
        UpdateAudio();
    }

    private void UpdateAudio()
    {
        bool want = _running.Values.Any(r => r.Spec.Effect == Effects.Music);
        if (want && !_audio) AudioLevel.Shared.Acquire();
        if (!want && _audio) AudioLevel.Shared.Release();
        _audio = want;
    }

    private void EnsureThread()
    {
        if (_thread is { IsAlive: true }) return;
        _stop = false;
        _thread = new Thread(Loop) { IsBackground = true, Name = "lights", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    private void Loop()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        timeBeginPeriod(1);
        try
        {
            while (!_stop)
            {
                long now = clock.ElapsedMilliseconds;
                List<Running> due;
                lock (_gate)
                {
                    if (_running.Count == 0) { _thread = null; return; }
                    due = _running.Values.Where(r => now >= r.NextFrame).ToList();
                }
                foreach (var r in due)
                {
                    bool moves = Effects.Moves(r.Spec.Effect);
                    r.NextFrame = now + (moves ? 1000 / Math.Max(1, r.Target.MaxFps) : 500);
                    var frame = r.Renderer.Frame(r.Spec, now / 1000.0, _audio ? AudioLevel.Shared : null);
                    bool same = r.Last != null && r.Last.SequenceEqual(frame);
                    if (same && now - r.LastPaint < 10_000) continue;
                    try
                    {
                        r.Target.Paint(frame);
                        r.Last = frame;
                        r.LastPaint = now;
                    }
                    catch (Exception e)
                    {
                        Log.Write($"lights: {r.Target.Name}: {e.Message}");
                        r.NextFrame = now + 3000;
                    }
                }
                Thread.Sleep(8);
            }
        }
        finally { timeEndPeriod(1); }
    }

    public void Stop()
    {
        _stop = true;
        _thread?.Join(500);
        lock (_gate)
        {
            foreach (var r in _running.Values) Safe(() => r.Target.End());
            _running.Clear();
        }
        if (_audio) AudioLevel.Shared.Release();
        _audio = false;
    }

    private static void Safe(Action a)
    {
        try { a(); } catch (Exception e) { Log.Write("lights: " + e.Message); }
    }

    [System.Runtime.InteropServices.DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
    [System.Runtime.InteropServices.DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);
}
