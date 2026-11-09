using VmTyper.Models;
using VmTyper.Services.Persistence;

namespace VmTyper.Services;

/// <summary>
/// Armazenamento da fila de blocos. A implementação real é
/// <see cref="QueuePersistenceService"/> (queue.json); os testes usam uma
/// versão em memória.
/// </summary>
public interface IQueueStore
{
    List<TextBlockItem> Load();

    LoadResult<List<TextBlockItem>>? LastLoadResult => null;

    event Action<string>? PersistenceFailed
    {
        add { }
        remove { }
    }

    void Save(IEnumerable<TextBlockItem> blocks);
}
