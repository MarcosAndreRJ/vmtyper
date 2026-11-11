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
                NotifyVisualStateChanged();
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
                OnPropertyChanged(nameof(IsTypingOrPaused));
                NotifyVisualStateChanged();
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
                NotifyVisualStateChanged();
        }
    }

    private int _sentUpToIndex;
    public int SentUpToIndex
    {
        get => _sentUpToIndex;
        set
        {
            if (SetField(ref _sentUpToIndex, value))
                NotifyVisualStateChanged();
        }
    }

    private string? _lastError;
    public string? LastError
    {
        get => _lastError;
        set
        {
            if (SetField(ref _lastError, value))
                NotifyVisualStateChanged();
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
                NotifyVisualStateChanged();
        }
    }

    private void NotifyVisualStateChanged()
    {
        OnPropertyChanged(nameof(StatusIcon));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(StatusColorHex));
        OnPropertyChanged(nameof(StatusBackgroundHex));
        OnPropertyChanged(nameof(DeliveryState));
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
    public DeliverySemanticState DeliveryState => DeliverySemantics.FromBlockStatus(Status);

    [JsonIgnore]
    public string StatusIcon => (Status == TextBlockStatus.Empty || IsEmpty)
        ? "○"
        : Status switch
        {
            TextBlockStatus.Sent => "↗",
            TextBlockStatus.Typing => "⌨",
            TextBlockStatus.Paused => "⏸",
            TextBlockStatus.PartiallySent => "◐",
            TextBlockStatus.Failed => "✗",
            TextBlockStatus.Interrupted => "⚠",
            TextBlockStatus.Cancelled when IsNext => "▶",
            TextBlockStatus.Cancelled => "⚠",
            TextBlockStatus.Ready when IsNext => "▶",
            TextBlockStatus.Ready => "●",
            _ => "•"
        };

    [JsonIgnore]
    public string StatusText
    {
        get
        {
            if (Status == TextBlockStatus.Empty || IsEmpty)
            {
                return string.IsNullOrEmpty(PersistenceNote) ? "Vazio" : $"Vazio ({PersistenceNote})";
            }

            string baseText = Status switch
            {
                TextBlockStatus.Sent => "Enviado",
                TextBlockStatus.Typing => "Digitando",
                TextBlockStatus.Paused => "Pausado",
                TextBlockStatus.PartiallySent => CharacterCount > 0
                    ? $"Parcial ({SentUpToIndex}/{CharacterCount})"
                    : $"Parcial ({SentUpToIndex})",
                TextBlockStatus.Failed => "Falha",
                TextBlockStatus.Interrupted => "Interrompido",
                TextBlockStatus.Cancelled => "Cancelado",
                TextBlockStatus.Ready when IsNext => "Próximo ▶",
                TextBlockStatus.Ready => "Pronto",
                _ => Status.ToString()
            };

            if (IsNext && Status is not TextBlockStatus.Ready and not TextBlockStatus.Typing and not TextBlockStatus.Paused)
            {
                return $"Próximo ▶ · {baseText}";
            }

            return baseText;
        }
    }

    [JsonIgnore]
    public string StatusColorHex => (Status == TextBlockStatus.Empty || IsEmpty)
        ? "#777777"
        : Status switch
        {
            TextBlockStatus.Sent => "#1E6BB8",
            TextBlockStatus.Typing or TextBlockStatus.Paused or TextBlockStatus.Interrupted => "#B86B11",
            TextBlockStatus.PartiallySent => "#B86B11",
            TextBlockStatus.Failed => "#B82E2E",
            TextBlockStatus.Cancelled when IsNext => "#1F7A3E",
            TextBlockStatus.Cancelled => "#B86B11",
            TextBlockStatus.Ready when IsNext => "#1F7A3E",
            TextBlockStatus.Ready => "#333333",
            _ => "#777777"
        };

    [JsonIgnore]
    public string StatusBackgroundHex => (Status == TextBlockStatus.Empty || IsEmpty)
        ? "#FFFFFF"
        : Status switch
        {
            TextBlockStatus.Sent => "#F1F7FC",
            TextBlockStatus.Typing or TextBlockStatus.Paused => "#FFF8E8",
            TextBlockStatus.PartiallySent or TextBlockStatus.Interrupted => "#FFF5E5",
            TextBlockStatus.Failed => "#FDECEC",
            TextBlockStatus.Cancelled when IsNext => "#F4FBF6",
            TextBlockStatus.Ready when IsNext => "#F4FBF6",
            _ => "#FFFFFF"
        };

    [JsonIgnore]
    public string StatusLabel => $"{StatusIcon} {StatusText}";
}
