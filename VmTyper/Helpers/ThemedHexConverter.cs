using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Color = System.Windows.Media.Color;

namespace VmTyper.Helpers;

/// <summary>
/// Adapta cores de estado definidas como hexadecimal fixo (badges de entrega, status de blocos) ao tema ativo.
/// Valores: [0] = hex da cor de destaque (string), [1] = tema escuro? (bool).
/// Parâmetro: <c>fg</c> (texto/ícone), <c>tint</c> (fundo translúcido) ou <c>border</c>.
/// Usar com MultiBinding incluindo <c>ThemeManager.Instance.IsDarkEffectively</c> para reavaliar na troca de tema.
/// </summary>
public sealed class ThemedHexConverter : IMultiValueConverter
{
    public static readonly ThemedHexConverter Instance = new();

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        string? hex = values.Length > 0 ? values[0] as string : null;
        bool dark = values.Length > 1 && values[1] is bool b && b;
        return CreateBrush(hex, dark, parameter as string ?? "fg");
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    /// <summary>Cria o pincel (congelado) de <paramref name="mode"/> (<c>fg</c>, <c>tint</c>, <c>border</c>) para a cor base.</summary>
    public static SolidColorBrush CreateBrush(string? hex, bool dark, string mode)
    {
        Color baseColor;
        try
        {
            baseColor = string.IsNullOrWhiteSpace(hex)
                ? Colors.Gray
                : (Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
        }
        catch
        {
            baseColor = Colors.Gray;
        }

        Color result = mode.ToLowerInvariant() switch
        {
            "tint" => Color.FromArgb(dark ? (byte)0x30 : (byte)0x1C, baseColor.R, baseColor.G, baseColor.B),
            "border" => Color.FromArgb(dark ? (byte)0x90 : (byte)0x80, baseColor.R, baseColor.G, baseColor.B),
            _ => dark ? Lighten(baseColor, 0.55) : baseColor
        };

        var brush = new SolidColorBrush(result);
        brush.Freeze();
        return brush;
    }

    private static Color Lighten(Color c, double amount)
    {
        byte Mix(byte channel) => (byte)Math.Round(channel + (255 - channel) * amount);
        return Color.FromRgb(Mix(c.R), Mix(c.G), Mix(c.B));
    }
}
