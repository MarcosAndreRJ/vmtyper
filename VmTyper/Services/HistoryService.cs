using System.IO;
using System.Text.Json;
using VmTyper.Models;

namespace VmTyper.Services;

/// <summary>
/// Persiste o histórico de sessões de digitação (apenas metadados, nunca o
/// texto digitado) em %AppData%\VmTyper\history.json.
/// </summary>
public sealed class HistoryService
{
    private const int MaxEntries = 50;
    private readonly string _filePath;

    public HistoryService()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VmTyper");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "history.json");
    }

    public List<TypingSession> Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return new List<TypingSession>();
            string json = File.ReadAllText(_filePath);
            var list = JsonSerializer.Deserialize<List<TypingSession>>(json);
            return list ?? new List<TypingSession>();
        }
        catch
        {
            return new List<TypingSession>();
        }
    }

    public void Save(IEnumerable<TypingSession> sessions)
    {
        try
        {
            var trimmed = sessions.TakeLast(MaxEntries).ToList();
            string json = JsonSerializer.Serialize(trimmed, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_filePath, json);
        }
        catch
        {
            // Melhor esforço.
        }
    }
}
