using System.Windows;
using System.Windows.Media;

namespace DynamicIsland;

/// <summary>Waveform bars driven by the live output spectrum: bass in the middle, treble towards the edges.</summary>
public sealed class Equalizer : FrameworkElement
{
    const double BarWidth = 3;
    const double Attack = 0.03, Release = 0.17; // seconds

    // each bar is measured against its own slowly moving baseline, so quiet and loud tracks both swing fully
    const double RangeDb = 10, Center = 0.5;    // a bar at its usual level sits half-way, +5 dB fills it
    const double Rise = 0.8, Sink = 2.5;        // seconds for a baseline to follow a louder / quieter passage
    const double SpreadDb = 12;                 // bands further than this under the loudest one stay small
    const double FloorDb = -70;                 // baselines never sink below this, so noise stays flat

    static readonly double[] F1 = { 7.1, 9.3, 6.2, 10.4, 8.0, 5.6, 9.9, 7.7 };
    static readonly double[] F2 = { 2.3, 3.1, 1.7, 2.9, 3.7, 2.1, 1.3, 3.3 };

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(Equalizer),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    int _bars;
    double[] _levels = null!, _targets = null!, _db = null!, _base = null!;
    int[] _rank = null!, _slot = null!; // band shown by each bar, and the bar showing each band

    public Equalizer() => Bars = 5;

    public Brush Fill
    {
        get => (Brush)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public int Bars
    {
        get => _bars;
        set
        {
            _bars = Math.Max(2, value);
            _levels = new double[_bars];
            _targets = new double[_bars];
            _db = new double[_bars];
            _base = new double[_bars];
            Array.Fill(_base, FloorDb);

            // the lowest band sits in the middle, higher ones alternate outwards
            _rank = new int[_bars];
            _slot = new int[_bars];
            int center = (_bars - 1) / 2;
            for (int k = 0; k < _bars; k++)
            {
                _slot[k] = center + (k % 2 == 1 ? (k + 1) / 2 : -k / 2);
                _rank[_slot[k]] = k;
            }
        }
    }

    /// <summary>Current height (0..1) of the bar that shows the given band, 0 being the lowest.</summary>
    public double Level(int band) => _levels[_slot[band]];

    /// <summary>
    /// Advances one frame. <paramref name="spectrum"/> holds band powers from bass to treble; pass null to fall
    /// back to a wobble scaled by the output <paramref name="peak"/>. Returns false once the bars are at rest.
    /// </summary>
    public bool Tick(float[]? spectrum, double peak, bool playing, double t, double dt)
    {
        if (!playing) Array.Clear(_targets);
        else if (spectrum != null) Level(spectrum, dt);
        else Wobble(peak, t);

        double rise = 1 - Math.Exp(-dt / Attack), fall = 1 - Math.Exp(-dt / Release);
        bool moved = false;
        for (int i = 0; i < _bars; i++)
        {
            // fast attack, slow decay
            double next = _levels[i] + (_targets[i] - _levels[i]) * (_targets[i] > _levels[i] ? rise : fall);
            if (Math.Abs(next - _levels[i]) > 0.002) moved = true;
            _levels[i] = next;
        }

        if (moved) InvalidateVisual();
        return moved;
    }

    void Level(float[] spectrum, double dt)
    {
        double headroom = RangeDb * (1 - Center), top = FloorDb;
        for (int i = 0; i < _bars; i++)
        {
            // mean power of the slice of the spectrum that belongs to this bar
            int from = _rank[i] * spectrum.Length / _bars, to = Math.Max((_rank[i] + 1) * spectrum.Length / _bars, from + 1);
            double power = 0;
            for (int b = from; b < to; b++) power += spectrum[b];
            double db = _db[i] = 10 * Math.Log10(power / (to - from) + 1e-14);

            // silence leaves the baseline alone, so a gap between tracks doesn't reset it
            if (db > FloorDb)
            {
                double b = _base[i];
                b += (db - b) * (1 - Math.Exp(-dt / (db > b ? Rise : Sink)));
                _base[i] = Math.Max(b, db - headroom);
            }
            top = Math.Max(top, _base[i]);
        }

        for (int i = 0; i < _bars; i++)
        {
            double reference = Math.Max(_base[i], top - SpreadDb);
            _targets[i] = Math.Clamp(Center + (_db[i] - reference) / RangeDb, 0, 1);
        }
    }

    void Wobble(double peak, double t)
    {
        double level = Math.Pow(Math.Clamp(peak * 1.8, 0, 1), 0.6);
        double mid = (_bars - 1) / 2.0;
        for (int i = 0; i < _bars; i++)
        {
            double noise = 0.5 + 0.5 * Math.Sin(t * F1[i % 8] + i * 1.9) * Math.Cos(t * F2[i % 8] + i * 0.7);
            double envelope = 1 - 0.3 * Math.Abs(i - mid) / mid;
            _targets[i] = level * envelope * (0.3 + 0.7 * noise);
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        double gap = (w - _bars * BarWidth) / (_bars - 1);
        for (int i = 0; i < _bars; i++)
        {
            double bh = BarWidth + _levels[i] * (h - BarWidth);
            var rect = new Rect(i * (BarWidth + gap), (h - bh) / 2, BarWidth, bh);
            // quiet bars sit back a little, loud ones come forward
            dc.PushOpacity(0.55 + 0.45 * _levels[i]);
            dc.DrawRoundedRectangle(Fill, null, rect, BarWidth / 2, BarWidth / 2);
            dc.Pop();
        }
    }
}
