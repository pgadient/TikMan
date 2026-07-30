using System;
using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Data.Converters;
using Avalonia.Media;
using TikMan.Core.Localization;
using TikMan.Core.Models;

namespace TikMan.App.Avalonia;

/// <summary>Renders an enum value in the dropdowns as its <b>translated</b> name, with a flag in front of
/// the languages (same presentation as the WPF client).
/// <para>⚠️ Deliberately a key lookup, not string cosmetics on the identifier. An earlier version just
/// inserted spaces before capitals ("SwissGerman" → "Swiss German"), which reads fine in English and is
/// wrong in every other language: the app speaks seven, and those dropdowns would have stayed stuck on the
/// C# identifiers. The keys already exist – the WPF client uses the same ones – so both clients name these
/// options identically.</para>
/// <para>The flags stay emoji on purpose. Everywhere else the icons are vector paths so they can follow the
/// light/dark theme, but a flag is inherently multi-coloured – tinting it would destroy it.</para>
/// <para>An enum member without a key falls back to the spaced identifier: a new option shows up readable
/// instead of blank, which is the failure mode you want when someone forgets a translation.</para></summary>
public sealed class EnumLabel : IValueConverter
{
    public static readonly EnumLabel Instance = new();

    /// <summary>Whether this system can actually draw a flag. Flags are regional-indicator pairs and need an
    /// emoji font: Windows and macOS ship one, a bare Linux install often does not (fonts-noto-color-emoji).
    /// Without it the text would degrade to tofu boxes, so the flag is dropped and only the name shown –
    /// missing decoration beats visible breakage. Probed once; the font set doesn't change mid-run.</summary>
    private static readonly bool FlagsRenderable = ProbeFlagFont();

    private static bool ProbeFlagFont()
    {
        // ⚠️ Windows never renders flag emoji: Segoe UI Emoji deliberately draws regional-indicator pairs
        // as the plain letters ("DE", "GB") – a Microsoft policy decision, not a missing font. The font
        // probe below can't see that (the characters ARE in the font, they just render as letters), which
        // is exactly how the dropdown ended up showing country codes in front of every language name.
        // No flag beats a wrong-looking code.
        if (OperatingSystem.IsWindows()) return false;
        try
        {
            // U+1F1E8 REGIONAL INDICATOR SYMBOL LETTER C – the first half of 🇨🇭.
            return FontManager.Current.TryMatchCharacter(
                0x1F1E8, FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, null, null, out _);
        }
        catch { return false; } // no font manager yet / unusual platform – be conservative
    }

    private static (string Flag, string Key) Language(AppLanguage l) => l switch
    {
        AppLanguage.System => ("🌐", "Set_LangSystem"),
        AppLanguage.German => ("🇩🇪", "Set_LangGerman"),
        AppLanguage.English => ("🇬🇧", "Set_LangEnglish"),
        AppLanguage.SwissGerman => ("🇨🇭", "Set_LangSwiss"),
        AppLanguage.Spanish => ("🇪🇸", "Set_LangSpanish"),
        AppLanguage.Italian => ("🇮🇹", "Set_LangItalian"),
        AppLanguage.French => ("🇫🇷", "Set_LangFrench"),
        AppLanguage.Portuguese => ("🇵🇹", "Set_LangPortuguese"),
        _ => ("", ""),
    };

    private static string BackupMethodKey(BackupMethod m) => m switch
    {
        BackupMethod.Auto => "Set_BackupAuto",
        BackupMethod.Web => "Set_BackupWeb",
        BackupMethod.Ssh => "Set_BackupSsh",
        _ => "",
    };

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        AppLanguage l when Language(l) is var (flag, key) =>
            FlagsRenderable && flag.Length > 0 ? $"{flag}  {Translate(key, l)}" : Translate(key, l),
        BackupMethod m => Translate(BackupMethodKey(m), m),
        null => "",
        _ => Spaced(value),
    };

    /// <summary>The translated text, or the spaced identifier when the key is missing – T() hands the key
    /// itself back in that case, which would otherwise show up as "Set_LangSwiss" in the dropdown.</summary>
    private static string Translate(string key, object fallback)
    {
        if (key.Length == 0) return Spaced(fallback);
        var text = LocalizationManager.T(key);
        return text.Length > 0 && text != key ? text : Spaced(fallback);
    }

    /// <summary>"SwissGerman" → "Swiss German".</summary>
    private static string Spaced(object value) =>
        Regex.Replace(value.ToString() ?? "", @"(?<=[a-z0-9])(?=[A-Z])", " ");

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
