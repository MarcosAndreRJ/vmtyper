namespace VmTyper.Helpers;

/// <summary>
/// Par Pause/Resume no estilo de CancellationTokenSource/CancellationToken,
/// usado para permitir Pausar/Continuar a digitação sem perder a posição
/// atual no texto. Diferente de cancelamento, pausar não é definitivo.
/// </summary>
public sealed class PauseTokenSource
{
    private volatile TaskCompletionSource<bool>? _pauseCompletionSource;

    public bool IsPaused => _pauseCompletionSource is not null;

    public PauseToken Token => new(this);

    public void Pause()
    {
        Interlocked.CompareExchange(ref _pauseCompletionSource, new TaskCompletionSource<bool>(), null);
    }

    public void Resume()
    {
        var tcs = Interlocked.Exchange(ref _pauseCompletionSource, null);
        tcs?.TrySetResult(true);
    }

    internal Task WaitWhilePausedAsync()
    {
        return _pauseCompletionSource?.Task ?? Task.CompletedTask;
    }
}

public readonly struct PauseToken
{
    private readonly PauseTokenSource? _source;

    internal PauseToken(PauseTokenSource source)
    {
        _source = source;
    }

    public bool IsPaused => _source?.IsPaused ?? false;

    public Task WaitWhilePausedAsync() => _source?.WaitWhilePausedAsync() ?? Task.CompletedTask;

    public static PauseToken None => default;
}
