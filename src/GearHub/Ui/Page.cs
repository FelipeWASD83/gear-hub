using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace GearHub.Ui;

/// <summary>
/// Builds one device page in code with the theme's pieces: sections (rounded cards), rows with a label on the left
/// and the control on the right, switches, sliders that commit when released, segmented choices and colour picks.
/// Hardware calls go through Work, which runs them off the interface thread and comes back with the result.
/// </summary>
internal sealed class Page
{
    public StackPanel Root { get; } = new();
    private readonly Action<string, bool> _say;
    private readonly Dispatcher _ui;
    public bool Closed { get; set; }

    public Page(Action<string, bool> say)
    {
        _say = say;
        _ui = Dispatcher.CurrentDispatcher;
    }

    public static Brush B(string key) => (Brush)Application.Current.Resources[key];
    public static Style S(string key) => (Style)Application.Current.Resources[key];

    /// <summary>A short message in the window's island (ok = true shows it in white, false in orange).</summary>
    public void Say(string text, bool ok = true) => _say(text, ok);

    public void Work<T>(Func<T> background, Action<T> done, string? failMessage = null)
    {
        Task.Run(() =>
        {
            try
            {
                T r = background();
                _ui.BeginInvoke(() => { if (!Closed) done(r); });
            }
            catch (Exception e)
            {
                Log.Error("page work", e);
                _ui.BeginInvoke(() => Say(failMessage ?? Lang.T("Não deu certo: ", "That did not work: ") + e.Message, false));
            }
        });
    }

    public void Work(Action background, Action? done = null, string? failMessage = null) =>
        Work<bool>(() => { background(); return true; }, _ => done?.Invoke(), failMessage);

    public void Header(string title, string sub)
    {
        var t = new TextBlock { Text = title, Style = S("H1"), Margin = new Thickness(4, 0, 0, 4) };
        var s = new TextBlock { Text = sub, Style = S("Sub"), Margin = new Thickness(4, 0, 0, 18) };
        Root.Children.Add(t);
        Root.Children.Add(s);
    }

    public StackPanel Section(string title, string? hint = null)
    {
        var inner = new StackPanel();
        var head = new TextBlock { Text = title, Style = S("H2"), Margin = new Thickness(0, 2, 0, hint == null ? 10 : 2) };
        inner.Children.Add(head);
        if (hint != null) inner.Children.Add(new TextBlock { Text = hint, Style = S("Small"), Margin = new Thickness(0, 0, 0, 10) });
        var card = new Border { Style = S("Section"), Child = inner, Margin = new Thickness(0, 0, 0, 14) };
        Root.Children.Add(card);
        return inner;
    }

    public Grid Row(Panel parent, string label, string? sub, UIElement control)
    {
        var g = new Grid { Margin = new Thickness(0, 6, 0, 6) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 18, 0) };
        left.Children.Add(new TextBlock { Text = label, FontSize = 14, Foreground = B("Ink"), TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrEmpty(sub)) left.Children.Add(new TextBlock { Text = sub, Style = S("Small"), Margin = new Thickness(0, 2, 0, 0) });
        g.Children.Add(left);
        if (control is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1);
        g.Children.Add(control);
        parent.Children.Add(g);
        return g;
    }

    public CheckBox Toggle(bool on, Action<bool> changed)
    {
        var c = new CheckBox { IsChecked = on };
        bool quiet = false;
        c.Checked += (_, _) => { if (!quiet) changed(true); };
        c.Unchecked += (_, _) => { if (!quiet) changed(false); };
        c.Tag = (Action<bool>)(v => { quiet = true; c.IsChecked = v; quiet = false; });
        return c;
    }

    /// <summary>Sets a toggle made by Toggle without firing its handler.</summary>
    public static void SetQuiet(CheckBox c, bool on) => (c.Tag as Action<bool>)?.Invoke(on);

    /// <summary>A slider with its value on the right; `commit` runs once when the drag ends (or after a key/wheel).</summary>
    public FrameworkElement Slider(double min, double max, double step, double value, Func<double, string> format, Action<double> commit, double width = 300)
    {
        var s = new Slider { Minimum = min, Maximum = max, Value = value, SmallChange = step, LargeChange = step * 5, IsSnapToTickEnabled = true, TickFrequency = step, Width = width };
        var label = new TextBlock { Text = format(value), Foreground = B("Ink"), FontSize = 14, Width = 86, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        double last = value;
        bool dragging = false;
        void Fire()
        {
            timer.Stop();
            if (Math.Abs(s.Value - last) < step / 2) return;
            last = s.Value;
            commit(s.Value);
        }
        timer.Tick += (_, _) => { if (!dragging) Fire(); };
        s.ValueChanged += (_, e) =>
        {
            label.Text = format(e.NewValue);
            if (s.Tag is "quiet") { last = e.NewValue; return; }
            timer.Stop();
            timer.Start();
        };
        s.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => dragging = true));
        s.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => { dragging = false; Fire(); }));
        s.PreviewMouseWheel += (_, e) =>
        {
            s.Value = Math.Clamp(s.Value + (e.Delta > 0 ? step : -step), min, max);
            e.Handled = true;
        };
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(s);
        panel.Children.Add(label);
        panel.Tag = s;
        return panel;
    }

    /// <summary>Moves a slider made by Slider without committing.</summary>
    public static void SetQuiet(FrameworkElement sliderPanel, double value)
    {
        if (sliderPanel.Tag is Slider s)
        {
            s.Tag = "quiet";
            s.Value = value;
            s.Tag = null;
        }
    }

    public Border Segments(IReadOnlyList<string> options, int selected, Action<int> changed)
    {
        var bar = new Border { Style = S("SegmentBar") };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        string group = "seg" + Guid.NewGuid().ToString("N");
        for (int i = 0; i < options.Count; i++)
        {
            int idx = i;
            var r = new RadioButton { Content = options[i], Style = S("Segment"), GroupName = group, IsChecked = i == selected };
            r.Checked += (_, _) => { if (bar.Tag is not "quiet") changed(idx); };
            row.Children.Add(r);
        }
        bar.Child = row;
        return bar;
    }

    public static void SetQuiet(Border segments, int index)
    {
        if (segments.Child is not StackPanel row || index < 0 || index >= row.Children.Count) return;
        segments.Tag = "quiet";
        ((RadioButton)row.Children[index]).IsChecked = true;
        segments.Tag = null;
    }

    public static readonly Color[] Swatches =
    {
        Color.FromRgb(255, 255, 255), Color.FromRgb(255, 59, 48), Color.FromRgb(255, 149, 0), Color.FromRgb(255, 204, 0),
        Color.FromRgb(52, 199, 89), Color.FromRgb(0, 199, 190), Color.FromRgb(0, 122, 255), Color.FromRgb(88, 86, 214),
        Color.FromRgb(175, 82, 222), Color.FromRgb(255, 45, 85), Color.FromRgb(0, 0, 0),
    };

    /// <summary>Colour dots and a hex box. Black means off for lights.</summary>
    public FrameworkElement ColorPick(Color current, Action<Color> picked)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var box = new TextBox { Width = 118, Text = Hex(current), Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 6, 12, 6) };
        foreach (var c in Swatches)
        {
            var col = c;
            var dot = new Button
            {
                Width = 24, Height = 24, Padding = new Thickness(0), Margin = new Thickness(0, 0, 6, 0),
                Background = new SolidColorBrush(c), BorderThickness = new Thickness(0),
                ToolTip = Hex(c),
            };
            if (c.R + c.G + c.B < 30)
            {
                dot.Content = new System.Windows.Shapes.Ellipse { Width = 22, Height = 22, Stroke = B("Ink3"), StrokeThickness = 1 };
                dot.ToolTip = Lang.T("Apagado", "Off");
            }
            dot.Click += (_, _) => { box.Text = Hex(col); picked(col); };
            panel.Children.Add(dot);
        }
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            if (TryParse(box.Text, out var c)) picked(c);
            else Say(Lang.T("Cor em hexadecimal, tipo #FF8800", "Colour as hex, like #FF8800"), false);
        };
        panel.Children.Add(box);
        return panel;
    }

    public static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public static bool TryParse(string text, out Color c)
    {
        c = Colors.Black;
        string t = text.Trim().TrimStart('#');
        if (t.Length != 6 || !int.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v)) return false;
        c = Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    public TextBlock Note(Panel parent, string text, bool warn = false)
    {
        var t = new TextBlock { Text = text, Style = S("Small"), Foreground = warn ? B("Warn") : B("Ink3"), Margin = new Thickness(0, 4, 0, 4) };
        parent.Children.Add(t);
        return t;
    }

    public Button Button(string text, Action click, bool primary = false)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(18, 8, 18, 8) };
        if (primary) b.Style = S("Primary");
        b.Click += (_, _) => click();
        return b;
    }

    public StackPanel Line(params UIElement[] items)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var i in items) p.Children.Add(i);
        return p;
    }

    public static string Mm(double v) => v.ToString("0.0#", CultureInfo.InvariantCulture).Replace('.', Lang.Pt ? ',' : '.') + " mm";
}
