using System.IO;
using PdfSharp.Fonts;

namespace TikMan.App;

/// <summary>Tells PdfSharp where to find the fonts for the vector PDF export. PdfSharp is platform-
/// neutral and ships no fonts, so it needs a resolver – and since TikMan is Windows-only we can point
/// it straight at the system font files (Segoe UI, Arial as fallback).</summary>
public sealed class PdfExportFonts : IFontResolver
{
    private static bool _registered;

    /// <summary>Installs the resolver once (idempotent); safe to call before every export.</summary>
    public static void EnsureRegistered()
    {
        if (_registered) return;
        GlobalFontSettings.FontResolver = new PdfExportFonts();
        _registered = true;
    }

    private static string FontsDir => Environment.GetFolderPath(Environment.SpecialFolder.Fonts);

    // The resolver returns the font file path as the "face name"; GetFont then reads that file.
    public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic)
    {
        string[] candidates = isBold
            ? new[] { "segoeuib.ttf", "arialbd.ttf", "segoeui.ttf", "arial.ttf" }
            : new[] { "segoeui.ttf", "arial.ttf" };
        foreach (var file in candidates)
        {
            var path = Path.Combine(FontsDir, file);
            if (File.Exists(path)) return new FontResolverInfo(path);
        }
        return null;
    }

    public byte[]? GetFont(string faceName)
    {
        try { return File.ReadAllBytes(faceName); }
        catch (IOException) { return null; }
    }
}
