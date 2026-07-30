namespace TikMan.Core.Discovery;

/// <summary>Line-length limits for the box captions in the markup exports (GraphML, draw.io).
///
/// <para>⚠️ Why only those two. The canvas, the PNG and the PDF measure the text and ellipsise it against
/// the real box width, which is strictly better – it fits exactly what fits. Neither yEd nor draw.io does
/// that for us: both lay out the label themselves and simply let it run past the box. Most values are short
/// enough that nobody noticed, until a device answered with a paragraph – an APC management card reports its
/// whole firmware manifest as its model – and one box wrote a line across half the diagram, overlapping
/// everything in its path. Two hundred characters in a 178-point box is not a rendering subtlety.</para>
///
/// <para>Clipping happens at the EXPORT, not at the source: the layout keeps the full strings, so the app's
/// own map, its tooltips and the <c>&lt;data&gt;</c> attributes of the GraphML file all still carry the
/// complete value. Only the drawn caption is shortened.</para></summary>
public static class TopoLabel
{
    // Character budgets for a 178-point box, per line, at the font size each line uses. Deliberately
    // generous rather than exact – the point is to stop a runaway string, not to pack the box.
    public const int KindMax = 26;
    public const int TitleMax = 24;      // bold and a size up, so fewer fit
    public const int HardwareMax = 28;
    public const int DetailMax = 30;
    public const int MacMax = 24;

    /// <summary>The text, cut to <paramref name="max"/> characters with a single-character ellipsis when it
    /// does not fit. Cuts on a word boundary when there is one nearby, so a clipped value ends at a word
    /// rather than mid-token – "APC Web/SNMP Management…" reads; "APC Web/SNMP Managem…" does not.</summary>
    public static string Clip(string text, int max)
    {
        var s = (text ?? "").Trim();
        if (max <= 1 || s.Length <= max) return s;

        var cut = s[..(max - 1)];
        // Only back up to a space if one sits in the last quarter – otherwise a line with no spaces near
        // the end (a long identifier) would lose most of its length to the search.
        var space = cut.LastIndexOf(' ');
        if (space >= max * 3 / 4) cut = cut[..space];
        return cut.TrimEnd(' ', ',', ';', ':', '-', '·', '(') + "…";
    }
}
