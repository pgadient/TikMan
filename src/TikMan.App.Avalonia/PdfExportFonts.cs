using System;
using System.Collections.Generic;
using System.IO;
using PdfSharp.Fonts;

namespace TikMan.App.Avalonia;

/// <summary>Tells PdfSharp where to find fonts for the vector PDF export. PdfSharp ships no fonts and this
/// app is cross-platform, so the resolver points at whatever sans-serif the OS has: Segoe UI/Arial on
/// Windows, DejaVu/Liberation on Linux, Arial/SF on macOS. The requested family name is ignored – any of
/// these renders the graph labels fine; we just need a real TTF file.</summary>
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

    // The resolver returns the font file path as the "face name"; GetFont then reads that file.
    public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic)
    {
        foreach (var path in Candidates(isBold))
            if (File.Exists(path)) return new FontResolverInfo(path);
        // Last resort: the first .ttf found in a system font directory.
        foreach (var dir in new[] { "/usr/share/fonts", "/usr/local/share/fonts", "/Library/Fonts" })
            if (Directory.Exists(dir))
                foreach (var f in Directory.EnumerateFiles(dir, "*.ttf", SearchOption.AllDirectories))
                    return new FontResolverInfo(f);
        return null;
    }

    public byte[]? GetFont(string faceName)
    {
        try { return File.ReadAllBytes(faceName); }
        catch (IOException) { return null; }
    }

    private static IEnumerable<string> Candidates(bool bold)
    {
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        if (win.Length > 0)
        {
            if (bold) { yield return Path.Combine(win, "segoeuib.ttf"); yield return Path.Combine(win, "arialbd.ttf"); }
            yield return Path.Combine(win, "segoeui.ttf");
            yield return Path.Combine(win, "arial.ttf");
        }
        if (bold)
        {
            yield return "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf";
            yield return "/usr/share/fonts/truetype/liberation/LiberationSans-Bold.ttf";
        }
        yield return "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf";
        yield return "/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf";
        yield return "/System/Library/Fonts/Supplemental/Arial.ttf";
        yield return "/Library/Fonts/Arial.ttf";
    }
}
