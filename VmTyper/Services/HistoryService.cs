using System.IO;
using System.Text.Json;
using VmTyper.Models;
using VmTyper.Services.Persistence;

namespace VmTyper.Services;

/// <summary>
/// Persiste o histórico de sessões de digitação (apenas metadados, nunca o
/// texto digitado) em %AppData%\VmTyper\history.json usando <see cref="AtomicJsonStore{T}"/> (§4.1).
/// </summary>
public sealed class HistoryService
{
    private const int MaxEntries = 50;
    private readonly AtomicJsonStore<List<TypingSession>> _store;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public LoadResult<List<TypingSession>>? LastLoadResult { get; private set; }

    public event Action<string>? PersistenceFailed
    {
        add => _store.PersistenceFailed += value;
        remove => _store.PersistenceFailed -= value;
    }

    public HistoryService()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VmTyper", "history.json"))
    {
    }

    internal HistoryService(string filePath)
    {
        _store = new AtomicJsonStore<List<TypingSession>>(
            filePath,
            DeserializeHistory,
            list => JsonSerializer.Serialize(list, JsonOptions),
            ValidateHistory);
    }

    public LoadResult<List<TypingSession>> LoadWithResult()
    {
        LastLoadResult = _store.Load();
        return LastLoadResult;
    }

    public List<TypingSession> Load()
    {
        var result = LoadWithResult();
        return result.Value ?? new List<TypingSession>();
    }

    public bool Save(IEnumerable<TypingSession> sessions)
    {
        var trimmed = sessions.TakeLast(MaxEntries).ToList();
        return _store.Save(trimmed);
    }

    private static List<TypingSession> DeserializeHistory(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var dataEl))
        {
            return dataEl.Deserialize<List<TypingSession>>(JsonOptions) ?? new List<TypingSession>();
        }
        if (root.ValueKind == JsonValueKind.Array)
        {
            return root.Deserialize<List<TypingSession>>(JsonOptions) ?? new List<TypingSession>();
        }
        throw new JsonException("Formato inválido de history.json.");
    }

    private static string? ValidateHistory(List<TypingSession> sessions)
    {
        foreach (var s in sessions)
        {
            if (s.CharacterCount < 0 || s.DurationSeconds < 0)
                return "Sessão de histórico com contagens negativas.";
        }
        return null;
    }
}
