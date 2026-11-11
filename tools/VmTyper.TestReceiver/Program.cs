using System.Windows;

namespace VmTyper.TestReceiver;

/// <summary>
/// Opções de linha de comando:
///   --hook              registra cada evento de teclado com WH_KEYBOARD_LL
///   --export &lt;arquivo&gt;   grava { text, events[] } em JSON ao fechar (e com Ctrl+E)
///   --pipe &lt;nome&gt;       atende comandos por pipe nomeado (testes de integração)
/// </summary>
internal sealed record ReceiverOptions(bool Hook, string? ExportPath, string? PipeName)
{
    public static ReceiverOptions Parse(string[] args)
    {
        bool hook = false;
        string? export = null, pipe = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--hook": hook = true; break;
                case "--export" when i + 1 < args.Length: export = args[++i]; break;
                case "--pipe" when i + 1 < args.Length: pipe = args[++i]; break;
            }
        }
        return new ReceiverOptions(hook, export, pipe);
    }
}

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var options = ReceiverOptions.Parse(args);
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        return app.Run(new ReceiverWindow(options));
    }
}
