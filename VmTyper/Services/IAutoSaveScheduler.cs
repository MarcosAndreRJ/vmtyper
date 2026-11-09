using System.Windows.Threading;

namespace VmTyper.Services;

/// <summary>
/// Temporizador "debounced" do autosave da fila. Separado de
/// <see cref="TypingQueueService"/> para que a fila não dependa de um
/// Dispatcher e possa ser testada com um disparo manual.
/// </summary>
public interface IAutoSaveScheduler
{
    /// <summary>Disparado quando o intervalo termina sem novo <see cref="Restart"/>.</summary>
    event Action? Elapsed;

    /// <summary>Reinicia a contagem do intervalo.</summary>
    void Restart();

    void Stop();
}

/// <summary>Implementação real sobre <see cref="DispatcherTimer"/>.</summary>
public sealed class DispatcherAutoSaveScheduler : IAutoSaveScheduler
{
    private readonly DispatcherTimer _timer;

    public event Action? Elapsed;

    public DispatcherAutoSaveScheduler(Dispatcher dispatcher, TimeSpan interval)
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = interval
        };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            Elapsed?.Invoke();
        };
    }

    public void Restart()
    {
        _timer.Stop();
        _timer.Start();
    }

    public void Stop() => _timer.Stop();
}
