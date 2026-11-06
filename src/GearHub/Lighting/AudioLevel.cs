using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace GearHub.Lighting;

/// <summary>
/// How loud the PC is right now (WASAPI loopback by COM, the capture the Fundo Vivo uses, without the FFT): overall
/// level and bass level, 0..1, smoothed with fast attack and slow release, plus a beat flag. Runs only while an
/// effect asks for it.
/// </summary>
internal sealed class AudioLevel : IDisposable
{
    private Thread? _thread;
    private volatile bool _running;
    private float _level, _bass, _peakLevel = 0.05f, _peakBass = 0.05f;
    private long _beatAt;

    public float Level => _level;
    public float Bass => _bass;

    /// <summary>A bass hit in the last ~120 ms.</summary>
    public bool Beat => Environment.TickCount64 - Interlocked.Read(ref _beatAt) < 120;

    public static AudioLevel Shared { get; } = new();
    private int _users;

    public void Acquire()
    {
        if (Interlocked.Increment(ref _users) == 1) Start();
    }

    public void Release()
    {
        if (Interlocked.Decrement(ref _users) <= 0) { _users = 0; Stop(); }
    }

    private void Start()
    {
        if (_running) return;
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "audio-level", Priority = ThreadPriority.BelowNormal };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    private void Stop()
    {
        _running = false;
        _thread?.Join(1000);
        _thread = null;
        _level = _bass = 0;
    }

    public void Dispose() => Stop();

    private void Loop()
    {
        while (_running)
        {
            try { Capture(); }
            catch (Exception e)
            {
                Log.Write("audio level: " + e.Message);
                for (int i = 0; i < 20 && _running; i++) Thread.Sleep(100);
            }
        }
    }

    private unsafe void Capture()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        enumerator.GetDefaultAudioEndpoint(0, 1, out IMMDevice device);
        device.GetId(out string deviceId);
        Guid iidClient = typeof(IAudioClient).GUID;
        device.Activate(ref iidClient, 0x17, IntPtr.Zero, out object clientObj);
        var client = (IAudioClient)clientObj;
        client.GetMixFormat(out IntPtr formatPtr);
        try
        {
            var fmt = Marshal.PtrToStructure<WaveFormatEx>(formatPtr);
            bool isFloat = fmt.wFormatTag == 3 || (fmt.wFormatTag == 0xFFFE && fmt.wBitsPerSample == 32 && Marshal.PtrToStructure<Guid>(formatPtr + 24) == new Guid("00000003-0000-0010-8000-00aa00389b71"));
            int channels = fmt.nChannels, bytes = fmt.wBitsPerSample / 8;
            // one-pole low pass at ~150 Hz for the bass
            float alpha = 1f - MathF.Exp(-2f * MathF.PI * 150f / fmt.nSamplesPerSec);
            float low = 0;
            client.Initialize(0, 0x00020000, 2_000_000, 0, formatPtr, IntPtr.Zero);
            Guid iidCapture = typeof(IAudioCaptureClient).GUID;
            client.GetService(ref iidCapture, out object captureObj);
            var capture = (IAudioCaptureClient)captureObj;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            long lastCheck = 0, lastPacket = 0;
            client.Start();
            try
            {
                while (_running)
                {
                    Thread.Sleep(15);
                    double sum = 0, sumLow = 0;
                    int n = 0;
                    capture.GetNextPacketSize(out int packet);
                    if (packet > 0) lastPacket = clock.ElapsedMilliseconds;
                    while (packet > 0)
                    {
                        capture.GetBuffer(out IntPtr data, out int frames, out int flags, out _, out _);
                        if ((flags & 0x2) == 0)
                            for (int f = 0; f < frames; f++)
                            {
                                byte* frame = (byte*)data + f * channels * bytes;
                                float s = Sample(frame, isFloat, bytes);
                                if (channels > 1) s = (s + Sample(frame + bytes, isFloat, bytes)) * 0.5f;
                                low += alpha * (s - low);
                                sum += s * s;
                                sumLow += low * low;
                                n++;
                            }
                        capture.ReleaseBuffer(frames);
                        capture.GetNextPacketSize(out packet);
                    }
                    float rms = n > 0 ? (float)Math.Sqrt(sum / n) : (clock.ElapsedMilliseconds - lastPacket > 80 ? 0 : _level * _peakLevel);
                    float bass = n > 0 ? (float)Math.Sqrt(sumLow / n) : 0;
                    // automatic gain: the loudest recent value maps to 1
                    _peakLevel = Math.Max(0.02f, Math.Max(rms, _peakLevel * 0.997f));
                    _peakBass = Math.Max(0.02f, Math.Max(bass, _peakBass * 0.997f));
                    float l = Math.Clamp(rms / _peakLevel, 0, 1), b = Math.Clamp(bass / _peakBass, 0, 1);
                    if (b > 0.85f && _bass < 0.6f) Interlocked.Exchange(ref _beatAt, Environment.TickCount64);
                    _level = l > _level ? l : _level * 0.88f + l * 0.12f;
                    _bass = b > _bass ? b : _bass * 0.85f + b * 0.15f;
                    if (clock.ElapsedMilliseconds - lastCheck > 2000)
                    {
                        lastCheck = clock.ElapsedMilliseconds;
                        enumerator.GetDefaultAudioEndpoint(0, 1, out IMMDevice current);
                        current.GetId(out string currentId);
                        Marshal.ReleaseComObject(current);
                        if (currentId != deviceId) break;
                    }
                }
            }
            finally { client.Stop(); Marshal.ReleaseComObject(capture); }
        }
        finally
        {
            Marshal.FreeCoTaskMem(formatPtr);
            Marshal.ReleaseComObject(client);
            Marshal.ReleaseComObject(device);
            Marshal.ReleaseComObject(enumerator);
        }
    }

    private static unsafe float Sample(byte* p, bool isFloat, int bytes)
    {
        if (isFloat) return *(float*)p;
        return bytes switch
        {
            2 => *(short*)p / 32768f,
            3 => ((p[0] << 8 | p[1] << 16 | p[2] << 24) >> 8) / 8388608f,
            4 => *(int*)p / 2147483648f,
            _ => 0,
        };
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormatEx { public ushort wFormatTag, nChannels; public int nSamplesPerSec, nAvgBytesPerSec; public ushort nBlockAlign, wBitsPerSample, cbSize; }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] private class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        void EnumAudioEndpoints(int dataFlow, int mask, out IntPtr devices);
        void GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        void Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        void OpenPropertyStore(int access, out IntPtr store);
        void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        void Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
        void GetBufferSize(out int frames);
        void GetStreamLatency(out long latency);
        void GetCurrentPadding(out int padding);
        void IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        void GetMixFormat(out IntPtr format);
        void GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        void Start();
        void Stop();
        void Reset();
        void SetEventHandle(IntPtr handle);
        void GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        void GetBuffer(out IntPtr data, out int frames, out int flags, out long devicePosition, out long qpcPosition);
        void ReleaseBuffer(int frames);
        void GetNextPacketSize(out int frames);
    }
}
