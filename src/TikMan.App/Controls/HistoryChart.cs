using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using TikMan.Core.Models;

namespace TikMan.App.Controls;

/// <summary>Minimalistic history chart (0–100 %) for CPU and RAM usage.
/// Deliberately without a chart library – for a monitoring tool two polylines are enough.</summary>
public class HistoryChart : FrameworkElement
{
    private static readonly Pen GridPen = MakePen(Color.FromRgb(0xE0, 0xE0, 0xE0), 1);
    private static readonly Pen CpuPen = MakePen(Color.FromRgb(0xD9, 0x6C, 0x1F), 2);
    private static readonly Pen MemPen = MakePen(Color.FromRgb(0x2D, 0x6C, 0xB5), 2);
    private static readonly Brush LabelBrush = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
    private static readonly Typeface LabelFont = new("Segoe UI");

    public static readonly DependencyProperty SnapshotsProperty = DependencyProperty.Register(
        nameof(Snapshots), typeof(IReadOnlyList<ResourceSnapshot>), typeof(HistoryChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnSnapshotsChanged));

    public IReadOnlyList<ResourceSnapshot>? Snapshots
    {
        get => (IReadOnlyList<ResourceSnapshot>?)GetValue(SnapshotsProperty);
        set => SetValue(SnapshotsProperty, value);
    }

    private static void OnSnapshotsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var chart = (HistoryChart)d;
        if (e.OldValue is INotifyCollectionChanged oldCol) oldCol.CollectionChanged -= chart.OnCollectionChanged;
        if (e.NewValue is INotifyCollectionChanged newCol) newCol.CollectionChanged += chart.OnCollectionChanged;
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 60 || h < 40) return;

        dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));

        const double marginLeft = 38, marginRight = 8, marginTop = 8, marginBottom = 22;
        double plotW = w - marginLeft - marginRight;
        double plotH = h - marginTop - marginBottom;
        if (plotW <= 0 || plotH <= 0) return;

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // Grid lines with % labels
        for (int pct = 0; pct <= 100; pct += 25)
        {
            double y = marginTop + plotH * (1 - pct / 100.0);
            dc.DrawLine(GridPen, new Point(marginLeft, y), new Point(w - marginRight, y));
            var label = new FormattedText($"{pct}%", CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, LabelFont, 10, LabelBrush, dpi);
            dc.DrawText(label, new Point(marginLeft - label.Width - 4, y - label.Height / 2));
        }

        var snapshots = Snapshots;
        if (snapshots is { Count: > 1 })
        {
            DrawSeries(dc, snapshots, s => s.CpuLoad, CpuPen, marginLeft, marginTop, plotW, plotH);
            DrawSeries(dc, snapshots, s => s.MemoryUsedPercent, MemPen, marginLeft, marginTop, plotW, plotH);

            var first = snapshots[0].Timestamp.ToString("HH:mm:ss");
            var last = snapshots[^1].Timestamp.ToString("HH:mm:ss");
            var firstText = new FormattedText(first, CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, LabelFont, 10, LabelBrush, dpi);
            var lastText = new FormattedText(last, CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, LabelFont, 10, LabelBrush, dpi);
            dc.DrawText(firstText, new Point(marginLeft, h - marginBottom + 4));
            dc.DrawText(lastText, new Point(w - marginRight - lastText.Width, h - marginBottom + 4));
        }
        else
        {
            var hint = new FormattedText(Localization.LocalizationManager.T("Chart_NoData"),
                CultureInfo.InvariantCulture, FlowDirection.LeftToRight, LabelFont, 11, LabelBrush, dpi);
            dc.DrawText(hint, new Point(marginLeft + 8, marginTop + 8));
        }

        DrawLegend(dc, w - marginRight, marginTop, dpi);
    }

    private static void DrawSeries(DrawingContext dc, IReadOnlyList<ResourceSnapshot> data,
        Func<ResourceSnapshot, double> selector, Pen pen,
        double left, double top, double plotW, double plotH)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            for (int i = 0; i < data.Count; i++)
            {
                double x = left + plotW * i / (data.Count - 1);
                double value = Math.Clamp(selector(data[i]), 0, 100);
                double y = top + plotH * (1 - value / 100.0);
                if (i == 0) ctx.BeginFigure(new Point(x, y), false, false);
                else ctx.LineTo(new Point(x, y), true, true);
            }
        }
        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }

    private static void DrawLegend(DrawingContext dc, double right, double top, double dpi)
    {
        var entries = new[] { ("RAM", MemPen), ("CPU", CpuPen) };
        double x = right;
        foreach (var (name, pen) in entries)
        {
            var text = new FormattedText(name, CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, LabelFont, 10, LabelBrush, dpi);
            x -= text.Width;
            dc.DrawText(text, new Point(x, top));
            x -= 16;
            dc.DrawLine(pen, new Point(x, top + text.Height / 2), new Point(x + 12, top + text.Height / 2));
            x -= 14;
        }
    }

    private static Pen MakePen(Color color, double thickness)
    {
        var pen = new Pen(new SolidColorBrush(color), thickness);
        pen.Freeze();
        return pen;
    }
}
