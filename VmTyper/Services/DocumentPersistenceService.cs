using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using VmTyper.Models;
using VmTyper.Services.Persistence;

namespace VmTyper.Services;

/// <summary>
/// Serviço de persistência para documentos e blocos inteligentes (§Fase 7).
/// Grava em %AppData%\VmTyper\documents.json usando <see cref="AtomicJsonStore{T}"/>,
/// com recuperação de corrupção, backup atômico e migração de blocos legados.
/// </summary>
public sealed class DocumentPersistenceService
{
    private readonly AtomicJsonStore<List<SourceDocument>> _store;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public LoadResult<List<SourceDocument>>? LastLoadResult { get; private set; }

    public event Action<string>? PersistenceFailed
    {
        add => _store.PersistenceFailed += value;
        remove => _store.PersistenceFailed -= value;
    }

    public DocumentPersistenceService()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VmTyper", "documents.json"))
    {
    }

    internal DocumentPersistenceService(string filePath)
    {
        _store = new AtomicJsonStore<List<SourceDocument>>(
            filePath,
            DeserializeDocuments,
            SerializeDocuments,
            ValidateDocuments);
    }

    public LoadResult<List<SourceDocument>> LoadWithResult()
    {
        LastLoadResult = _store.Load();
        return LastLoadResult;
    }

    public List<SourceDocument> Load()
    {
        return LoadWithResult().Value ?? new List<SourceDocument>();
    }

    public void Save(List<SourceDocument> documents)
    {
        _store.Save(documents ?? new List<SourceDocument>());
    }

    private static List<SourceDocument> DeserializeDocuments(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<SourceDocument>();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var dataElement))
        {
            return JsonSerializer.Deserialize<List<SourceDocument>>(dataElement.GetRawText(), JsonOptions) ?? new List<SourceDocument>();
        }
        if (root.ValueKind == JsonValueKind.Array)
        {
            return JsonSerializer.Deserialize<List<SourceDocument>>(root.GetRawText(), JsonOptions) ?? new List<SourceDocument>();
        }
        return new List<SourceDocument>();
    }

    private static string SerializeDocuments(List<SourceDocument> documents)
    {
        return JsonSerializer.Serialize(documents, JsonOptions);
    }

    private static string? ValidateDocuments(List<SourceDocument> documents)
    {
        return documents != null ? null : "Lista de documentos não pode ser nula.";
    }

    /// <summary>
    /// Migração suave a partir de blocos legados da fila antiga (§Fase 7).
    /// Se não houver nenhum documento cadastrado, cria um documento a partir dos blocos existentes
    /// para não descartar os textos anteriormente salvos pelo usuário.
    /// </summary>
    public static SourceDocument? MigrateFromLegacyBlocks(IEnumerable<TextBlockItem> legacyBlocks)
    {
        var blocksList = legacyBlocks?.Where(b => !string.IsNullOrEmpty(b.Text)).ToList();
        if (blocksList == null || blocksList.Count == 0) return null;

        var sb = new System.Text.StringBuilder();
        var codeBlocks = new List<CodeBlock>();

        foreach (var b in blocksList)
        {
            int start = sb.Length;
            sb.Append(b.Text);
            int end = sb.Length;

            codeBlocks.Add(new CodeBlock
            {
                StartOffset = start,
                EndOffset = end,
                Title = b.Header,
                Kind = CodeBlockKind.Unclassified,
                Status = b.Status,
                IsEnabled = true
            });

            if (!b.Text.EndsWith("\n", StringComparison.Ordinal))
            {
                sb.Append("\n\n");
            }
        }

        var doc = new SourceDocument
        {
            Name = "Fila Importada (Legado)",
            Text = sb.ToString(),
            Language = SourceLanguage.PlainText
        };

        foreach (var cb in codeBlocks)
        {
            cb.DocumentId = doc.Id;
            doc.Blocks.Add(cb);
        }

        return doc;
    }
}
