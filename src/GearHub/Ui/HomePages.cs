using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GearHub.Devices;
using GearHub.Devices.Razer;
using GearHub.Lighting;
using GearHub.Profiles;

namespace GearHub.Ui;

/// <summary>The first page: every device as a card (status and battery), the profiles one click away.</summary>
internal static class HomePage
{
    public static void Build(Page p)
    {
        var gears = DeviceHub.Instance.Gears;
        p.Header(Lang.T("Visão geral", "Overview"), Lang.T($"{gears.Count} aparelho(s) encontrados. Clique num cartão para abrir.", $"{gears.Count} device(s) found. Click a card to open it."));

        var profiles = p.Section(Lang.T("Perfis", "Profiles"), ProfileStore.All.Any(x => x.Devices.Count > 0) ? null
            : Lang.T("Nenhum perfil guardado ainda: crie um na página Perfis.", "No saved profile yet: make one on the Profiles page."));
        var row = new WrapPanel();
        foreach (var pr in ProfileStore.All.Where(x => x.Devices.Count > 0))
        {
            var prof = pr;
            bool active = prof.Name == ProfileStore.Active;
            var b = p.Button((active ? "● " : "") + ProfileStore.Display(prof), () =>
            {
                p.Say(Lang.T("Aplicando...", "Applying..."));
                p.Work(() => ProfileStore.Apply(prof, "manual").Wait(), () => { p.Say(Lang.T("Perfil ", "Profile ") + ProfileStore.Display(prof)); App.Window?.Select("home"); });
            }, primary: active);
            b.Margin = new Thickness(0, 0, 8, 8);
            row.Children.Add(b);
        }
        profiles.Children.Add(row);

        var cards = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        foreach (var g in gears)
        {
            var gear = g;
            var stack = new StackPanel();
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(Icons.Make(Icons.For(g.Kind), size: 26));
            head.Children.Add(new TextBlock { Text = g.Brand, Style = Page.S("Small"), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            stack.Children.Add(head);
            stack.Children.Add(new TextBlock { Text = g.Name, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Page.B("Ink"), Margin = new Thickness(0, 12, 0, 2), TextTrimming = TextTrimming.CharacterEllipsis });
            stack.Children.Add(new TextBlock { Text = g.Status == "" ? " " : g.Status, Style = Page.S("Small"), TextTrimming = TextTrimming.CharacterEllipsis });
            if (g.Battery is int pct)
            {
                var track = new Grid { Height = 6, Margin = new Thickness(0, 10, 0, 0) };
                track.Children.Add(new Border { Background = Page.B("Line"), CornerRadius = new CornerRadius(3) });
                track.Children.Add(new Border
                {
                    Background = pct <= 15 ? Page.B("Bad") : pct <= 30 ? Page.B("Warn") : Page.B("Good"),
                    CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left, Width = 196 * pct / 100.0,
                });
                stack.Children.Add(track);
            }
            var card = new Border
            {
                Width = 236, Height = 148, Margin = new Thickness(0, 0, 14, 14), Padding = new Thickness(20, 18, 20, 16),
                CornerRadius = new CornerRadius(24), Background = Page.B("Surface"), BorderBrush = Page.B("Line"), BorderThickness = new Thickness(1),
                Child = stack, Cursor = Cursors.Hand,
            };
            card.MouseEnter += (_, _) => card.Background = Page.B("Surface2");
            card.MouseLeave += (_, _) => card.Background = Page.B("Surface");
            card.MouseLeftButtonUp += (_, _) => App.Window?.Select(gear.Key);
            cards.Children.Add(card);
        }
        p.Root.Children.Add(cards);
    }
}

/// <summary>One light effect for everything: the software targets and, if asked, the keyboard's closest own effect.</summary>
internal static class LightsPage
{
    public static void Build(Page p)
    {
        var targets = LightEngine.Targets().ToList();
        var keyboard = DeviceHub.Instance.Gears.OfType<RazerKeyboard>().FirstOrDefault();
        p.Header(Lang.T("Luzes", "Lights"), Lang.T("Um efeito para todos os aparelhos de uma vez. Cada aparelho também tem o seu na própria página.",
            "One effect for every device at once. Each device also has its own on its page."));
        if (targets.Count == 0 && keyboard == null) { p.Note(p.Root, Lang.T("Nenhum aparelho com luz que o Gear Hub controle.", "No device with lights Gear Hub can drive."), true); return; }
        var who = p.Section(Lang.T("Em quais", "Which ones"));
        foreach (var t in targets) p.Note(who, "• " + t.Name);
        bool withKeyboard = keyboard != null;
        if (keyboard != null)
            p.Row(who, keyboard.Name, Lang.T("Ele roda o efeito parecido que o próprio teclado tem (onda, respiração, estrelas, fogo...).", "It runs the closest effect the keyboard itself has (wave, breathing, starlight, fire...)."),
                p.Toggle(true, v => withKeyboard = v));
        EffectsPanel.Build(p, targets, Lang.T("Sincronizado: todos andam no mesmo tempo.", "In sync: they all move together."),
            extra: spec => withKeyboard && keyboard != null && !keyboard.WriteChromaFor(spec) ? Lang.T("O teclado recusou o efeito", "The keyboard refused the effect") : null);
    }
}
