using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using VmTyper.Models;
using VmTyper.Services.Persistence;

namespace VmTyper.Services;

/// <summary>
/// Persiste AppSettings como JSON local em %AppData%\VmTyper\settings.json
/// usando <see cref="AtomicJsonStore{T}"/> (§4.1).
/// </summary>
public sealed class SettingsService
{
    private readonly AtomicJsonStore<AppSettings> _store;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public LoadResult<AppSettings>? LastLoadResult { get; private set; }

    public event Action<string>? PersistenceFailed
    {
        add => _store.PersistenceFailed += value;
        remove => _store.PersistenceFailed -= value;
    }

    public SettingsService()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VmTyper", "settings.json"))
    {
    }

    internal SettingsService(string filePath)
    {
        _store = new AtomicJsonStore<AppSettings>(
            filePath,
            DeserializeSettings,
            s => JsonSerializer.Serialize(s, JsonOptions),
            ValidateSettings);
    }

    public LoadResult<AppSettings> LoadWithResult()
    {
        LastLoadResult = _store.Load();
        return LastLoadResult;
    }

    public AppSettings Load()
    {
        var result = LoadWithResult();
        return result.Value ?? new AppSettings();
    }

    public bool Save(AppSettings settings)
    {
        return _store.Save(settings);
    }

    private static AppSettings DeserializeSettings(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var dataEl) && root.TryGetProperty("schemaVersion", out _))
        {
            return dataEl.Deserialize<AppSettings>(JsonOptions) ?? throw new JsonException("Settings nulos no envelope.");
        }
        return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? throw new JsonException("Settings nulos.");
    }

    public static string? ValidateSettings(AppSettings s)
    {
        if (!Enum.IsDefined(s.Method)) return $"InputMethod inválido: {s.Method}.";
        if (!Enum.IsDefined(s.ExpectedBrowser)) return $"ExpectedBrowser inválido: {s.ExpectedBrowser}.";
        if (!Enum.IsDefined(s.QueueTextPersistence)) return $"QueueTextPersistence inválido: {s.QueueTextPersistence}.";
        if (s.CharacterSubstitutions is not null)
        {
            foreach (var sub in s.CharacterSubstitutions)
            {
                if (!Enum.IsDefined(sub.Mode)) return $"SubstitutionMode inválido: {sub.Mode}.";
            }
        }
        return null;
    }
}
