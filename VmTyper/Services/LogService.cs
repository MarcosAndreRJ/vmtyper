using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Threading;

namespace VmTyper.Services;

/// <summary>
/// Log técnico simples, em memória (para exibição na UI) e em arquivo
/// (para diagnóstico posterior). Por padrão (§4.3 / N4), o conteúdo digitado
/// nunca é gravado — apenas metadados como índice, categoria Unicode, método
/// usado, VK e scan code.
/// Quando <see cref="DetailedTrace"/> é ativado (opt-in explícito por sessão),
/// as entradas detalhadas vão para a subpasta <c>diagnostics/</c> com nome
/// indicando conteúdo sensível.
/// </summary>
public sealed class LogService
{
    private readonly string _logDirectory;
    private readonly string _diagnosticsDirectory;
    private readonly Dispatcher _dispatcher;
    private readonly object _fileLock = new();

    public ObservableCollection<string> Lines { get; } = new();

    /// <summary>Feature flag de rastreamento detalhado (desligada por padrão e ao fechar o app).</summary>
    public bool DetailedTrace { get; set; }

    public LogService(Dispatcher dispatcher)
        : this(dispatcher, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VmTyper", "logs"))
    {
    }

    internal LogService(Dispatcher dispatcher, string logDirectory)
    {
        _dispatcher = dispatcher;
        _logDirectory = logDirectory;
        _diagnosticsDirectory = Path.Combine(Path.GetDirectoryName(logDirectory.TrimEnd(Path.DirectorySeparatorChar)) ?? logDirectory, "diagnostics");
        Directory.CreateDirectory(_logDirectory);
    }

    public void Info(string message) => Write(message, sensitive: false);

    public void Warn(string message) => Write("AVISO: " + message, sensitive: false);

    public void Error(string message) => Write("ERRO: " + message, sensitive: false);

    /// <summary>
    /// Registra uma linha que contém o caractere literal apenas se <see cref="DetailedTrace"/>
    /// estiver ativo, gravando em <c>diagnostics/sensitive-trace-YYYYMMDD.log</c>.
    /// </summary>
    public void TraceSensitive(string message)
    {
        if (!DetailedTrace) return;
        Write("[SENSÍVEL] " + message, sensitive: true);
    }

    /// <summary>
    /// Classifica um grapheme ou caractere em uma categoria sem expor o conteúdo (§4.3):
    /// letra, dígito, espaço, pontuação, acentuado ou controle.
    /// </summary>
    public static string Categorize(string grapheme)
    {
        if (string.IsNullOrEmpty(grapheme)) return "vazio";
        if (grapheme is "\n" or "\r" or "\r\n" or "\t") return "controle";

        int codePoint = char.ConvertToUtf32(grapheme, 0);
        var category = CharUnicodeInfo.GetUnicodeCategory(codePoint);

        return category switch
        {
            UnicodeCategory.UppercaseLetter or
            UnicodeCategory.LowercaseLetter or
            UnicodeCategory.TitlecaseLetter or
            UnicodeCategory.ModifierLetter or
            UnicodeCategory.OtherLetter => codePoint > 0x7F ? "acentuado" : "letra",
            UnicodeCategory.NonSpacingMark or
            UnicodeCategory.SpacingCombiningMark => "acentuado",
            UnicodeCategory.DecimalDigitNumber or
            UnicodeCategory.LetterNumber or
            UnicodeCategory.OtherNumber => "dígito",
            UnicodeCategory.SpaceSeparator => "espaço",
            UnicodeCategory.Control or
            UnicodeCategory.LineSeparator or
            UnicodeCategory.ParagraphSeparator or
            UnicodeCategory.Format => "controle",
            _ => "pontuação"
        };
    }

    public static string Categorize(char c) => Categorize(c.ToString());

    private void Write(string message, bool sensitive)
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
                if (sensitive)
                {
                    Directory.CreateDirectory(_diagnosticsDirectory);
                    var diagFile = Path.Combine(_diagnosticsDirectory, $"sensitive-trace-{DateTime.Now:yyyyMMdd}.log");
                    File.AppendAllText(diagFile, line + Environment.NewLine);
                }
                else
                {
                    var file = Path.Combine(_logDirectory, $"vmtyper-{DateTime.Now:yyyyMMdd}.log");
                    File.AppendAllText(file, line + Environment.NewLine);
                }
            }
        }
        catch
        {
            // Falha ao gravar em disco não deve interromper o aplicativo.
        }
    }
}
