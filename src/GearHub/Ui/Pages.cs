using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GearHub.Devices;
using GearHub.Profiles;

namespace GearHub.Ui;

/// <summary>Profiles: a snapshot of every device's settings, optionally tied to the games that switch to it.</summary>
internal static class ProfilesPage
{
    public static void Build(Page p)
    {
        p.Header(Lang.T("Perfis", "Profiles"), Lang.T("Um perfil guarda como cada aparelho está agora. Ligue programas a ele (cs2.exe, por exemplo) e ele entra sozinho quando o jogo vem para a frente; ao sair, volta o Padrão.",
            "A profile keeps how every device is set right now. Tie apps to it (cs2.exe, say) and it kicks in when the game comes to the front; leaving it brings Default back."));
        var top = p.Section(Lang.T("Troca automática", "Automatic switching"));
        p.Row(top, Lang.T("Trocar pelo programa em foco", "Switch by the app in front"), Lang.T("Sem abrir nada dentro do jogo: só o nome do .exe, como a ilha faz.", "Nothing is opened inside the game: only the .exe name, like the ilha does."),
            p.Toggle(AppSettings.Current.AutoProfiles, v => { AppSettings.Current.AutoProfiles = v; AppSettings.Save(); p.Say(v ? Lang.T("Troca automática ligada", "Automatic switching on") : Lang.T("Troca automática desligada", "Automatic switching off")); }));
        if (App.LastForeground != "")
            p.Note(top, Lang.T("Último programa em foco: ", "Last app in front: ") + App.LastForeground);

        foreach (var profile in ProfileStore.All.ToList())
        {
            var pr = profile;
            bool active = pr.Name == ProfileStore.Active;
            string saved = pr.Devices.Count == 0 ? Lang.T("Nada guardado ainda.", "Nothing saved yet.")
                : Lang.T($"{pr.Devices.Count} aparelho(s), guardado em {pr.SavedAt:dd/MM/yyyy HH:mm}.", $"{pr.Devices.Count} device(s), saved {pr.SavedAt:yyyy-MM-dd HH:mm}.");
            var sec = p.Section(ProfileStore.Display(pr) + (active ? Lang.T("  · ativo", "  · active") : ""), saved);
            if (!pr.IsDefault)
            {
                var apps = new TextBox { Width = 360, Text = string.Join(", ", pr.Apps) };
                void SaveApps()
                {
                    pr.Apps = apps.Text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(a => a.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? a : a + ".exe").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    ProfileStore.Save();
                    p.Say(Lang.T("Programas guardados", "Apps saved"));
                }
                apps.KeyDown += (_, e) => { if (e.Key == Key.Enter) SaveApps(); };
                apps.LostFocus += (_, _) => { if (apps.Text != string.Join(", ", pr.Apps)) SaveApps(); };
                var line = p.Line(apps);
                if (App.LastForeground != "")
                {
                    var last = p.Button(Lang.T("+ último em foco", "+ last in front"), () =>
                    {
                        if (!pr.Apps.Contains(App.LastForeground, StringComparer.OrdinalIgnoreCase)) pr.Apps.Add(App.LastForeground);
                        apps.Text = string.Join(", ", pr.Apps);
                        ProfileStore.Save();
                        p.Say(App.LastForeground + Lang.T(" ligado ao perfil", " tied to the profile"));
                    });
                    last.Margin = new Thickness(8, 0, 0, 0);
                    line.Children.Add(last);
                }
                p.Row(sec, Lang.T("Programas", "Apps"), Lang.T("Nomes dos .exe, separados por vírgula.", ".exe names, comma separated."), line);
            }
            var buttons = p.Line(
                p.Button(Lang.T("Guardar como está agora", "Save current state"), () =>
                {
                    p.Say(Lang.T("Lendo os aparelhos...", "Reading the devices..."));
                    p.Work(() => ProfileStore.Capture(pr), n => { p.Say(Lang.T($"{n} aparelho(s) guardado(s)", $"{n} device(s) saved")); }, Lang.T("Não deu para ler tudo", "Could not read everything"));
                }, primary: true),
                p.Button(Lang.T("Aplicar agora", "Apply now"), () =>
                {
                    if (pr.Devices.Count == 0) { p.Say(Lang.T("Esse perfil ainda não tem nada guardado", "This profile has nothing saved yet"), false); return; }
                    p.Say(Lang.T("Aplicando...", "Applying..."));
                    p.Work(() => ProfileStore.Apply(pr, "manual").Wait(), () => p.Say(Lang.T("Perfil aplicado: ", "Profile applied: ") + ProfileStore.Display(pr)));
                }));
            if (!pr.IsDefault)
                buttons.Children.Add(p.Button(Lang.T("Apagar", "Delete"), () =>
                {
                    ProfileStore.All.Remove(pr);
                    ProfileStore.Save();
                    p.Say(Lang.T("Perfil apagado", "Profile deleted"));
                    App.Window?.Select("profiles");
                }));
            buttons.Margin = new Thickness(0, 8, 0, 0);
            sec.Children.Add(buttons);
        }

        var add = p.Section(Lang.T("Novo perfil", "New profile"), Lang.T("Ele nasce com o estado atual de todos os aparelhos.", "It starts with every device's current state."));
        var name = new TextBox { Width = 280 };
        void Create()
        {
            string n = name.Text.Trim();
            if (n == "" || n == ProfileStore.DefaultName || ProfileStore.All.Any(x => string.Equals(x.Name, n, StringComparison.OrdinalIgnoreCase)))
            {
                p.Say(Lang.T("Escolha outro nome", "Pick another name"), false);
                return;
            }
            var pr = new Profile { Name = n };
            ProfileStore.All.Add(pr);
            p.Say(Lang.T("Lendo os aparelhos...", "Reading the devices..."));
            p.Work(() => ProfileStore.Capture(pr), _ => App.Window?.Select("profiles"));
        }
        name.KeyDown += (_, e) => { if (e.Key == Key.Enter) Create(); };
        var b = p.Button(Lang.T("Criar", "Create"), Create, primary: true);
        b.Margin = new Thickness(8, 0, 0, 0);
        p.Row(add, Lang.T("Nome", "Name"), null, p.Line(name, b));
    }
}

/// <summary>The app's own settings.</summary>
internal static class SettingsPage
{
    public static void Build(Page p)
    {
        p.Header(Lang.T("Ajustes", "Settings"), Lang.T($"Central de Periféricos {App.Version}.", $"Gear Hub {App.Version}."));
        var sec = p.Section(Lang.T("Geral", "General"));
        p.Row(sec, Lang.T("Abrir com o Windows", "Start with Windows"),
            Paths.IsInstalledCopy ? Lang.T("Abre escondido na bandeja.", "Opens hidden in the tray.") : Lang.T("Vale para a cópia instalada (pelo Desktop Glow ou pelo instalar.ps1).", "Applies to the installed copy (Desktop Glow or instalar.ps1)."),
            p.Toggle(AppSettings.Current.StartWithWindows, v =>
            {
                AppSettings.Current.StartWithWindows = v;
                AppSettings.Save();
                Startup.Sync();
                p.Say(v ? Lang.T("Abre com o Windows", "Starts with Windows") : Lang.T("Não abre mais com o Windows", "No longer starts with Windows"));
            }));
        string[] langs = { "auto", "pt", "en" };
        p.Row(sec, Lang.T("Idioma", "Language"), Lang.T("Muda ao reabrir a janela.", "Changes when the window reopens."),
            p.Segments(new[] { Lang.T("Do Windows", "Windows"), "Português", "English" }, Array.IndexOf(langs, AppSettings.Current.Language), i =>
            {
                AppSettings.Current.Language = langs[i];
                AppSettings.Save();
                Lang.Apply(langs[i]);
                p.Say(Lang.T("Idioma trocado", "Language changed"));
            }));

        var up = p.Section(Lang.T("Atualização", "Updates"), Lang.T("Pelas releases do GitHub (FelipeWASD83/gear-hub), com o SHA-256 conferido e volta sozinha se a versão nova não abrir.",
            "From the GitHub releases (FelipeWASD83/gear-hub), SHA-256 checked, and it rolls back if the new version does not open."));
        p.Row(up, Lang.T("Procurar sozinho", "Check by itself"), Lang.T("Uma vez por dia, só avisa.", "Once a day, it only tells you."),
            p.Toggle(AppSettings.Current.CheckUpdates, v => { AppSettings.Current.CheckUpdates = v; AppSettings.Save(); }));
        var status = p.Note(up, Updater.LastText);
        Button? install = null;
        var check = p.Button(Lang.T("Procurar agora", "Check now"), () =>
        {
            status.Text = Lang.T("Procurando...", "Checking...");
            p.Work(() => Updater.Check(force: true), r =>
            {
                status.Text = Updater.LastText;
                if (install != null) install.Visibility = r is { IsNewer: true } ? Visibility.Visible : Visibility.Collapsed;
            }, Lang.T("Não deu para falar com o GitHub", "Could not reach GitHub"));
        });
        install = p.Button(Lang.T("Instalar", "Install"), () =>
        {
            status.Text = Lang.T("Baixando...", "Downloading...");
            p.Work(() => Updater.Install(), err => { status.Text = err ?? Lang.T("Reabrindo na versão nova...", "Reopening on the new version..."); if (err == null) App.Quit(); });
        }, primary: true);
        install.Visibility = Updater.Last is { IsNewer: true } ? Visibility.Visible : Visibility.Collapsed;
        up.Children.Add(p.Line(check, install));

        var who = p.Section(Lang.T("Quem mais está mexendo nos aparelhos", "Who else is driving the devices"), Lang.T("Os apps das marcas podem ficar abertos, mas regravam o que é deles quando trocam de perfil.",
            "The vendor apps may stay open, but they write their own settings back when they switch profile."));
        void Vendor(string name, bool running) => p.Row(who, name, null, new TextBlock { Text = running ? Lang.T("aberto", "running") : Lang.T("fechado", "closed"), Foreground = running ? Page.B("Warn") : Page.B("Ink3"), FontSize = 14 });
        Vendor("Razer Synapse", VendorApps.Synapse);
        Vendor("Logitech G HUB", VendorApps.GHub);
        Vendor("Corsair iCUE", VendorApps.Icue);
        Vendor("DSX", VendorApps.Dsx);

        var files = p.Section(Lang.T("Arquivos", "Files"));
        p.Row(files, Lang.T("Ajustes, perfis e log", "Settings, profiles and log"), Paths.Data, p.Button(Lang.T("Abrir pasta", "Open folder"), () =>
        {
            try { System.Diagnostics.Process.Start("explorer.exe", Paths.Data); } catch (Exception) { }
        }));
    }
}
