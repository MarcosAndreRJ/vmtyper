namespace VmTyper.Models;

/// <summary>
/// Registro de histórico de uma sessão de digitação (uma tentativa de um
/// bloco específico da fila). Por privacidade, o conteúdo digitado nunca é
/// armazenado — apenas metadados. Um mesmo bloco pode aparecer várias vezes
/// no histórico (ex.: uma tentativa cancelada seguida de uma concluída).
/// </summary>
public sealed class TypingSession
{
    public DateTime StartedAt { get; set; }

    /// <summary>Número do bloco (posição visual) no momento da execução. 0 = desconhecido (registros antigos).</summary>
    public int BlockOrder { get; set; }

    public int CharacterCount { get; set; }
    public bool Completed { get; set; }
    public double DurationSeconds { get; set; }

    public string StatusText => Completed ? "Concluído" : "Cancelado";

    public string DisplayText => BlockOrder > 0
        ? $"{StartedAt:dd/MM/yyyy HH:mm:ss}  |  Bloco {BlockOrder}  |  {CharacterCount} caracteres  |  {StatusText}  |  {DurationSeconds:0.0}s"
        : $"{StartedAt:dd/MM/yyyy HH:mm:ss}  |  {CharacterCount} caracteres  |  {StatusText}  |  {DurationSeconds:0.0}s";
}
