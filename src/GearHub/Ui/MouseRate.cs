using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace GearHub.Ui;

/// <summary>
/// Measures how many reports a second the mouse really sends: raw input (WM_INPUT) on this window for 3 s while the
/// mouse moves, then 1 / the median gap between reports (pauses longer than 20 ms are left out). Only while the
/// window is in front; nothing stays registered afterwards.
/// </summary>
internal static class MouseRate
{
    public static void Measure(Window window, Action<int?> done)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var source = HwndSource.FromHwnd(handle);
        if (source == null) { done(null); return; }
        var stamps = new List<long>(8000);
        var clock = Stopwatch.StartNew();
        HwndSourceHook hook = (IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
        {
            if (msg == 0x00FF) stamps.Add(clock.ElapsedTicks);
            return IntPtr.Zero;
        };
        source.AddHook(hook);
        Register(handle, 0);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Register(IntPtr.Zero, 0x00000001 /* RIDEV_REMOVE */);
            source.RemoveHook(hook);
            var gaps = new List<double>();
            for (int i = 1; i < stamps.Count; i++)
            {
                double ms = (stamps[i] - stamps[i - 1]) * 1000.0 / Stopwatch.Frequency;
                if (ms > 0 && ms < 20) gaps.Add(ms);
            }
            if (gaps.Count < 50) { done(null); return; }
            gaps.Sort();
            double median = gaps[gaps.Count / 2];
            done((int)Math.Round(1000 / median));
        };
        timer.Start();
    }

    private static void Register(IntPtr hwnd, uint flags)
    {
        var dev = new RAWINPUTDEVICE { UsagePage = 0x01, Usage = 0x02, Flags = flags, Target = hwnd };
        RegisterRawInputDevices(new[] { dev }, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE { public ushort UsagePage, Usage; public uint Flags; public IntPtr Target; }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);
}
