using System.Windows;
using System.Windows.Threading;

namespace VmTyper.Helpers;

/// <summary>
/// Implementação de <see cref="IProgress{T}"/> que executa o callback de forma
/// síncrona quando já estamos na thread do Dispatcher (ou quando não há
/// <see cref="Application.Current"/> rodando loop de mensagens, ex.: testes unitários),
/// e usa <c>BeginInvoke</c> quando chamada de outra thread no aplicativo WPF real.
/// </summary>
public sealed class DirectProgress<T> : IProgress<T>
{
    private readonly Action<T> _handler;
    private readonly Dispatcher _dispatcher;

    public DirectProgress(Action<T> handler)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
    }

    public void Report(T value)
    {
        if (System.Windows.Application.Current is null || _dispatcher.CheckAccess() || _dispatcher.HasShutdownStarted)
        {
            _handler(value);
        }
        else
        {
            _dispatcher.BeginInvoke(_handler, value);
        }
    }
}
