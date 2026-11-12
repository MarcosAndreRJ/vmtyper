using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using Brush = System.Windows.Media.Brush;

namespace VmTyper.Editor;

/// <summary>Pinta, na camada de seleção do AvalonEdit, o trecho que está sendo digitado.</summary>
public sealed class TypingHighlightRenderer : IBackgroundRenderer
{
    private readonly Func<Brush> _brush;

    public TypingHighlightRenderer(Func<Brush> brush)
    {
        _brush = brush;
    }

    public TextSegment? Segment { get; set; }

    public KnownLayer Layer => KnownLayer.Selection;

    public void Draw(TextView textView, System.Windows.Media.DrawingContext drawingContext)
    {
        if (Segment is not { } segment || !textView.VisualLinesValid) return;
        var builder = new BackgroundGeometryBuilder { CornerRadius = 2, AlignToWholePixels = true };
        builder.AddSegment(textView, segment);
        if (builder.CreateGeometry() is { } geometry)
        {
            drawingContext.DrawGeometry(_brush(), null, geometry);
        }
    }
}
