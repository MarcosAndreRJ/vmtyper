using System.Globalization;
using System.Windows.Data;
using Binding = System.Windows.Data.Binding;

namespace VmTyper.Helpers;

/// <summary>
/// Liga um RadioButton.IsChecked a um valor específico de um enum (via
/// ConverterParameter = nome do membro do enum). Two-way: marcar o
/// RadioButton escreve esse valor de volta na propriedade de origem.
/// </summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public static readonly EnumEqualsConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value?.ToString() == parameter?.ToString();

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b && b && parameter is not null)
        {
            return Enum.Parse(targetType, parameter.ToString()!);
        }
        return Binding.DoNothing;
    }
}
