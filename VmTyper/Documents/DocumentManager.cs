using System.Collections.ObjectModel;
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
            if (SetField(ref _currentDocument, value))
            {
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
        set => SetField(ref _selectedBlock, value);
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

    public DocumentManager(DocumentPersistenceService? persistence = null)
    {
        _persistence = persistence ?? new DocumentPersistenceService();
        LoadPersistedDocuments();
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

        CurrentDocument.Text = newText;

        // Se a linguagem não foi definida manualmente pelo usuário, reavalia a detecção por conteúdo
        if (!CurrentDocument.IsManualLanguage)
        {
            var detect = LanguageDetectionService.Detect(CurrentDocument.FilePath, newText);
            if (detect.Confidence >= DetectionConfidence.Medium)
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

        var blocks = CodeSplitterService.SplitDocument(CurrentDocument);
        CurrentDocument.Blocks.Clear();
        foreach (var b in blocks)
        {
            CurrentDocument.Blocks.Add(b);
        }

        SelectedBlock = CurrentDocument.Blocks.FirstOrDefault();
        SaveAll();
    }

    public bool SplitCurrentBlockAtCaret(int caretOffset)
    {
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
        if (CurrentDocument == null || startOffset >= endOffset) return false;

        var block = CodeSplitterService.CreateBlockFromSelection(CurrentDocument, startOffset, endOffset, title);
        CurrentDocument.Blocks.Add(block);
        SelectedBlock = block;
        SaveAll();
        return true;
    }

    public bool MergeBlockWithNext(CodeBlock block)
    {
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
