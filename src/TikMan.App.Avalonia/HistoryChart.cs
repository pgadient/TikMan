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

    /// <summary>How many samples fill the full plot width. The fleet keeps roughly this many points of
    /// history per device (see FleetService.HistoryPoints); a fuller window would only ever show the newest
    /// this-many anyway, so they are kept in step.</summary>
    private const int Capacity = 50;

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

        // ⚠️ marginTop reserves a band ABOVE the plot for the legend, so the swatches sit outside the graph
        // area instead of floating over the top-right of the curves (where they collided with a series that
        // ran high). Everything the plot draws starts at marginTop, so the legend band stays clear.
        const double marginLeft = 38, marginRight = 8, marginTop = 22, marginBottom = 22;
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
        if (data is { Count: >= 1 })
        {
            // ⚠️ A fixed-width time window, newest on the RIGHT. The old code spread whatever points existed
            // across the whole plot, so two readings drew one line straight across the screen. Instead each
            // sample gets a fixed x-step (full width == Capacity samples) and the newest pins to the right
            // edge, so a fresh chart shows a short stub on the right that grows leftward as readings arrive,
            // then scrolls once it is full.
            var count = Math.Min(data.Count, Capacity);
            var start = data.Count - count;
            var step = plotW / (Capacity - 1);
            var rightX = marginLeft + plotW;
            // Position p (0 = oldest shown, count-1 = newest) → x, counted back from the right edge.
            double X(int p) => rightX - (count - 1 - p) * step;

            DrawSeries(dc, data, start, count, s => s.CpuLoad, CpuBrush, X, marginTop, plotH);
            // A device that reports no memory (an old ZyNOS switch) must not draw a flat 0 % RAM line – skip
            // the series entirely, and DrawLegend below drops its swatch to match.
            if (data[^1].HasMemory)
                DrawSeries(dc, data, start, count, s => s.MemoryUsedPercent, MemBrush, X, marginTop, plotH);

            var newest = Text(data[^1].Timestamp.ToString("HH:mm:ss"), 10, labelBrush);
            dc.DrawText(newest, new Point(rightX - newest.Width, h - marginBottom + 4));
            // The oldest label sits under the oldest sample, not at the far left – with a partly-filled
            // window the left of the plot is empty, and a label there would point at nothing.
            if (count > 1)
            {
                var oldest = Text(data[start].Timestamp.ToString("HH:mm:ss"), 10, labelBrush);
                dc.DrawText(oldest, new Point(Math.Min(X(0), rightX - newest.Width - oldest.Width - 8),
                    h - marginBottom + 4));
            }
        }
        else
        {
            dc.DrawText(Text(T("Chart_NoData"), 11, labelBrush), new Point(marginLeft + 8, marginTop + 8));
        }

        // In the reserved top band (above the plot), vertically centred – "outside the graph". The RAM entry
        // is dropped for a device known to report no memory (newest sample says so); when there is no data
        // yet we can't know, so both are shown.
        var showMem = data is not { Count: >= 1 } || data[^1].HasMemory;
        DrawLegend(dc, w - marginRight, 4, labelBrush, showMem);
    }

    private static void DrawSeries(DrawingContext dc, IReadOnlyList<ResourceSnapshot> data, int start, int count,
        Func<ResourceSnapshot, double> value, IBrush brush, Func<int, double> x, double top, double plotH)
    {
        double Y(int p) => top + plotH * (1 - Math.Clamp(value(data[start + p]), 0, 100) / 100.0);

        // A single reading has no line to draw – show it as a dot on the right so the chart isn't blank
        // between the first and second poll.
        if (count == 1)
        {
            dc.DrawEllipse(brush, null, new Point(x(0), Y(0)), 2.5, 2.5);
            return;
        }

        var pen = new Pen(brush, 2);
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            for (var p = 0; p < count; p++)
            {
                var pt = new Point(x(p), Y(p));
                if (p == 0) ctx.BeginFigure(pt, false);
                else ctx.LineTo(pt);
            }
            ctx.EndFigure(false);
        }
        dc.DrawGeometry(null, pen, geometry);
    }

    /// <summary>Colour swatches with labels, laid out right-to-left from the plot's right edge.</summary>
    private void DrawLegend(DrawingContext dc, double right, double top, IBrush labelBrush, bool showMem)
    {
        const double swatch = 12, gap = 4, spacing = 14;

        // Right-most entry first, moving left by each entry's own width – so the labels can be any length
        // and in any language without the two colliding. RAM is omitted when the device reports no memory.
        var entries = showMem
            ? new[] { ("Chart_Ram", MemBrush), ("Chart_Cpu", CpuBrush) }
            : new[] { ("Chart_Cpu", CpuBrush) };
        var x = right;
        foreach (var (key, brush) in entries)
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
