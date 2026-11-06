using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace GearHub.Hid;

/// <summary>One HID collection the system lists: its path, ids and report sizes.</summary>
internal sealed record HidInfo(string Path, ushort Vendor, ushort Product, ushort UsagePage, ushort Usage, int InputLength, int OutputLength, int FeatureLength)
{
    /// <summary>"mi_02", "col01"... parts of the path (lower case), "" when absent.</summary>
    public string Part(string prefix)
    {
        string p = Path.ToLowerInvariant();
        int i = p.IndexOf(prefix, StringComparison.Ordinal);
        if (i < 0) return "";
        int end = i + prefix.Length;
        while (end < p.Length && char.IsLetterOrDigit(p[end])) end++;
        return p[i..end];
    }

    public int Interface => int.TryParse(Part("mi_").Replace("mi_", ""), System.Globalization.NumberStyles.HexNumber, null, out int v) ? v : -1;
}

/// <summary>SetupAPI enumeration and plain HID calls on hid.dll and kernel32 (no native library of our own).</summary>
internal static class HidApi
{
    public static List<HidInfo> Enumerate(ushort vendor = 0)
    {
        var list = new List<HidInfo>();
        HidD_GetHidGuid(out var guid);
        IntPtr set = SetupDiGetClassDevsW(ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == new IntPtr(-1)) return list;
        try
        {
            var data = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data); i++)
            {
                SetupDiGetDeviceInterfaceDetailW(set, ref data, IntPtr.Zero, 0, out uint need, IntPtr.Zero);
                if (need == 0) continue;
                IntPtr buf = Marshal.AllocHGlobal((int)need);
                try
                {
                    Marshal.WriteInt32(buf, 8);
                    if (!SetupDiGetDeviceInterfaceDetailW(set, ref data, buf, need, out _, IntPtr.Zero)) continue;
                    string path = Marshal.PtrToStringUni(buf + 4) ?? "";
                    if (vendor != 0 && !path.Contains($"vid_{vendor:x4}", StringComparison.OrdinalIgnoreCase)) continue;
                    var info = Describe(path);
                    if (info != null) list.Add(info);
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return list;
    }

    /// <summary>Opens without asking for read/write first (works even for collections owned exclusively, like keyboards).</summary>
    private static HidInfo? Describe(string path)
    {
        using var h = CreateFileW(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid) return null;
        var attr = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
        if (!HidD_GetAttributes(h, ref attr)) return null;
        if (!HidD_GetPreparsedData(h, out var pd)) return null;
        try
        {
            var caps = new HIDP_CAPS();
            HidP_GetCaps(pd, ref caps);
            return new HidInfo(path, attr.VendorID, attr.ProductID, caps.UsagePage, caps.Usage, caps.InputReportByteLength, caps.OutputReportByteLength, caps.FeatureReportByteLength);
        }
        finally { HidD_FreePreparsedData(pd); }
    }

    public static SafeFileHandle? Open(string path, bool overlapped)
    {
        var h = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING,
            overlapped ? FILE_FLAG_OVERLAPPED : 0, IntPtr.Zero);
        if (!h.IsInvalid) return h;
        h.Dispose();
        return null;
    }

    public static bool SetFeature(SafeFileHandle h, byte[] report) => HidD_SetFeature(h, report, report.Length);
    public static bool GetFeature(SafeFileHandle h, byte[] report) => HidD_GetFeature(h, report, report.Length);

    /// <summary>Writes an output report on a handle opened with overlapped I/O (waits at most `timeoutMs`).</summary>
    public static unsafe bool Write(SafeFileHandle h, byte[] report, int timeoutMs = 1000)
    {
        using var ev = new ManualResetEvent(false);
        var ov = new NativeOverlapped { EventHandle = ev.SafeWaitHandle.DangerousGetHandle() };
        fixed (byte* p = report)
        {
            if (WriteFile(h, p, report.Length, IntPtr.Zero, &ov)) return true;
            if (Marshal.GetLastWin32Error() != ERROR_IO_PENDING) return false;
            if (!ev.WaitOne(timeoutMs))
            {
                CancelIoEx(h, &ov);
                GetOverlappedResult(h, &ov, out _, true);
                return false;
            }
            return GetOverlappedResult(h, &ov, out _, false);
        }
    }

    public const uint ERROR_IO_PENDING = 997;
    private const uint DIGCF_PRESENT = 0x2, DIGCF_DEVICEINTERFACE = 0x10;
    private const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000, FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
    private const uint FILE_FLAG_OVERLAPPED = 0x40000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID; public ushort ProductID; public ushort VersionNumber; }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
            NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices, NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid g);
    [DllImport("hid.dll")] private static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES a);
    [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_SetFeature(SafeFileHandle h, byte[] buf, int len);
    [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buf, int len);
    [DllImport("hid.dll")] private static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr data);
    [DllImport("hid.dll")] private static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr data, ref HIDP_CAPS caps);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern IntPtr SetupDiGetClassDevsW(ref Guid g, IntPtr enumerator, IntPtr hwnd, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid g, uint index, ref SP_DEVICE_INTERFACE_DATA d);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, ref SP_DEVICE_INTERFACE_DATA d, IntPtr detail, uint size, out uint need, IntPtr info);
    [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sec, uint create, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern unsafe bool WriteFile(SafeFileHandle h, byte* buf, int len, IntPtr written, NativeOverlapped* ov);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern unsafe bool ReadFile(SafeFileHandle h, byte* buf, int len, IntPtr read, NativeOverlapped* ov);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern unsafe bool GetOverlappedResult(SafeFileHandle h, NativeOverlapped* ov, out int bytes, bool wait);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern unsafe bool CancelIoEx(SafeFileHandle h, NativeOverlapped* ov);
}

/// <summary>
/// An open HID collection with a reader thread: every input report lands in a queue the caller drains. Reports are
/// delivered to every open handle (the vendor app sees ours and we see theirs), so callers filter what they take.
/// </summary>
internal sealed class HidStream : IDisposable
{
    private readonly SafeFileHandle _h;
    private readonly int _inLength;
    private readonly ManualResetEvent _stop = new(false);
    private readonly Thread _reader;
    public BlockingCollection<byte[]> Reports { get; } = new(new ConcurrentQueue<byte[]>(), 256);
    public HidInfo Info { get; }
    public bool Alive { get; private set; } = true;

    private HidStream(HidInfo info, SafeFileHandle h)
    {
        Info = info;
        _h = h;
        _inLength = info.InputLength;
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "hid-read" };
        _reader.Start();
    }

    public static HidStream? Open(HidInfo info)
    {
        var h = HidApi.Open(info.Path, true);
        return h == null ? null : new HidStream(info, h);
    }

    public bool Write(byte[] report) => Alive && HidApi.Write(_h, report);

    public void Drain()
    {
        while (Reports.TryTake(out _)) { }
    }

    private unsafe void ReadLoop()
    {
        if (_inLength <= 0) return;
        var buf = new byte[_inLength];
        using var ev = new ManualResetEvent(false);
        var waits = new WaitHandle[] { ev, _stop };
        try
        {
            while (true)
            {
                ev.Reset();
                var ov = new NativeOverlapped { EventHandle = ev.SafeWaitHandle.DangerousGetHandle() };
                fixed (byte* p = buf)
                {
                    bool done = HidApi.ReadFile(_h, p, buf.Length, IntPtr.Zero, &ov);
                    if (!done && Marshal.GetLastWin32Error() != HidApi.ERROR_IO_PENDING) { Alive = false; return; }
                    if (!done)
                    {
                        int w = WaitHandle.WaitAny(waits);
                        if (w == 1)
                        {
                            HidApi.CancelIoEx(_h, &ov);
                            HidApi.GetOverlappedResult(_h, &ov, out _, true);
                            return;
                        }
                    }
                    if (!HidApi.GetOverlappedResult(_h, &ov, out int n, false)) { Alive = false; return; }
                    if (n > 0)
                    {
                        var copy = new byte[n];
                        Buffer.BlockCopy(buf, 0, copy, 0, n);
                        if (!Reports.TryAdd(copy)) { Reports.TryTake(out _); Reports.TryAdd(copy); }
                    }
                }
            }
        }
        catch (Exception e) { Log.Error("hid read", e); Alive = false; }
    }

    public void Dispose()
    {
        _stop.Set();
        _reader.Join(500);
        _h.Dispose();
        Alive = false;
    }
}
