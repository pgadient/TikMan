using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using TikMan.Core.Models;

namespace TikMan.App.Localization;

/// <summary>Runtime switching between German and English. XAML binds through the
/// <see cref="LocExtension"/> to the indexer; a language change fires PropertyChanged
/// for "Item[]", causing all bound texts to be re-evaluated.</summary>
public sealed class LocalizationManager : INotifyPropertyChanged
{
    public static LocalizationManager Instance { get; } = new();

    private Dictionary<string, string> _current;

    private LocalizationManager()
    {
        _current = Strings.German; // set at startup via Apply()
        Culture = CultureInfo.GetCultureInfo("de-CH");
    }

    /// <summary>Currently active language (resolved, never System).</summary>
    public AppLanguage Effective { get; private set; } = AppLanguage.German;

    /// <summary>Culture for date/number formatting matching the language.</summary>
    public CultureInfo Culture { get; private set; }

    public string this[string key] => _current.TryGetValue(key, out var v) ? v : key;

    /// <summary>Shorthand for code-behind: LocalizationManager.T("key").</summary>
    public static string T(string key) => Instance[key];

    /// <summary>Formatted shorthand: T("key", arg0, arg1, …).</summary>
    public static string T(string key, params object[] args) =>
        string.Format(Instance.Culture, Instance[key], args);

    public void Apply(AppLanguage language)
    {
        Effective = Resolve(language);
        _current = Effective switch
        {
            AppLanguage.German => Strings.German,
            AppLanguage.SwissGerman => Strings.SwissGerman,
            _ => Strings.English,
        };
        // German/Swiss German use de-CH formats, English uses en-US.
        Culture = CultureInfo.GetCultureInfo(Effective == AppLanguage.English ? "en-US" : "de-CH");
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
    }

    /// <summary>Resolve the system language based on the Windows display language:
    /// de-CH → Swiss German, otherwise «de…» → German, everything else → English.</summary>
    private static AppLanguage Resolve(AppLanguage language)
    {
        if (language != AppLanguage.System) return language;

        var ui = CultureInfo.CurrentUICulture;
        if (ui.Name.Equals("de-CH", StringComparison.OrdinalIgnoreCase)) return AppLanguage.SwissGerman;
        if (ui.TwoLetterISOLanguageName.Equals("de", StringComparison.OrdinalIgnoreCase)) return AppLanguage.German;
        return AppLanguage.English;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
