using System;
using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Data.Converters;
using TikMan.Core.Localization;
using TikMan.Core.Models;

namespace TikMan.App.Avalonia;

/// <summary>Renders an enum value in the dropdowns as its <b>translated</b> name.
/// <para>⚠️ Deliberately a key lookup, not string cosmetics on the identifier. An earlier version just
/// inserted spaces before capitals ("SwissGerman" → "Swiss German"), which reads fine in English and is
/// wrong in every other language: the app speaks seven, and those dropdowns would have stayed stuck on the
/// C# identifiers. The keys already exist – the WPF client uses the same ones – so both clients name these
/// options identically.</para>
/// <para>The language flag is <b>not</b> here any more – it used to be an emoji prefixed to the name, but
/// Windows draws regional-indicator pairs as bare letters ("DE"), so it only ever showed on macOS/Linux.
/// The flag is now a real vector image (<see cref="LanguageFlag"/>) drawn beside this text in the item
/// template, which looks the same on every OS. This converter is just the name.</para>
/// <para>An enum member without a key falls back to the spaced identifier: a new option shows up readable
/// instead of blank, which is the failure mode you want when someone forgets a translation.</para></summary>
public sealed class EnumLabel : IValueConverter
{
    public static readonly EnumLabel Instance = new();

    private static string LanguageKey(AppLanguage l) => l switch
    {
        AppLanguage.System => "Set_LangSystem",
        AppLanguage.German => "Set_LangGerman",
        AppLanguage.English => "Set_LangEnglish",
        AppLanguage.SwissGerman => "Set_LangSwiss",
        AppLanguage.Spanish => "Set_LangSpanish",
        AppLanguage.Italian => "Set_LangItalian",
        AppLanguage.French => "Set_LangFrench",
        AppLanguage.Portuguese => "Set_LangPortuguese",
        _ => "",
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
        AppLanguage l => Translate(LanguageKey(l), l),
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
