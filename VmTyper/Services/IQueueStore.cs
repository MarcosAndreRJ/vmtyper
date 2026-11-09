using VmTyper.Models;

namespace VmTyper.Services;

/// <summary>
/// Armazenamento da fila de blocos. A implementação real é
/// <see cref="QueuePersistenceService"/> (queue.json); os testes usam uma
/// versão em memória.
/// </summary>
public interface IQueueStore
{
    List<TextBlockItem> Load();

    void Save(IEnumerable<TextBlockItem> blocks);
}
