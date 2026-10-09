using VmTyper.Helpers;

namespace VmTyper.Models;

/// <summary>
/// Modo de uma substituição: <see cref="Texto"/> digita <see cref="CharacterSubstitution.To"/>
/// pelo caminho normal (resolução de layout, teclas mortas, fallback Unicode
/// — igual a qualquer outro caractere do texto). <see cref="Combinacao"/>
/// envia uma combinação de teclas física explícita (modificadores + tecla),
/// ignorando completamente a resolução de layout — recurso adicional para
/// quando a VM interpreta um caractere específico de forma diferente do
/// esperado mesmo com o modificador correto fisicamente pressionado (ex.:
/// VM que só aceita "/" do numpad com Shift para produzir certo símbolo,
/// mas trata "/" do teclado principal com Shift de outra forma).
/// </summary>
public enum SubstitutionMode
{
    Texto,
    Combinacao
}

/// <summary>
/// Substituição manual de caractere/símbolo: sempre que o texto original
/// contiver <see cref="From"/>, o VmTyper digita o substituto no lugar —
/// como texto simples (<see cref="Mode"/> = Texto, comportamento original)
/// ou como uma combinação de teclas física explícita (<see cref="Mode"/> =
/// Combinacao, recurso adicional). Usado para contornar teclas que
/// produzem o caractere errado numa VM com layout de teclado
/// desconfigurado — o usuário mapeia manualmente os símbolos problemáticos
/// que já identificou.
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
}
