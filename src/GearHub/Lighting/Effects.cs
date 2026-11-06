using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace GearHub.Lighting;

/// <summary>One light setting: which effect, its two colours, speed (1 = normal) and brightness (0..1).</summary>
internal sealed class EffectSpec
{
    [JsonPropertyName("effect")] public string Effect { get; set; } = Effects.Static;
    [JsonPropertyName("a")] public string A { get; set; } = "#FF3B30";
    [JsonPropertyName("b")] public string B { get; set; } = "#007AFF";
    [JsonPropertyName("speed")] public double Speed { get; set; } = 1;
    [JsonPropertyName("brightness")] public double Brightness { get; set; } = 1;

    [JsonIgnore] public Color ColorA => Ui.Page.TryParse(A, out var c) ? c : Colors.White;
    [JsonIgnore] public Color ColorB => Ui.Page.TryParse(B, out var c) ? c : Colors.Blue;

    public EffectSpec Clone() => (EffectSpec)MemberwiseClone();

    public bool SameAs(EffectSpec o) => o.Effect == Effect && o.A == A && o.B == B && Math.Abs(o.Speed - Speed) < 1e-6 && Math.Abs(o.Brightness - Brightness) < 1e-6;
}

/// <summary>
/// The software effects, each a function of LED position (0..1, left to right) and time, so a strip of 20 RAM LEDs
/// and a headset with 2 zones run the same effect in step. Twinkle and fire keep a little state per LED.
/// </summary>
internal static class Effects
{
    public const string Off = "off", Static = "static", Gradient = "gradient", Cycle = "cycle", Rainbow = "rainbow", Wave = "wave",
        Breathing = "breathing", Comet = "comet", Twinkle = "twinkle", Fire = "fire", Music = "music";

    /// <summary>Id, Portuguese, English, uses colour A, uses colour B, moves (needs frames).</summary>
    public static readonly (string Id, string Pt, string En, bool A, bool B, bool Moves)[] All =
    {
        (Static, "Fixa", "Fixed", true, false, false),
        (Gradient, "Degradê", "Gradient", true, true, false),
        (Cycle, "Troca de cor", "Colour cycle", false, false, true),
        (Rainbow, "Arco-íris", "Rainbow", false, false, true),
        (Wave, "Onda", "Wave", true, true, true),
        (Breathing, "Respiração", "Breathing", true, true, true),
        (Comet, "Cometa", "Comet", true, true, true),
        (Twinkle, "Estrelas", "Twinkle", true, true, true),
        (Fire, "Fogo", "Fire", false, false, true),
        (Music, "Música", "Music", true, true, true),
    };

    public static string Name(string id)
    {
        foreach (var e in All) if (e.Id == id) return Lang.T(e.Pt, e.En);
        return id == Off ? Lang.T("Do aparelho", "Device's own") : id;
    }

    public static bool Moves(string id)
    {
        foreach (var e in All) if (e.Id == id) return e.Moves;
        return false;
    }

    public static Color Hue(double h, double s = 1, double v = 1)
    {
        h = (h % 1 + 1) % 1;
        double r = Math.Clamp(Math.Abs(h * 6 - 3) - 1, 0, 1);
        double g = Math.Clamp(2 - Math.Abs(h * 6 - 2), 0, 1);
        double b = Math.Clamp(2 - Math.Abs(h * 6 - 4), 0, 1);
        r = v * (1 - s + s * r); g = v * (1 - s + s * g); b = v * (1 - s + s * b);
        return Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }

    public static Color Mix(Color a, Color b, double k)
    {
        k = Math.Clamp(k, 0, 1);
        return Color.FromRgb((byte)(a.R + (b.R - a.R) * k), (byte)(a.G + (b.G - a.G) * k), (byte)(a.B + (b.B - a.B) * k));
    }

    public static Color Scale(Color c, double k)
    {
        k = Math.Clamp(k, 0, 1);
        return Color.FromRgb((byte)(c.R * k), (byte)(c.G * k), (byte)(c.B * k));
    }

    /// <summary>Draws one frame of an effect for a set of LEDs.</summary>
    internal sealed class Renderer
    {
        private readonly double[] _x;
        private readonly double[] _spark;
        private readonly double[] _heat;
        private readonly Random _rng = new();
        private double _lastT;

        public Renderer(double[] positions)
        {
            _x = positions;
            _spark = new double[positions.Length];
            _heat = new double[positions.Length];
        }

        public Color[] Frame(EffectSpec s, double t, AudioLevel? audio)
        {
            var a = s.ColorA;
            var b = s.ColorB;
            double sp = Math.Clamp(s.Speed, 0.1, 5);
            double dt = Math.Clamp(t - _lastT, 0, 0.25);
            _lastT = t;
            var o = new Color[_x.Length];
            for (int i = 0; i < _x.Length; i++)
            {
                double x = _x[i];
                Color c = s.Effect switch
                {
                    Static => a,
                    Gradient => Mix(a, b, x),
                    Cycle => Hue(t * 0.08 * sp),
                    Rainbow => Hue(x * 0.75 - t * 0.12 * sp),
                    // two colours chasing each other along the strip
                    Wave => Mix(a, b, 0.5 + 0.5 * Math.Sin((x * 2 - t * 0.6 * sp) * Math.PI * 2)),
                    // never off: it eases between the two colours and dips to 35%
                    Breathing => Scale(Mix(a, b, 0.5 - 0.5 * Math.Cos(t * 0.35 * sp * Math.PI)), 0.35 + 0.65 * (0.5 - 0.5 * Math.Cos(t * 0.7 * sp * Math.PI * 2))),
                    Comet => CometAt(a, b, x, t * 0.45 * sp),
                    Twinkle => TwinkleAt(i, a, b, dt * sp),
                    Fire => FireAt(i, dt * sp),
                    Music => MusicAt(a, b, x, audio),
                    _ => a,
                };
                o[i] = Scale(c, s.Brightness);
            }
            return o;
        }

        private static Color CometAt(Color head, Color tail, double x, double phase)
        {
            double p = phase % 1.3 - 0.15;
            double d = p - x;
            if (d < 0) return Scale(tail, 0.06);
            double k = Math.Exp(-d * 9);
            return d < 0.06 ? Mix(head, Colors.White, 0.35) : Mix(Scale(tail, 0.06), Mix(tail, head, k), k * 1.2);
        }

        private Color TwinkleAt(int i, Color baseColor, Color star, double dt)
        {
            _spark[i] = Math.Max(0, _spark[i] - dt * 1.6);
            if (_rng.NextDouble() < dt * 0.9) _spark[i] = 1;
            return Mix(Scale(baseColor, 0.35), star, _spark[i]);
        }

        private Color FireAt(int i, double dt)
        {
            double target = 0.55 + 0.45 * _rng.NextDouble();
            _heat[i] += (target - _heat[i]) * Math.Min(1, dt * 9);
            double h = _heat[i];
            // dark red to orange to yellow
            return Color.FromRgb(255, (byte)(40 + 150 * h * h), (byte)(h > 0.9 ? 60 * (h - 0.9) * 10 : 0));
        }

        private static Color MusicAt(Color low, Color high, double x, AudioLevel? audio)
        {
            double level = audio?.Level ?? 0, bass = audio?.Bass ?? 0;
            // a bar that grows from the left with the volume, coloured by the bass
            double lit = Math.Clamp(level * 1.1 - x, 0, 0.15) / 0.15;
            var c = Mix(low, high, bass);
            return Scale(c, 0.08 + 0.92 * Math.Max(lit, bass * 0.5));
        }
    }
}

/// <summary>Something whose LEDs the engine paints frame by frame.</summary>
internal interface ILightTarget
{
    string Key { get; }
    string Name { get; }
    /// <summary>LED positions 0..1 from left to right (one entry per LED or zone).</summary>
    double[] LedPositions();
    /// <summary>Takes the LEDs (connect, ask for control). Returns an error text or null.</summary>
    string? Begin();
    void Paint(Color[] colors);
    /// <summary>Lets go: the device goes back to its own lighting.</summary>
    void End();
    int MaxFps { get; }
    /// <summary>False when this device has no light the engine can drive.</summary>
    bool LightsAvailable => true;
}
