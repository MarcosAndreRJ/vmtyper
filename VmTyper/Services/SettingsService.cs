using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using VmTyper.Models;

namespace VmTyper.Services;

/// <summary>
/// Persiste AppSettings como JSON local em
/// %AppData%\VmTyper\settings.json.
/// </summary>
public sealed class SettingsService
{
    private readonly string _filePath;

    // Enums (Method, ExpectedBrowser) são serializados como texto
    // ("Automatico", "Chrome" etc.), não como número — mais legível/editável
    // manualmente em caso de suporte, e consistente com a fila de blocos
    // (TextBlockStatus já usa o mesmo esquema).
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public SettingsService()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VmTyper");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "settings.json");
    }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return new AppSettings();
            string json = File.ReadAllText(_filePath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            return settings ?? new AppSettings();
        }
        catch
        {
            // Configurações corrompidas/ilegíveis: usa padrões em vez de falhar.
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            string json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(_filePath, json);
        }
        catch
        {
            // Melhor esforço: falha ao persistir não deve derrubar o app.
        }
    }
}
