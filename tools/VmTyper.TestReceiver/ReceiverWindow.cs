using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace VmTyper.TestReceiver;

/// <summary>Resultado exportado: o texto que chegou ao TextBox e, com --hook, os eventos brutos.</summary>
public sealed record ReceiverExport(
    string Text,
    string Sha256Prefix12,
    int CharLength,
    int GraphemeCount,
    int RepeatedDownCount,
    IReadOnlyList<HookEvent> Events,
    bool HookActive,
    DateTime ExportedAtUtc);

/// <summary>
/// Janela do receptor: um TextBox multilinha que aceita Enter e Tab. Sem XAML
/// para manter a ferramenta num punhado de arquivos.
/// </summary>
internal sealed class ReceiverWindow : Window
{
    public const string WindowTitle = "VmTyper Test Receiver";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly ReceiverOptions _options;
    private readonly TextBox _textBox;
    private readonly TextBlock _status;
    private readonly TextBlock _fingerprintBar;
    private readonly KeyboardHook? _hook;
    private readonly CancellationTokenSource _pipeCts = new();
    private string? _expectedCorpusText;
    private string? _expectedCorpusName;

    public ReceiverWindow(ReceiverOptions options)
    {
        _options = options;
        Title = WindowTitle;
        Width = 780;
        Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _textBox = new TextBox
        {
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 14
        };
        _textBox.TextChanged += (_, _) => UpdateFingerprintBar();

        var export = new Button { Content = "Exportar (Ctrl+E)", Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(8, 2, 8, 2) };
        export.Click += (_, _) => ExportInteractive();
        var clear = new Button { Content = "Limpar", Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(8, 2, 8, 2) };
        clear.Click += (_, _) => Clear();
        var loadCorpus = new Button { Content = "Corpus esperado...", Padding = new Thickness(8, 2, 8, 2) };
        loadCorpus.Click += (_, _) => LoadCorpusInteractive();
        _status = new TextBlock { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };

        _fingerprintBar = new TextBlock
        {
            Margin = new Thickness(8, 4, 8, 4),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        };

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 4, 8, 8) };
        bar.Children.Add(export);
        bar.Children.Add(clear);
        bar.Children.Add(loadCorpus);
        bar.Children.Add(_status);

        var bottomPanel = new StackPanel { Orientation = Orientation.Vertical };
        bottomPanel.Children.Add(_fingerprintBar);
        bottomPanel.Children.Add(bar);

        var root = new DockPanel();
        DockPanel.SetDock(bottomPanel, Dock.Bottom);
        root.Children.Add(bottomPanel);
        root.Children.Add(_textBox);
        Content = root;

        // Ctrl+E é tratado antes do TextBox para não virar texto.
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.E && Keyboard.Modifiers == ModifierKeys.Control)
            {
                ExportInteractive();
                e.Handled = true;
            }
        };

        if (options.Hook)
        {
            try { _hook = new KeyboardHook(); }
            catch (Exception ex) { _status.Text = "Hook indisponível: " + ex.Message; }
        }

        Loaded += (_, _) =>
        {
            _textBox.Focus();
            if (_options.PipeName is not null) _ = Task.Run(() => ServePipeAsync(_options.PipeName, _pipeCts.Token));
            UpdateStatus();
            UpdateFingerprintBar();
        };

        Closed += (_, _) =>
        {
            if (_options.ExportPath is not null) File.WriteAllText(_options.ExportPath, Serialize());
            _pipeCts.Cancel();
            _hook?.Dispose();
        };
    }

    private void LoadCorpusInteractive()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Selecionar corpus esperado",
            Filter = "Arquivos de texto (*.txt;*.pas;*.cs)|*.txt;*.pas;*.cs|Todos os arquivos (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) == true)
        {
            _expectedCorpusText = File.ReadAllText(dialog.FileName, Encoding.UTF8).Replace("\r\n", "\n").Replace("\r", "\n");
            _expectedCorpusName = Path.GetFileName(dialog.FileName);
            UpdateFingerprintBar();
        }
    }

    private void UpdateFingerprintBar()
    {
        string normalized = (_textBox.Text ?? string.Empty).Replace("\r\n", "\n").Replace("\r", "\n");
        var (sha12, charLen, graphemes, letters, digits, punct, spaces, newlines, tabs) = ComputeStats(normalized);
        var events = _hook?.Snapshot() ?? Array.Empty<HookEvent>();
        int repeatedDown = events.Count(e => e.RepeatedDownWithoutUp);

        string diffInfo = string.Empty;
        if (_expectedCorpusText is not null)
        {
            int firstDiff = FindFirstGraphemeDifference(_expectedCorpusText, normalized);
            diffInfo = firstDiff < 0
                ? $" | Corpus ({_expectedCorpusName}): IDÊNTICO"
                : $" | Corpus ({_expectedCorpusName}): 1ª dif em pos {firstDiff}";
        }

        _fingerprintBar.Text =
            $"SHA-256(12): {sha12} | len={charLen} (graf={graphemes}) | " +
            $"L:{letters} D:{digits} P:{punct} S:{spaces} NL:{newlines} T:{tabs} | " +
            $"DownRepetido:{repeatedDown}{diffInfo}";
    }

    private static (string Sha12, int CharLen, int Graphemes, int Letters, int Digits, int Punct, int Spaces, int Newlines, int Tabs)
        ComputeStats(string normalized)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(normalized);
        byte[] hash = SHA256.HashData(bytes);
        string sha12 = Convert.ToHexString(hash)[..12].ToLowerInvariant();

        int graphemes = 0, letters = 0, digits = 0, punct = 0, spaces = 0, newlines = 0, tabs = 0;
        var enumerator = StringInfo.GetTextElementEnumerator(normalized);
        while (enumerator.MoveNext())
        {
            string g = enumerator.GetTextElement();
            graphemes++;
            if (g == "\n") newlines++;
            else if (g == "\t") tabs++;
            else if (g == " ") spaces++;
            else if (char.IsLetter(g, 0)) letters++;
            else if (char.IsDigit(g, 0)) digits++;
            else punct++;
        }

        return (sha12, normalized.Length, graphemes, letters, digits, punct, spaces, newlines, tabs);
    }

    private static int FindFirstGraphemeDifference(string expected, string actual)
    {
        var expList = SplitGraphemes(expected);
        var actList = SplitGraphemes(actual);
        int min = Math.Min(expList.Count, actList.Count);
        for (int i = 0; i < min; i++)
        {
            if (!string.Equals(expList[i], actList[i], StringComparison.Ordinal))
                return i;
        }
        return expList.Count == actList.Count ? -1 : min;
    }

    private static List<string> SplitGraphemes(string text)
    {
        var list = new List<string>();
        var e = StringInfo.GetTextElementEnumerator(text);
        while (e.MoveNext()) list.Add(e.GetTextElement());
        return list;
    }

    private void UpdateStatus()
    {
        if (_status.Text.StartsWith("Hook indisponível", StringComparison.Ordinal)) return;
        _status.Text = $"hook: {(_hook is null ? "não" : "sim")}   pipe: {_options.PipeName ?? "não"}";
    }

    private void Clear()
    {
        _textBox.Clear();
        _hook?.Clear();
        UpdateFingerprintBar();
        _textBox.Focus();
    }

    private string Serialize()
    {
        string normalized = (_textBox.Text ?? string.Empty).Replace("\r\n", "\n").Replace("\r", "\n");
        var stats = ComputeStats(normalized);
        var events = _hook?.Snapshot() ?? Array.Empty<HookEvent>();
        int repeatedDown = events.Count(e => e.RepeatedDownWithoutUp);
        return JsonSerializer.Serialize(
            new ReceiverExport(_textBox.Text ?? string.Empty, stats.Sha12, stats.CharLen, stats.Graphemes, repeatedDown, events, _hook is not null, DateTime.UtcNow),
            JsonOptions);
    }

    private void ExportInteractive()
    {
        string path = _options.ExportPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            $"vmtyper-receiver-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        File.WriteAllText(path, Serialize());
        _status.Text = "Exportado: " + path;
    }

    /// <summary>
    /// Protocolo de linha (UTF-8): PING → PONG; CLEAR → OK; ACTIVATE → OK;
    /// EXPORT → JSON de <see cref="ReceiverExport"/>; QUIT → OK e fecha.
    /// Um cliente por vez; o servidor volta a escutar depois de cada conexão.
    /// </summary>
    private async Task ServePipeAsync(string pipeName, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(ct);
                using var reader = new StreamReader(server, leaveOpen: true);
                await using var writer = new StreamWriter(server, leaveOpen: true) { AutoFlush = true };

                string? line;
                while ((line = await reader.ReadLineAsync(ct)) is not null)
                {
                    string reply = Dispatcher.Invoke(() => Handle(line.Trim()));
                    await writer.WriteLineAsync(reply);
                    if (line.Trim() == "QUIT") return;
                }
            }
            catch (OperationCanceledException) { return; }
            catch (IOException) { /* cliente caiu; volta a escutar */ }
        }
    }

    private string Handle(string command)
    {
        switch (command)
        {
            case "PING":
                return "PONG";
            case "CLEAR":
                Clear();
                return "OK";
            case "ACTIVATE":
                ForceForeground();
                return "OK";
            case "EXPORT":
                return Serialize();
            case "QUIT":
                Dispatcher.BeginInvoke(Close);
                return "OK";
            default:
                return "ERR comando desconhecido";
        }
    }

    private void ForceForeground()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        IntPtr fg = GetForegroundWindow();
        uint curThread = GetCurrentThreadId();
        uint fgThread = fg != IntPtr.Zero ? GetWindowThreadProcessId(fg, out _) : 0;

        bool attached = false;
        if (fgThread != 0 && fgThread != curThread)
        {
            attached = AttachThreadInput(fgThread, curThread, true);
        }

        try
        {
            // Desbloqueia a trava de foreground do Windows para processos iniciados por CLI de teste.
            keybd_event(0x12, 0, 0x0002, UIntPtr.Zero); // VK_MENU KeyUp
            Topmost = true;
            Show();
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
            SwitchToThisWindow(hwnd, true);
            Activate();
        }
        finally
        {
            if (attached) AttachThreadInput(fgThread, curThread, false);
        }

        _textBox.Focus();
        Keyboard.Focus(_textBox);
        _textBox.CaretIndex = _textBox.Text.Length;
    }

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern void SwitchToThisWindow(IntPtr hWnd, bool fAltTab);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
