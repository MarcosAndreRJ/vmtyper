using System.Text.Json.Serialization;
using VmTyper.Helpers;

namespace VmTyper.Models;

/// <summary>
/// Representa um bloco inteligente derivado de um documento-fonte (§Fase 5.2).
/// Princípio fundamental: o bloco é um intervalo [StartOffset, EndOffset) no documento original,
/// garantindo uma única fonte da verdade e ausência de cópias divergentes.
/// </summary>
public sealed class CodeBlock : ObservableObject
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DocumentId { get; set; }
    public int SourceRevision { get; set; }

    private int _startOffset;
    public int StartOffset
    {
        get => _startOffset;
        set => SetField(ref _startOffset, value);
    }

    private int _endOffset;
    public int EndOffset
    {
        get => _endOffset;
        set => SetField(ref _endOffset, value);
    }

    public int Length => Math.Max(0, EndOffset - StartOffset);

    private string _title = string.Empty;
    public string Title
    {
        get => _title;
        set => SetField(ref _title, value);
    }

    private CodeBlockKind _kind = CodeBlockKind.Unclassified;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public CodeBlockKind Kind
    {
        get => _kind;
        set => SetField(ref _kind, value);
    }

    private int _startLine = 1;
    public int StartLine
    {
        get => _startLine;
        set
        {
            if (SetField(ref _startLine, value))
                OnPropertyChanged(nameof(LineRangeDisplay));
        }
    }

    private int _endLine = 1;
    public int EndLine
    {
        get => _endLine;
        set
        {
            if (SetField(ref _endLine, value))
                OnPropertyChanged(nameof(LineRangeDisplay));
        }
    }

    private TextBlockStatus _status = TextBlockStatus.Ready;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TextBlockStatus Status
    {
        get => _status;
        set => SetField(ref _status, value);
    }

    private bool _isEnabled = true;
    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetField(ref _isEnabled, value);
    }

    private string? _warningMessage;
    public string? WarningMessage
    {
        get => _warningMessage;
        set => SetField(ref _warningMessage, value);
    }

    private bool _isSelected;
    [JsonIgnore]
    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    public string LineRangeDisplay => StartLine == EndLine ? $"L{StartLine}" : $"L{StartLine}–L{EndLine}";

    /// <summary>Rótulo curto, em português, do tipo estrutural (usado no navegador de estrutura).</summary>
    [JsonIgnore]
    public string KindLabel => Kind switch
    {
        CodeBlockKind.FullDocument => "Documento",
        CodeBlockKind.DelphiUnitHeader => "Cabeçalho da unit",
        CodeBlockKind.DelphiInterface => "Interface",
        CodeBlockKind.DelphiUses => "Uses",
        CodeBlockKind.DelphiTypeSection => "Types",
        CodeBlockKind.DelphiConstSection => "Const",
        CodeBlockKind.DelphiVarSection => "Var",
        CodeBlockKind.DelphiImplementation => "Implementation",
        CodeBlockKind.DelphiMethod => "Método",
        CodeBlockKind.DelphiInitialization => "Initialization",
        CodeBlockKind.DelphiFinalization => "Finalization",
        CodeBlockKind.DelphiUnitEnd => "Fim da unit",
        CodeBlockKind.SqlCommand => "Comando SQL",
        CodeBlockKind.SqlDdl => "DDL",
        CodeBlockKind.SqlProcedure => "Procedure",
        CodeBlockKind.SqlTrigger => "Trigger",
        CodeBlockKind.SqlExecuteBlock => "Execute block",
        CodeBlockKind.SqlSetTerm => "SET TERM",
        CodeBlockKind.CustomSelection => "Seleção manual",
        _ => "Bloco"
    };

    public string GetText(string documentText)
    {
        if (string.IsNullOrEmpty(documentText) || StartOffset < 0) return string.Empty;
        int start = Math.Clamp(StartOffset, 0, documentText.Length);
        int end = Math.Clamp(EndOffset, start, documentText.Length);
        return documentText.Substring(start, end - start);
    }
}

