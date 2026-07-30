namespace TikMan.Core.Discovery;

/// <summary>The line-by-line text shading a topology box uses, in one place.
///
/// <para>⚠️ Why this exists: the canvas, the PNG and the draw.io export set an <b>opacity</b> per line, so
/// the category and the address recede and the name leads. The PDF and GraphML exports could not do that –
/// PdfSharp draws with a brush and yEd's HTML label subset has no opacity – so both drew every line in the
/// same flat colour and read noticeably heavier than the map they came from. Blending toward the box fill
/// produces the same result on an opaque box while being expressible as a plain colour, which every
/// renderer can do.</para>
///
/// <para>The amounts are the on-screen opacities restated as "how far toward the background": a line drawn
/// at 0.7 opacity over the fill is the same pixel as one blended 30% into it. Keeping the numbers here
/// rather than in each renderer is what stops the five outputs from drifting apart again.</para></summary>
public static class TopoColours
{
    /// <summary>The category caption above the name – quiet, it labels rather than states.</summary>
    public const double KindFade = 0.30;
    /// <summary>Vendor and model – nearly full strength; they are facts, not decoration.</summary>
    public const double HardwareFade = 0.10;
    /// <summary>The address line.</summary>
    public const double DetailFade = 0.15;
    /// <summary>The MAC, where a renderer shows it – the least-read line in the box.</summary>
    public const double MacFade = 0.45;

    /// <summary>Blends <paramref name="colour"/> <paramref name="amount"/> of the way toward
    /// <paramref name="towards"/> and returns it as "#RRGGBB". Both inputs are the "#RRGGBB" strings the
    /// layout already carries; anything unparseable is returned unchanged rather than silently turned
    /// black – a wrong colour is easier to live with than an invisible one.</summary>
    public static string Fade(string colour, string towards, double amount)
    {
        if (!TryParse(colour, out var cr, out var cg, out var cb)) return colour;
        if (!TryParse(towards, out var tr, out var tg, out var tb)) return colour;
        var k = Math.Clamp(amount, 0, 1);
        int Mix(int c, int t) => (int)Math.Round(c + (t - c) * k);
        return $"#{Mix(cr, tr):X2}{Mix(cg, tg):X2}{Mix(cb, tb):X2}";
    }

    private static bool TryParse(string hex, out int r, out int g, out int b)
    {
        r = g = b = 0;
        var s = (hex ?? "").Trim().TrimStart('#');
        // "#AARRGGBB" is accepted too – the alpha is dropped, because the blend replaces it.
        if (s.Length == 8) s = s[2..];
        if (s.Length != 6) return false;
        return int.TryParse(s[..2], System.Globalization.NumberStyles.HexNumber, null, out r)
            && int.TryParse(s[2..4], System.Globalization.NumberStyles.HexNumber, null, out g)
            && int.TryParse(s[4..], System.Globalization.NumberStyles.HexNumber, null, out b);
    }
}
