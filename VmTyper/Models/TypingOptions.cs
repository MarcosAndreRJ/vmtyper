namespace VmTyper.Models;

/// <summary>
/// Método usado para converter caracteres em eventos de teclado.
/// </summary>
public enum InputMethod
{
    /// <summary>
    /// Tenta o Teclado Físico primeiro; se um caractere não puder ser
    /// mapeado no layout ativo, cai para Unicode apenas para esse caractere.
    /// </summary>
    Automatico,

    /// <summary>
    /// Converte cada caractere em Virtual Key + Scan Code (com modificadores
    /// Shift/Ctrl/AltGr e sequências de tecla morta quando necessário),
    /// considerando o layout de teclado ativo na janela em primeiro plano.
    /// É o modo mais próximo de uma digitação física real e, por isso, o
    /// mais compatível com consoles VMware HTML5 no navegador, que
    /// costumam encaminhar eventos de teclado físico (scan codes) para a VM.
    /// </summary>
    TecladoFisico,

    /// <summary>
    /// Usa SendInput com KEYEVENTF_UNICODE, que injeta o código de caractere
    /// diretamente (like um WM_CHAR sintético via VK_PACKET), sem depender
    /// do layout de teclado. É mais simples, mas alguns consoles HTML5/VMware
    /// que capturam apenas eventos de tecla física (keydown/keyup com
    /// scan code) podem ignorar ou tratar mal esses eventos.
    /// </summary>
    Unicode
}

public sealed class TypingOptions
{
    public int CharDelayMs { get; init; } = 2;
    public int EnterDelayMs { get; init; } = 20;
    public InputMethod Method { get; init; } = InputMethod.Automatico;

    /// <summary>
    /// Quando true, após cada Enter gerado pelo próprio VmTyper (nunca na
    /// primeira linha do texto), neutraliza a autoindentação inserida pelo
    /// editor de destino antes de reaplicar a indentação real da próxima
    /// linha do texto original. Recomendado para editores de código como o
    /// Delphi IDE, que auto-indentam após Enter.
    /// </summary>
    public bool PreserveOriginalIndentation { get; init; }

    /// <summary>Estratégia usada para neutralizar a autoindentação (ver <see cref="IndentationResetStrategy"/>).</summary>
    public IndentationResetStrategy IndentationStrategy { get; init; } = IndentationResetStrategy.Auto;

    /// <summary>
    /// Quando true, registra no log um resumo por caractere (índice, código
    /// Unicode, método usado) — apenas para diagnóstico; não deve ser usado
    /// em digitações normais pois é verboso.
    /// </summary>
    public bool DiagnosticMode { get; init; }

    /// <summary>
    /// Tabela de substituição manual de caracteres (ver <see cref="CharacterSubstitution"/>),
    /// aplicada ao texto inteiro antes de qualquer processamento de linha
    /// ou digitação — independe do método (Automático/Físico/Unicode).
    /// </summary>
    public IReadOnlyList<CharacterSubstitution> Substitutions { get; init; } = Array.Empty<CharacterSubstitution>();
}
