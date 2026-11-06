using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

namespace GearHub;

/// <summary>One running copy: a second start shows the window of the first, --quit asks the first to leave.</summary>
internal static class SingleInstance
{
    private const string MutexName = "GearHub.Instance";
    private const string ShowName = "GearHub.Show";
    private const string QuitName = "GearHub.Quit";
    private static Mutex? _mutex;

    public static bool Claim()
    {
        _mutex = new Mutex(true, MutexName, out bool created);
        return created;
    }

    public static void Release()
    {
        try { _mutex?.ReleaseMutex(); } catch (Exception) { }
    }

    public static void SignalShow() => Signal(ShowName);
    public static void SignalQuit() => Signal(QuitName);

    private static void Signal(string name)
    {
        try
        {
            using var e = EventWaitHandle.OpenExisting(name);
            e.Set();
        }
        catch (Exception) { }
    }

    /// <summary>Waits for the show and quit signals on a background thread.</summary>
    public static void Listen(Action show, Action quit)
    {
        var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowName);
        var quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, QuitName);
        var t = new Thread(() =>
        {
            var handles = new WaitHandle[] { showEvent, quitEvent };
            while (true)
            {
                int i = WaitHandle.WaitAny(handles);
                if (i == 0) show();
                else { quit(); return; }
            }
        }) { IsBackground = true, Name = "single-instance" };
        t.Start();
    }

    /// <summary>True when another copy is running (for --quit).</summary>
    public static bool OtherRunning()
    {
        try
        {
            using var m = Mutex.OpenExisting(MutexName);
            return true;
        }
        catch (Exception) { return false; }
    }
}

/// <summary>The HKCU Run entry. Only the installed copy writes it: a build in the project folder changes with every build.</summary>
internal static class Startup
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "GearHub";

    public static string Command => $"\"{Paths.Exe}\" --tray";

    public static void Sync()
    {
        if (!Paths.IsInstalledCopy) return;
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(Key);
            if (AppSettings.Current.StartWithWindows) k.SetValue(ValueName, Command);
            else if (k.GetValue(ValueName) is string s && s.Contains("GearHub.exe", StringComparison.OrdinalIgnoreCase)) k.DeleteValue(ValueName, false);
        }
        catch (Exception e) { Log.Error("startup", e); }
    }

    public static bool Registered()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(Key);
            return k?.GetValue(ValueName) is string;
        }
        catch (Exception) { return false; }
    }
}

/// <summary>
/// Process names from the Toolhelp list, never through a handle on the process: anti-cheats watch who opens their game
/// (the ilha's rule).
/// </summary>
internal static class Processes
{
    public static Dictionary<uint, string> Snapshot()
    {
        var map = new Dictionary<uint, string>();
        IntPtr snap = CreateToolhelp32Snapshot(0x2, 0);
        if (snap == new IntPtr(-1)) return map;
        try
        {
            var e = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!Process32FirstW(snap, ref e)) return map;
            do map[e.th32ProcessID] = e.szExeFile; while (Process32NextW(snap, ref e));
        }
        finally { CloseHandle(snap); }
        return map;
    }

    public static string? NameOf(uint pid) => Snapshot().TryGetValue(pid, out var n) ? n : null;

    public static bool Running(string exe)
    {
        foreach (var n in Snapshot().Values)
            if (string.Equals(n, exe, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32FirstW(IntPtr snap, ref PROCESSENTRY32W e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32NextW(IntPtr snap, ref PROCESSENTRY32W e);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
}

/// <summary>
/// The app in the foreground, by event: SetWinEventHook(EVENT_SYSTEM_FOREGROUND) out of context (nothing injected) and
/// the name from Toolhelp. Needs a thread with a message loop (the WPF one).
/// </summary>
internal sealed class ForegroundWatcher : IDisposable
{
    private readonly WinEventDelegate _callback;
    private IntPtr _hook;
    public event Action<string>? Changed;
    public string Current { get; private set; } = "";

    public ForegroundWatcher()
    {
        _callback = OnEvent;
        _hook = SetWinEventHook(3, 3, IntPtr.Zero, _callback, 0, 0, 0);
        Fire(GetForegroundWindow());
    }

    private void OnEvent(IntPtr hook, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time) => Fire(hwnd);

    private void Fire(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == Environment.ProcessId) return;
        string? name = Processes.NameOf(pid);
        if (string.IsNullOrEmpty(name) || name == Current) return;
        Current = name;
        Changed?.Invoke(name);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
    }

    private delegate void WinEventDelegate(IntPtr hook, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventDelegate cb, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
}

/// <summary>The vendor apps that may share (and fight over) a device.</summary>
internal static class VendorApps
{
    public static bool Synapse => Processes.Running("RazerAppEngine.exe");
    public static bool GHub => Processes.Running("lghub_agent.exe");
    public static bool Icue => Processes.Running("iCUE.exe");
    public static bool Dsx => Processes.Running("DSX.exe");
}
