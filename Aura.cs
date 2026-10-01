using System.Windows;
using System.Windows.Media;

namespace DynamicIsland;

/// <summary>Soft light rising from the bottom edge of the player: one drifting patch per slice of the spectrum.</summary>
public sealed class Aura : FrameworkElement
{
    const int Patches = 4;
    const double Attack = 0.07, Release = 0.45; // seconds: slower than the bars, so the light breathes instead of flickering
    const double Drift = 0.06;                  // how far a patch wanders sideways, as a fraction of the width

    // where each patch rests (fraction of the width) and how it wanders; the bass sits near the middle
    static readonly double[] Anchor = { 0.40, 0.63, 0.15, 0.86 };
    static readonly double[] Speed = { 0.55, 0.43, 0.71, 0.62 };
    static readonly double[] Phase = { 0.0, 2.1, 4.0, 5.3 };

    // alpha along the radius: a bell curve, so no patch shows an edge
    static readonly (double Offset, double Alpha)[] Falloff = { (0, 1), (0.25, 0.7), (0.5, 0.3), (0.75, 0.07), (1, 0) };

    public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(
        nameof(Color), typeof(Color), typeof(Aura),
        new FrameworkPropertyMetadata(Colors.White, (d, _) => ((Aura)d).Tint()));

    readonly RadialGradientBrush _brush = new();
    readonly double[] _levels = new double[Patches];
    double _time;

    public Aura()
    {
        foreach (var (offset, _) in Falloff) _brush.GradientStops.Add(new GradientStop(Colors.Transparent, offset));
        Tint();
    }

    public Color Color
    {
        get => (Color)GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    void Tint()
    {
        Color c = Color;
        for (int i = 0; i < Falloff.Length; i++)
            _brush.GradientStops[i].Color = Color.FromArgb((byte)(255 * Falloff[i].Alpha), c.R, c.G, c.B);
    }

    /// <summary>Advances one frame, following the bars of <paramref name="source"/>. Returns false once the light is at rest.</summary>
    public bool Tick(Equalizer source, double t, double dt)
    {
        double rise = 1 - Math.Exp(-dt / Attack), fall = 1 - Math.Exp(-dt / Release);
        bool moved = false;

        for (int p = 0; p < Patches; p++)
        {
            int from = p * source.Bars / Patches, to = Math.Max((p + 1) * source.Bars / Patches, from + 1);
            double target = 0;
            for (int b = from; b < to; b++) target += source.Level(b);
            target /= to - from;

            double next = _levels[p] + (target - _levels[p]) * (target > _levels[p] ? rise : fall);
            if (Math.Abs(next - _levels[p]) > 0.002) moved = true;
            _levels[p] = next;
        }

        _time = t;
        if (moved) InvalidateVisual();
        return moved;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        for (int p = 0; p < Patches; p++)
        {
            double level = _levels[p];
            double x = w * (Anchor[p] + Drift * Math.Sin(_time * Speed[p] + Phase[p]));
            // centred on the bottom edge, so only the upper half of each patch shows
            dc.PushOpacity(0.16 + 0.5 * level);
            dc.DrawEllipse(_brush, null, new Point(x, h), w * (0.26 + 0.1 * level), h * (0.3 + 0.4 * level));
            dc.Pop();
        }
    }
}
