namespace VmTyper.Models;

/// <summary>
/// Estado de um bloco de texto na fila de digitação (modelo v2).
/// "Próximo" não é mais um status: fica em <see cref="Services.TypingQueueService.NextBlockId"/>
/// e é refletido em <see cref="TextBlockItem.IsNext"/>.
/// </summary>
public enum TextBlockStatus
{
    /// <summary>Sem texto (ou só espaços).</summary>
    Empty,

    /// <summary>Tem texto e pode ser digitado.</summary>
    Ready,

    /// <summary>Em envio neste momento. Ao recarregar o arquivo, vira <see cref="Interrupted"/>.</summary>
    Typing,

    /// <summary>Sessão pausada neste bloco. Ao recarregar o arquivo, vira <see cref="Interrupted"/>.</summary>
    Paused,

    /// <summary>Todas as unidades foram enviadas (não "confirmadas" no destino).</summary>
    Sent,

    /// <summary>Cancelado ou falhou depois de enviar pelo menos 1 unidade.</summary>
    PartiallySent,

    /// <summary>Cancelado antes da primeira unidade (transita imediatamente para <see cref="Ready"/>).</summary>
    Cancelled,

    /// <summary>Erro antes de enviar qualquer unidade.</summary>
    Failed,

    /// <summary>O aplicativo encerrou durante <see cref="Typing"/> ou <see cref="Paused"/>: entrega desconhecida.</summary>
    Interrupted
}

/// <summary>
/// Eventos da máquina de estados da fila (<see cref="Services.TypingQueueService.Transition"/>).
/// </summary>
public enum BlockTransitionEvent
{
    TextEdited,
    BeginTyping,
    PauseOrResume,
    AllSent,
    Cancel,
    Fail,
    SetAsNext,
    Restart
}
