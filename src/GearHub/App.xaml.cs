using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using GearHub.Devices;
using GearHub.Profiles;
using GearHub.Ui;
using Forms = System.Windows.Forms;

namespace GearHub;

public partial class App : Application
{
    public static string Version { get; } = (Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.0.0").Split('+')[0];
    internal static MainWindow? Window { get; private set; }
    public static string LastForeground { get; private set; } = "";
    private static Forms.NotifyIcon? _tray;
    private static ForegroundWatcher? _foreground;
    private static DispatcherTimer? _updateTimer;

    [STAThread]
    public static int Main(string[] args)
    {
        string first = args.Length > 0 ? args[0] : "";
        if (first == Updater.ApplyArg && args.Length >= 3) return Updater.RunExecutor(args[1], int.TryParse(args[2], out int pid) ? pid : 0);
        if (first == "--ping") return 0;
        if (first is "--quit" or "--sair")
        {
            SingleInstance.SignalQuit();
            for (int i = 0; i < 50 && SingleInstance.OtherRunning(); i++) Thread.Sleep(100);
            return 0;
        }
        AppSettings.Load();
        if (first == "--self-test") return SelfTest.Run(args.Length > 1 ? args[1] : null);
        if (first == "--probe") return SelfTest.Probe(args.Length > 1 ? args[1] : null);
        if (first == "--listen" && args.Length > 2) return SelfTest.Listen(args[1], double.TryParse(args[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double gap) ? gap : 3, args.Length > 3 ? args[3] : null);
        if (first == "--write-test" && args.Length > 1) return SelfTest.WriteTest(args[1], args.Length > 2 ? args[2] : null);

        bool screenshot = first == "--screenshot";
        if (!screenshot && !SingleInstance.Claim())
        {
            // at logon the Desktop Glow task and the Run entry both start a copy with --tray: the late one leaves
            // quietly; only a copy the person opened (no --tray) brings the window up
            if (first != "--tray") SingleInstance.SignalShow();
            return 0;
        }

        ProfileStore.Load();
        GearHub.Startup.Sync();
        var app = new App();
        app.InitializeComponent();
        app.DispatcherUnhandledException += (_, e) => { Log.Error("ui", e.Exception); e.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("fatal: " + e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Error("task", e.Exception); e.SetObserved(); };

        if (screenshot) return app.Screenshot(args.Length > 1 ? args[1] : "screenshot.png", args.Length > 2 ? args[2] : "", args.Length > 3 && double.TryParse(args[3], out double sc) ? sc : 0);

        Log.Write($"start {Version} ({string.Join(' ', args)}) from {AppContext.BaseDirectory}");
        if (first == Updater.AfterArg) Updater.MarkOpened();
        DeviceHub.Instance.Start();
        Bridge.ShowWindow = page => app.Dispatcher.BeginInvoke(() =>
        {
            ShowWindow();
            if (!string.IsNullOrEmpty(page)) Window?.Select(page);
        });
        Bridge.Start();
        SingleInstance.Listen(() => app.Dispatcher.BeginInvoke(ShowWindow), () => app.Dispatcher.BeginInvoke(Quit));
        app.Startup += (_, _) =>
        {
            CreateTray();
            Hotkeys.Notify = text => Current.Dispatcher.BeginInvoke(() => Window?.Say(text, true));
            Hotkeys.Start();
            _foreground = new ForegroundWatcher();
            _foreground.Changed += exe =>
            {
                LastForeground = exe;
                ProfileStore.OnForeground(exe);
            };
            if (first != "--tray") ShowWindow();
            ScheduleUpdates();
        };
        app.SessionEnding += (_, _) => GiveBack("logoff");
        int code = app.Run();
        EventChannel.Stop();
        Hotkeys.Stop();
        GiveBack("quit");
        _tray?.Dispose();
        _foreground?.Dispose();
        DeviceHub.Instance.Dispose();
        SingleInstance.Release();
        return code;
    }

    private static int _gaveBack;

    /// <summary>Lights let go and every device back to what the vendor apps had set (once, when the app leaves).</summary>
    private static void GiveBack(string why)
    {
        if (Interlocked.Exchange(ref _gaveBack, 1) == 1) return;
        Lighting.LightEngine.Instance.Stop();
        if (!AppSettings.Current.RestoreOnExit) { Log.Write($"{why}: restore on exit is off"); return; }
        DeviceHub.Instance.RestoreAll();
    }

    private int Screenshot(string path, string page, double scroll)
    {
        int code = 1;
        Startup += (_, _) =>
        {
            Task.Run(() => DeviceHub.Instance.Scan(force: true)).Wait();
            // drawn off screen, without taking focus: the picture comes from the visual tree, not from the monitor
            if (page != "") AppSettings.Current.LastPage = page;
            var w = new MainWindow { WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0, ShowActivated = false, ShowInTaskbar = false };
            w.Show();
            // let the page's background reads come back before drawing
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                try
                {
                    if (scroll > 0) { w.ScrollPage(scroll); w.UpdateLayout(); }
                    w.SaveScreenshot(path);
                    code = 0;
                }
                catch (Exception e) { Log.Error("screenshot", e); }
                Shutdown();
            };
            t.Start();
        };
        Run();
        DeviceHub.Instance.Dispose();
        return code;
    }

    public static void ShowWindow()
    {
        if (Window == null)
        {
            Window = new MainWindow();
            Window.Closed += (_, _) =>
            {
                Window = null;
                // give back what the window used: the app lives in the tray with only the device threads.
                // Aggressive collection decommits the GC's free regions; then the working set is trimmed.
                var later = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                later.Tick += (_, _) =>
                {
                    later.Stop();
                    if (Window != null) return;
                    System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                    GC.Collect(2, GCCollectionMode.Aggressive, true, true);
                    GC.WaitForPendingFinalizers();
                    GC.Collect(2, GCCollectionMode.Aggressive, true, true);
                    TrimWorkingSet();
                };
                later.Start();
            };
            Window.Show();
        }
        if (Window.WindowState == WindowState.Minimized) Window.WindowState = WindowState.Normal;
        Window.Activate();
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);

    private static void TrimWorkingSet()
    {
        try { SetProcessWorkingSetSize(System.Diagnostics.Process.GetCurrentProcess().Handle, new IntPtr(-1), new IntPtr(-1)); }
        catch (Exception) { }
    }

    public static void Quit()
    {
        Window?.Close();
        Current.Shutdown();
    }

    private static void CreateTray()
    {
        var icon = new System.Drawing.Icon(GetResourceStream(new Uri("pack://application:,,,/Assets/icone.ico")).Stream);
        _tray = new Forms.NotifyIcon { Icon = icon, Text = Lang.T("Central de Periféricos", "Gear Hub"), Visible = true };
        _tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) ShowWindow(); };
        var menu = new Forms.ContextMenuStrip();
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            menu.Items.Add(Lang.T("Abrir", "Open"), null, (_, _) => ShowWindow());
            var profiles = new Forms.ToolStripMenuItem(Lang.T("Perfil", "Profile"));
            foreach (var p in ProfileStore.All)
            {
                var pr = p;
                var item = new Forms.ToolStripMenuItem(ProfileStore.Display(pr)) { Checked = pr.Name == ProfileStore.Active, Enabled = pr.Devices.Count > 0 };
                item.Click += (_, _) => _ = ProfileStore.Apply(pr, "tray");
                profiles.DropDownItems.Add(item);
            }
            menu.Items.Add(profiles);
            var razer = DeviceHub.Instance.Gears.OfType<Devices.Razer.RazerKeyboard>().FirstOrDefault();
            if (razer != null)
            {
                var snap = new Forms.ToolStripMenuItem("Snap Tap") { Checked = razer.SnapOn == true };
                snap.Click += (_, _) => Hotkeys.Run(Hotkeys.SnapTap);
                menu.Items.Add(snap);
            }
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(Lang.T("Sair", "Quit"), null, (_, _) => Quit());
        };
        menu.Items.Add("...");
        _tray.ContextMenuStrip = menu;
    }

    private static void ScheduleUpdates()
    {
        void Check()
        {
            if (!AppSettings.Current.CheckUpdates) return;
            Task.Run(() =>
            {
                var r = Updater.Check(false);
                if (r is { IsNewer: true })
                    Current.Dispatcher.BeginInvoke(() => _tray?.ShowBalloonTip(6000, Lang.T("Central de Periféricos", "Gear Hub"), Updater.LastText, Forms.ToolTipIcon.Info));
            });
        }
        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(24) };
        _updateTimer.Tick += (_, _) => Check();
        _updateTimer.Start();
        var once = new DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
        once.Tick += (_, _) => { once.Stop(); Check(); };
        once.Start();
    }
}
