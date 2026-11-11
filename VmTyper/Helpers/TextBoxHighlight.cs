using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using VmTyper.Models;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;
using TextBox = System.Windows.Controls.TextBox;

namespace VmTyper.Helpers;

/// <summary>
/// Comportamento anexado que destaca o trecho de um <see cref="TextBox"/> que está sendo
/// digitado e o mantém visível. O realce é desenhado numa camada de adorno, então aparece
/// mesmo com a janela inativa (a seleção nativa fica apagada nesse caso).
/// </summary>
public static class TextBoxHighlight
{
    public static readonly DependencyProperty RangeProperty =
        DependencyProperty.RegisterAttached(
            "Range",
            typeof(TextHighlight?),
            typeof(TextBoxHighlight),
            new PropertyMetadata(null, OnRangeChanged));

    private static readonly DependencyProperty AdornerProperty =
        DependencyProperty.RegisterAttached("Adorner", typeof(HighlightAdorner), typeof(TextBoxHighlight));

    public static TextHighlight? GetRange(DependencyObject d) => (TextHighlight?)d.GetValue(RangeProperty);

    public static void SetRange(DependencyObject d, TextHighlight? value) => d.SetValue(RangeProperty, value);

    private static void OnRangeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box) return;

        var adorner = EnsureAdorner(box);
        var range = e.NewValue as TextHighlight?;
        if (adorner is not null)
        {
            adorner.Range = range;
        }

        if (range is { } r && r.Start >= 0 && r.Start < box.Text.Length)
        {
            int line = box.GetLineIndexFromCharacterIndex(r.Start);
            if (line >= 0)
            {
                box.ScrollToLine(line);
            }
        }
    }

    private static HighlightAdorner? EnsureAdorner(TextBox box)
    {
        if (box.GetValue(AdornerProperty) is HighlightAdorner existing) return existing;

        var layer = AdornerLayer.GetAdornerLayer(box);
        if (layer is null) return null;

        var adorner = new HighlightAdorner(box);
        layer.Add(adorner);
        box.SetValue(AdornerProperty, adorner);
        return adorner;
    }

    private sealed class HighlightAdorner : Adorner
    {
        private static readonly Brush Fill = Freeze(new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xC1, 0x07)));
        private static readonly Pen Outline = new(Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07))), 1);

        private readonly TextBox _box;
        private TextHighlight? _range;

        public HighlightAdorner(TextBox box) : base(box)
        {
            _box = box;
            IsHitTestVisible = false;
            box.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => InvalidateVisual()));
            box.TextChanged += (_, _) => InvalidateVisual();
            box.SizeChanged += (_, _) => InvalidateVisual();
        }

        public TextHighlight? Range
        {
            get => _range;
            set
            {
                _range = value;
                InvalidateVisual();
            }
        }

        private static Brush Freeze(Brush brush)
        {
            brush.Freeze();
            return brush;
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (_range is not { } r || r.Start < 0 || r.Start >= _box.Text.Length) return;

            dc.PushClip(new RectangleGeometry(new Rect(0, 0, _box.ActualWidth, _box.ActualHeight)));
            int end = Math.Min(r.Start + Math.Max(1, r.Length), _box.Text.Length);

            // Um retângulo por linha visual: a palavra pode quebrar entre duas linhas.
            Rect current = Rect.Empty;
            for (int i = r.Start; i < end; i++)
            {
                var rect = _box.GetRectFromCharacterIndex(i);
                if (rect.IsEmpty || double.IsInfinity(rect.Top)) continue;
                var charRect = new Rect(rect.Left, rect.Top, Math.Max(rect.Width, 1), rect.Height);

                if (!current.IsEmpty && Math.Abs(current.Top - charRect.Top) < 1)
                {
                    current.Union(charRect);
                }
                else
                {
                    Draw(dc, current);
                    current = charRect;
                }
            }
            Draw(dc, current);
            dc.Pop();
        }

        private static void Draw(DrawingContext dc, Rect rect)
        {
            if (!rect.IsEmpty)
            {
                dc.DrawRoundedRectangle(Fill, Outline, rect, 2, 2);
            }
        }
    }
}
