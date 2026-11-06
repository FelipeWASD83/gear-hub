using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Interop;
using GearHub.Devices;
using GearHub.Devices.Razer;
using GearHub.Profiles;

namespace GearHub;

/// <summary>
/// Global hotkeys through RegisterHotKey (the plain Windows API: nothing is hooked or injected, games see nothing).
/// Three actions: next saved profile, Snap Tap on/off, open the window. Kept in settings as text like "Ctrl+Alt+P".
/// </summary>
internal static class Hotkeys
{
    public const string NextProfile = "profile", SnapTap = "snaptap", Window = "window";
    private static HwndSource? _source;
    private static readonly Dictionary<int, string> Registered = new();
    public static Action<string>? Notify { get; set; }

    public static void Start()
    {
        var p = new HwndSourceParameters("GearHubHotkeys") { Width = 0, Height = 0, WindowStyle = 0, ParentWindow = new IntPtr(-3) };
        _source = new HwndSource(p);
        _source.AddHook(Hook);
        Apply();
    }

    /// <summary>Registers what the settings say (again, after a change). Returns the ones Windows refused.</summary>
    public static List<string> Apply()
    {
        var failed = new List<string>();
        if (_source == null) return failed;
        foreach (var id in Registered.Keys.ToList()) UnregisterHotKey(_source.Handle, id);
        Registered.Clear();
        var s = AppSettings.Current;
        int next = 1;
        foreach (var (action, text) in new[] { (NextProfile, s.HotkeyProfile), (SnapTap, s.HotkeySnapTap), (Window, s.HotkeyWindow) })
        {
            if (!TryParse(text, out uint mods, out uint vk)) continue;
            int id = next++;
            if (RegisterHotKey(_source.Handle, id, mods | 0x4000 /* MOD_NOREPEAT */, vk)) Registered[id] = action;
            else { failed.Add(text); Log.Write($"hotkey {text}: refused (taken by another app?)"); }
        }
        return failed;
    }

    public static bool TryParse(string text, out uint mods, out uint vk)
    {
        mods = 0;
        vk = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (var part in text.Split('+').Select(x => x.Trim()))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl": mods |= 0x2; break;
                case "alt": mods |= 0x1; break;
                case "shift": mods |= 0x4; break;
                case "win": mods |= 0x8; break;
                default:
                    if (Enum.TryParse<Key>(part, true, out var key)) vk = (uint)KeyInterop.VirtualKeyFromKey(key);
                    break;
            }
        }
        return vk != 0 && mods != 0;
    }

    /// <summary>"Ctrl+Alt+P" from a key press (modifiers held), or null when only modifiers are down.</summary>
    public static string? Describe(Key key, ModifierKeys mods)
    {
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System) return null;
        if (mods == ModifierKeys.None) return null;
        var parts = new List<string>();
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(key.ToString());
        return string.Join("+", parts);
    }

    private static IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0312 && Registered.TryGetValue(wParam.ToInt32(), out var action))
        {
            handled = true;
            Run(action);
        }
        return IntPtr.Zero;
    }

    public static void Run(string action)
    {
        switch (action)
        {
            case NextProfile:
            {
                var saved = ProfileStore.All.Where(p => p.Devices.Count > 0).ToList();
                if (saved.Count == 0) { Notify?.Invoke(Lang.T("Nenhum perfil guardado", "No saved profile")); return; }
                int i = saved.FindIndex(p => p.Name == ProfileStore.Active);
                var next = saved[(i + 1) % saved.Count];
                _ = ProfileStore.Apply(next, "hotkey");
                break;
            }
            case SnapTap:
                Task.Run(() =>
                {
                    var k = DeviceHub.Instance.Gears.OfType<RazerKeyboard>().FirstOrDefault();
                    if (k == null) return;
                    bool on = !(k.SnapOn ?? k.ReadSnapTap()?.Enabled ?? false);
                    if (k.SetSnapTapOn(on)) Notify?.Invoke("Snap Tap " + (on ? Lang.T("ligado", "on") : Lang.T("desligado", "off")));
                });
                break;
            case Window:
                App.ShowWindow();
                break;
        }
    }

    public static void Stop()
    {
        if (_source == null) return;
        foreach (var id in Registered.Keys) UnregisterHotKey(_source.Handle, id);
        Registered.Clear();
        _source.Dispose();
        _source = null;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
