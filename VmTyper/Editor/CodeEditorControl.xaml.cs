using System.Windows;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using ICSharpCode.AvalonEdit.Search;
using VmTyper.Models;
using WpfUserControl = System.Windows.Controls.UserControl;

namespace VmTyper.Editor;

public partial class CodeEditorControl : WpfUserControl
{
    private SearchPanel? _searchPanel;
    private bool _isUpdatingTextInternally;

    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(
            nameof(Text),
            typeof(string),
            typeof(CodeEditorControl),
            new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnTextChangedCallback));

    public static readonly DependencyProperty SourceLanguageProperty =
        DependencyProperty.Register(
            nameof(SourceLanguage),
            typeof(SourceLanguage),
            typeof(CodeEditorControl),
            new PropertyMetadata(SourceLanguage.PlainText, OnSourceLanguageChangedCallback));

    public static readonly DependencyProperty SelectedTextProperty =
        DependencyProperty.Register(
            nameof(SelectedText),
            typeof(string),
            typeof(CodeEditorControl),
            new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public SourceLanguage SourceLanguage
    {
        get => (SourceLanguage)GetValue(SourceLanguageProperty);
        set => SetValue(SourceLanguageProperty, value);
    }

    public string SelectedText
    {
        get => (string)GetValue(SelectedTextProperty);
        set => SetValue(SelectedTextProperty, value);
    }

    public int CaretOffset
    {
        get => TextEditor.CaretOffset;
        set => TextEditor.CaretOffset = Math.Clamp(value, 0, TextEditor.Text.Length);
    }

    public int SelectionStart => TextEditor.SelectionStart;
    public int SelectionLength => TextEditor.SelectionLength;

    public CodeEditorControl()
    {
        InitializeComponent();

        try
        {
            TextEditor.Options.HighlightCurrentLine = true;
            TextEditor.Options.EnableHyperlinks = false;
            TextEditor.Options.ConvertTabsToSpaces = false;

            _searchPanel = SearchPanel.Install(TextEditor);

            TextEditor.TextChanged += TextEditor_TextChanged;
            TextEditor.TextArea.Caret.PositionChanged += Caret_PositionChanged;
            TextEditor.TextArea.SelectionChanged += TextArea_SelectionChanged;

            Services.ThemeManager.Instance.ThemeChanged += ThemeManager_ThemeChanged;
            Loaded += (_, _) => UpdateEditorTheme();
            Unloaded += (_, _) => Services.ThemeManager.Instance.ThemeChanged -= ThemeManager_ThemeChanged;

            UpdateSyntaxHighlighting();
            UpdateEditorTheme();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CodeEditorControl] Erro na inicialização: {ex}");
        }
    }

    private void ThemeManager_ThemeChanged(object? sender, bool isDark)
    {
        Dispatcher.Invoke(() =>
        {
            UpdateSyntaxHighlighting();
            UpdateEditorTheme();
        });
    }

    public void UpdateEditorTheme()
    {
        try
        {
            System.Windows.Media.Brush? Find(string key) => TryFindResource(key) as System.Windows.Media.Brush;

            if (Find("EditorBackgroundBrush") is { } bg)
            {
                TextEditor.Background = bg;
            }
            if (Find("EditorForegroundBrush") is { } fg)
            {
                TextEditor.Foreground = fg;
            }
            if (Find("EditorLineNumberBrush") is { } lnb)
            {
                TextEditor.LineNumbersForeground = lnb;
            }
            if (Find("EditorSelectionBrush") is { } sel)
            {
                TextEditor.TextArea.SelectionBrush = sel;
                TextEditor.TextArea.SelectionBorder = null;
                TextEditor.TextArea.SelectionForeground = null; // mantém a cor da sintaxe sobre a seleção
            }
            if (Find("EditorCaretBrush") is { } caret)
            {
                TextEditor.TextArea.Caret.CaretBrush = caret;
            }
            if (Find("EditorCurrentLineBrush") is { } cur)
            {
                var view = TextEditor.TextArea.TextView;
                view.CurrentLineBackground = cur;
                view.CurrentLineBorder = Find("EditorCurrentLineBorderBrush") is { } curBorder
                    ? new System.Windows.Media.Pen(curBorder, 1)
                    : null;
            }

            // Painel de busca (Ctrl+F): acompanha as superfícies e o marcador de resultados do tema.
            if (_searchPanel != null)
            {
                if (Find("SurfaceElevatedBrush") is { } panelBg) _searchPanel.Background = panelBg;
                if (Find("TextPrimaryBrush") is { } panelFg) _searchPanel.Foreground = panelFg;
                if (Find("BorderBrush") is { } panelBorder) _searchPanel.BorderBrush = panelBorder;
                if (Find("EditorMarkerBrush") is { } marker) _searchPanel.MarkerBrush = marker;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CodeEditorControl] Erro ao atualizar tema do editor: {ex}");
        }
    }

    private static void OnTextChangedCallback(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CodeEditorControl control && !control._isUpdatingTextInternally)
        {
            string newText = (string)e.NewValue ?? string.Empty;
            if (control.TextEditor.Text != newText)
            {
                control.TextEditor.Text = newText;
            }
        }
    }

    private static void OnSourceLanguageChangedCallback(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CodeEditorControl control)
        {
            control.UpdateSyntaxHighlighting();
        }
    }

    private void TextEditor_TextChanged(object? sender, EventArgs e)
    {
        _isUpdatingTextInternally = true;
        try
        {
            Text = TextEditor.Text;
        }
        finally
        {
            _isUpdatingTextInternally = false;
        }
    }

    private void Caret_PositionChanged(object? sender, EventArgs e)
    {
        var pos = TextEditor.TextArea.Caret.Location;
        PositionText.Text = $"Lin {pos.Line}, Col {pos.Column}";
    }

    private void TextArea_SelectionChanged(object? sender, EventArgs e)
    {
        string sel = TextEditor.SelectedText ?? string.Empty;
        SelectedText = sel;
        SelectionInfoText.Text = sel.Length > 0 ? $"{sel.Length:N0} caracteres selecionados" : "Nada selecionado";
    }

    public void UpdateSyntaxHighlighting()
    {
        TextEditor.SyntaxHighlighting = SyntaxHighlightingManager.GetDefinition(SourceLanguage);
    }

    public void SelectRange(int startOffset, int length)
    {
        if (startOffset >= 0 && startOffset + length <= TextEditor.Text.Length)
        {
            TextEditor.Select(startOffset, length);
            TextEditor.ScrollTo(TextEditor.TextArea.Caret.Line, TextEditor.TextArea.Caret.Column);
        }
    }

    public void ScrollToAndSelect(int startOffset, int endOffset, int startLine)
    {
        try
        {
            if (TextEditor.Document != null)
            {
                int maxOffset = TextEditor.Document.TextLength;
                int start = Math.Clamp(startOffset, 0, maxOffset);
                int end = Math.Clamp(endOffset, start, maxOffset);
                TextEditor.ScrollTo(Math.Max(1, startLine), 1);
                TextEditor.Select(start, Math.Max(0, end - start));
                TextEditor.Focus();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CodeEditorControl] Erro ao selecionar bloco: {ex.Message}");
        }
    }

    public void ScrollToLine(int line)
    {
        if (line > 0 && line <= TextEditor.LineCount)
        {
            TextEditor.ScrollToLine(line);
        }
    }

    // --- API pública usada pela barra de ferramentas e atalhos da janela principal ---------------------
    public bool CanUndo => TextEditor.CanUndo;
    public bool CanRedo => TextEditor.CanRedo;
    public int LineCount => TextEditor.LineCount;

    public void Undo()
    {
        if (TextEditor.CanUndo) TextEditor.Undo();
    }

    public void Redo()
    {
        if (TextEditor.CanRedo) TextEditor.Redo();
    }

    public void OpenSearch()
    {
        _searchPanel?.Open();
        TextEditor.Focus();
    }

    public void FocusEditor() => TextEditor.Focus();

    /// <summary>Destaca o trecho em digitação (offset &lt; 0 ou length 0 remove o destaque) e o mantém visível.</summary>
    public void SetTypingHighlight(int offset, int length)
    {
        try
        {
            var doc = TextEditor.Document;
            if (doc == null) return;

            _typingHighlight ??= InstallTypingHighlight();
            if (offset < 0 || length <= 0 || offset >= doc.TextLength)
            {
                _typingHighlight.Segment = null;
            }
            else
            {
                length = Math.Min(length, doc.TextLength - offset);
                _typingHighlight.Segment = new TextSegment { StartOffset = offset, Length = length };
                BringLineIntoView(doc.GetLineByOffset(offset).LineNumber);
            }
            TextEditor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CodeEditorControl] Erro ao destacar digitação: {ex.Message}");
        }
    }

    private TypingHighlightRenderer InstallTypingHighlight()
    {
        var renderer = new TypingHighlightRenderer(TextEditor.TextArea.TextView, () =>
            TryFindResource("EditorTypingHighlightBrush") as System.Windows.Media.Brush ?? DefaultHighlightBrush);
        TextEditor.TextArea.TextView.BackgroundRenderers.Add(renderer);
        return renderer;
    }

    private static readonly System.Windows.Media.Brush DefaultHighlightBrush = CreateDefaultHighlightBrush();

    private static System.Windows.Media.Brush CreateDefaultHighlightBrush()
    {
        var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x99, 0xFF, 0xB3, 0x00));
        brush.Freeze();
        return brush;
    }

    private void BringLineIntoView(int line)
    {
        var view = TextEditor.TextArea.TextView;
        if (!view.VisualLinesValid || view.VisualLines.Count == 0) return;
        int first = view.VisualLines[0].FirstDocumentLine.LineNumber;
        int last = view.VisualLines[^1].LastDocumentLine.LineNumber;
        if (line <= first || line >= last)
        {
            TextEditor.ScrollToLine(line);
        }
    }

    private TypingHighlightRenderer? _typingHighlight;

    private sealed class TypingHighlightRenderer : IBackgroundRenderer
    {
        private readonly TextView _view;
        private readonly Func<System.Windows.Media.Brush> _brush;

        public TypingHighlightRenderer(TextView view, Func<System.Windows.Media.Brush> brush)
        {
            _view = view;
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

    /// <summary>Move o cursor para o início da linha informada (1-based, limitada ao documento).</summary>
    public void GoToLine(int line)
    {
        if (TextEditor.Document == null || TextEditor.LineCount == 0) return;
        int target = Math.Clamp(line, 1, TextEditor.LineCount);
        var docLine = TextEditor.Document.GetLineByNumber(target);
        TextEditor.CaretOffset = docLine.Offset;
        TextEditor.ScrollToLine(target);
        TextEditor.Focus();
    }

    /// <summary>Abre o diálogo "Ir para linha" (herda tema e estilos do sistema de design).</summary>
    public void ShowGoToLineDialog()
    {
        var window = new Window
        {
            Title = "Ir para linha",
            Width = 320,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Window.GetWindow(this),
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false
        };
        window.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "SurfaceBrush");
        window.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "TextPrimaryBrush");

        var sp = new System.Windows.Controls.StackPanel { Margin = new Thickness(16) };
        sp.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = $"Número da linha (1 a {TextEditor.LineCount}):",
            Margin = new Thickness(0, 0, 0, 8)
        });
        var tb = new System.Windows.Controls.TextBox { Text = TextEditor.TextArea.Caret.Line.ToString() };
        sp.Children.Add(tb);

        var buttons = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var ok = new System.Windows.Controls.Button { Content = "Ir", MinWidth = 72, IsDefault = true };
        ok.Style = (Style)System.Windows.Application.Current.FindResource("PrimaryButtonStyle");
        var cancel = new System.Windows.Controls.Button { Content = "Cancelar", MinWidth = 72, IsCancel = true };
        ok.Click += (_, _) =>
        {
            if (int.TryParse(tb.Text, out int targetLine))
            {
                GoToLine(targetLine);
                window.DialogResult = true;
            }
            else
            {
                tb.SelectAll();
                tb.Focus();
            }
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        sp.Children.Add(buttons);
        window.Content = sp;
        window.Loaded += (_, _) => { tb.Focus(); tb.SelectAll(); };
        window.ShowDialog();
    }

    public void ZoomIn() => ZoomIn_Click(this, new RoutedEventArgs());
    public void ZoomOut() => ZoomOut_Click(this, new RoutedEventArgs());

    private void ZoomIn_Click(object sender, RoutedEventArgs e)
    {
        if (TextEditor.FontSize < 36)
        {
            TextEditor.FontSize += 1;
            FontSizeText.Text = TextEditor.FontSize.ToString("0");
        }
    }

    private void ZoomOut_Click(object sender, RoutedEventArgs e)
    {
        if (TextEditor.FontSize > 8)
        {
            TextEditor.FontSize -= 1;
            FontSizeText.Text = TextEditor.FontSize.ToString("0");
        }
    }
}
