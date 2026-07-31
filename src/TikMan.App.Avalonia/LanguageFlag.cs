using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using TikMan.Core.Models;

namespace TikMan.App.Avalonia;

/// <summary>A small vector flag for a language, for the settings dropdown. Returns an <see cref="IImage"/>
/// (a 20×14 <see cref="DrawingImage"/>), or null for a non-language value.
///
/// <para>⚠️ Why vectors and not the flag emoji: Windows' <i>Segoe UI Emoji</i> deliberately renders
/// regional-indicator pairs (🇩🇪) as the bare letters "DE", so an emoji flag only ever appeared on
/// macOS/Linux. Drawn as geometry, the flag looks identical on every OS and needs no emoji font. The shapes
/// are deliberately simplified (no coats of arms, Portugal's armillary is a plain disc, the Union Jack is
/// its crosses without the pinwheel offset) – at 20×14 the extra detail would be mud anyway, and the point
/// is recognition, not heraldic accuracy.</para>
///
/// <para>Built once per language and cached; a DrawingImage is immutable enough to share across every
/// dropdown row.</para></summary>
public sealed class LanguageFlag : IValueConverter
{
    public static readonly LanguageFlag Instance = new();

    private const double W = 20, H = 14;
    private static readonly Dictionary<AppLanguage, DrawingImage> Cache = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not AppLanguage lang) return null;
        if (!Cache.TryGetValue(lang, out var img)) Cache[lang] = img = Build(lang);
        return img;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static DrawingImage Build(AppLanguage lang)
    {
        var g = new DrawingGroup { ClipGeometry = new RectangleGeometry(new Rect(0, 0, W, H)) };
        var d = g.Children;
        switch (lang)
        {
            case AppLanguage.German:      // black / red / gold, horizontal
                HBand(d, 0, 3, "#000000"); HBand(d, 1, 3, "#DD0000"); HBand(d, 2, 3, "#FFCE00");
                break;
            case AppLanguage.SwissGerman: // ⚠️ square (1:1) – the only square national flag, so it is
                                          // letterboxed inside the 20-wide box rather than stretched to fill it.
                Fill(d, 3, 0, 14, 14, "#D52B1E");        // red field, 14×14 centred
                Fill(d, 8.6, 3.2, 2.8, 7.6, "#FFFFFF");  // white cross – vertical bar
                Fill(d, 6.2, 5.6, 7.6, 2.8, "#FFFFFF");  // white cross – horizontal bar
                break;
            case AppLanguage.Italian:     // green / white / red, vertical
                VBand(d, 0, 3, "#008C45"); VBand(d, 1, 3, "#F4F5F0"); VBand(d, 2, 3, "#CD212A");
                break;
            case AppLanguage.French:      // blue / white / red, vertical
                VBand(d, 0, 3, "#0055A4"); VBand(d, 1, 3, "#FFFFFF"); VBand(d, 2, 3, "#EF4135");
                break;
            case AppLanguage.Spanish:     // red / yellow / red, 1:2:1 horizontal
                Fill(d, 0, 0, W, H * 0.25, "#AA151B");
                Fill(d, 0, H * 0.25, W, H * 0.5, "#F1BF00");
                Fill(d, 0, H * 0.75, W, H * 0.25, "#AA151B");
                break;
            case AppLanguage.Portuguese:  // green (2/5) / red (3/5), disc on the seam
                Fill(d, 0, 0, W * 0.4, H, "#006600");
                Fill(d, W * 0.4, 0, W * 0.6, H, "#FF0000");
                Disc(d, W * 0.4, H / 2, 2.6, "#FFCC00");
                Disc(d, W * 0.4, H / 2, 1.1, "#DA020E");
                break;
            case AppLanguage.English:     // Union Jack – blue field, white then red crosses over diagonals
                Fill(d, 0, 0, W, H, "#012169");
                Line(d, 0, 0, W, H, "#FFFFFF", 3.4); Line(d, W, 0, 0, H, "#FFFFFF", 3.4);   // white saltire
                Line(d, 0, 0, W, H, "#C8102E", 1.3); Line(d, W, 0, 0, H, "#C8102E", 1.3);   // red saltire
                Fill(d, 7.9, 0, 4.2, H, "#FFFFFF"); Fill(d, 0, 4.9, W, 4.2, "#FFFFFF");      // white cross
                Fill(d, 8.7, 0, 2.6, H, "#C8102E"); Fill(d, 0, 5.7, W, 2.6, "#C8102E");      // red cross
                break;
            default:                      // System / follow OS – a simple globe
                Disc(d, W / 2, H / 2, 6.4, "#4C6FB5");
                Stroke(d, new EllipseGeometry(new Rect(W / 2 - 3.0, H / 2 - 6.4, 6.0, 12.8)), "#FFFFFF", 0.8);
                Line(d, 3.6, H / 2, 16.4, H / 2, "#FFFFFF", 0.8);
                Line(d, W / 2, 0.6, W / 2, H - 0.6, "#FFFFFF", 0.8);
                Line(d, 5.4, 3.9, 14.6, 3.9, "#FFFFFF", 0.6);
                Line(d, 5.4, 10.1, 14.6, 10.1, "#FFFFFF", 0.6);
                break;
        }
        return new DrawingImage { Drawing = g };
    }

    // A full-width horizontal band: index `i` of `n` equal stripes.
    private static void HBand(IList<Drawing> d, int i, int n, string hex) =>
        Fill(d, 0, H * i / n, W, H / n, hex);

    // A full-height vertical band: index `i` of `n` equal stripes.
    private static void VBand(IList<Drawing> d, int i, int n, string hex) =>
        Fill(d, W * i / n, 0, W / n, H, hex);

    private static void Fill(IList<Drawing> d, double x, double y, double w, double h, string hex) =>
        d.Add(new GeometryDrawing { Geometry = new RectangleGeometry(new Rect(x, y, w, h)), Brush = Brush(hex) });

    private static void Disc(IList<Drawing> d, double cx, double cy, double r, string hex) =>
        d.Add(new GeometryDrawing { Geometry = new EllipseGeometry(new Rect(cx - r, cy - r, 2 * r, 2 * r)), Brush = Brush(hex) });

    private static void Line(IList<Drawing> d, double x1, double y1, double x2, double y2, string hex, double thickness) =>
        d.Add(new GeometryDrawing
        {
            Geometry = new LineGeometry { StartPoint = new Point(x1, y1), EndPoint = new Point(x2, y2) },
            Pen = new Pen(Brush(hex), thickness),
        });

    private static void Stroke(IList<Drawing> d, Geometry geo, string hex, double thickness) =>
        d.Add(new GeometryDrawing { Geometry = geo, Pen = new Pen(Brush(hex), thickness) });

    private static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));
}
