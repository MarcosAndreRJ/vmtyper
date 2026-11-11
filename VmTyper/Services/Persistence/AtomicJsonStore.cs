using System.IO;
using System.Text;
using System.Text.Json;

namespace VmTyper.Services.Persistence;

public enum LoadStatus
{
    Ok,
    RestoredFromBackup,
    Missing,
    Corrupt
}

public sealed record LoadResult<T>(
    T? Value,
    LoadStatus Status,
    string? Detail = null,
    string? PreservedCorruptPath = null);

public sealed record JsonEnvelope<T>(
    int schemaVersion,
    DateTime savedAt,
    T data);

/// <summary>
/// Armazenamento JSON atômico com envelope versionado (v2), backup (.bak),
/// preservação de arquivos corrompidos (.corrupt-yyyyMMddHHmmss) e validação pós-desserialização.
/// Nunca sobrescreve nem retorna "vazio silencioso" quando o arquivo está corrompido (§4.1 / ADR 0004).
/// </summary>
public sealed class AtomicJsonStore<T>
{
    public const int CurrentSchemaVersion = 2;

    private readonly string _filePath;
    private readonly Func<string, T> _deserializeV1OrV2;
    private readonly Func<T, string> _serializeData;
    private readonly Func<T, string?> _validate;

    /// <summary>Disparado quando uma operação de escrita (Save) falha.</summary>
    public event Action<string>? PersistenceFailed;

    public string FilePath => _filePath;
    public string TmpPath => _filePath + ".tmp";
    public string BakPath => _filePath + ".bak";

    public AtomicJsonStore(
        string filePath,
        Func<string, T> deserializeV1OrV2,
        Func<T, string> serializeData,
        Func<T, string?> validate)
    {
        _filePath = filePath;
        _deserializeV1OrV2 = deserializeV1OrV2;
        _serializeData = serializeData;
        _validate = validate;

        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    public bool Save(T value)
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string dataJson = _serializeData(value);
            using var dataDoc = JsonDocument.Parse(dataJson);
            var envelope = new
            {
                schemaVersion = CurrentSchemaVersion,
                savedAt = DateTime.UtcNow.ToString("O"),
                data = dataDoc.RootElement
            };

            string fullJson = JsonSerializer.Serialize(envelope, new JsonSerializerOptions { WriteIndented = true });
            byte[] bytes = Encoding.UTF8.GetBytes(fullJson);

            using (var fs = new FileStream(TmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(flushToDisk: true);
            }

            if (File.Exists(_filePath))
            {
                File.Replace(TmpPath, _filePath, BakPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(TmpPath, _filePath);
            }

            return true;
        }
        catch (Exception ex)
        {
            PersistenceFailed?.Invoke(ex.Message);
            return false;
        }
    }

    public LoadResult<T> Load()
    {
        if (!File.Exists(_filePath))
        {
            if (File.Exists(BakPath) && TryReadAndValidate(BakPath, out var bakVal, out var bakErr))
            {
                return new LoadResult<T>(bakVal, LoadStatus.RestoredFromBackup, "Arquivo principal ausente; restaurado do backup (.bak).");
            }
            return new LoadResult<T>(default, LoadStatus.Missing);
        }

        if (TryReadAndValidate(_filePath, out var mainVal, out var mainErr))
        {
            return new LoadResult<T>(mainVal, LoadStatus.Ok);
        }

        // Principal falhou: tenta .bak antes de declarar Corrupt.
        if (File.Exists(BakPath) && TryReadAndValidate(BakPath, out var restoredVal, out _))
        {
            // Preserva também o arquivo principal corrompido para auditoria e promove o backup.
            string? preserved = PreserveCorruptFile(_filePath);
            try
            {
                File.Copy(BakPath, _filePath, overwrite: true);
            }
            catch
            {
                // Melhor esforço.
            }
            return new LoadResult<T>(
                restoredVal,
                LoadStatus.RestoredFromBackup,
                $"Restaurado do backup (.bak) após falha no arquivo principal: {mainErr}",
                preserved);
        }

        string? corruptPath = PreserveCorruptFile(_filePath);
        return new LoadResult<T>(
            default,
            LoadStatus.Corrupt,
            mainErr,
            corruptPath);
    }

    private bool TryReadAndValidate(string path, out T? value, out string? error)
    {
        value = default;
        try
        {
            string json = File.ReadAllText(path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(json))
            {
                error = "Arquivo vazio.";
                return false;
            }

            var parsed = _deserializeV1OrV2(json);
            if (parsed is null)
            {
                error = "Desserialização retornou nulo.";
                return false;
            }

            string? validationError = _validate(parsed);
            if (validationError is not null)
            {
                error = validationError;
                return false;
            }

            value = parsed;
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string? PreserveCorruptFile(string path)
    {
        if (!File.Exists(path)) return null;

        string stamp = DateTime.Now.ToString("yyyyMMddHHmmss");
        string dest = $"{path}.corrupt-{stamp}";
        int suffix = 1;
        while (File.Exists(dest))
        {
            dest = $"{path}.corrupt-{stamp}-{suffix++}";
        }

        try
        {
            File.Move(path, dest);
            return dest;
        }
        catch
        {
            return path;
        }
    }
}
