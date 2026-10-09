using System.Globalization;
using System.Windows.Data;

namespace VmTyper.Helpers;

/// <summary>
/// Usado para desabilitar a caixa de texto principal enquanto uma
/// digitação está em andamento (evita editar o texto no meio do envio).
/// </summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public static readonly InverseBoolConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : false;
}
