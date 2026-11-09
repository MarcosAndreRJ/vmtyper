using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace VmTyper.TestReceiver;

/// <summary>Resultado exportado: o texto que chegou ao TextBox e, com --hook, os eventos brutos.</summary>
public sealed record ReceiverExport(string Text, IReadOnlyList<HookEvent> Events, bool HookActive, DateTime ExportedAtUtc);

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
    private readonly KeyboardHook? _hook;
    private readonly CancellationTokenSource _pipeCts = new();

    public ReceiverWindow(ReceiverOptions options)
    {
        _options = options;
        Title = WindowTitle;
        Width = 720;
        Height = 480;
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

        var export = new Button { Content = "Exportar (Ctrl+E)", Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(8, 2, 8, 2) };
        export.Click += (_, _) => ExportInteractive();
        var clear = new Button { Content = "Limpar", Padding = new Thickness(8, 2, 8, 2) };
        clear.Click += (_, _) => Clear();
        _status = new TextBlock { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8) };
        bar.Children.Add(export);
        bar.Children.Add(clear);
        bar.Children.Add(_status);

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Bottom);
        root.Children.Add(bar);
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
        };

        Closed += (_, _) =>
        {
            if (_options.ExportPath is not null) File.WriteAllText(_options.ExportPath, Serialize());
            _pipeCts.Cancel();
            _hook?.Dispose();
        };
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
        _textBox.Focus();
    }

    private string Serialize() => JsonSerializer.Serialize(
        new ReceiverExport(_textBox.Text, _hook?.Snapshot() ?? Array.Empty<HookEvent>(), _hook is not null, DateTime.UtcNow),
        JsonOptions);

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
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                Activate();
                _textBox.Focus();
                Keyboard.Focus(_textBox);
                _textBox.CaretIndex = _textBox.Text.Length;
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
}
