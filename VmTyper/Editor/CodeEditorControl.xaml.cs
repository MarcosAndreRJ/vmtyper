using System.Windows;
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

        TextEditor.Options.HighlightCurrentLine = true;
        TextEditor.Options.EnableHyperlinks = false;
        TextEditor.Options.ConvertTabsToSpaces = false;

        _searchPanel = SearchPanel.Install(TextEditor);

        TextEditor.TextChanged += TextEditor_TextChanged;
        TextEditor.TextArea.Caret.PositionChanged += Caret_PositionChanged;
        TextEditor.TextArea.SelectionChanged += TextArea_SelectionChanged;

        UpdateSyntaxHighlighting();
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
        SelectionInfoText.Text = sel.Length > 0 ? $"{sel.Length} carac. selecionados" : "0 carac. selecionados";
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

    public void ScrollToLine(int line)
    {
        if (line > 0 && line <= TextEditor.LineCount)
        {
            TextEditor.ScrollToLine(line);
        }
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (TextEditor.CanUndo) TextEditor.Undo();
    }

    private void Redo_Click(object sender, RoutedEventArgs e)
    {
        if (TextEditor.CanRedo) TextEditor.Redo();
    }

    private void Search_Click(object sender, RoutedEventArgs e)
    {
        _searchPanel?.Open();
    }

    private void GoToLine_Click(object sender, RoutedEventArgs e)
    {
        var window = new Window
        {
            Title = "Ir para linha",
            Width = 300,
            Height = 140,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Window.GetWindow(this),
            ResizeMode = ResizeMode.NoResize
        };

        var sp = new System.Windows.Controls.StackPanel { Margin = new Thickness(12) };
        sp.Children.Add(new System.Windows.Controls.TextBlock { Text = "Número da linha:", Margin = new Thickness(0, 0, 0, 6) });
        var tb = new System.Windows.Controls.TextBox { Text = TextEditor.TextArea.Caret.Line.ToString() };
        sp.Children.Add(tb);

        var btn = new System.Windows.Controls.Button
        {
            Content = "Ir",
            Width = 70,
            Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            IsDefault = true
        };
        btn.Click += (_, _) =>
        {
            if (int.TryParse(tb.Text, out int targetLine))
            {
                ScrollToLine(targetLine);
                window.Close();
            }
        };
        sp.Children.Add(btn);
        window.Content = sp;
        window.ShowDialog();
    }

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
