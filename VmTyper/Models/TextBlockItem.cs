using System.Text.Json.Serialization;
using VmTyper.Helpers;

namespace VmTyper.Models;

/// <summary>
/// Um bloco de texto dentro da fila de digitação. A identidade do bloco
/// (<see cref="Id"/>) é estável e independe da posição visual (<see
/// cref="Order"/>), que pode mudar por reordenação — isso é o que permite
/// histórico, persistência e exclusão funcionarem de forma confiável mesmo
/// depois de o usuário reordenar a fila.
/// </summary>
public sealed class TextBlockItem : ObservableObject
{
    public Guid Id { get; set; } = Guid.NewGuid();

    private int _order;
    public int Order
    {
        get => _order;
        set
        {
            if (SetField(ref _order, value))
                OnPropertyChanged(nameof(Header));
        }
    }

    private string _text = string.Empty;
    public string Text
    {
        get => _text;
        set
        {
            if (SetField(ref _text, value))
            {
                OnPropertyChanged(nameof(CharacterCount));
                OnPropertyChanged(nameof(LineCount));
                OnPropertyChanged(nameof(IsEmpty));
                OnPropertyChanged(nameof(StatusLabel));
            }
        }
    }

    private TextBlockStatus _status = TextBlockStatus.Empty;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TextBlockStatus Status
    {
        get => _status;
        set
        {
            if (SetField(ref _status, value))
            {
                OnPropertyChanged(nameof(StatusLabel));
                OnPropertyChanged(nameof(IsTypingOrPaused));
            }
        }
    }

    private bool _isNext;

    /// <summary>Calculado e notificado por <see cref="Services.TypingQueueService"/>.</summary>
    [JsonIgnore]
    public bool IsNext
    {
        get => _isNext;
        internal set
        {
            if (SetField(ref _isNext, value))
                OnPropertyChanged(nameof(StatusLabel));
        }
    }

    private int _sentUpToIndex;
    public int SentUpToIndex
    {
        get => _sentUpToIndex;
        set
        {
            if (SetField(ref _sentUpToIndex, value))
                OnPropertyChanged(nameof(StatusLabel));
        }
    }

    private string? _lastError;
    public string? LastError
    {
        get => _lastError;
        set
        {
            if (SetField(ref _lastError, value))
                OnPropertyChanged(nameof(StatusLabel));
        }
    }

    private Guid? _lastSessionId;
    public Guid? LastSessionId
    {
        get => _lastSessionId;
        set => SetField(ref _lastSessionId, value);
    }

    private string? _persistenceNote;
    [JsonIgnore]
    public string? PersistenceNote
    {
        get => _persistenceNote;
        set
        {
            if (SetField(ref _persistenceNote, value))
                OnPropertyChanged(nameof(StatusLabel));
        }
    }

    [JsonIgnore]
    public bool IsTypingOrPaused => Status is TextBlockStatus.Typing or TextBlockStatus.Paused;

    [JsonIgnore]
    public string Header => $"BLOCO {Order}";

    [JsonIgnore]
    public int CharacterCount => Text.Length;

    [JsonIgnore]
    public int LineCount => string.IsNullOrEmpty(Text) ? 0 : Text.Split('\n').Length;

    /// <summary>
    /// Um bloco só com espaços/quebras de linha é tratado como vazio (não
    /// executável); quebras de linha intencionais dentro de um texto com
    /// conteúdo real continuam preservadas normalmente na digitação.
    /// </summary>
    [JsonIgnore]
    public bool IsEmpty => string.IsNullOrWhiteSpace(Text);

    [JsonIgnore]
    public string StatusLabel
    {
        get
        {
            string baseLabel = Status switch
            {
                TextBlockStatus.Sent => "✓ Enviado",
                TextBlockStatus.Typing => "⌨ Digitando",
                TextBlockStatus.Paused => "⏸ Pausado",
                TextBlockStatus.PartiallySent => $"◐ Parcial ({SentUpToIndex})",
                TextBlockStatus.Failed => "✗ Falhou",
                TextBlockStatus.Interrupted => "⚠ Interrompido",
                TextBlockStatus.Empty => string.IsNullOrEmpty(PersistenceNote) ? "○ Vazio" : $"○ Vazio ({PersistenceNote})",
                TextBlockStatus.Ready when IsNext => "▶ Próximo",
                TextBlockStatus.Ready => "○ Pronto",
                _ => Status.ToString()
            };

            if (IsNext && Status is not TextBlockStatus.Ready and not TextBlockStatus.Typing and not TextBlockStatus.Paused)
            {
                return $"▶ Próximo · {baseLabel}";
            }

            return baseLabel;
        }
    }
}
