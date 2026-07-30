using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Input;
using TikMan.Core.Discovery;

namespace TikMan.App.Avalonia;

/// <summary>Gives clickable service badges a hand cursor and leaves the rest alone – the badge itself is the
/// affordance, so the pointer has to say which ones actually do something.
/// <para>Bound to the whole <see cref="ServiceBadge"/>: a badge is "clickable" when it has a URL to open, and
/// additionally the SMB/NetBIOS badge, which has no URL but opens the row's share list. (Still accepts a bare
/// bool for any binding that passes <c>IsClickable</c> directly.)</para></summary>
public sealed class BadgeCursor : IValueConverter
{
    public static readonly BadgeCursor Instance = new();

    private static readonly Cursor Hand = new(StandardCursorType.Hand);
    private static readonly Cursor Arrow = new(StandardCursorType.Arrow);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            ServiceBadge b => b.IsClickable || b.Name is "smb" or "netbios" ? Hand : Arrow,
            true => Hand,
            _ => Arrow,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
