using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using VmTyper.Helpers;
using VmTyper.Models;
using VmTyper.Services;
using VmTyper.Tests;
using VmTyper.Tests.Fakes;
using Xunit.Abstractions;

namespace VmTyper.IntegrationTests;

/// <summary>Só roda com VMTYPER_MEASURE=1 (leva cerca de um minuto e ocupa todos os núcleos na parte com carga).</summary>
public sealed class MeasurementFactAttribute : FactAttribute
{
    public const string Variable = "VMTYPER_MEASURE";

    public MeasurementFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(Variable) != "1")
        {
            Skip = $"Medição demorada; defina {Variable}=1.";
        }
    }
}

/// <summary>
/// Passo 0.8: mede o ritmo real do motor v1 sem mudar o motor. O injetor é
/// o falso com carimbo de tempo (Stopwatch) no instante da chamada, então o
/// que se mede é a temporização do próprio motor (Task.Delay entre unidades
/// e PreciseDelay entre KeyDown e KeyUp), sem o custo de SendInput e sem
/// injetar nada na sessão.
/// </summary>
[Trait("Category", "Measurement")]
[Trait("Category", "Integration")]
public sealed class RhythmBaselineTests
{
    private static readonly int[] Delays = { 0, 1, 3, 10, 30 };

    private readonly ITestOutputHelper _output;

    public RhythmBaselineTests(ITestOutputHelper output) => _output = output;

    [DllImport("ntdll.dll")]
    private static extern int NtQueryTimerResolution(out uint minimum, out uint maximum, out uint current);

    private static double TimerResolutionMs()
    {
        NtQueryTimerResolution(out _, out _, out uint current);
        return current / 10_000.0;
    }

    [MeasurementFact]
    public async Task MeasureIntervalAndHold_IdleAndUnderLoad()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Linha de base de ritmo — motor v1");
        sb.AppendLine();
        sb.AppendLine($"- Data: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- Máquina: {Environment.MachineName}, {Environment.ProcessorCount} processadores lógicos, {RuntimeInformation.OSDescription}");
        sb.AppendLine($"- Runtime: {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"- Resolução do timer do sistema no início: {TimerResolutionMs():0.###} ms");
        sb.AppendLine();
        sb.AppendLine("| Condição | CharDelayMs | N | Intervalo mediana (ms) | p95 | máx | Hold mediana (ms) | p95 | máx | Timer (ms) |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|");

        foreach (bool load in new[] { false, true })
        {
            using var stress = load ? new CpuStress() : null;
            foreach (int delay in Delays)
            {
                int n = delay >= 10 ? 100 : 300;
                var (interval, hold) = await MeasureAsync(delay, n);
                sb.AppendLine($"| {(load ? "carga" : "ocioso")} | {delay} | {n} | {interval.Median:0.00} | {interval.P95:0.00} | {interval.Max:0.00} | {hold.Median:0.00} | {hold.P95:0.00} | {hold.Max:0.00} | {TimerResolutionMs():0.###} |");
            }
        }

        sb.AppendLine();
        sb.AppendLine("Intervalo = KeyDown(unidade i) → KeyDown(unidade i+1). Hold = KeyDown → KeyUp da mesma unidade.");
        sb.AppendLine("Carga = um thread de spin por processador lógico, prioridade normal.");

        string outDir = Path.Combine(AppContext.BaseDirectory, "baseline");
        Directory.CreateDirectory(outDir);
        string outFile = Path.Combine(outDir, $"rhythm-v1-{DateTime.Now:yyyyMMdd-HHmmss}.md");
        await File.WriteAllTextAsync(outFile, sb.ToString());
        _output.WriteLine(sb.ToString());
        _output.WriteLine("Salvo em " + outFile);
    }

    /// <summary>
    /// Fase 2 §1.3: compara o provedor legado (Task.Delay + PreciseDelay) com o
    /// HighResolutionDelayProvider (CreateWaitableTimerExW + thread dedicada AboveNormal),
    /// registrando mediana, p95, máximo e uso de CPU (Process.TotalProcessorTime).
    /// </summary>
    [MeasurementFact]
    public async Task MeasureF2_LegacyTaskDelay_Vs_HighResolutionProvider_IdleAndUnderLoad()
    {
        using var highRes = new HighResolutionDelayProvider(recordSamples: true);
        var legacyTaskDelay = new LegacyTaskDelayProvider();

        var sb = new StringBuilder();
        sb.AppendLine("# Medição de precisão de atraso — Fase 2 (§1.3)");
        sb.AppendLine();
        sb.AppendLine($"- Data: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- Máquina: {Environment.MachineName}, {Environment.ProcessorCount} processadores lógicos, {RuntimeInformation.OSDescription}");
        sb.AppendLine($"- Runtime: {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"- Suporte a `CREATE_WAITABLE_TIMER_HIGH_RESOLUTION`: {(highRes.IsHighResolutionTimerSupported ? "sim" : "não (fallback híbrido)")}");
        sb.AppendLine($"- Resolução do timer do sistema no início: {TimerResolutionMs():0.###} ms");
        sb.AppendLine();
        sb.AppendLine("| Provedor | Condição | CharDelayMs | N | Intervalo med (ms) | p95 | máx | Hold med (ms) | p95 | máx | CPU motor (ms) | Timer SO (ms) |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|");

        foreach (var (providerName, provider) in new (string, IDelayProvider)[]
        {
            ("Legado (Task.Delay)", legacyTaskDelay),
            ("HighResolutionDelayProvider", highRes)
        })
        {
            foreach (bool load in new[] { false, true })
            {
                using var stress = load ? new CpuStress() : null;
                foreach (int delay in Delays)
                {
                    int n = delay >= 10 ? 100 : 300;
                    var (interval, hold, cpuMs) = await MeasureWithProviderAsync(provider, delay, n, stress);
                    sb.AppendLine($"| {providerName} | {(load ? "carga" : "ocioso")} | {delay} | {n} | {interval.Median:0.00} | {interval.P95:0.00} | {interval.Max:0.00} | {hold.Median:0.00} | {hold.P95:0.00} | {hold.Max:0.00} | {cpuMs:0.0} | {TimerResolutionMs():0.###} |");
                }
            }
        }

        sb.AppendLine();
        sb.AppendLine("- **Intervalo** = KeyDown(unidade `i`) → KeyDown(unidade `i+1`) (inclui Hold de 2 ms + Gap configurado).");
        sb.AppendLine("- **Hold** = KeyDown → KeyUp da mesma unidade (2 ms solicitados).");
        sb.AppendLine("- **CPU motor (ms)** = variação de `Process.TotalProcessorTime` descontando o tempo de CPU dos threads do gerador de carga (`CpuStress`).");

        string outDir = Path.Combine(AppContext.BaseDirectory, "baseline");
        Directory.CreateDirectory(outDir);
        string outFile = Path.Combine(outDir, $"rhythm-f2-{DateTime.Now:yyyyMMdd-HHmmss}.md");
        await File.WriteAllTextAsync(outFile, sb.ToString());
        _output.WriteLine(sb.ToString());
        _output.WriteLine("Salvo em " + outFile);
    }

    private static async Task<(Stats interval, Stats hold, double cpuMs)> MeasureWithProviderAsync(
        IDelayProvider provider, int charDelayMs, int units, CpuStress? stress)
    {
        using var dir = new TempDir();
        var layout = FakeKeyboardLayout.Abnt2();
        var injector = new RecordingInjector();
        var engine = new KeyboardInputService(
            new KeyboardMappingService(layout), TestLog.Create(dir), injector, layout, provider);

        var proc = Process.GetCurrentProcess();
        proc.Refresh();
        double stressBeforeMs = stress?.ReadTotalStressCpuMs() ?? 0.0;
        double procBeforeMs = proc.TotalProcessorTime.TotalMilliseconds;

        await engine.TypeTextAsync(
            new string('a', units),
            new TypingOptions
            {
                CharDelayMs = charDelayMs,
                EnterDelayMs = 0,
                Method = InputMethod.TecladoFisico,
                Profile = TypingProfile.Legacy(charDelayMs, 0)
            },
            null,
            PauseToken.None,
            CancellationToken.None);

        proc.Refresh();
        double procAfterMs = proc.TotalProcessorTime.TotalMilliseconds;
        double stressAfterMs = stress?.ReadTotalStressCpuMs() ?? 0.0;
        double cpuMs = Math.Max(0.0, (procAfterMs - procBeforeMs) - (stressAfterMs - stressBeforeMs));

        var main = injector.Delivered.Where(e => e.IsScanCode).ToList();
        var downs = main.Where(e => !e.IsKeyUp).Select(e => e.Timestamp).ToList();
        var ups = main.Where(e => e.IsKeyUp).Select(e => e.Timestamp).ToList();

        var intervals = downs.Zip(downs.Skip(1), (a, b) => ToMs(b - a)).ToList();
        var holds = downs.Zip(ups, (d, u) => ToMs(u - d)).ToList();
        return (Stats.Of(intervals), Stats.Of(holds), cpuMs);
    }

    private sealed class LegacyTaskDelayProvider : IDelayProvider
    {
        public long Timestamp() => Stopwatch.GetTimestamp();
        public void HoldExact(TimeSpan d) => KeyStrokeExecutor.PreciseDelay((int)Math.Round(d.TotalMilliseconds));
        public ValueTask WaitAsync(TimeSpan d, CancellationToken ct) =>
            d > TimeSpan.Zero ? new ValueTask(Task.Delay(d, ct)) : ValueTask.CompletedTask;
    }

    private static async Task<(Stats interval, Stats hold)> MeasureAsync(int charDelayMs, int units)
    {
        using var h = new EngineHarness();
        await h.TypeAsync(new string('a', units), EngineHarness.Options(charDelayMs: charDelayMs));

        var main = h.Injector.Delivered.Where(e => e.IsScanCode).ToList();
        var downs = main.Where(e => !e.IsKeyUp).Select(e => e.Timestamp).ToList();
        var ups = main.Where(e => e.IsKeyUp).Select(e => e.Timestamp).ToList();

        var intervals = downs.Zip(downs.Skip(1), (a, b) => ToMs(b - a)).ToList();
        var holds = downs.Zip(ups, (d, u) => ToMs(u - d)).ToList();
        return (Stats.Of(intervals), Stats.Of(holds));
    }

    private static double ToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    private readonly record struct Stats(double Median, double P95, double Max)
    {
        public static Stats Of(List<double> values)
        {
            values.Sort();
            double At(double q) => values[Math.Min(values.Count - 1, (int)Math.Ceiling(q * values.Count) - 1)];
            return new Stats(At(0.5), At(0.95), values[^1]);
        }
    }

    private sealed class CpuStress : IDisposable
    {
        private volatile bool _stop;
        private readonly List<Thread> _threads = new();
        private readonly List<uint> _osThreadIds = new();
        private readonly object _lock = new();

        public CpuStress()
        {
            using var ready = new CountdownEvent(Environment.ProcessorCount);
            for (int i = 0; i < Environment.ProcessorCount; i++)
            {
                var t = new Thread(() =>
                {
                    lock (_lock) _osThreadIds.Add(GetCurrentThreadId());
                    ready.Signal();
                    while (!_stop) Thread.SpinWait(1000);
                })
                {
                    IsBackground = true,
                    Priority = ThreadPriority.Normal
                };
                t.Start();
                _threads.Add(t);
            }
            ready.Wait(TimeSpan.FromSeconds(2));
        }

        public double ReadTotalStressCpuMs()
        {
            var proc = Process.GetCurrentProcess();
            proc.Refresh();
            HashSet<uint> ids;
            lock (_lock) ids = new HashSet<uint>(_osThreadIds);

            double totalMs = 0.0;
            foreach (ProcessThread pt in proc.Threads)
            {
                if (ids.Contains((uint)pt.Id))
                {
                    try { totalMs += pt.TotalProcessorTime.TotalMilliseconds; } catch { }
                }
            }
            return totalMs;
        }

        public void Dispose()
        {
            _stop = true;
            foreach (var t in _threads) t.Join();
        }

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();
    }
}
