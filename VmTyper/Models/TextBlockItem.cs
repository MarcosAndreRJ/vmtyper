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

    private TextBlockStatus _status = TextBlockStatus.Pending;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TextBlockStatus Status
    {
        get => _status;
        set
        {
            if (SetField(ref _status, value))
                OnPropertyChanged(nameof(StatusLabel));
        }
    }

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
    public string StatusLabel => (Status, IsEmpty) switch
    {
        (TextBlockStatus.Completed, _) => "✓ Concluído",
        (TextBlockStatus.Typing, _) => "⌨ Digitando",
        (TextBlockStatus.Current, _) => "▶ Próximo",
        (TextBlockStatus.Pending, true) => "○ Vazio",
        (TextBlockStatus.Pending, false) => "○ Pendente",
        _ => Status.ToString()
    };
}
