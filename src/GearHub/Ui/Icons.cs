using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using GearHub.Devices;

namespace GearHub.Ui;

/// <summary>Line icons drawn from shapes (never a letter), 22 DIP, in the ink colour.</summary>
internal static class Icons
{
    private const string Keyboard = "M2,6 H20 A1.5,1.5 0 0 1 21.5,7.5 V15 A1.5,1.5 0 0 1 20,16.5 H2 A1.5,1.5 0 0 1 0.5,15 V7.5 A1.5,1.5 0 0 1 2,6 Z M4,9.5 H5 M8,9.5 H9 M12,9.5 H13 M16,9.5 H17 M6,13 H16";
    private const string Mouse = "M11,1.5 C15.5,1.5 17.5,5 17.5,9.5 V14 C17.5,18 15,20.5 11,20.5 C7,20.5 4.5,18 4.5,14 V9.5 C4.5,5 6.5,1.5 11,1.5 Z M11,4.5 V8.5";
    private const string Headset = "M3,14 V11 A8,8 0 0 1 19,11 V14 M3,13 H6 V19 H4.5 A1.5,1.5 0 0 1 3,17.5 Z M19,13 H16 V19 H17.5 A1.5,1.5 0 0 0 19,17.5 Z";
    private const string Memory = "M1,7 H21 V15 H1 Z M4,10 H7 V12 H4 Z M9.5,10 H12.5 V12 H9.5 Z M15,10 H18 V12 H15 Z M3,15 V17.5 M6,15 V17.5 M9,15 V17.5 M13,15 V17.5 M16,15 V17.5 M19,15 V17.5";
    private const string Controller = "M6,6.5 H16 C19,6.5 21,10 21,14 C21,16.5 19.5,17.5 18,17 L15.5,14.5 H6.5 L4,17 C2.5,17.5 1,16.5 1,14 C1,10 3,6.5 6,6.5 Z M6.5,9.5 V12.5 M5,11 H8 M15,10.5 H15.2 M17,12 H17.2";
    private const string Camera = "M11,2 A7,7 0 1 1 10.99,2 Z M11,6.5 A2.5,2.5 0 1 1 10.99,6.5 Z M6,19.5 H16";
    private const string Light = "M11,2 A6,6 0 0 1 15,12.5 V15 H7 V12.5 A6,6 0 0 1 11,2 Z M8,18 H14 M9.5,20.5 H12.5";
    public const string Home = "M2,10 L11,2.5 L20,10 M4.5,8 V19.5 H17.5 V8 M9,19.5 V13.5 H13 V19.5";
    public const string Profiles = "M3,4 H19 M3,11 H19 M3,18 H19 M6,2.5 V5.5 M14,9.5 V12.5 M9,16.5 V19.5";
    public const string Settings = "M11,7.5 A3.5,3.5 0 1 1 10.99,7.5 Z M11,1.5 V4 M11,18 V20.5 M1.5,11 H4 M18,11 H20.5 M4.3,4.3 L6,6 M16,16 L17.7,17.7 M4.3,17.7 L6,16 M16,6 L17.7,4.3";

    public static string For(GearKind k) => k switch
    {
        GearKind.Keyboard => Keyboard,
        GearKind.Mouse => Mouse,
        GearKind.Headset => Headset,
        GearKind.Memory => Memory,
        GearKind.Controller => Controller,
        GearKind.Camera => Camera,
        _ => Light,
    };

    public static Path Make(string data, Brush? stroke = null, double size = 22) => new()
    {
        Data = Geometry.Parse(data),
        Stroke = stroke ?? Page.B("Ink"),
        StrokeThickness = 1.5,
        StrokeLineJoin = PenLineJoin.Round,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        Width = size,
        Height = size,
        Stretch = Stretch.Uniform,
        VerticalAlignment = VerticalAlignment.Center,
    };
}
