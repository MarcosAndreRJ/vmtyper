using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using VmTyper.Services;

namespace VmTyper.Helpers;

/// <summary>
/// Propriedade anexada que pinta badges de estado a partir de um hex fixo (ex.: <c>DeliveryVisualBadge.ColorHex</c>),
/// adaptando fundo/borda/texto ao tema ativo e reaplicando quando o tema muda.
/// <list type="bullet">
/// <item><c>Border</c>: fundo translúcido + borda; <c>TextBlock</c>/<c>Control</c>: cor do texto.</item>
/// </list>
/// </summary>
public static class BadgeColors
{
    public static readonly DependencyProperty HexProperty =
        DependencyProperty.RegisterAttached(
            "Hex", typeof(string), typeof(BadgeColors),
            new PropertyMetadata(null, OnHexChanged));

    public static string? GetHex(DependencyObject obj) => (string?)obj.GetValue(HexProperty);
    public static void SetHex(DependencyObject obj, string? value) => obj.SetValue(HexProperty, value);

    private static readonly DependencyProperty ThemeHandlerProperty =
        DependencyProperty.RegisterAttached("ThemeHandler", typeof(PropertyChangedEventHandler), typeof(BadgeColors));

    private static void OnHexChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        Apply(d);

        if (d is FrameworkElement fe && fe.GetValue(ThemeHandlerProperty) is null)
        {
            // Assina a troca de tema apenas enquanto o elemento está carregado (evita vazamento em células virtualizadas).
            void Attach()
            {
                if (fe.GetValue(ThemeHandlerProperty) is not null) return;
                PropertyChangedEventHandler handler = (_, args) =>
                {
                    if (args.PropertyName is nameof(ThemeManager.IsDarkEffectively))
                        fe.Dispatcher.BeginInvoke(() => Apply(fe));
                };
                fe.SetValue(ThemeHandlerProperty, handler);
                ThemeManager.Instance.PropertyChanged += handler;
                Apply(fe);
            }

            void Detach()
            {
                if (fe.GetValue(ThemeHandlerProperty) is PropertyChangedEventHandler handler)
                {
                    ThemeManager.Instance.PropertyChanged -= handler;
                    fe.ClearValue(ThemeHandlerProperty);
                }
            }

            fe.Loaded += (_, _) => Attach();
            fe.Unloaded += (_, _) => Detach();
            if (fe.IsLoaded) Attach();
        }
    }

    private static void Apply(DependencyObject d)
    {
        string? hex = GetHex(d);
        bool dark = ThemeManager.Instance.IsDarkEffectively;

        switch (d)
        {
            case Border border:
                border.Background = ThemedHexConverter.CreateBrush(hex, dark, "tint");
                border.BorderBrush = ThemedHexConverter.CreateBrush(hex, dark, "border");
                break;
            case TextBlock text:
                text.Foreground = ThemedHexConverter.CreateBrush(hex, dark, "fg");
                break;
            case System.Windows.Controls.Control control:
                control.Foreground = ThemedHexConverter.CreateBrush(hex, dark, "fg");
                break;
        }
    }
}
