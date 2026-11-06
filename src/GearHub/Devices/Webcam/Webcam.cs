using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text.Json.Nodes;
using GearHub.Ui;

namespace GearHub.Devices.Webcam;

/// <summary>
/// Any webcam's standard UVC controls through DirectShow (IAMCameraControl and IAMVideoProcAmp on the capture
/// filter): zoom, focus, exposure, white balance, brightness and the rest, with "auto" where the camera offers it.
/// Binding the filter does not start a capture, so the camera light stays off. G HUB drives the C920 the same way.
/// </summary>
internal sealed class Webcam : Gear
{
    private readonly string _name;
    private readonly string _moniker;
    private readonly int _index;

    private Webcam(string name, string moniker, int index)
    {
        _name = name;
        _moniker = moniker;
        _index = index;
    }

    public override string Key => "cam-" + Sanitize(_name) + (_index > 0 ? "-" + _index : "");
    public override string Name => _name;
    public override GearKind Kind => GearKind.Camera;
    public override string Brand => _name.Contains("Logi", StringComparison.OrdinalIgnoreCase) || _name.Contains("C920") ? "Logitech" : "";

    private static string Sanitize(string s) => new string(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');

    // CameraControlProperty / VideoProcAmpProperty
    private static readonly (int Id, string Pt, string En)[] CameraProps =
    {
        (0, "Pan", "Pan"), (1, "Tilt", "Tilt"), (2, "Giro", "Roll"), (3, "Zoom", "Zoom"), (4, "Exposição", "Exposure"), (5, "Íris", "Iris"), (6, "Foco", "Focus"),
    };
    private static readonly (int Id, string Pt, string En)[] VideoProps =
    {
        (0, "Brilho", "Brightness"), (1, "Contraste", "Contrast"), (2, "Matiz", "Hue"), (3, "Saturação", "Saturation"), (4, "Nitidez", "Sharpness"),
        (5, "Gama", "Gamma"), (7, "Balanço de branco", "White balance"), (8, "Compensação de luz de fundo", "Backlight compensation"), (9, "Ganho", "Gain"),
    };

    public static List<Webcam> Find()
    {
        var list = new List<Webcam>();
        try
        {
            var devEnum = (ICreateDevEnum)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("62BE5D10-60EB-11d0-BD3B-00A0C911CE86"))!)!;
            var cat = new Guid("860BB310-5D01-11d0-BD3B-00A0C911CE86"); // CLSID_VideoInputDeviceCategory
            if (devEnum.CreateClassEnumerator(ref cat, out var e, 0) != 0 || e == null) return list;
            var m = new IMoniker[1];
            var seen = new Dictionary<string, int>();
            while (e.Next(1, m, IntPtr.Zero) == 0)
            {
                string name = FriendlyName(m[0]) ?? "Webcam";
                m[0].GetDisplayName(null!, null, out string display);
                int n = seen.TryGetValue(name, out int c) ? c + 1 : 0;
                seen[name] = n;
                list.Add(new Webcam(name, display, n));
                Marshal.ReleaseComObject(m[0]);
            }
            Marshal.ReleaseComObject(e);
            Marshal.ReleaseComObject(devEnum);
        }
        catch (Exception ex) { Log.Error("webcam enum", ex); }
        return list;
    }

    private static string? FriendlyName(IMoniker m)
    {
        var bagId = typeof(IPropertyBag).GUID;
        m.BindToStorage(null!, null, ref bagId, out object bagObj);
        var bag = (IPropertyBag)bagObj;
        try
        {
            bag.Read("FriendlyName", out object v, IntPtr.Zero);
            return v as string;
        }
        catch (Exception) { return null; }
        finally { Marshal.ReleaseComObject(bag); }
    }

    /// <summary>Binds the capture filter, runs `f` with its two control interfaces and lets go.</summary>
    private T With<T>(Func<IAMCameraControl?, IAMVideoProcAmp?, T> f)
    {
        CreateBindCtx(0, out var ctx);
        MkParseDisplayName(ctx, _moniker, out _, out var moniker);
        var filterId = new Guid("56a86895-0ad4-11ce-b03a-0020af0ba770"); // IBaseFilter
        moniker.BindToObject(ctx, null, ref filterId, out object filter);
        try { return f(filter as IAMCameraControl, filter as IAMVideoProcAmp); }
        finally
        {
            Marshal.ReleaseComObject(filter);
            Marshal.ReleaseComObject(moniker);
            Marshal.ReleaseComObject(ctx);
        }
    }

    public sealed record Prop(bool Camera, int Id, string Label, int Min, int Max, int Step, int Default, bool CanAuto, int Value, bool Auto);

    public List<Prop> Read() => With((cam, amp) =>
    {
        var props = new List<Prop>();
        if (cam != null)
            foreach (var (id, pt, en) in CameraProps)
                if (cam.GetRange(id, out int min, out int max, out int step, out int def, out int caps) == 0 && max > min && cam.Get(id, out int v, out int flags) == 0)
                    props.Add(new Prop(true, id, Lang.T(pt, en), min, max, Math.Max(1, step), def, (caps & 1) != 0, v, (flags & 1) != 0));
        if (amp != null)
            foreach (var (id, pt, en) in VideoProps)
                if (amp.GetRange(id, out int min, out int max, out int step, out int def, out int caps) == 0 && max > min && amp.Get(id, out int v, out int flags) == 0)
                    props.Add(new Prop(false, id, Lang.T(pt, en), min, max, Math.Max(1, step), def, (caps & 1) != 0, v, (flags & 1) != 0));
        return props;
    });

    public bool Write(bool camera, int id, int value, bool auto) => With((cam, amp) =>
        camera ? cam?.Set(id, value, auto ? 1 : 2) == 0 : amp?.Set(id, value, auto ? 1 : 2) == 0);

    public override JsonObject? Capture()
    {
        var o = new JsonObject();
        foreach (var p in Read()) o[(p.Camera ? "c" : "v") + p.Id] = new JsonArray(p.Value, p.Auto);
        return o.Count > 0 ? o : null;
    }

    public override void Apply(JsonObject s)
    {
        var now = Read().ToDictionary(p => (p.Camera ? "c" : "v") + p.Id);
        foreach (var kv in s)
            if (kv.Value is JsonArray a && now.TryGetValue(kv.Key, out var p) && ((int)a[0]! != p.Value || a[1]!.GetValue<bool>() != p.Auto))
                Write(p.Camera, p.Id, (int)a[0]!, a[1]!.GetValue<bool>());
    }

    public override void BuildPage(Page p)
    {
        p.Header(_name, Lang.T("Ajustes da própria câmera (valem em qualquer programa que usar ela). A luz da câmera não acende.",
            "The camera's own settings (they apply in every app that uses it). The camera light does not turn on."));
        // live picture, only on request (the camera light turns on while it shows)
        var view = p.Section(Lang.T("Imagem", "Picture"), Lang.T("A luz da câmera acende enquanto a imagem estiver ligada. Sair da página desliga.",
            "The camera light is on while the picture shows. Leaving the page turns it off."));
        var image = new System.Windows.Controls.Image { Width = 640, Stretch = System.Windows.Media.Stretch.Uniform, HorizontalAlignment = System.Windows.HorizontalAlignment.Left, Visibility = System.Windows.Visibility.Collapsed };
        var frame = new System.Windows.Controls.Border { CornerRadius = new System.Windows.CornerRadius(18), ClipToBounds = true, Child = image, HorizontalAlignment = System.Windows.HorizontalAlignment.Left, Margin = new System.Windows.Thickness(0, 8, 0, 4) };
        CameraPreview? preview = null;
        void StopPreview()
        {
            preview?.Dispose();
            preview = null;
            image.Source = null;
            image.Visibility = System.Windows.Visibility.Collapsed;
        }
        p.OnClose += StopPreview;
        p.Row(view, Lang.T("Mostrar a imagem", "Show the picture"), null, p.Toggle(false, on =>
        {
            if (!on) { StopPreview(); return; }
            preview = new CameraPreview(image);
            image.Visibility = System.Windows.Visibility.Visible;
            var pv = preview;
            p.Work(() => pv.StartAsync(_name).GetAwaiter().GetResult(), ok =>
            {
                if (!ok) { p.Say(pv.Problem ?? Lang.T("A câmera não abriu", "The camera did not open"), false); StopPreview(); }
                else if (pv.Shared) p.Say(Lang.T("Outro programa está com a câmera: imagem só para ver", "Another app holds the camera: view only"));
            });
        }));
        view.Children.Add(frame);

        var loading = p.Note(p.Root, Lang.T("Lendo a câmera...", "Reading the camera..."));
        string fail = Lang.T("A câmera não aceitou", "The camera did not take it");
        p.Work(Read, props =>
        {
            p.Root.Children.Remove(loading);
            if (props.Count == 0) { p.Note(p.Root, Lang.T("Essa câmera não mostra nenhum ajuste.", "This camera shows no settings."), true); return; }
            foreach (var group in new[] { (true, Lang.T("Imagem e foco", "Framing and focus")), (false, Lang.T("Cor e luz", "Colour and light")) })
            {
                var items = props.Where(x => x.Camera == group.Item1).ToList();
                if (items.Count == 0) continue;
                var sec = p.Section(group.Item2);
                foreach (var prop in items)
                {
                    var pr = prop;
                    bool auto = pr.Auto;
                    int value = pr.Value;
                    var slider = p.Slider(pr.Min, pr.Max, pr.Step, pr.Value, v => $"{v:0}", v =>
                    {
                        value = (int)v;
                        auto = false;
                        p.Work(() => Write(pr.Camera, pr.Id, value, false), ok => p.Say(ok ? $"{pr.Label}: {value}" : fail, ok));
                    }, 260);
                    slider.IsEnabled = !auto;
                    var line = p.Line(slider);
                    if (pr.CanAuto)
                    {
                        var t = p.Toggle(auto, a =>
                        {
                            auto = a;
                            slider.IsEnabled = !a;
                            p.Work(() => Write(pr.Camera, pr.Id, value, a), ok => p.Say(ok ? $"{pr.Label}: " + (a ? Lang.T("automático", "auto") : value.ToString()) : fail, ok));
                        });
                        t.Margin = new System.Windows.Thickness(14, 0, 0, 0);
                        t.ToolTip = Lang.T("Automático", "Automatic");
                        line.Children.Add(t);
                    }
                    p.Row(sec, pr.Label, pr.CanAuto ? Lang.T("A chave liga o automático.", "The switch turns auto on.") : null, line);
                }
            }
            p.Root.Children.Add(p.Line(p.Button(Lang.T("Voltar ao padrão da câmera", "Camera defaults"), () =>
                p.Work(() => { foreach (var x in props) Write(x.Camera, x.Id, x.Default, x.CanAuto); }, () => p.Say(Lang.T("Padrão de fábrica (reabra a página)", "Factory defaults (reopen the page)"))))));
        });
    }

    [DllImport("ole32.dll")] private static extern int CreateBindCtx(int reserved, out IBindCtx ctx);
    [DllImport("ole32.dll", CharSet = CharSet.Unicode)] private static extern int MkParseDisplayName(IBindCtx ctx, string name, out int eaten, out IMoniker moniker);

    [ComImport, Guid("29840822-5B84-11D0-BD3B-00A0C911CE86"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICreateDevEnum
    {
        [PreserveSig] int CreateClassEnumerator(ref Guid type, out IEnumMoniker? e, int flags);
    }

    [ComImport, Guid("55272A00-42CB-11CE-8135-00AA004BB851"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyBag
    {
        [PreserveSig] int Read([MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.Struct)] out object value, IntPtr errorLog);
        [PreserveSig] int Write([MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.Struct)] ref object value);
    }

    [ComImport, Guid("C6E13370-30AC-11d0-A18C-00A0C9118956"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAMCameraControl
    {
        [PreserveSig] int GetRange(int property, out int min, out int max, out int step, out int def, out int caps);
        [PreserveSig] int Set(int property, int value, int flags);
        [PreserveSig] int Get(int property, out int value, out int flags);
    }

    [ComImport, Guid("C6E13360-30AC-11d0-A18C-00A0C9118956"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAMVideoProcAmp
    {
        [PreserveSig] int GetRange(int property, out int min, out int max, out int step, out int def, out int caps);
        [PreserveSig] int Set(int property, int value, int flags);
        [PreserveSig] int Get(int property, out int value, out int flags);
    }
}
