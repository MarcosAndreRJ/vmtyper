using System.Windows;
using System.Windows.Controls;
using TextBox = System.Windows.Controls.TextBox;
using VmTyper.Models;

namespace VmTyper.Helpers;

/// <summary>
/// Comportamento anexado que destaca (como seleção, mesmo sem foco) o trecho de um
/// <see cref="TextBox"/> que está sendo digitado e o mantém visível.
/// </summary>
public static class TextBoxHighlight
{
    public static readonly DependencyProperty RangeProperty =
        DependencyProperty.RegisterAttached(
            "Range",
            typeof(TextHighlight?),
            typeof(TextBoxHighlight),
            new PropertyMetadata(null, OnRangeChanged));

    public static TextHighlight? GetRange(DependencyObject d) => (TextHighlight?)d.GetValue(RangeProperty);

    public static void SetRange(DependencyObject d, TextHighlight? value) => d.SetValue(RangeProperty, value);

    private static void OnRangeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box) return;

        if (e.NewValue is not TextHighlight range)
        {
            // Só desfaz a seleção que este comportamento criou; não mexe na seleção do usuário.
            if (e.OldValue is TextHighlight && box.IsReadOnly) box.Select(0, 0);
            return;
        }

        if (range.Start < 0 || range.Start >= box.Text.Length) return;
        int length = Math.Min(range.Length, box.Text.Length - range.Start);
        box.Select(range.Start, length);

        int line = box.GetLineIndexFromCharacterIndex(range.Start);
        if (line >= 0)
        {
            box.ScrollToLine(line);
        }
    }
}
