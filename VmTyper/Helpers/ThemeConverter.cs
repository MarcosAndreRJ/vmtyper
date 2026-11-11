using System.Globalization;
using System.Windows.Data;
using VmTyper.Models;

namespace VmTyper.Helpers;

/// <summary>
/// Converte a preferência de tema em rótulo ("Escuro", "Claro", "Auto") ou, com o parâmetro
/// <c>glyph</c>, no ícone correspondente da fonte Segoe MDL2 Assets.
/// </summary>
public sealed class ThemeConverter : IValueConverter
{
    public static readonly ThemeConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var theme = value is AppTheme t ? t : AppTheme.Auto;

        if (parameter is string p && p.Equals("glyph", StringComparison.OrdinalIgnoreCase))
        {
            return theme switch
            {
                AppTheme.Dark => "",   // lua (QuietHours)
                AppTheme.Light => "",  // sol (Brightness)
                _ => ""                // personalizar (automático)
            };
        }

        if (parameter is string tip && tip.Equals("tooltip", StringComparison.OrdinalIgnoreCase))
        {
            return theme switch
            {
                AppTheme.Dark => "Tema escuro. Clique para alternar para Claro.",
                AppTheme.Light => "Tema claro. Clique para alternar para Automático (segue o Windows).",
                _ => "Tema automático (segue o Windows). Clique para alternar para Escuro."
            };
        }

        return theme switch
        {
            AppTheme.Dark => "Escuro",
            AppTheme.Light => "Claro",
            _ => "Auto"
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
