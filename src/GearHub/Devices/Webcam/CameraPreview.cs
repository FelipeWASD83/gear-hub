using System;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Devices.Enumeration;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;

namespace GearHub.Devices.Webcam;

/// <summary>
/// Live picture of a webcam (MediaCapture + MediaFrameReader in BGRA), drawn into a WriteableBitmap at up to 30 frames
/// a second, at most 1280 wide. Exclusive control first (so the format can be picked); if another app holds the camera,
/// shared read-only. The camera light is on while this runs. Stop() or the page closing ends it.
/// </summary>
internal sealed class CameraPreview : IDisposable
{
    private MediaCapture? _capture;
    private MediaFrameReader? _reader;
    private WriteableBitmap? _bitmap;
    private readonly Dispatcher _ui;
    private readonly System.Windows.Controls.Image _image;
    private int _busy;
    private long _lastFrame;
    public string? Problem { get; private set; }
    public bool Shared { get; private set; }

    public CameraPreview(System.Windows.Controls.Image image)
    {
        _image = image;
        _ui = image.Dispatcher;
    }

    public async Task<bool> StartAsync(string cameraName)
    {
        try
        {
            var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture).AsTask().ConfigureAwait(false);
            var dev = devices.FirstOrDefault(d => string.Equals(d.Name, cameraName, StringComparison.OrdinalIgnoreCase))
                ?? devices.FirstOrDefault(d => d.Name.Contains(cameraName, StringComparison.OrdinalIgnoreCase) || cameraName.Contains(d.Name, StringComparison.OrdinalIgnoreCase));
            if (dev == null) { Problem = Lang.T("O Windows não achou essa câmera.", "Windows did not find this camera."); return false; }
            foreach (var mode in new[] { MediaCaptureSharingMode.ExclusiveControl, MediaCaptureSharingMode.SharedReadOnly })
            {
                try
                {
                    var cap = new MediaCapture();
                    await cap.InitializeAsync(new MediaCaptureInitializationSettings
                    {
                        VideoDeviceId = dev.Id,
                        StreamingCaptureMode = StreamingCaptureMode.Video,
                        MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                        SharingMode = mode,
                    }).AsTask().ConfigureAwait(false);
                    _capture = cap;
                    Shared = mode == MediaCaptureSharingMode.SharedReadOnly;
                    break;
                }
                catch (Exception e) { Log.Write($"camera {mode}: {e.Message}"); }
            }
            if (_capture == null) { Problem = Lang.T("A câmera está ocupada ou bloqueada (Configurações > Privacidade > Câmera).", "The camera is busy or blocked (Settings > Privacy > Camera)."); return false; }

            var source = _capture.FrameSources.Values.FirstOrDefault(s => s.Info.SourceKind == MediaFrameSourceKind.Color && s.Info.MediaStreamType == Windows.Media.Capture.MediaStreamType.VideoPreview)
                ?? _capture.FrameSources.Values.FirstOrDefault(s => s.Info.SourceKind == MediaFrameSourceKind.Color);
            if (source == null) { Problem = Lang.T("A câmera não mostra imagem colorida.", "The camera gives no colour picture."); return false; }
            if (!Shared)
            {
                // the largest format up to 1280 wide, 30 fps or more
                var format = source.SupportedFormats
                    .Where(f => f.VideoFormat.Width <= 1280 && f.FrameRate.Denominator > 0 && f.FrameRate.Numerator / (double)f.FrameRate.Denominator >= 29)
                    .OrderByDescending(f => f.VideoFormat.Width).FirstOrDefault();
                if (format != null) await source.SetFormatAsync(format).AsTask().ConfigureAwait(false);
            }
            _reader = await _capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8).AsTask().ConfigureAwait(false);
            _reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
            _reader.FrameArrived += OnFrame;
            var status = await _reader.StartAsync().AsTask().ConfigureAwait(false);
            if (status != MediaFrameReaderStartStatus.Success) { Problem = Lang.T("A câmera não começou: ", "The camera did not start: ") + status; return false; }
            return true;
        }
        catch (Exception e)
        {
            Log.Error("camera preview", e);
            Problem = e.Message;
            return false;
        }
    }

    private void OnFrame(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        long now = Environment.TickCount64;
        if (now - _lastFrame < 30 || Interlocked.Exchange(ref _busy, 1) == 1) return;
        _lastFrame = now;
        try
        {
            using var frame = sender.TryAcquireLatestFrame();
            var sb = frame?.VideoMediaFrame?.SoftwareBitmap;
            if (sb == null) { _busy = 0; return; }
            SoftwareBitmap bgra = sb.BitmapPixelFormat == BitmapPixelFormat.Bgra8 ? sb : SoftwareBitmap.Convert(sb, BitmapPixelFormat.Bgra8);
            int w = bgra.PixelWidth, h = bgra.PixelHeight;
            var buffer = new Windows.Storage.Streams.Buffer((uint)(w * h * 4));
            bgra.CopyToBuffer(buffer);
            byte[] pixels = buffer.ToArray();
            if (!ReferenceEquals(bgra, sb)) bgra.Dispose();
            _ui.BeginInvoke(() =>
            {
                try
                {
                    if (_bitmap == null || _bitmap.PixelWidth != w || _bitmap.PixelHeight != h)
                    {
                        _bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
                        _image.Source = _bitmap;
                    }
                    _bitmap.WritePixels(new Int32Rect(0, 0, w, h), pixels, w * 4, 0);
                }
                finally { _busy = 0; }
            }, DispatcherPriority.Render);
        }
        catch (Exception e)
        {
            _busy = 0;
            Log.Write("camera frame: " + e.Message);
        }
    }

    public void Dispose()
    {
        var reader = _reader;
        var cap = _capture;
        _reader = null;
        _capture = null;
        Task.Run(async () =>
        {
            try
            {
                if (reader != null)
                {
                    reader.FrameArrived -= OnFrame;
                    await reader.StopAsync();
                    reader.Dispose();
                }
            }
            catch (Exception) { }
            try { cap?.Dispose(); } catch (Exception) { }
        });
    }
}
