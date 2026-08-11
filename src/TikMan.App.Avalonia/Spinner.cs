using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Threading;

namespace TikMan.App.Avalonia;

/// <summary>A small indeterminate spinner: a rotating arc on a faint track. Used where the app is
/// <b>waiting for a turn</b> (e.g. the map waiting for the scan to finish) as opposed to actually working –
/// the working states keep the indeterminate progress BAR, so the two are visually distinct.
/// <para>⚠️ Hand-rolled for the same reason as <see cref="HistoryChart"/>: rebuilding an indeterminate
/// ProgressBar in a refresh loop restarts its animation every time (which reads as "it keeps starting
/// over"), and Avalonia ships no circular spinner. A DispatcherTimer-driven arc has no such state to
/// reset, and the timer only runs while the spinner is actually in the tree.</para></summary>
public class Spinner : Control
{
    private readonly DispatcherTimer _timer;
    private double _angle;

    public Spinner()
    {
        Width = 26;
        Height = 26;
        HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Center;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Render,
            (_, _) => { _angle = (_angle + 12) % 360; InvalidateVisual(); });
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _timer.Stop();
    }

    public override void Render(DrawingContext dc)
    {
        // Theme-aware ink, same trick as HistoryChart: derive from the inherited text colour.
        var ink = (TextElement.GetForeground(this) as ISolidColorBrush)?.Color ?? Colors.Gray;
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var r = Math.Min(Bounds.Width, Bounds.Height) / 2 - 3;
        if (r <= 0) return;

        dc.DrawEllipse(null, new Pen(new SolidColorBrush(ink, 0.15), 3), center, r, r);

        Point At(double deg)
        {
            var a = deg * Math.PI / 180;
            return new Point(center.X + r * Math.Cos(a), center.Y + r * Math.Sin(a));
        }
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(At(_angle), false);
            ctx.ArcTo(At(_angle + 100), new Size(r, r), 0, false, SweepDirection.Clockwise);
            ctx.EndFigure(false);
        }
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(ink, 0.8), 3, lineCap: PenLineCap.Round), geo);
    }
}
