namespace VmTyper.Models;

/// <summary>
/// Estratégia usada para neutralizar a autoindentação que o editor de
/// destino (ex.: Delphi IDE) insere automaticamente após um Enter, antes de
/// reaplicar a indentação real do texto original. Mantido como enum interno
/// (sem exposição na UI ainda) para permitir ajuste fino por tipo de editor
/// no futuro, sem alterar a lógica de digitação em si.
/// </summary>
public enum IndentationResetStrategy
{
    /// <summary>Escolhe automaticamente a estratégia mais compatível (hoje: ShiftHomeDelete).</summary>
    Auto,

    /// <summary>Shift+Home (seleciona da posição atual até o início da linha) seguido de Delete.</summary>
    ShiftHomeDelete,

    /// <summary>Home, Home — depende do editor tratar a segunda pressão como "ir para a coluna 0".</summary>
    DoubleHome,

    /// <summary>Não faz nada além do Enter — comportamento antigo, sem neutralizar autoindentação.</summary>
    Disabled
}
