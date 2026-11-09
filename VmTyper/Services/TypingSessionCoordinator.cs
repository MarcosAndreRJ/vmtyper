using VmTyper.Helpers;
using VmTyper.Native;

namespace VmTyper.Services;

/// <summary>
/// Abstração para registro de atalhos globais, permitindo testar o coordenador sem janela HWND_MESSAGE.
/// </summary>
public interface IHotkeyRegistrar
{
    bool RegisterHotkey(int id, uint modifiers, uint virtualKey, Action handler);
    void UnregisterHotkey(int id);
    bool IsRegistered(int id);
}

/// <summary>
/// Representa a posse exclusiva da sessão de digitação ativa no processo.
/// Ao ser descartada (Dispose), libera a trava global e desregistra o atalho ESC.
/// </summary>
public sealed class TypingSessionLease : IDisposable
{
    private readonly TypingSessionCoordinator _coordinator;
    private readonly CancellationTokenSource _cts = new();
    private readonly PauseTokenSource _pauseSource = new();
    private readonly TaskCompletionSource<bool> _completedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    public Guid SessionId { get; } = Guid.NewGuid();
    public string Origin { get; }
    public CancellationToken Token => _cts.Token;
    public PauseToken Pause => _pauseSource.Token;
    public bool IsPaused => _pauseSource.IsPaused;
    public Task Completion => _completedTcs.Task;

    internal TypingSessionLease(TypingSessionCoordinator coordinator, string origin)
    {
        _coordinator = coordinator;
        Origin = origin;
    }

    public void PauseSession() => _pauseSource.Pause();

    public void ResumeSession() => _pauseSource.Resume();

    public void Cancel()
    {
        if (_disposed != 0) return;
        try
        {
            if (!_cts.IsCancellationRequested)
            {
                _cts.Cancel();
            }
            _pauseSource.Resume();
        }
        catch (ObjectDisposedException)
        {
            // Já descartado.
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            _coordinator.Release(this);
        }
        finally
        {
            _pauseSource.Resume();
            _cts.Dispose();
            _completedTcs.TrySetResult(true);
        }
    }
}

/// <summary>
/// Garante que no máximo uma sessão de injeção de teclado (janela principal ou
/// janela de diagnóstico) execute por vez no processo e centraliza o registro
/// exclusivo do atalho de emergência ESC.
/// </summary>
public sealed class TypingSessionCoordinator
{
    public const int HotkeyIdCancel = 3;

    private readonly IHotkeyRegistrar? _hotkeyRegistrar;
    private readonly LogService? _log;
    private TypingSessionLease? _activeLease;
    private bool _escRegistered;

    public bool IsActive => Volatile.Read(ref _activeLease) is not null;

    public string? ActiveOrigin => Volatile.Read(ref _activeLease)?.Origin;

    public event Action<bool>? ActiveChanged;

    public TypingSessionCoordinator(IHotkeyRegistrar? hotkeyRegistrar = null, LogService? log = null)
    {
        _hotkeyRegistrar = hotkeyRegistrar;
        _log = log;
    }

    /// <summary>
    /// Tenta iniciar uma sessão exclusiva. Retorna null se já houver sessão ativa.
    /// </summary>
    /// <param name="origin">"Main" | "Diagnostics"</param>
    public TypingSessionLease? TryBegin(string origin)
    {
        var candidate = new TypingSessionLease(this, origin);
        if (Interlocked.CompareExchange(ref _activeLease, candidate, null) is not null)
        {
            _log?.Info($"Sessão '{origin}' recusada: já existe sessão ativa ('{ActiveOrigin}').");
            candidate.Dispose();
            return null;
        }

        _escRegistered = false;
        if (_hotkeyRegistrar is not null)
        {
            try
            {
                _escRegistered = _hotkeyRegistrar.RegisterHotkey(
                    HotkeyIdCancel, 0, Win32.VK_ESCAPE, () => Volatile.Read(ref _activeLease)?.Cancel());
                if (!_escRegistered)
                {
                    _log?.Warn("Não foi possível registrar ESC como tecla de emergência (pode já estar em uso).");
                }
            }
            catch (Exception ex)
            {
                _log?.Warn($"Falha ao registrar ESC global: {ex.Message}");
            }
        }

        ActiveChanged?.Invoke(true);
        return candidate;
    }

    public void CancelActive() => Volatile.Read(ref _activeLease)?.Cancel();

    /// <summary>
    /// Cancela a sessão ativa (se houver) e aguarda sua conclusão até o tempo limite.
    /// </summary>
    public bool CancelAndWait(TimeSpan timeout)
    {
        var lease = Volatile.Read(ref _activeLease);
        if (lease is null) return true;

        lease.Cancel();
        try
        {
            return lease.Completion.Wait(timeout);
        }
        catch
        {
            return false;
        }
    }

    internal void Release(TypingSessionLease lease)
    {
        if (Interlocked.CompareExchange(ref _activeLease, null, lease) != lease)
        {
            return;
        }

        if (_escRegistered && _hotkeyRegistrar is not null)
        {
            try
            {
                _hotkeyRegistrar.UnregisterHotkey(HotkeyIdCancel);
            }
            catch
            {
                // Melhor esforço na limpeza.
            }
            _escRegistered = false;
        }

        ActiveChanged?.Invoke(false);
    }
}
