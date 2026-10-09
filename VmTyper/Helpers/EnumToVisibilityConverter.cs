using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace VmTyper.Helpers;

/// <summary>
/// Mostra o elemento (Visible) quando o valor do enum bindado é igual ao
/// membro indicado em ConverterParameter; Collapsed caso contrário.
/// </summary>
public sealed class EnumToVisibilityConverter : IValueConverter
{
    public static readonly EnumToVisibilityConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value?.ToString() == parameter?.ToString() ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
