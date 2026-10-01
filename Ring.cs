using System.Windows;
using System.Windows.Media;

namespace DynamicIsland;

/// <summary>Countdown ring: a faint full circle with what is left drawn over it, emptying clockwise from the top.</summary>
public sealed class Ring : FrameworkElement
{
    const double Thickness = 2.5;

    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(Ring),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Ring),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Share still to go, 1 → 0.</summary>
    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double r = (Math.Min(ActualWidth, ActualHeight) - Thickness) / 2;
        if (r <= 0) return;

        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var pen = new Pen(Stroke, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };

        dc.PushOpacity(0.3);
        dc.DrawEllipse(null, pen, center, r, r);
        dc.Pop();

        double share = Math.Clamp(Progress, 0, 1);
        if (share <= 0.001) return;
        if (share >= 0.999)
        {
            dc.DrawEllipse(null, pen, center, r, r);
            return;
        }

        // what is left runs from the moving end round to twelve o'clock
        double angle = 2 * Math.PI * (1 - share);
        var from = new Point(center.X + r * Math.Sin(angle), center.Y - r * Math.Cos(angle));
        var arc = new StreamGeometry();
        using (StreamGeometryContext g = arc.Open())
        {
            g.BeginFigure(from, false, false);
            g.ArcTo(new Point(center.X, center.Y - r), new Size(r, r), 0, share > 0.5, SweepDirection.Clockwise, true, false);
        }
        arc.Freeze();
        dc.DrawGeometry(null, pen, arc);
    }
}
