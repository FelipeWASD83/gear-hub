using System;
using System.Linq;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace GearHub.Ui;

/// <summary>
/// The window's own Dynamic Island: a black pill with a lit rim at the top that changes size on a spring and swaps
/// what it shows with a quick fade and grow, like the ilha does between its states.
/// </summary>
public sealed class Island : Border
{
    readonly Spring _w, _h;
    readonly Grid _host = new();
    readonly Stopwatch _clock = new();
    bool _ticking;
    double _last;

    public Island()
    {
        _w = new Spring(150);
        _h = new Spring(36);
        Width = _w.Value;
        Height = _h.Value;
        Background = Brushes.Black;
        BorderBrush = new SolidColorBrush(Color.FromArgb(0x4D, 0xFF, 0xFF, 0xFF));
        BorderThickness = new Thickness(1);
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Top;
        Effect = new DropShadowEffect { Color = Colors.White, BlurRadius = 26, ShadowDepth = 0, Opacity = 0.13 };
        // the content is clipped to the pill itself; clipping the border would also cut its glow into a rectangle
        Child = _host;
        SizeChanged += (_, _) =>
        {
            double r = Math.Min(ActualHeight / 2, 30);
            CornerRadius = new CornerRadius(r);
            double inner = Math.Max(0, r - 1);
            _host.Clip = new RectangleGeometry(new Rect(0, 0, Math.Max(0, ActualWidth - 2), Math.Max(0, ActualHeight - 2)), inner, inner);
        };
    }

    /// <summary>Springs to the new size and fades the new content in over the old one.</summary>
    public void Morph(double width, double height, UIElement content, bool animate = true)
    {
        _w.Target = width;
        _h.Target = height;

        foreach (UIElement old in _host.Children.Cast<UIElement>().ToList())
        {
            if (!animate) { _host.Children.Remove(old); continue; }
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(110));
            fade.Completed += (_, _) => _host.Children.Remove(old);
            old.IsHitTestVisible = false;
            old.BeginAnimation(OpacityProperty, fade);
        }

        if (content is FrameworkElement fe)
        {
            fe.HorizontalAlignment = HorizontalAlignment.Center;
            fe.VerticalAlignment = VerticalAlignment.Center;
        }
        _host.Children.Add(content);
        if (animate)
        {
            content.Opacity = 0;
            var scale = new ScaleTransform(0.86, 0.86);
            content.RenderTransformOrigin = new Point(0.5, 0.5);
            content.RenderTransform = scale;
            var delay = TimeSpan.FromMilliseconds(70);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            content.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(220)) { BeginTime = delay });
            var grow = new DoubleAnimation(1, TimeSpan.FromMilliseconds(320)) { BeginTime = delay, EasingFunction = ease };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            StartTicking();
        }
        else Snap();
    }

    /// <summary>Jumps to the end of every motion (screenshots).</summary>
    public void Snap()
    {
        _w.Snap();
        _h.Snap();
        Width = _w.Value;
        Height = _h.Value;
    }

    void StartTicking()
    {
        if (_ticking) return;
        _ticking = true;
        _clock.Restart();
        _last = 0;
        CompositionTarget.Rendering += Tick;
    }

    void Tick(object? sender, EventArgs e)
    {
        double now = _clock.Elapsed.TotalSeconds;
        double dt = Math.Min(now - _last, 1.0 / 20);
        _last = now;
        _w.Step(dt);
        _h.Step(dt);
        Width = Math.Max(10, _w.Value);
        Height = Math.Max(10, _h.Value);
        if (_w.Settled && _h.Settled)
        {
            CompositionTarget.Rendering -= Tick;
            _ticking = false;
        }
    }
}
