namespace VmTyper.Models;

/// <summary>
/// Estado de um bloco de texto na fila de digitação.
/// </summary>
public enum TextBlockStatus
{
    /// <summary>Ainda não é o próximo a ser digitado.</summary>
    Pending,

    /// <summary>É o bloco que será digitado no próximo acionamento (botão ou hotkey).</summary>
    Current,

    /// <summary>Está sendo digitado neste exato momento.</summary>
    Typing,

    /// <summary>Foi digitado integralmente com sucesso.</summary>
    Completed
}
