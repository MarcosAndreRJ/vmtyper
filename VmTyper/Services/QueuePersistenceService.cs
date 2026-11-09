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
            return DeserializeBlocks(json);
        }
        catch
        {
            // Fila corrompida/ilegível: melhor começar do zero do que travar o app.
            return new List<TextBlockItem>();
        }
    }

    public static List<TextBlockItem> DeserializeBlocks(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var dataElement))
        {
            return ParseBlockArray(dataElement);
        }
        if (root.ValueKind == JsonValueKind.Array)
        {
            return ParseBlockArray(root);
        }
        throw new JsonException("Formato inválido de queue.json.");
    }

    private static List<TextBlockItem> ParseBlockArray(JsonElement arrayElement)
    {
        var list = new List<TextBlockItem>();
        foreach (var el in arrayElement.EnumerateArray())
        {
            var id = el.TryGetProperty("Id", out var idProp) && idProp.TryGetGuid(out var g) ? g : Guid.NewGuid();
            int order = el.TryGetProperty("Order", out var orderProp) && orderProp.TryGetInt32(out int o) ? o : list.Count + 1;
            string text = el.TryGetProperty("Text", out var textProp) ? textProp.GetString() ?? string.Empty : string.Empty;
            string statusStr = el.TryGetProperty("Status", out var statusProp) ? statusProp.GetString() ?? "Empty" : "Empty";
            int sentUpTo = el.TryGetProperty("SentUpToIndex", out var sentProp) && sentProp.TryGetInt32(out int s) ? s : 0;
            string? lastError = el.TryGetProperty("LastError", out var errProp) ? errProp.GetString() : null;
            Guid? lastSessionId = el.TryGetProperty("LastSessionId", out var sessProp) && sessProp.ValueKind == JsonValueKind.String && sessProp.TryGetGuid(out var sg) ? sg : null;

            bool isEmpty = string.IsNullOrWhiteSpace(text);
            TextBlockStatus status = statusStr switch
            {
                "Pending" => isEmpty ? TextBlockStatus.Empty : TextBlockStatus.Ready,
                "Current" => isEmpty ? TextBlockStatus.Empty : TextBlockStatus.Ready,
                "Completed" => TextBlockStatus.Sent,
                "Typing" => TextBlockStatus.Interrupted,
                "Paused" => TextBlockStatus.Interrupted,
                _ when Enum.TryParse<TextBlockStatus>(statusStr, ignoreCase: false, out var parsed) => parsed,
                _ => throw new JsonException($"Status de bloco inválido: '{statusStr}'.")
            };

            var item = new TextBlockItem
            {
                Id = id,
                Order = order,
                Text = text,
                Status = status,
                SentUpToIndex = sentUpTo,
                LastError = lastError,
                LastSessionId = lastSessionId
            };
            TypingQueueService.NormalizeLoadedBlock(item);
            list.Add(item);
        }
        return list;
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
