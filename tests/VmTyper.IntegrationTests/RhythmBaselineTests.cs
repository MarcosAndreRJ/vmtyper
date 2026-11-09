using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
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

        public CpuStress()
        {
            for (int i = 0; i < Environment.ProcessorCount; i++)
            {
                var t = new Thread(() => { while (!_stop) Thread.SpinWait(1000); }) { IsBackground = true };
                t.Start();
                _threads.Add(t);
            }
        }

        public void Dispose()
        {
            _stop = true;
            foreach (var t in _threads) t.Join();
        }
    }
}
