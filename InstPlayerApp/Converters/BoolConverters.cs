using System.Globalization;

namespace InstPlayerApp.Converters;

public class InvertBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : value!;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : value!;
}

public class BoolToActiveColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Color.FromArgb("#4A9EFF") : Color.FromArgb("#2D2D30");

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class DivideBy100Converter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch { double d => d / 100.0, int i => i / 100.0, _ => 0.0 };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch { double d => d * 100.0, int i => (double)i * 100.0, _ => 0.0 };
}
