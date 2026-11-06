using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using GearHub.Ui;
using Windows.Devices.Enumeration;
using Windows.Devices.Lights;
using Windows.Gaming.Input;

namespace GearHub.Devices.Lighting;

/// <summary>
/// Windows Dynamic Lighting (LampArray): any device that offers it, of any brand. Windows hands the lamps to the app in
/// front (or the background controller chosen in Settings), so a colour set here holds while this window is in front
/// and may go back when another app takes over. On this PC on 06/11/2026 the G733 and the G502 were there
/// (virtual lamp arrays made by Logitech's own lamparray service).
/// </summary>
internal sealed class DynamicLights : Gear
{
    private List<(string Name, LampArray Lamps)> _arrays = new();

    public override string Key => "dynamic-lighting";
    public override string Name => Lang.T("Iluminação Dinâmica", "Dynamic Lighting");
    public override GearKind Kind => GearKind.Light;
    public override string Brand => "Windows";

    public static async Task<DynamicLights?> FindAsync()
    {
        try
        {
            // ConfigureAwait(false): the hub waits for this synchronously, so the continuation must not need the
            // caller's thread (on the interface thread that was a deadlock)
            var infos = await DeviceInformation.FindAllAsync(LampArray.GetDeviceSelector()).AsTask().ConfigureAwait(false);
            var g = new DynamicLights();
            foreach (var d in infos)
            {
                var la = await LampArray.FromIdAsync(d.Id).AsTask().ConfigureAwait(false);
                if (la != null) g._arrays.Add((d.Name, la));
            }
            if (g._arrays.Count == 0) return null;
            g.Status = Lang.T($"{g._arrays.Count} aparelho(s)", $"{g._arrays.Count} device(s)");
            return g;
        }
        catch (Exception e)
        {
            Log.Error("lamparray", e);
            return null;
        }
    }

    public override void BuildPage(Page p)
    {
        p.Header(Name, Lang.T("Aparelhos de qualquer marca que aceitam a Iluminação Dinâmica do Windows. O Windows só deixa mudar a luz enquanto esta janela está na frente, a não ser que este app seja o controlador de fundo em Configurações > Personalização > Iluminação dinâmica.",
            "Devices of any brand that take Windows Dynamic Lighting. Windows only lets an app change them while its window is in front, unless the app is the background controller in Settings > Personalization > Dynamic Lighting."));
        foreach (var (name, la) in _arrays)
        {
            var lamps = la;
            var sec = p.Section(name, $"{lamps.LampCount} " + Lang.T("luzes", "lamps") + " · " + (lamps.IsAvailable ? Lang.T("disponível agora", "available now") : Lang.T("outro app está mandando agora", "another app is driving it now")));
            p.Row(sec, Lang.T("Cor", "Colour"), null, p.ColorPick(Colors.White, c =>
            {
                try
                {
                    lamps.SetColor(Windows.UI.Color.FromArgb(255, c.R, c.G, c.B));
                    p.Say(lamps.IsAvailable ? Lang.T("Luz trocada", "Light changed") : Lang.T("O Windows não deu a luz para este app agora", "Windows did not hand the lights to this app right now"), lamps.IsAvailable);
                }
                catch (Exception e) { p.Say(e.Message, false); }
            }));
            p.Row(sec, Lang.T("Brilho", "Brightness"), null, p.Slider(0, 100, 5, Math.Round(lamps.BrightnessLevel * 100), v => $"{v:0}%", v =>
            {
                try { lamps.BrightnessLevel = v / 100.0; p.Say(Lang.T("Brilho ", "Brightness ") + $"{v:0}%"); }
                catch (Exception e) { p.Say(e.Message, false); }
            }));
        }
    }
}

/// <summary>Game controllers Windows sees (Xbox and others): name and battery.</summary>
internal sealed class GameControllers : Gear
{
    public override string Key => "controllers";
    public override string Name => Lang.T("Controles", "Controllers");
    public override GearKind Kind => GearKind.Controller;
    public override string Brand => "Windows";

    public static GameControllers? Find() => RawGameController.RawGameControllers.Count > 0 ? new GameControllers() : null;

    public static List<(string Name, string Battery)> List()
    {
        var list = new List<(string, string)>();
        foreach (var c in RawGameController.RawGameControllers)
        {
            string battery = "";
            try
            {
                var r = c.TryGetBatteryReport();
                if (r?.RemainingCapacityInMilliwattHours is int rem && r.FullChargeCapacityInMilliwattHours is int full && full > 0)
                    battery = $"{100 * rem / full}%" + (r.Status == Windows.System.Power.BatteryStatus.Charging ? Lang.T(" carregando", " charging") : "");
                else if (r?.Status == Windows.System.Power.BatteryStatus.NotPresent) battery = Lang.T("com fio", "wired");
            }
            catch (Exception) { }
            string name = c.DisplayName;
            if (string.IsNullOrWhiteSpace(name)) name = c.HardwareVendorId == 0x045E ? Lang.T("Controle Xbox", "Xbox controller") : Lang.T("Controle", "Controller");
            list.Add((name, battery));
        }
        return list;
    }

    public override void Poll()
    {
        var l = List();
        string s = string.Join(", ", l.Select(x => x.Battery != "" ? x.Battery : x.Name));
        if (s != Status) { Status = s; RaiseChanged(); }
    }

    public override void BuildPage(Page p)
    {
        p.Header(Name, Lang.T("Controles que o Windows enxerga, com a bateria quando o controle informa.", "Controllers Windows sees, with the battery when the controller reports it."));
        var sec = p.Section(Lang.T("Conectados", "Connected"));
        foreach (var (name, battery) in List())
            p.Row(sec, name, null, new System.Windows.Controls.TextBlock { Text = battery == "" ? Lang.T("sem leitura", "no reading") : battery, Foreground = Page.B("Ink2"), FontSize = 14 });
    }
}
