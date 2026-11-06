using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace GearHub.Ui;

/// <summary>
/// A magnetic switch seen from the side, drawn in code: the keycap and stem over the housing with the magnet at the
/// bottom, a 0..4 mm ruler beside it, the press point (where the key counts), the release point and, with rapid
/// trigger on, the reset distance. With a live depth the stem follows the real key on a spring-like ease.
/// </summary>
internal sealed class SwitchView : FrameworkElement
{
    private double _press = 1.5, _release = 1.4, _rapid = 0.1, _shown;
    private bool _rapidOn;
    private double? _live;
    private string _key = "W";
    private bool _ticking;
    private readonly System.Diagnostics.Stopwatch _clock = new();
    private double _last;

    public const double Travel = 4.0;

    public SwitchView()
    {
        Width = 520;
        Height = 260;
    }

    public double PressMm { get => _press; set { _press = value; InvalidateVisual(); } }
    public double ReleaseMm { get => _release; set { _release = value; InvalidateVisual(); } }
    public bool RapidOn { get => _rapidOn; set { _rapidOn = value; InvalidateVisual(); } }
    public double RapidMm { get => _rapid; set { _rapid = value; InvalidateVisual(); } }
    public string KeyName { get => _key; set { _key = value; InvalidateVisual(); } }

    /// <summary>Live depth in mm, or null to show the key at rest.</summary>
    public double? LiveMm
    {
        get => _live;
        set
        {
            _live = value;
            if (!_ticking)
            {
                _ticking = true;
                _clock.Restart();
                _last = 0;
                CompositionTarget.Rendering += Tick;
            }
        }
    }

    private void Tick(object? sender, EventArgs e)
    {
        double now = _clock.Elapsed.TotalSeconds, dt = Math.Min(0.05, now - _last);
        _last = now;
        double target = Math.Clamp(_live ?? 0, 0, Travel);
        _shown += (target - _shown) * Math.Min(1, dt * 22);
        InvalidateVisual();
        if (Math.Abs(target - _shown) < 0.005 && _live == null)
        {
            _shown = 0;
            CompositionTarget.Rendering -= Tick;
            _ticking = false;
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        var ink = (Brush)Application.Current.Resources["Ink"];
        var ink2 = (Brush)Application.Current.Resources["Ink2"];
        var ink3 = (Brush)Application.Current.Resources["Ink3"];
        var well = (Brush)Application.Current.Resources["Well"];
        var line = (Brush)Application.Current.Resources["Line"];
        var good = (Brush)Application.Current.Resources["Good"];
        var warn = (Brush)Application.Current.Resources["Warn"];
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        Typeface face = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        FormattedText Text(string s, double size, Brush b) => new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, size, b, dpi);
        string Mm(double v) => v.ToString("0.0#", CultureInfo.InvariantCulture).Replace('.', Lang.Pt ? ',' : '.') + " mm";

        // the ruler: top of travel at y0, bottom at y1
        const double y0 = 70, y1 = 230, rulerX = 300;
        double Y(double mm) => y0 + (y1 - y0) * Math.Clamp(mm, 0, Travel) / Travel;
        double depth = _live != null || _ticking ? _shown : 0;

        // housing (bottom half) and the magnet
        var housing = new Rect(70, 150, 170, 90);
        dc.DrawRoundedRectangle(well, null, housing, 14, 14);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x40)), null, new Rect(125, 214, 60, 18), 6, 6);
        dc.DrawText(Text(Lang.T("ímã", "magnet"), 11, ink3), new Point(140, 216));

        // stem and keycap move down with the depth
        double dy = (y1 - y0) * depth / Travel * 0.55;
        var stem = new Rect(140, 92 + dy, 30, 90);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x5A, 0x5A, 0x62)), null, stem, 6, 6);
        var cap = new Rect(55, 40 + dy, 200, 56);
        bool pressed = depth >= _press - 0.001;
        dc.DrawRoundedRectangle(pressed ? good : ink, null, cap, 16, 16);
        var label = Text(_key, 22, Brushes.Black);
        dc.DrawText(label, new Point(cap.X + (cap.Width - label.Width) / 2, cap.Y + (cap.Height - label.Height) / 2));

        // ruler with ticks every 0.5 mm
        dc.DrawLine(new Pen(line, 2), new Point(rulerX, y0), new Point(rulerX, y1));
        for (double mm = 0; mm <= Travel + 1e-6; mm += 0.5)
        {
            bool major = Math.Abs(mm % 1) < 1e-6;
            dc.DrawLine(new Pen(major ? ink3 : line, 1.5), new Point(rulerX - (major ? 9 : 5), Y(mm)), new Point(rulerX, Y(mm)));
            if (major) dc.DrawText(Text(((int)mm).ToString(), 11, ink3), new Point(rulerX - 24, Y(mm) - 8));
        }

        // rapid trigger: the reset band above the press point
        if (_rapidOn)
        {
            var band = new Rect(rulerX + 2, Y(_press - _rapid), 14, Math.Max(2, Y(_press) - Y(_press - _rapid)));
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0x9F, 0x0A)), null, band, 3, 3);
        }

        // release (dashed) and press (solid) markers
        var dashed = new Pen(ink2, 1.5) { DashStyle = new DashStyle(new double[] { 3, 3 }, 0) };
        dc.DrawLine(dashed, new Point(rulerX, Y(_release)), new Point(rulerX + 40, Y(_release)));
        dc.DrawLine(new Pen(good, 3), new Point(rulerX, Y(_press)), new Point(rulerX + 40, Y(_press)));
        dc.DrawText(Text(Lang.T("vale em ", "fires at ") + Mm(_press), 14, ink), new Point(rulerX + 48, Y(_press) - 10));
        dc.DrawText(Text(Lang.T("solta em ", "resets at ") + Mm(_release), 12, ink2), new Point(rulerX + 48, Y(_release) - 24));
        if (_rapidOn) dc.DrawText(Text("rapid trigger " + Mm(_rapid), 12, warn), new Point(rulerX + 48, Y(_press) + 10));

        // live depth needle
        if (_live != null)
        {
            dc.DrawEllipse(pressed ? good : warn, null, new Point(rulerX, Y(depth)), 6, 6);
            dc.DrawText(Text(Mm(depth), 12, ink2), new Point(rulerX - 70, Y(depth) - 8));
        }
        dc.DrawText(Text(Lang.T("profundidade", "depth"), 11, ink3), new Point(rulerX - 34, y0 - 26));
    }
}
