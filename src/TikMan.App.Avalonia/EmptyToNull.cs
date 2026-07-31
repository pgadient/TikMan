using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace TikMan.App.Avalonia;

/// <summary>Passes a string through, but turns "" (and whitespace) into null. Bound to <c>ToolTip.Tip</c>,
/// this is the difference between "no tooltip" and "an empty tooltip box on hover": Avalonia shows a tooltip
/// whenever the tip is non-null, so a blank string would pop an empty rectangle over every cell that has no
/// link. Returning null suppresses it, leaving the tooltip only where there is actually something to show.</summary>
public sealed class EmptyToNull : IValueConverter
{
    public static readonly EmptyToNull Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string s && s.Trim().Length > 0 ? s : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
