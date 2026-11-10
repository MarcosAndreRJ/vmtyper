using System.Text.Json.Serialization;
using VmTyper.Helpers;

namespace VmTyper.Models;

/// <summary>
/// Modo de uma substituição:
/// <see cref="Texto"/> digita <see cref="CharacterSubstitution.To"/> pelo caminho normal.
/// <see cref="Combinacao"/> envia uma combinação de teclas física única (modificadores + tecla).
/// <see cref="Sequencia"/> envia uma sequência ordenada de ações físicas (teclas mortas, scancodes diretos,
/// passos com intervalos de espera, etc.).
/// </summary>
public enum SubstitutionMode
{
    Texto,
    Combinacao,
    Sequencia
}

/// <summary>
/// Tipo de ação em um passo de sequência física.
/// </summary>
public enum KeySequenceAction
{
    Press,   // Pressiona e solta (KeyDown seguido de KeyUp com holdDuration)
    KeyDown, // Apenas desce a tecla (KeyDown)
    KeyUp    // Apenas sobe a tecla (KeyUp)
}

/// <summary>
/// Representa um passo ordenado em uma sequência de digitação física (§Tarefa 2).
/// </summary>
public sealed class KeySequenceStep : ObservableObject
{
    private ushort _virtualKey;
    public ushort VirtualKey
    {
        get => _virtualKey;
        set => SetField(ref _virtualKey, value);
    }

    private ushort _scanCode;
    /// <summary>Scan code de hardware explícito. Se 0, resolvido a partir de <see cref="VirtualKey"/>.</summary>
    public ushort ScanCode
    {
        get => _scanCode;
        set => SetField(ref _scanCode, value);
    }

    private bool _extended;
    /// <summary>Indica se deve enviar o flag KEYEVENTF_EXTENDEDKEY (prefixo E0 no teclado PC).</summary>
    public bool Extended
    {
        get => _extended;
        set => SetField(ref _extended, value);
    }

    private bool _shift;
    public bool Shift
    {
        get => _shift;
        set => SetField(ref _shift, value);
    }

    private bool _ctrl;
    public bool Ctrl
    {
        get => _ctrl;
        set => SetField(ref _ctrl, value);
    }

    private bool _alt;
    public bool Alt
    {
        get => _alt;
        set => SetField(ref _alt, value);
    }

    private bool _altGr;
    public bool AltGr
    {
        get => _altGr;
        set => SetField(ref _altGr, value);
    }

    private KeySequenceAction _action = KeySequenceAction.Press;
    public KeySequenceAction Action
    {
        get => _action;
        set => SetField(ref _action, value);
    }

    private int _delayMs;
    /// <summary>Pausa em milissegundos introduzida após a conclusão deste passo.</summary>
    public int DelayMs
    {
        get => _delayMs;
        set => SetField(ref _delayMs, value);
    }

    private string _label = string.Empty;
    /// <summary>Descrição amigável do passo (ex.: "Agudo (´) com pausa 15ms" ou "Numpad 4").</summary>
    public string Label
    {
        get => _label;
        set => SetField(ref _label, value);
    }
}

/// <summary>
/// Substituição manual de caractere/símbolo: sempre que o texto original
/// contiver <see cref="From"/>, o VmTyper digita o substituto no lugar —
/// como texto simples (<see cref="Mode"/> = Texto), como combinação única
/// (<see cref="Mode"/> = Combinacao) ou como uma sequência física ordenada
/// (<see cref="Mode"/> = Sequencia).
/// </summary>
public sealed class CharacterSubstitution : ObservableObject
{
    private string _from = string.Empty;
    public string From
    {
        get => _from;
        set => SetField(ref _from, value);
    }

    private string _to = string.Empty;
    public string To
    {
        get => _to;
        set => SetField(ref _to, value);
    }

    private SubstitutionMode _mode = SubstitutionMode.Texto;
    public SubstitutionMode Mode
    {
        get => _mode;
        set => SetField(ref _mode, value);
    }

    // --- Usados apenas quando Mode == Combinacao ---
    public bool ComboShift { get; set; }
    public bool ComboCtrl { get; set; }
    public bool ComboAlt { get; set; }
    public uint ComboVirtualKey { get; set; }

    private string _comboLabel = string.Empty;
    /// <summary>Rótulo amigável da combinação capturada (ex.: "Shift + / (numpad)"), só para exibição.</summary>
    public string ComboLabel
    {
        get => _comboLabel;
        set => SetField(ref _comboLabel, value);
    }

    // --- Usados apenas quando Mode == Sequencia ---
    private List<KeySequenceStep> _sequenceSteps = new();
    public List<KeySequenceStep> SequenceSteps
    {
        get => _sequenceSteps;
        set
        {
            if (SetField(ref _sequenceSteps, value))
            {
                OnPropertyChanged(nameof(SequenceSummary));
            }
        }
    }

    [JsonIgnore]
    public string SequenceSummary
    {
        get
        {
            if (SequenceSteps == null || SequenceSteps.Count == 0)
            {
                return "Nenhum passo definido";
            }
            if (SequenceSteps.Count == 1)
            {
                var s = SequenceSteps[0];
                return !string.IsNullOrEmpty(s.Label) ? s.Label : (s.ScanCode > 0 ? $"Scan 0x{s.ScanCode:X2}" : $"VK 0x{s.VirtualKey:X2}");
            }
            return $"{SequenceSteps.Count} passos ({string.Join(" → ", SequenceSteps.Select(s => !string.IsNullOrEmpty(s.Label) ? s.Label : (s.ScanCode > 0 ? $"0x{s.ScanCode:X2}" : $"0x{s.VirtualKey:X2}")))})";
        }
    }
}
