using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using VmTyper.Helpers;

namespace VmTyper.Models;

/// <summary>
/// Fonte única da verdade para edição e divisão estrutural de código (§Fase 5.2).
/// Preserva o texto integral, quebras de linha e codificação original.
/// </summary>
public sealed class SourceDocument : ObservableObject
{
    public Guid Id { get; set; } = Guid.NewGuid();

    private string _name = "Novo Documento";
    public string Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }

    private string? _filePath;
    public string? FilePath
    {
        get => _filePath;
        set => SetField(ref _filePath, value);
    }

    private SourceLanguage _language = SourceLanguage.PlainText;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SourceLanguage Language
    {
        get => _language;
        set
        {
            if (SetField(ref _language, value))
            {
                OnPropertyChanged(nameof(LanguageLabel));
            }
        }
    }

    private bool _isManualLanguage;
    public bool IsManualLanguage
    {
        get => _isManualLanguage;
        set => SetField(ref _isManualLanguage, value);
    }

    private string _text = string.Empty;
    public string Text
    {
        get => _text;
        set
        {
            if (SetField(ref _text, value))
            {
                Revision++;
                OnPropertyChanged(nameof(CharacterCount));
                OnPropertyChanged(nameof(LineCount));
                OnPropertyChanged(nameof(IsModified));
            }
        }
    }

    private string? _savedText;
    /// <summary>
    /// Texto no momento da última abertura/gravação em disco. <c>null</c> = nunca salvo
    /// (o documento só é considerado modificado se tiver conteúdo).
    /// </summary>
    public string? SavedText
    {
        get => _savedText;
        set
        {
            if (SetField(ref _savedText, value))
            {
                OnPropertyChanged(nameof(IsModified));
            }
        }
    }

    /// <summary>Indica alterações ainda não gravadas (mostrado como "●" na aba do documento).</summary>
    [JsonIgnore]
    public bool IsModified => _savedText is null
        ? !string.IsNullOrEmpty(_text)
        : !string.Equals(_savedText, _text, StringComparison.Ordinal);

    /// <summary>Nome da linguagem para exibição compacta na aba.</summary>
    [JsonIgnore]
    public string LanguageLabel => Language switch
    {
        SourceLanguage.PlainText => "Texto",
        SourceLanguage.SqlFirebird => "SQL",
        _ => Language.ToString()
    };

    private string _encodingName = "UTF-8";
    public string EncodingName
    {
        get => _encodingName;
        set => SetField(ref _encodingName, value);
    }

    private int _revision;
    public int Revision
    {
        get => _revision;
        set => SetField(ref _revision, value);
    }

    private int _splitLevel = 3; // Nível 1: Arquivo, Nível 2: Seções, Nível 3: Declarações/Métodos, Nível 4: Personalizado
    public int SplitLevel
    {
        get => _splitLevel;
        set => SetField(ref _splitLevel, value);
    }

    public ObservableCollection<CodeBlock> Blocks { get; set; } = new();

    private bool _isSelected;
    [JsonIgnore]
    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    public int CharacterCount => Text?.Length ?? 0;
    public int LineCount => string.IsNullOrEmpty(Text) ? 0 : Text.Split('\n').Length;
}

