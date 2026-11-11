using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Threading;
using VmTyper.CodeAnalysis;
using VmTyper.Helpers;
using VmTyper.Models;
using VmTyper.Services;

namespace VmTyper.Documents;

/// <summary>
/// Gerenciador central de documentos de código e blocos derivados (§Fase 5.2).
/// Garante que o documento original seja a única fonte da verdade e que os blocos
/// representem intervalos sobre ele.
/// </summary>
public sealed class DocumentManager : ObservableObject
{
    private readonly DocumentPersistenceService _persistence;

    public ObservableCollection<SourceDocument> Documents { get; } = new();

    private SourceDocument? _currentDocument;
    public SourceDocument? CurrentDocument
    {
        get => _currentDocument;
        set
        {
            var previous = _currentDocument;
            if (!ReferenceEquals(previous, value))
            {
                // Garante que o texto pendente do documento anterior já esteja dividido antes de trocar.
                FlushPendingSplit();
            }

            if (SetField(ref _currentDocument, value))
            {
                if (previous != null) previous.PropertyChanged -= OnWatchedDocumentChanged;
                if (value != null) value.PropertyChanged += OnWatchedDocumentChanged;

                foreach (var d in Documents)
                {
                    d.IsSelected = (d == value);
                }
                OnPropertyChanged(nameof(HasDocument));
                OnPropertyChanged(nameof(CurrentLanguage));
                OnPropertyChanged(nameof(CurrentSplitLevel));
                SelectedBlock = value?.Blocks.FirstOrDefault();
            }
        }
    }

    private CodeBlock? _selectedBlock;
    public CodeBlock? SelectedBlock
    {
        get => _selectedBlock;
        set
        {
            if (SetField(ref _selectedBlock, value))
            {
                if (CurrentDocument != null)
                {
                    foreach (var b in CurrentDocument.Blocks)
                    {
                        b.IsSelected = (b == value);
                    }
                }
            }
        }
    }

    public bool HasDocument => CurrentDocument != null;

    public SourceLanguage CurrentLanguage
    {
        get => CurrentDocument?.Language ?? SourceLanguage.PlainText;
        set
        {
            if (CurrentDocument != null && CurrentDocument.Language != value)
            {
                CurrentDocument.Language = value;
                CurrentDocument.IsManualLanguage = true;
                OnPropertyChanged(nameof(CurrentLanguage));
                ReSplitCurrentDocument();
            }
        }
    }

    public int CurrentSplitLevel
    {
        get => CurrentDocument?.SplitLevel ?? 3;
        set
        {
            if (CurrentDocument != null && CurrentDocument.SplitLevel != value)
            {
                CurrentDocument.SplitLevel = value;
                OnPropertyChanged(nameof(CurrentSplitLevel));
                ReSplitCurrentDocument();
            }
        }
    }

    // --- Divisão automática ao digitar (debounce) -------------------------------------------------
    // O editor grava direto em SourceDocument.Text; sem isto os blocos ficavam desatualizados
    // (botão "Digitar" desabilitado / "Documento vazio" e risco de digitar o intervalo errado).
    private readonly Dispatcher _dispatcher;
    private DispatcherTimer? _splitTimer;
    private SourceDocument? _pendingSplitDocument;
    private bool _suppressTextWatch;

    /// <summary>Atraso entre a última edição e a nova divisão estrutural.</summary>
    public TimeSpan SplitDebounce { get; set; } = TimeSpan.FromMilliseconds(350);

    /// <summary>Há edição de texto ainda não refletida nos blocos.</summary>
    public bool HasPendingSplit => _pendingSplitDocument != null;

    /// <summary>Disparado depois que os blocos do documento atual foram recalculados por edição de texto.</summary>
    public event Action? BlocksRefreshed;

    public DocumentManager(DocumentPersistenceService? persistence = null)
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _persistence = persistence ?? new DocumentPersistenceService();
        LoadPersistedDocuments();
    }

    private void OnWatchedDocumentChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressTextWatch) return;
        if (e.PropertyName != nameof(SourceDocument.Text)) return;
        if (sender is not SourceDocument doc) return;

        _pendingSplitDocument = doc;
        OnPropertyChanged(nameof(HasPendingSplit));

        if (_splitTimer == null)
        {
            _splitTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher);
            _splitTimer.Tick += (_, _) => FlushPendingSplit();
        }
        _splitTimer.Stop();
        _splitTimer.Interval = SplitDebounce;
        _splitTimer.Start();
    }

    /// <summary>
    /// Aplica imediatamente a divisão pendente por edição. Deve ser chamado antes de qualquer digitação
    /// de bloco/estrutura para que os intervalos correspondam ao texto atual.
    /// </summary>
    public void FlushPendingSplit()
    {
        _splitTimer?.Stop();
        var doc = _pendingSplitDocument;
        if (doc == null) return;
        _pendingSplitDocument = null;
        OnPropertyChanged(nameof(HasPendingSplit));

        if (!Documents.Contains(doc)) return;

        if (!doc.IsManualLanguage)
        {
            var detect = LanguageDetectionService.Detect(doc.FilePath, doc.Text);
            // DetectionConfidence é ordenado de High (0) a Uncertain (3): "no mínimo Medium" = <= Medium.
            if (detect.Confidence <= DetectionConfidence.Medium && detect.Language != doc.Language)
            {
                doc.Language = detect.Language;
                if (ReferenceEquals(doc, CurrentDocument)) OnPropertyChanged(nameof(CurrentLanguage));
            }
        }

        int previousIndex = ReferenceEquals(doc, CurrentDocument) && SelectedBlock != null
            ? Math.Max(0, doc.Blocks.IndexOf(SelectedBlock))
            : 0;

        ResplitDocument(doc);

        if (ReferenceEquals(doc, CurrentDocument))
        {
            var target = doc.Blocks.Count == 0
                ? null
                : doc.Blocks[Math.Min(previousIndex, doc.Blocks.Count - 1)];
            SelectBlockInternal(target);
            BlocksRefreshed?.Invoke();
        }

        SaveAll();
    }

    private void ResplitDocument(SourceDocument doc)
    {
        var blocks = CodeSplitterService.SplitDocument(doc);
        doc.Blocks.Clear();
        foreach (var b in blocks)
        {
            doc.Blocks.Add(b);
        }
    }

    private void SelectBlockInternal(CodeBlock? block)
    {
        _selectedBlock = block;
        if (CurrentDocument != null)
        {
            foreach (var b in CurrentDocument.Blocks)
            {
                b.IsSelected = (b == block);
            }
        }
        OnPropertyChanged(nameof(SelectedBlock));
    }

    // --- Gravação em disco ---------------------------------------------------------------------
    /// <summary>Grava o documento no caminho informado (ou no próprio FilePath), preservando a codificação.</summary>
    public bool SaveDocument(SourceDocument doc, string? path, out string? error)
    {
        error = null;
        path ??= doc.FilePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "O documento ainda não possui um arquivo associado.";
            return false;
        }

        try
        {
            var encoding = ResolveEncoding(doc, out string effectiveName);
            var bytes = new List<byte>(encoding.GetPreamble());
            bytes.AddRange(encoding.GetBytes(doc.Text));
            System.IO.File.WriteAllBytes(path, bytes.ToArray());

            doc.FilePath = path;
            doc.Name = System.IO.Path.GetFileName(path);
            doc.EncodingName = effectiveName;
            doc.SavedText = doc.Text;
            SaveAll();
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static System.Text.Encoding ResolveEncoding(SourceDocument doc, out string effectiveName)
    {
        string name = doc.EncodingName ?? "UTF-8";
        effectiveName = name;

        if (name.Contains("UTF-16BE", StringComparison.OrdinalIgnoreCase))
            return new System.Text.UnicodeEncoding(true, true);
        if (name.Contains("UTF-16", StringComparison.OrdinalIgnoreCase))
            return new System.Text.UnicodeEncoding(false, true);
        if (name.Contains("BOM", StringComparison.OrdinalIgnoreCase))
            return new System.Text.UTF8Encoding(true);

        if (name.Contains("1252", StringComparison.OrdinalIgnoreCase)
            || name.Contains("8859", StringComparison.OrdinalIgnoreCase)
            || name.Contains("ANSI", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Latin", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
                var strict = System.Text.Encoding.GetEncoding(
                    1252,
                    System.Text.EncoderFallback.ExceptionFallback,
                    System.Text.DecoderFallback.ExceptionFallback);
                strict.GetByteCount(doc.Text); // lança se algum caractere não existir em Windows-1252
                effectiveName = "Windows-1252";
                return strict;
            }
            catch
            {
                // Há caracteres que não cabem em ANSI: grava em UTF-8 com BOM para não perder dados.
                effectiveName = "UTF-8-BOM";
                return new System.Text.UTF8Encoding(true);
            }
        }

        effectiveName = "UTF-8";
        return new System.Text.UTF8Encoding(false);
    }

    public void LoadPersistedDocuments()
    {
        var loaded = _persistence.Load();
        Documents.Clear();
        foreach (var doc in loaded)
        {
            Documents.Add(doc);
        }

        if (Documents.Count > 0)
        {
            CurrentDocument = Documents[0];
        }
        else
        {
            // Garante sempre pelo menos um documento inicial ativo para edição imediata
            CreateNewDocument(string.Empty, "Documento 1");
        }
    }

    public SourceDocument OpenFile(string filePath)
    {
        var readResult = FileTextReader.ReadFile(filePath);
        string fileName = System.IO.Path.GetFileName(filePath);

        var detectResult = LanguageDetectionService.Detect(filePath, readResult.Text);

        var doc = new SourceDocument
        {
            Name = fileName,
            FilePath = filePath,
            Text = readResult.Text,
            EncodingName = readResult.EncodingName,
            SavedText = readResult.Text,
            Language = detectResult.Language,
            IsManualLanguage = false,
            SplitLevel = 3
        };

        var blocks = CodeSplitterService.SplitDocument(doc);
        doc.Blocks.Clear();
        foreach (var b in blocks)
        {
            doc.Blocks.Add(b);
        }

        Documents.Add(doc);
        CurrentDocument = doc;
        SaveAll();

        return doc;
    }

    public SourceDocument CreateNewDocument(string text = "", string name = "Novo Documento")
    {
        var detectResult = LanguageDetectionService.DetectFromContent(text);

        var doc = new SourceDocument
        {
            Name = name,
            Text = text,
            Language = detectResult.Language,
            IsManualLanguage = false,
            SplitLevel = 3
        };

        var blocks = CodeSplitterService.SplitDocument(doc);
        doc.Blocks.Clear();
        foreach (var b in blocks)
        {
            doc.Blocks.Add(b);
        }

        Documents.Add(doc);
        CurrentDocument = doc;
        SaveAll();

        return doc;
    }

    public void UpdateDocumentText(string newText)
    {
        if (CurrentDocument == null) return;
        if (CurrentDocument.Text == newText) return;

        _suppressTextWatch = true;
        try { CurrentDocument.Text = newText; }
        finally { _suppressTextWatch = false; }
        _pendingSplitDocument = null;
        _splitTimer?.Stop();

        // Se a linguagem não foi definida manualmente pelo usuário, reavalia a detecção por conteúdo
        if (!CurrentDocument.IsManualLanguage)
        {
            var detect = LanguageDetectionService.Detect(CurrentDocument.FilePath, newText);
            if (detect.Confidence <= DetectionConfidence.Medium)
            {
                CurrentDocument.Language = detect.Language;
                OnPropertyChanged(nameof(CurrentLanguage));
            }
        }

        ReSplitCurrentDocument();
    }

    public void ReSplitCurrentDocument()
    {
        if (CurrentDocument == null) return;

        _pendingSplitDocument = null;
        _splitTimer?.Stop();
        ResplitDocument(CurrentDocument);

        var first = CurrentDocument.Blocks.FirstOrDefault();
        _selectedBlock = first;
        foreach (var b in CurrentDocument.Blocks)
        {
            b.IsSelected = (b == first);
        }
        OnPropertyChanged(nameof(SelectedBlock));
        SaveAll();
    }

    public bool SplitCurrentBlockAtCaret(int caretOffset)
    {
        FlushPendingSplit();
        if (CurrentDocument == null || SelectedBlock == null) return false;

        var result = CodeSplitterService.SplitBlockAtOffset(CurrentDocument, SelectedBlock, caretOffset);
        if (result == null) return false;

        int index = CurrentDocument.Blocks.IndexOf(SelectedBlock);
        if (index >= 0)
        {
            CurrentDocument.Blocks.RemoveAt(index);
            CurrentDocument.Blocks.Insert(index, result.Value.second);
            CurrentDocument.Blocks.Insert(index, result.Value.first);
            SelectedBlock = result.Value.first;
            SaveAll();
            return true;
        }

        return false;
    }

    public bool CreateBlockFromSelection(int startOffset, int endOffset, string? title = null)
    {
        FlushPendingSplit();
        if (CurrentDocument == null || startOffset >= endOffset) return false;

        var block = CodeSplitterService.CreateBlockFromSelection(CurrentDocument, startOffset, endOffset, title);
        CurrentDocument.Blocks.Add(block);
        SelectedBlock = block;
        SaveAll();
        return true;
    }

    public bool MergeBlockWithNext(CodeBlock block)
    {
        FlushPendingSplit();
        if (CurrentDocument == null) return false;

        int index = CurrentDocument.Blocks.IndexOf(block);
        if (index >= 0 && index + 1 < CurrentDocument.Blocks.Count)
        {
            var next = CurrentDocument.Blocks[index + 1];
            var merged = CodeSplitterService.MergeAdjacentBlocks(CurrentDocument, block, next);
            if (merged != null)
            {
                CurrentDocument.Blocks.RemoveAt(index + 1);
                CurrentDocument.Blocks[index] = merged;
                SelectedBlock = merged;
                SaveAll();
                return true;
            }
        }

        return false;
    }

    public void CloseDocument(SourceDocument doc)
    {
        Documents.Remove(doc);
        if (CurrentDocument == doc)
        {
            CurrentDocument = Documents.FirstOrDefault();
        }
        SaveAll();
    }

    public void SaveAll()
    {
        try
        {
            _persistence.Save(Documents.ToList());
        }
        catch { }
    }
}

