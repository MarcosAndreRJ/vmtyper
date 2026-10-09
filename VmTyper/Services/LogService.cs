using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;

namespace VmTyper.Services;

/// <summary>
/// Log técnico simples, em memória (para exibição na UI) e em arquivo
/// (para diagnóstico posterior). Por padrão, o conteúdo digitado nunca é
/// gravado — apenas metadados como contagem de caracteres, método usado,
/// layout, duração etc.
/// </summary>
public sealed class LogService
{
    private readonly string _logDirectory;
    private readonly Dispatcher _dispatcher;
    private readonly object _fileLock = new();

    public ObservableCollection<string> Lines { get; } = new();

    public LogService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VmTyper", "logs");
        Directory.CreateDirectory(_logDirectory);
    }

    public void Info(string message) => Write(message);

    public void Warn(string message) => Write("AVISO: " + message);

    public void Error(string message) => Write("ERRO: " + message);

    private void Write(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss} - {message}";

        void AddToUi()
        {
            Lines.Add(line);
            while (Lines.Count > 500)
                Lines.RemoveAt(0);
        }

        if (_dispatcher.CheckAccess()) AddToUi();
        else _dispatcher.BeginInvoke(AddToUi);

        try
        {
            lock (_fileLock)
            {
                var file = Path.Combine(_logDirectory, $"vmtyper-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(file, line + Environment.NewLine);
            }
        }
        catch
        {
            // Falha ao gravar em disco não deve interromper o aplicativo.
        }
    }
}
