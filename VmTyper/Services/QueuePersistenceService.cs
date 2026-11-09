using System.IO;
using System.Text.Json;
using VmTyper.Models;

namespace VmTyper.Services;

/// <summary>
/// Persiste a fila de blocos de texto (conteúdo, ordem e estado) em
/// %AppData%\VmTyper\queue.json, para que o usuário não perca os textos
/// preparados caso o aplicativo seja fechado acidentalmente.
/// </summary>
public sealed class QueuePersistenceService : IQueueStore
{
    private readonly string _filePath;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public QueuePersistenceService()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VmTyper", "queue.json"))
    {
    }

    internal QueuePersistenceService(string filePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        _filePath = filePath;
    }

    public List<TextBlockItem> Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return new List<TextBlockItem>();
            string json = File.ReadAllText(_filePath);
            var list = JsonSerializer.Deserialize<List<TextBlockItem>>(json, JsonOptions);
            return list ?? new List<TextBlockItem>();
        }
        catch
        {
            // Fila corrompida/ilegível: melhor começar do zero do que travar o app.
            return new List<TextBlockItem>();
        }
    }

    public void Save(IEnumerable<TextBlockItem> blocks)
    {
        try
        {
            string json = JsonSerializer.Serialize(blocks.ToList(), JsonOptions);
            File.WriteAllText(_filePath, json);
        }
        catch
        {
            // Melhor esforço: falha ao persistir não deve derrubar o app.
        }
    }
}
