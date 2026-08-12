using System.Globalization;

namespace SeattleCarsInBikeLanes.Mobile.Converters;

/// <summary>
/// True when the bound value is present, for showing things only when there is something to show.
/// </summary>
public sealed class IsNotNullConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // An empty string is nothing worth showing either, and every use of this is a message.
        return value is not null && value is not string { Length: 0 };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Flips a boolean, for binding one flag to both a control and its opposite.
/// </summary>
public sealed class InvertedBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not bool flag || !flag;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not bool flag || !flag;
}
