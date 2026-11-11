using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using VmTyper.Native;

namespace VmTyper.IntegrationTests;

/// <summary>Resultado exportado pelo receptor (espelha ReceiverExport de tools/VmTyper.TestReceiver).</summary>
public sealed record ReceiverResult(string Text, List<ReceiverHookEvent> Events, bool HookActive, DateTime ExportedAtUtc);

public sealed record ReceiverHookEvent(long Timestamp, double ElapsedMs, bool KeyUp, uint Vk, uint Scan, uint Flags, bool Extended, bool Injected);

/// <summary>Inicia o receptor local e conversa com ele pelo pipe nomeado.</summary>
internal sealed class ReceiverProcess : IDisposable
{
    public const string ExeVariable = "VMTYPER_RECEIVER_EXE";

    private readonly Process _process;
    private readonly string _pipeName = "VmTyperReceiver-" + Guid.NewGuid().ToString("N");

    public int ProcessId => _process.Id;

    private ReceiverProcess(string exe, bool hook)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        psi.ArgumentList.Add("--pipe");
        psi.ArgumentList.Add(_pipeName);
        if (hook) psi.ArgumentList.Add("--hook");
        _process = Process.Start(psi) ?? throw new InvalidOperationException("Não foi possível iniciar o receptor.");
    }

    public static ReceiverProcess Start(bool hook = true)
    {
        var receiver = new ReceiverProcess(LocateExe(), hook);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            try
            {
                if (receiver.Send("PING", TimeSpan.FromSeconds(1)) == "PONG") return receiver;
            }
            catch (Exception) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(200);
            }
        }
    }

    public string Send(string command, TimeSpan? timeout = null)
    {
        using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut);
        client.Connect((int)(timeout ?? TimeSpan.FromSeconds(5)).TotalMilliseconds);
        using var writer = new StreamWriter(client, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(client, leaveOpen: true);
        writer.WriteLine(command);
        return reader.ReadLine() ?? throw new IOException("Receptor fechou o pipe sem responder.");
    }

    public ReceiverResult Export() =>
        JsonSerializer.Deserialize<ReceiverResult>(Send("EXPORT"))
        ?? throw new InvalidDataException("Exportação vazia.");

    /// <summary>Verdadeiro se a janela em primeiro plano pertence ao processo do receptor.</summary>
    public bool IsForeground()
    {
        IntPtr hwnd = Win32.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;
        Win32.GetWindowThreadProcessId(hwnd, out uint pid);
        return pid == (uint)_process.Id;
    }

    public string DescribeForeground()
    {
        IntPtr hwnd = Win32.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return "HWND=0x0";
        Win32.GetWindowThreadProcessId(hwnd, out uint pid);
        return $"HWND=0x{hwnd:X}, fgPid={pid}, receiverPid={_process.Id}";
    }

    private static string LocateExe()
    {
        var overridePath = Environment.GetEnvironmentVariable(ExeVariable);
        if (!string.IsNullOrEmpty(overridePath)) return overridePath;

        // Sobe a partir da saída dos testes até a raiz da solução.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "VmTyper.sln"))) dir = dir.Parent;
        if (dir is null) throw new FileNotFoundException("Raiz da solução não encontrada.");

        var bin = Path.Combine(dir.FullName, "tools", "VmTyper.TestReceiver", "bin");
        var exe = Directory.Exists(bin)
            ? Directory.EnumerateFiles(bin, "VmTyper.TestReceiver.exe", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
            : null;
        return exe ?? throw new FileNotFoundException(
            $"Receptor não compilado. Rode 'dotnet build tools/VmTyper.TestReceiver' ou defina {ExeVariable}.");
    }

    public void Dispose()
    {
        try { Send("QUIT", TimeSpan.FromSeconds(1)); } catch { /* já fechado */ }
        if (!_process.WaitForExit(3000)) _process.Kill();
        _process.Dispose();
    }
}
