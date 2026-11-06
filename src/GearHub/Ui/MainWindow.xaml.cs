using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GearHub.Devices;
using GearHub.Profiles;

namespace GearHub.Ui;

/// <summary>
/// The hub window: the ilha's look (true black card, lit rim, a pill at the top that springs to show what just
/// happened), the devices on the left and the chosen device's page on the right. Closing it only closes the window;
/// the app keeps running in the tray.
/// </summary>
public partial class MainWindow : Window
{
    private string _selected = "";
    private Page? _page;
    private readonly DispatcherTimer _islandBack = new() { Interval = TimeSpan.FromSeconds(2.8) };

    public MainWindow()
    {
        InitializeComponent();
        Title = Lang.T("Central de Periféricos", "Gear Hub");
        Brand.Text = Title;
        TopBar.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 1) DragMove(); };
        MinButton.Click += (_, _) => WindowState = WindowState.Minimized;
        CloseButton.Click += (_, _) => Close();
        _islandBack.Tick += (_, _) => { _islandBack.Stop(); IdleIsland(); };

        var hub = DeviceHub.Instance;
        hub.ListChanged += OnListChanged;
        hub.GearChanged += OnGearChanged;
        ProfileStore.Changed += OnProfilesChanged;
        Closed += (_, _) =>
        {
            hub.ListChanged -= OnListChanged;
            hub.GearChanged -= OnGearChanged;
            ProfileStore.Changed -= OnProfilesChanged;
            if (_page != null) _page.Closed = true;
        };

        _selected = AppSettings.Current.LastPage;
        BuildNav();
        IdleIsland(animate: false);
        Select(_selected != "" ? _selected : hub.Gears.FirstOrDefault()?.Key ?? "profiles");
    }

    private void OnListChanged() => Dispatcher.BeginInvoke(() => { BuildNav(); IdleIsland(); if (_page == null || (_selected is not "profiles" and not "settings" && DeviceHub.Instance.ByKey(_selected) == null)) Select(_selected); });
    private void OnGearChanged(Gear g) => Dispatcher.BeginInvoke(BuildNav);
    private void OnProfilesChanged() => Dispatcher.BeginInvoke(() => { IdleIsland(); });

    private void BuildNav()
    {
        Nav.Children.Clear();
        NavBottom.Children.Clear();
        var gears = DeviceHub.Instance.Gears;
        if (gears.Count == 0)
            Nav.Children.Add(new TextBlock { Text = Lang.T("Procurando aparelhos...", "Looking for devices..."), Style = Page.S("Small"), Margin = new Thickness(12, 6, 0, 0) });
        foreach (var g in gears)
            Nav.Children.Add(NavItem(g.Key, Icons.For(g.Kind), g.Name, g.Status != "" ? g.Status : g.Brand));
        NavBottom.Children.Add(NavItem("profiles", Icons.Profiles, Lang.T("Perfis", "Profiles"), ProfileStore.Display(ProfileStore.All.FirstOrDefault(p => p.Name == ProfileStore.Active) ?? ProfileStore.Default)));
        NavBottom.Children.Add(NavItem("settings", Icons.Settings, Lang.T("Ajustes", "Settings"), Lang.T("Iniciar com o Windows, idioma, atualização", "Start with Windows, language, updates")));
    }

    private RadioButton NavItem(string key, string icon, string name, string sub)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(Icons.Make(icon));
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = name, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Page.B("Ink"), TextTrimming = TextTrimming.CharacterEllipsis });
        if (!string.IsNullOrEmpty(sub)) text.Children.Add(new TextBlock { Text = sub, FontSize = 12, Foreground = Page.B("Ink3"), TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        var r = new RadioButton { Style = Page.S("Nav"), Content = grid, GroupName = "nav", IsChecked = key == _selected, Tag = key };
        r.Checked += (_, _) => { if (_selected != key) Select(key); };
        return r;
    }

    public void Select(string key)
    {
        if (_page != null) _page.Closed = true;
        _page = new Page(Say);
        Gear? gear = key is "profiles" or "settings" ? null : DeviceHub.Instance.ByKey(key);
        if (gear == null && key is not "profiles" and not "settings")
        {
            gear = DeviceHub.Instance.Gears.FirstOrDefault();
            key = gear?.Key ?? "profiles";
        }
        _selected = key;
        AppSettings.Current.LastPage = key;
        AppSettings.Save();
        try
        {
            if (key == "profiles") ProfilesPage.Build(_page);
            else if (key == "settings") SettingsPage.Build(_page);
            else gear!.BuildPage(_page);
        }
        catch (Exception e)
        {
            Log.Error("page " + key, e);
            _page.Note(_page.Root, Lang.T("Essa página deu erro: ", "This page failed: ") + e.Message, true);
        }
        PageHost.Content = _page.Root;
        PageScroll.ScrollToTop();
        foreach (var r in Nav.Children.OfType<RadioButton>().Concat(NavBottom.Children.OfType<RadioButton>()))
            r.IsChecked = (string)r.Tag == key;
    }

    /// <summary>The pill at rest: how many devices and which profile.</summary>
    private void IdleIsland(bool animate = true)
    {
        int n = DeviceHub.Instance.Gears.Count;
        var active = ProfileStore.All.FirstOrDefault(p => p.Name == ProfileStore.Active) ?? ProfileStore.Default;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Fill = n > 0 ? Page.B("Good") : Page.B("Ink3"), Margin = new Thickness(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock
        {
            Text = Lang.T($"{n} aparelho{(n == 1 ? "" : "s")} · perfil {ProfileStore.Display(active)}", $"{n} device{(n == 1 ? "" : "s")} · {ProfileStore.Display(active)} profile"),
            Foreground = Page.B("Ink"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
        });
        double w = Math.Max(200, Measure(row) + 44);
        Isle.Morph(w, 36, row, animate);
    }

    /// <summary>A message on the pill for a moment, then back to rest.</summary>
    public void Say(string text, bool ok)
    {
        var t = new TextBlock { Text = text, Foreground = ok ? Page.B("Ink") : Page.B("Warn"), FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 560 };
        double w = Math.Min(620, Math.Max(220, Measure(t) + 56));
        Isle.Morph(w, 44, t);
        _islandBack.Stop();
        _islandBack.Start();
    }

    private static double Measure(UIElement e)
    {
        e.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return e.DesiredSize.Width;
    }

    /// <summary>Renders the window to a PNG (the --screenshot option, to check the layout without looking).</summary>
    public void SaveScreenshot(string path)
    {
        Isle.Snap();
        UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(this);
        var bmp = new RenderTargetBitmap((int)(ActualWidth * dpi.DpiScaleX), (int)(ActualHeight * dpi.DpiScaleY), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        bmp.Render(this);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
