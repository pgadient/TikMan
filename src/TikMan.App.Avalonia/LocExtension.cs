using System;
using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;
using TikMan.Core.Localization;

namespace TikMan.App.Avalonia;

/// <summary>Avalonia markup extension: <c>{loc:Loc Some_Key}</c> binds a property to the localized text and
/// re-evaluates on a language change. It binds to the shared <see cref="LocalizationManager"/>'s
/// <see cref="LocalizationManager.Version"/> (a named int that bumps on every Apply – its PropertyChanged
/// reliably refreshes an Avalonia binding, unlike the "Item[]" indexer notification) and runs a converter
/// that returns <c>T(key)</c>. So switching the language re-reads every localized text at runtime.
/// <para><c>NoIcon=True</c> drops a leading emoji from the label. The shared strings carry their own emoji
/// for the WPF client; where this UI draws its own theme-coloured glyph, the label must not bring a second
/// one along.</para></summary>
public sealed class LocExtension : MarkupExtension
{
    private static readonly LocConverter Converter = new();

    public LocExtension() { }
    public LocExtension(string key) => Key = key;

    public string Key { get; set; } = "";

    /// <summary>Strip a leading emoji/symbol from the translated text (see the class remarks).</summary>
    public bool NoIcon { get; set; }

    /// <summary>Strip a trailing "…". The shared labels mark actions that open a dialog that way; on a
    /// toolbar button that is just noise, so the caller decides per use rather than the string deciding
    /// for everyone.</summary>
    public bool NoEllipsis { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding(nameof(LocalizationManager.Version))
        {
            Source = LocalizationManager.Instance,
            Mode = BindingMode.OneWay,
            Converter = Converter,
            ConverterParameter = new LocRequest(Key, NoIcon, NoEllipsis),
        };

    private sealed record LocRequest(string Key, bool NoIcon, bool NoEllipsis);

    private sealed class LocConverter : IValueConverter
    {
        // Anything before the first letter or digit: the emoji, its variation selector and the space after.
        private static readonly Regex LeadingIcon = new(@"^[^\p{L}\p{N}]+", RegexOptions.Compiled);
        private static readonly Regex TrailingEllipsis = new(@"\s*(…|\.\.\.)$", RegexOptions.Compiled);

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (parameter is not LocRequest req) return LocalizationManager.T(parameter as string ?? "");
            var text = LocalizationManager.T(req.Key);
            if (req.NoIcon) text = LeadingIcon.Replace(text, "");
            if (req.NoEllipsis) text = TrailingEllipsis.Replace(text, "");
            return text;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
