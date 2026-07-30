using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using TikMan.Core.Models;
using static TikMan.Core.Localization.LocalizationManager;

namespace TikMan.App.Avalonia;

/// <summary>Minimal CPU/RAM history chart (0–100 %), ported from the WPF control.
/// <para>Deliberately without a charting library: two polylines and a grid are the whole requirement, and a
/// dependency would be larger than the code.</para>
/// <para>Unlike the WPF original this does not paint its own white background – it inherits the panel's, so
/// it works in both the light and the dark theme. The grid and label colours come from the theme's
/// foreground at low opacity for the same reason; the two series keep fixed hues because they are the
/// legend, and a colour the reader has to re-learn per theme is no legend at all.</para></summary>
public class HistoryChart : Control
{
    private static readonly IBrush CpuBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0x6C, 0x1F));
    private static readonly IBrush MemBrush = new SolidColorBrush(Color.FromRgb(0x2D, 0x6C, 0xB5));

    public static readonly StyledProperty<IReadOnlyList<ResourceSnapshot>?> SnapshotsProperty =
        AvaloniaProperty.Register<HistoryChart, IReadOnlyList<ResourceSnapshot>?>(nameof(Snapshots));

    public IReadOnlyList<ResourceSnapshot>? Snapshots
    {
        get => GetValue(SnapshotsProperty);
        set => SetValue(SnapshotsProperty, value);
    }

    static HistoryChart()
    {
        // Re-render whenever the data is replaced. The fleet hands out a fresh list each poll rather than
        // mutating one, so watching the property is enough – no collection-changed subscription needed.
        AffectsRender<HistoryChart>(SnapshotsProperty);
    }

    public override void Render(DrawingContext dc)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w < 60 || h < 40) return;

        const double marginLeft = 38, marginRight = 8, marginTop = 8, marginBottom = 22;
        double plotW = w - marginLeft - marginRight;
        double plotH = h - marginTop - marginBottom;
        if (plotW <= 0 || plotH <= 0) return;

        // Theme-aware ink: the panel behind us may be light or dark, so derive from the inherited text
        // colour. Foreground lives on TextElement as an attached, inheriting property – a plain Control
        // has no Foreground of its own.
        var ink = (TextElement.GetForeground(this) as ISolidColorBrush)?.Color ?? Colors.Gray;
        var gridPen = new Pen(new SolidColorBrush(ink, 0.18), 1);
        var labelBrush = new SolidColorBrush(ink, 0.6);

        for (var pct = 0; pct <= 100; pct += 25)
        {
            var y = marginTop + plotH * (1 - pct / 100.0);
            dc.DrawLine(gridPen, new Point(marginLeft, y), new Point(w - marginRight, y));
            var label = Text($"{pct}%", 10, labelBrush);
            dc.DrawText(label, new Point(marginLeft - label.Width - 4, y - label.Height / 2));
        }

        var data = Snapshots;
        if (data is { Count: > 1 })
        {
            DrawSeries(dc, data, s => s.CpuLoad, CpuBrush, marginLeft, marginTop, plotW, plotH);
            DrawSeries(dc, data, s => s.MemoryUsedPercent, MemBrush, marginLeft, marginTop, plotW, plotH);

            var first = Text(data[0].Timestamp.ToString("HH:mm:ss"), 10, labelBrush);
            var last = Text(data[^1].Timestamp.ToString("HH:mm:ss"), 10, labelBrush);
            dc.DrawText(first, new Point(marginLeft, h - marginBottom + 4));
            dc.DrawText(last, new Point(w - marginRight - last.Width, h - marginBottom + 4));
        }
        else
        {
            dc.DrawText(Text(T("Chart_NoData"), 11, labelBrush), new Point(marginLeft + 8, marginTop + 8));
        }

        DrawLegend(dc, w - marginRight, marginTop, labelBrush);
    }

    private static void DrawSeries(DrawingContext dc, IReadOnlyList<ResourceSnapshot> data,
        Func<ResourceSnapshot, double> value, IBrush brush,
        double left, double top, double plotW, double plotH)
    {
        var pen = new Pen(brush, 2);
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            for (var i = 0; i < data.Count; i++)
            {
                var x = left + plotW * i / (data.Count - 1);
                var v = Math.Clamp(value(data[i]), 0, 100);
                var y = top + plotH * (1 - v / 100.0);
                if (i == 0) ctx.BeginFigure(new Point(x, y), false);
                else ctx.LineTo(new Point(x, y));
            }
            ctx.EndFigure(false);
        }
        dc.DrawGeometry(null, pen, geometry);
    }

    /// <summary>Colour swatches with labels, laid out right-to-left from the plot's right edge.</summary>
    private void DrawLegend(DrawingContext dc, double right, double top, IBrush labelBrush)
    {
        const double swatch = 12, gap = 4, spacing = 14;

        // Right-most entry first, moving left by each entry's own width – so the labels can be any length
        // and in any language without the two colliding.
        var x = right;
        foreach (var (key, brush) in new[] { ("Chart_Ram", MemBrush), ("Chart_Cpu", CpuBrush) })
        {
            var label = Text(T(key), 10, labelBrush);
            x -= label.Width;
            dc.DrawText(label, new Point(x, top));

            var midY = top + label.Height / 2;
            x -= gap + swatch;
            dc.DrawLine(new Pen(brush, 2), new Point(x, midY), new Point(x + swatch, midY));
            x -= spacing;
        }
    }

    private static FormattedText Text(string text, double size, IBrush brush) =>
        new(text, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            Typeface.Default, size, brush);
}
