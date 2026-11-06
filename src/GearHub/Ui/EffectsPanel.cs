using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using GearHub.Lighting;

namespace GearHub.Ui;

/// <summary>
/// The light effect controls shared by the RAM page, the Logitech pages and the Lights page: effect chips (with a
/// small live preview strip), the two colours, speed and brightness. Every change is sent at once to the targets.
/// </summary>
internal static class EffectsPanel
{
    public static void Build(Page p, IReadOnlyList<ILightTarget> targets, string? hint, Action? changed = null, string? title = null, Func<EffectSpec, string?>? extra = null)
    {
        var sec = p.Section(title ?? Lang.T("Efeito de luz", "Light effect"), hint);
        var first = targets.Count > 0 ? LightEngine.Instance.SpecFor(targets[0].Key) : null;
        var spec = first ?? new EffectSpec { Effect = Effects.Off };
        var preview = new Preview();
        sec.Children.Add(preview.Element);

        var colorsA = new Grid();
        var colorsB = new Grid();
        FrameworkElement? rowA = null, rowB = null, rowSpeed = null;

        void Send()
        {
            preview.Spec = spec.Clone();
            UpdateRows();
            var copy = spec.Clone();
            p.Work(() =>
            {
                var errors = new List<string>();
                foreach (var t in targets)
                {
                    string? e = LightEngine.Instance.Set(t, copy);
                    if (e != null) errors.Add($"{t.Name}: {e}");
                }
                if (extra?.Invoke(copy) is string x) errors.Add(x);
                return errors;
            }, errors =>
            {
                p.Say(errors.Count > 0 ? errors[0] : Effects.Name(copy.Effect), errors.Count == 0);
                changed?.Invoke();
            });
        }

        void UpdateRows()
        {
            var info = Effects.All.FirstOrDefault(e => e.Id == spec.Effect);
            if (rowA != null) rowA.Visibility = info.A ? Visibility.Visible : Visibility.Collapsed;
            if (rowB != null) rowB.Visibility = info.B ? Visibility.Visible : Visibility.Collapsed;
            if (rowSpeed != null) rowSpeed.Visibility = info.Moves ? Visibility.Visible : Visibility.Collapsed;
        }

        var chips = new WrapPanel { Margin = new Thickness(0, 10, 0, 6) };
        string group = "fx" + Guid.NewGuid().ToString("N");
        void AddChip(string id, string label)
        {
            var r = new RadioButton { Content = label, Style = Page.S("Chip"), GroupName = group, IsChecked = spec.Effect == id, Padding = new Thickness(12, 6, 12, 6) };
            r.Checked += (_, _) => { spec.Effect = id; Send(); };
            chips.Children.Add(r);
        }
        AddChip(Effects.Off, Effects.Name(Effects.Off));
        foreach (var e in Effects.All) AddChip(e.Id, Lang.T(e.Pt, e.En));
        sec.Children.Add(chips);

        rowA = p.Row(sec, Lang.T("Cor", "Colour"), null, p.ColorPick(spec.ColorA, c => { spec.A = Page.Hex(c); Send(); }));
        rowB = p.Row(sec, Lang.T("Segunda cor", "Second colour"), null, p.ColorPick(spec.ColorB, c => { spec.B = Page.Hex(c); Send(); }));
        rowSpeed = p.Row(sec, Lang.T("Velocidade", "Speed"), null, p.Slider(0.25, 3, 0.25, spec.Speed, v => $"{v:0.##}x", v => { spec.Speed = v; Send(); }));
        p.Row(sec, Lang.T("Brilho", "Brightness"), null, p.Slider(5, 100, 5, Math.Round(spec.Brightness * 100), v => $"{v:0}%", v => { spec.Brightness = v / 100; Send(); }));
        preview.Spec = spec.Clone();
        UpdateRows();
        preview.Start(p);
    }

    /// <summary>A strip of 20 dots that runs the effect live, so the choice is visible before the hardware shows it.</summary>
    private sealed class Preview
    {
        private readonly Effects.Renderer _renderer;
        private readonly System.Windows.Shapes.Ellipse[] _dots;
        public FrameworkElement Element { get; }
        public EffectSpec Spec { get; set; } = new();

        public Preview()
        {
            const int n = 20;
            _renderer = new Effects.Renderer(Enumerable.Range(0, n).Select(i => i / (double)(n - 1)).ToArray());
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
            _dots = new System.Windows.Shapes.Ellipse[n];
            for (int i = 0; i < n; i++)
            {
                _dots[i] = new System.Windows.Shapes.Ellipse { Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0), Fill = Brushes.Black };
                row.Children.Add(_dots[i]);
            }
            Element = new Border { Background = Page.B("Well"), CornerRadius = new CornerRadius(14), Padding = new Thickness(14, 10, 6, 10), Child = row, HorizontalAlignment = HorizontalAlignment.Left };
        }

        public void Start(Page p)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
            bool audio = false;
            timer.Tick += (_, _) =>
            {
                if (p.Closed)
                {
                    timer.Stop();
                    if (audio) AudioLevel.Shared.Release();
                    return;
                }
                bool wantAudio = Spec.Effect == Effects.Music;
                if (wantAudio != audio) { if (wantAudio) AudioLevel.Shared.Acquire(); else AudioLevel.Shared.Release(); audio = wantAudio; }
                if (Spec.Effect == Effects.Off)
                {
                    foreach (var d in _dots) d.Fill = Page.B("Line");
                    return;
                }
                var frame = _renderer.Frame(Spec, clock.Elapsed.TotalSeconds, audio ? AudioLevel.Shared : null);
                for (int i = 0; i < _dots.Length; i++) _dots[i].Fill = new SolidColorBrush(frame[i]);
            };
            timer.Start();
        }
    }
}
