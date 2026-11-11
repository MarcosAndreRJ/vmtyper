using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VmTyper.Models;
using VmTyper.Services.Persistence;

namespace VmTyper.Services;

/// <summary>
/// Persiste a fila de blocos de texto (conteúdo, ordem e estado) em
/// %AppData%\VmTyper\queue.json usando <see cref="AtomicJsonStore{T}"/> (§4.1)
/// e respeitando a política de privacidade <see cref="QueueTextPersistence"/> (§4.2).
/// </summary>
public sealed class QueuePersistenceService : IQueueStore
{
    private const string EncryptedPrefix = "dpapi:";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("VmTyper.Queue.v2");

    private readonly AtomicJsonStore<List<TextBlockItem>> _store;
    private readonly Func<QueueTextPersistence> _persistenceModeProvider;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public LoadResult<List<TextBlockItem>>? LastLoadResult { get; private set; }

    public event Action<string>? PersistenceFailed
    {
        add => _store.PersistenceFailed += value;
        remove => _store.PersistenceFailed -= value;
    }

    public QueuePersistenceService(Func<QueueTextPersistence>? persistenceModeProvider = null)
        : this(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VmTyper", "queue.json"),
            persistenceModeProvider)
    {
    }

    internal QueuePersistenceService(string filePath, Func<QueueTextPersistence>? persistenceModeProvider = null)
    {
        _persistenceModeProvider = persistenceModeProvider ?? (() => QueueTextPersistence.Full);
        _store = new AtomicJsonStore<List<TextBlockItem>>(
            filePath,
            DeserializeBlocks,
            SerializeBlocks,
            ValidateBlocks);
    }

    public LoadResult<List<TextBlockItem>> LoadWithResult()
    {
        LastLoadResult = _store.Load();
        return LastLoadResult;
    }

    public List<TextBlockItem> Load()
    {
        var result = LoadWithResult();
        return result.Value ?? new List<TextBlockItem>();
    }

    public void Save(IEnumerable<TextBlockItem> blocks)
    {
        _store.Save(blocks.ToList());
    }

    private string SerializeBlocks(List<TextBlockItem> blocks)
    {
        var mode = _persistenceModeProvider();
        var dtoList = blocks.Select(b =>
        {
            string persistedText = mode switch
            {
                QueueTextPersistence.None => string.Empty,
                QueueTextPersistence.Encrypted when !string.IsNullOrEmpty(b.Text) => EncryptText(b.Text),
                _ => b.Text
            };

            return new
            {
                b.Id,
                b.Order,
                Text = persistedText,
                TextPersistence = mode.ToString(),
                Status = b.Status.ToString(),
                SentUpToIndex = mode == QueueTextPersistence.None ? 0 : b.SentUpToIndex,
                b.LastError,
                b.LastSessionId
            };
        }).ToList();

        return JsonSerializer.Serialize(dtoList, JsonOptions);
    }

    public static List<TextBlockItem> DeserializeBlocks(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (!root.TryGetProperty("data", out var dataElement) || dataElement.ValueKind != JsonValueKind.Array)
            {
                throw new JsonException("Envelope queue.json inválido: propriedade 'data' ausente ou não é um array.");
            }
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
            if (el.ValueKind != JsonValueKind.Object)
                throw new JsonException("Elemento de bloco inválido.");

            if (!el.TryGetProperty("Id", out var idProp) || !idProp.TryGetGuid(out var id))
                throw new JsonException("Bloco sem Id Guid válido.");

            int order = el.TryGetProperty("Order", out var orderProp) && orderProp.TryGetInt32(out int o)
                ? o
                : list.Count + 1;

            string rawText = el.TryGetProperty("Text", out var textProp) && textProp.ValueKind == JsonValueKind.String
                ? textProp.GetString() ?? string.Empty
                : string.Empty;

            string persistenceMode = el.TryGetProperty("TextPersistence", out var tpProp) && tpProp.ValueKind == JsonValueKind.String
                ? tpProp.GetString() ?? "Full"
                : "Full";

            string text = rawText;
            string? note = null;
            if (persistenceMode == nameof(QueueTextPersistence.None))
            {
                text = string.Empty;
                note = "texto não persistido";
            }
            else if (rawText.StartsWith(EncryptedPrefix, StringComparison.Ordinal))
            {
                text = DecryptText(rawText);
            }

            string statusStr = el.TryGetProperty("Status", out var statusProp) && statusProp.ValueKind == JsonValueKind.String
                ? statusProp.GetString() ?? "Empty"
                : "Empty";

            int sentUpTo = el.TryGetProperty("SentUpToIndex", out var sentProp) && sentProp.TryGetInt32(out int s) ? s : 0;
            string? lastError = el.TryGetProperty("LastError", out var errProp) && errProp.ValueKind == JsonValueKind.String
                ? errProp.GetString()
                : null;
            Guid? lastSessionId = el.TryGetProperty("LastSessionId", out var sessProp) && sessProp.ValueKind == JsonValueKind.String && sessProp.TryGetGuid(out var sg)
                ? sg
                : null;

            bool isEmpty = string.IsNullOrWhiteSpace(text);
            TextBlockStatus status = statusStr switch
            {
                "Pending" => isEmpty ? TextBlockStatus.Empty : TextBlockStatus.Ready,
                "Current" => isEmpty ? TextBlockStatus.Empty : TextBlockStatus.Ready,
                "Completed" => TextBlockStatus.Sent,
                "Typing" => TextBlockStatus.Interrupted,
                "Paused" => TextBlockStatus.Interrupted,
                _ when Enum.TryParse<TextBlockStatus>(statusStr, ignoreCase: false, out var parsed) && Enum.IsDefined(parsed) => parsed,
                _ => throw new JsonException($"Status de bloco inválido: '{statusStr}'.")
            };

            if (persistenceMode == nameof(QueueTextPersistence.None))
            {
                status = TextBlockStatus.Empty;
                sentUpTo = 0;
            }

            // Validação SentUpToIndex <= Text.Length antes de NormalizeLoadedBlock.
            if (sentUpTo < 0 || sentUpTo > text.Length)
            {
                throw new JsonException($"SentUpToIndex ({sentUpTo}) fora dos limites do texto ({text.Length}).");
            }

            var item = new TextBlockItem
            {
                Id = id,
                Order = order,
                Text = text,
                Status = status,
                SentUpToIndex = sentUpTo,
                LastError = lastError,
                LastSessionId = lastSessionId,
                PersistenceNote = note
            };
            TypingQueueService.NormalizeLoadedBlock(item);
            list.Add(item);
        }
        return list;
    }

    public static string? ValidateBlocks(List<TextBlockItem> blocks)
    {
        var ids = new HashSet<Guid>();
        foreach (var b in blocks)
        {
            if (b.Id == Guid.Empty) return "Bloco com Id vazio.";
            if (!ids.Add(b.Id)) return $"Id duplicado na fila: {b.Id}.";
            if (!Enum.IsDefined(b.Status)) return $"Status inválido no bloco {b.Id}: {b.Status}.";
            if (b.SentUpToIndex < 0 || b.SentUpToIndex > b.Text.Length)
                return $"SentUpToIndex ({b.SentUpToIndex}) inválido para texto de tamanho {b.Text.Length}.";
        }

        var ordered = blocks.Select(b => b.Order).OrderBy(o => o).ToList();
        for (int i = 0; i < ordered.Count; i++)
        {
            if (ordered[i] != i + 1)
                return $"Order não contíguo na fila (esperado {i + 1}, encontrado {ordered[i]}).";
        }

        return null;
    }

    private static string EncryptText(string plainText)
    {
        byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
        byte[] encrypted = ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.CurrentUser);
        return EncryptedPrefix + Convert.ToBase64String(encrypted);
    }

    private static string DecryptText(string payload)
    {
        string b64 = payload.Substring(EncryptedPrefix.Length);
        byte[] encrypted = Convert.FromBase64String(b64);
        byte[] plainBytes = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(plainBytes);
    }
}
