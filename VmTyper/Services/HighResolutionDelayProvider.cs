using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace VmTyper.Services;

/// <summary>
/// Provedor de temporização de alta resolução baseado em <c>CreateWaitableTimerExW</c>
/// com <c>CREATE_WAITABLE_TIMER_HIGH_RESOLUTION</c> (Windows 10 1803+) e thread dedicada
/// com prioridade <see cref="ThreadPriority.AboveNormal"/> (Fase 2 §1.2).
/// Não utiliza <c>timeBeginPeriod</c> global.
/// </summary>
public sealed class HighResolutionDelayProvider : IDelayProvider, IDisposable
{
    private const uint CREATE_WAITABLE_TIMER_MANUAL_RESET = 0x00000001;
    private const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x00000002;
    private const uint TIMER_ALL_ACCESS = 0x001F0003;
    private const uint INFINITE = 0xFFFFFFFF;
    private const uint WAIT_OBJECT_0 = 0x00000000;

    private readonly record struct WaitRequest(
        TimeSpan Duration,
        CancellationToken CancellationToken,
        TaskCompletionSource<bool> Completion);

    private readonly Channel<WaitRequest> _channel;
    private readonly Thread _workerThread;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly ConcurrentQueue<DelaySample> _holdSamples = new();
    private readonly ConcurrentQueue<DelaySample> _waitSamples = new();
    private readonly bool _recordSamples;
    private bool _disposed;

    public bool IsHighResolutionTimerSupported { get; private set; }
    public IReadOnlyCollection<DelaySample> HoldSamples => _holdSamples;
    public IReadOnlyCollection<DelaySample> WaitSamples => _waitSamples;

    public HighResolutionDelayProvider(bool recordSamples = false)
    {
        _recordSamples = recordSamples;
        _channel = Channel.CreateUnbounded<WaitRequest>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = true
        });

        // Verifica suporte ao timer de alta resolução no SO atual.
        IntPtr testTimer = CreateWaitableTimerExW(
            IntPtr.Zero,
            null,
            CREATE_WAITABLE_TIMER_HIGH_RESOLUTION,
            TIMER_ALL_ACCESS);
        if (testTimer != IntPtr.Zero)
        {
            IsHighResolutionTimerSupported = true;
            CloseHandle(testTimer);
        }

        _workerThread = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
            Name = "VmTyper-HighResDelay"
        };
        _workerThread.Start();
    }

    public long Timestamp() => Stopwatch.GetTimestamp();

    /// <summary>
    /// Hold não cancelável (KeyDown→KeyUp): spin com <see cref="Stopwatch"/> para ≤ 5 ms,
    /// e espera híbrida (timer/sleep curto + spin nos 1,5 ms finais) acima disso (§1.2).
    /// </summary>
    public void HoldExact(TimeSpan d)
    {
        if (d <= TimeSpan.Zero)
        {
            if (_recordSamples) _holdSamples.Enqueue(new DelaySample(TimeSpan.Zero, TimeSpan.Zero));
            return;
        }

        long start = Stopwatch.GetTimestamp();
        long targetTicks = (long)(d.TotalSeconds * Stopwatch.Frequency);
        long spinThresholdTicks = (long)(0.0015 * Stopwatch.Frequency); // 1,5 ms

        if (d <= TimeSpan.FromMilliseconds(5))
        {
            while (Stopwatch.GetTimestamp() - start < targetTicks)
            {
                Thread.SpinWait(64);
            }
        }
        else
        {
            while (true)
            {
                long elapsed = Stopwatch.GetTimestamp() - start;
                long remaining = targetTicks - elapsed;
                if (remaining <= 0) break;

                if (remaining > spinThresholdTicks)
                {
                    Thread.Sleep(1);
                }
                else
                {
                    Thread.SpinWait(64);
                }
            }
        }

        if (_recordSamples)
        {
            long end = Stopwatch.GetTimestamp();
            var actual = TimeSpan.FromSeconds((double)(end - start) / Stopwatch.Frequency);
            _holdSamples.Enqueue(new DelaySample(d, actual));
        }
    }

    public ValueTask WaitAsync(TimeSpan d, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return ValueTask.FromCanceled(ct);
        }

        if (d <= TimeSpan.Zero)
        {
            if (_recordSamples) _waitSamples.Enqueue(new DelaySample(TimeSpan.Zero, TimeSpan.Zero));
            return ValueTask.CompletedTask;
        }

        var tcs = new TaskCompletionSource<bool>();
        if (!_channel.Writer.TryWrite(new WaitRequest(d, ct, tcs)))
        {
            return ValueTask.FromCanceled(ct);
        }

        return new ValueTask(tcs.Task);
    }

    public void ClearSamples()
    {
        while (_holdSamples.TryDequeue(out _)) { }
        while (_waitSamples.TryDequeue(out _)) { }
    }

    private void WorkerLoop()
    {
        IntPtr timer = CreateWaitableTimerExW(
            IntPtr.Zero,
            null,
            CREATE_WAITABLE_TIMER_HIGH_RESOLUTION,
            TIMER_ALL_ACCESS);

        try
        {
            var reader = _channel.Reader;
            while (!_disposeCts.IsCancellationRequested)
            {
                if (!reader.WaitToReadAsync(_disposeCts.Token).AsTask().GetAwaiter().GetResult())
                {
                    break;
                }

                while (reader.TryRead(out var req))
                {
                    ExecuteWaitOnDedicatedThread(timer, req);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Encerrando provedor.
        }
        finally
        {
            if (timer != IntPtr.Zero)
            {
                CloseHandle(timer);
            }
        }
    }

    private void ExecuteWaitOnDedicatedThread(IntPtr timer, WaitRequest req)
    {
        var ct = req.CancellationToken;
        if (ct.IsCancellationRequested)
        {
            req.Completion.TrySetCanceled(ct);
            return;
        }

        long start = Stopwatch.GetTimestamp();
        try
        {
            if (timer != IntPtr.Zero)
            {
                // Tempo relativo em unidades de 100 ns (negativo).
                // Subtraímos 0,4 ms para absorver a latência de despertar e completamos no spin.
                double timerMs = Math.Max(0.0, req.Duration.TotalMilliseconds - 0.4);
                if (timerMs >= 0.5)
                {
                    long dueTime100Ns = -(long)(timerMs * 10_000.0);
                    if (SetWaitableTimer(timer, ref dueTime100Ns, 0, IntPtr.Zero, IntPtr.Zero, false))
                    {
                        IntPtr cancelHandle = ct.WaitHandle.SafeWaitHandle.DangerousGetHandle();
                        Span<IntPtr> handles = stackalloc IntPtr[2] { timer, cancelHandle };
                        uint waitRes = WaitForMultipleObjects(2, ref MemoryMarshal.GetReference(handles), false, INFINITE);
                        if (waitRes == WAIT_OBJECT_0 + 1 || ct.IsCancellationRequested)
                        {
                            req.Completion.TrySetCanceled(ct);
                            return;
                        }
                    }
                }
            }
            else
            {
                // Fallback híbrido sem CreateWaitableTimerExW e sem timeBeginPeriod global.
                long targetTicks = (long)(req.Duration.TotalSeconds * Stopwatch.Frequency);
                long spinThresholdTicks = (long)(0.0015 * Stopwatch.Frequency);
                while (true)
                {
                    if (ct.IsCancellationRequested)
                    {
                        req.Completion.TrySetCanceled(ct);
                        return;
                    }
                    long remaining = targetTicks - (Stopwatch.GetTimestamp() - start);
                    if (remaining <= spinThresholdTicks) break;
                    Thread.Sleep(1);
                }
            }

            // Ajuste fino final por spin com verificação de cancelamento.
            long totalTicks = (long)(req.Duration.TotalSeconds * Stopwatch.Frequency);
            while (Stopwatch.GetTimestamp() - start < totalTicks)
            {
                if (ct.IsCancellationRequested)
                {
                    req.Completion.TrySetCanceled(ct);
                    return;
                }
                Thread.SpinWait(32);
            }

            if (_recordSamples)
            {
                long end = Stopwatch.GetTimestamp();
                var actual = TimeSpan.FromSeconds((double)(end - start) / Stopwatch.Frequency);
                _waitSamples.Enqueue(new DelaySample(req.Duration, actual));
            }

            req.Completion.TrySetResult(true);
        }
        catch (OperationCanceledException)
        {
            req.Completion.TrySetCanceled(ct);
        }
        catch (Exception ex)
        {
            req.Completion.TrySetException(ex);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _channel.Writer.TryComplete();
        _disposeCts.Cancel();
        if (_workerThread.IsAlive)
        {
            _workerThread.Join(TimeSpan.FromMilliseconds(500));
        }
        _disposeCts.Dispose();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWaitableTimerExW(
        IntPtr lpTimerAttributes,
        string? lpTimerName,
        uint dwFlags,
        uint dwDesiredAccess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetWaitableTimer(
        IntPtr hTimer,
        ref long pDueTime,
        int lPeriod,
        IntPtr pfnCompletionRoutine,
        IntPtr lpArgToCompletionRoutine,
        bool fResume);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForMultipleObjects(
        uint nCount,
        ref IntPtr lpHandles,
        bool bWaitAll,
        uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}

