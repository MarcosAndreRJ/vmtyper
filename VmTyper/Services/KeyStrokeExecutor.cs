using System.Diagnostics;
using VmTyper.Native;

namespace VmTyper.Services;

public enum UnitOutcome
{
    Sent,
    Skipped,
    Failed
}

public sealed record UnitResult(
    int Index,
    UnitOutcome Outcome,
    int EventsSent,
    int NativeError,
    long TimestampDown,
    long TimestampUp,
    string? ErrorMessage = null);

/// <summary>
/// Rastreia exatamente as teclas que foram descidas (KeyDown) com sucesso via
/// <see cref="IInputInjector"/> e garante que, em caso de erro parcial ou
/// exceção, somente elas sejam liberadas (KeyUp) com a MESMA codificação
/// (Scan ou Unicode) usada na descida (§2.2 H1–H3 / ADR 0001).
/// </summary>
internal sealed class InjectedKeyTracker
{
    public readonly record struct TrackedKey(ushort Code, bool Extended, KeyEncoding Encoding);

    private readonly object _lock = new();
    private readonly List<TrackedKey> _pressed = new();

    public int Count
    {
        get { lock (_lock) return _pressed.Count; }
    }

    public void Add(ushort code, bool extended, KeyEncoding encoding)
    {
        lock (_lock)
        {
            var key = new TrackedKey(code, extended, encoding);
            if (!_pressed.Contains(key))
            {
                _pressed.Add(key);
            }
        }
    }

    public void Remove(ushort code, bool extended, KeyEncoding encoding)
    {
        lock (_lock)
        {
            var key = new TrackedKey(code, extended, encoding);
            int idx = _pressed.LastIndexOf(key);
            if (idx >= 0)
            {
                _pressed.RemoveAt(idx);
            }
        }
    }

    public bool IsTrackedScan(ushort scan, bool extended)
    {
        lock (_lock)
        {
            return _pressed.Any(k => k.Encoding == KeyEncoding.Scan && k.Code == scan && k.Extended == extended);
        }
    }

    /// <summary>
    /// Libera em ordem inversa somente as teclas contidas no tracker, com a mesma codificação
    /// usada na descida, tentando até 3 vezes com 5 ms entre tentativas (§2.2 item 5).
    /// </summary>
    public void ReleaseAll(IInputInjector injector, LogService? log = null)
    {
        List<TrackedKey> snapshot;
        lock (_lock)
        {
            if (_pressed.Count == 0) return;
            snapshot = new List<TrackedKey>(_pressed);
            snapshot.Reverse();
        }

        Span<Win32.INPUT> single = stackalloc Win32.INPUT[1];
        foreach (var key in snapshot)
        {
            single[0] = BuildUpInput(key.Code, key.Extended, key.Encoding);
            bool released = false;
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                if (injector.Send(single) == 1)
                {
                    released = true;
                    Remove(key.Code, key.Extended, key.Encoding);
                    break;
                }
                if (attempt < 3)
                {
                    Thread.Sleep(5);
                }
            }

            if (!released)
            {
                log?.Error($"StuckKeyRisk: falha ao liberar tecla injetada (Encoding={key.Encoding}, Code=0x{key.Code:X4}, Extended={key.Extended}, Win32Error={injector.LastError}).");
            }
        }
    }

    public static Win32.INPUT BuildDownInput(ushort code, bool extended, KeyEncoding encoding) =>
        encoding == KeyEncoding.Unicode
            ? Win32.UnicodeKeyDown((char)code)
            : Win32.ScanKeyDown(code, extended);

    public static Win32.INPUT BuildUpInput(ushort code, bool extended, KeyEncoding encoding) =>
        encoding == KeyEncoding.Unicode
            ? Win32.UnicodeKeyUp((char)code)
            : Win32.ScanKeyUp(code, extended);
}

/// <summary>
/// Executor de unidades planejadas (<see cref="InputUnit"/>) com envio de 1 evento por
/// chamada <c>SendInput</c> e garantia de liberação via <see cref="InjectedKeyTracker"/> (§2.2 / ADR 0001).
/// </summary>
internal sealed class KeyStrokeExecutor
{
    public const int DefaultKeyPressDurationMs = 2;
    public static TimeSpan PhysicalModifierTimeout { get; set; } = TimeSpan.FromSeconds(2);

    private static readonly ushort[] PhysicalModifiers =
    {
        Win32.VK_LSHIFT,
        Win32.VK_RSHIFT,
        Win32.VK_LCONTROL,
        Win32.VK_RCONTROL,
        Win32.VK_LMENU,
        Win32.VK_RMENU,
        Win32.VK_LWIN,
        Win32.VK_RWIN
    };

    private readonly IInputInjector _injector;
    private readonly KeyboardMappingService _mapping;
    private readonly InjectedKeyTracker _tracker;
    private readonly LogService _log;
    private readonly Action<TimeSpan> _holdDelay;
    private readonly Func<long> _timestamp;

    public InjectedKeyTracker Tracker => _tracker;
    internal IInputInjector Injector => _injector;

    public KeyStrokeExecutor(
        IInputInjector injector,
        KeyboardMappingService mapping,
        InjectedKeyTracker tracker,
        LogService log,
        IDelayProvider? delayProvider = null,
        Action<int>? holdDelay = null)
    {
        _injector = injector;
        _mapping = mapping;
        _tracker = tracker;
        _log = log;
        if (delayProvider is not null)
        {
            _holdDelay = delayProvider.HoldExact;
            _timestamp = delayProvider.Timestamp;
        }
        else if (holdDelay is not null)
        {
            _holdDelay = ts => holdDelay((int)Math.Round(ts.TotalMilliseconds));
            _timestamp = Stopwatch.GetTimestamp;
        }
        else
        {
            _holdDelay = ts => PreciseDelay((int)Math.Round(ts.TotalMilliseconds));
            _timestamp = Stopwatch.GetTimestamp;
        }
    }

    /// <summary>
    /// Aguarda até <paramref name="timeout"/> que nenhum modificador físico esteja
    /// pressionado (descontando o que estiver no tracker). Se o tempo esgotar, lança
    /// <see cref="InvalidOperationException"/> com a mensagem exigida em §2.2 item 1 (H4).
    /// </summary>
    public async Task WaitForPhysicalModifiersReleasedAsync(
        IntPtr layout,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        while (HasPhysicalModifierPressed(layout))
        {
            if (sw.Elapsed >= timeout)
            {
                throw new InvalidOperationException("Solte Ctrl/Shift/Alt antes de iniciar");
            }
            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
        }
    }

    public bool HasPhysicalModifierPressed(IntPtr layout)
    {
        foreach (ushort vk in PhysicalModifiers)
        {
            if (!_injector.IsPhysicalKeyDown(vk)) continue;

            var (scan, ext) = _mapping.ResolveScan(vk, layout);
            if (_tracker.IsTrackedScan(scan, ext)) continue;

            return true;
        }
        return false;
    }

    public UnitResult ExecuteUnit(InputUnit unit, int holdDurationMs = DefaultKeyPressDurationMs) =>
        ExecuteUnit(unit, TimeSpan.FromMilliseconds(holdDurationMs));

    /// <summary>
    /// Executa uma única <see cref="InputUnit"/>. Cada KeyDown e cada KeyUp vai em
    /// uma chamada <c>SendInput</c> de 1 evento; o <c>finally</c> da unidade garante
    /// <c>_tracker.ReleaseAll()</c> caso qualquer evento falhe (§2.2 itens 2–5).
    /// </summary>
    public UnitResult ExecuteUnit(InputUnit unit, TimeSpan holdDuration)
    {
        if (unit.Kind == UnitKind.Unsupported || unit.Events.Count == 0)
        {
            return new UnitResult(unit.Index, UnitOutcome.Skipped, 0, 0, 0, 0, unit.UnsupportedReason);
        }

        int eventsSent = 0;
        long tsDown = 0;
        long tsUp = 0;
        Span<Win32.INPUT> single = stackalloc Win32.INPUT[1];

        try
        {
            for (int i = 0; i < unit.Events.Count; i++)
            {
                var ev = unit.Events[i];
                bool prevWasDown = i > 0 && unit.Events[i - 1].Kind == KeyEventKind.Down;
                if (ev.Kind == KeyEventKind.Up && prevWasDown)
                {
                    _holdDelay(holdDuration);
                }

                single[0] = ev.Kind == KeyEventKind.Down
                    ? InjectedKeyTracker.BuildDownInput(ev.Code, ev.Extended, ev.Enc)
                    : InjectedKeyTracker.BuildUpInput(ev.Code, ev.Extended, ev.Enc);

                uint sent = _injector.Send(single);
                long now = _timestamp();

                if (sent == 1)
                {
                    eventsSent++;
                    if (ev.Kind == KeyEventKind.Down)
                    {
                        if (tsDown == 0) tsDown = now;
                        _tracker.Add(ev.Code, ev.Extended, ev.Enc);
                    }
                    else
                    {
                        tsUp = now;
                        _tracker.Remove(ev.Code, ev.Extended, ev.Enc);
                    }

                    if (ev.DelayAfterMs > 0)
                    {
                        _holdDelay(TimeSpan.FromMilliseconds(ev.DelayAfterMs));
                    }
                }
                else
                {
                    int err = _injector.LastError;
                    string detail = err == 5
                        ? $"SendInput bloqueado pelo Windows (erro 5: destino elevado/UIPI)."
                        : $"SendInput retornou 0 na unidade {unit.Index} (erro Win32: {err}).";
                    _log.Error(detail);
                    return new UnitResult(unit.Index, UnitOutcome.Failed, eventsSent, err, tsDown, tsUp, detail);
                }
            }

            return new UnitResult(unit.Index, UnitOutcome.Sent, eventsSent, 0, tsDown, tsUp);
        }
        finally
        {
            // Se tudo subiu normalmente, o tracker já está vazio (zero chamadas extras).
            // Se houve falha parcial ou exceção, solta apenas o que ficou preso no tracker.
            _tracker.ReleaseAll(_injector, _log);
        }
    }

    /// <summary>
    /// Limpa qualquer estado residual de tecla morta pendente no SO de destino enviando
    /// Space e Backspace (§Tarefa 3), evitando que caracteres acentuados contaminem
    /// digitações subsequentes.
    /// </summary>
    public void FlushDeadKeyState(IntPtr layout)
    {
        var (spaceScan, spaceExt) = _mapping.ResolveScan(Win32.VK_SPACE, layout);
        var (backScan, backExt) = _mapping.ResolveScan(Win32.VK_BACK, layout);

        Span<Win32.INPUT> single = stackalloc Win32.INPUT[1];
        // Space Down + Up
        single[0] = InjectedKeyTracker.BuildDownInput(spaceScan, spaceExt, KeyEncoding.Scan);
        _injector.Send(single);
        _holdDelay(TimeSpan.FromMilliseconds(DefaultKeyPressDurationMs));
        single[0] = InjectedKeyTracker.BuildUpInput(spaceScan, spaceExt, KeyEncoding.Scan);
        _injector.Send(single);
        _holdDelay(TimeSpan.FromMilliseconds(DefaultKeyPressDurationMs));

        // Backspace Down + Up
        single[0] = InjectedKeyTracker.BuildDownInput(backScan, backExt, KeyEncoding.Scan);
        _injector.Send(single);
        _holdDelay(TimeSpan.FromMilliseconds(DefaultKeyPressDurationMs));
        single[0] = InjectedKeyTracker.BuildUpInput(backScan, backExt, KeyEncoding.Scan);
        _injector.Send(single);
    }

    public static void PreciseDelay(int milliseconds)
    {
        if (milliseconds <= 0) return;

        var sw = Stopwatch.StartNew();
        long targetTicks = (long)(milliseconds * (Stopwatch.Frequency / 1000.0));
        while (sw.ElapsedTicks < targetTicks)
        {
            Thread.SpinWait(100);
        }
    }
}
