using System.Diagnostics;
using VmTyper.Services;

namespace VmTyper.Models;

/// <summary>
/// Resumo numérico (histograma por faixas + percentis solicitados vs. obtidos em ms)
/// persistível no histórico sem expor conteúdo do texto (Fase 2 §5).
/// </summary>
public sealed record TimingHistogramSummary(
    int SampleCount,
    double RequestedMedianMs,
    double RequestedP95Ms,
    double RequestedMaxMs,
    double ActualMedianMs,
    double ActualP95Ms,
    double ActualMaxMs,
    IReadOnlyDictionary<string, int> Buckets)
{
    public static TimingHistogramSummary Empty { get; } = new(
        0, 0, 0, 0, 0, 0, 0, new Dictionary<string, int>());
}

/// <summary>
/// Fotografia imutável de métricas de uma sessão de digitação (Fase 2 §5).
/// Não inclui duplicações confirmadas (que pertencem exclusivamente à Fase 3 com receptor).
/// </summary>
public sealed record SessionMetrics(
    string ProfileName,
    int AttemptedUnits,
    int SentUnits,
    int EventsSent,
    int SkippedUnits,
    int FailedUnits,
    double AverageCps,
    double TotalSeconds,
    double CpuMilliseconds,
    int TargetLostIncidents,
    TimingHistogramSummary HoldSummary,
    TimingHistogramSummary GapSummary);

/// <summary>
/// Acumulador de métricas durante uma sessão de digitação (Fase 2 §5).
/// </summary>
public sealed class TypingMetrics
{
    private static readonly (string Label, double UpperBoundMs)[] BucketBounds =
    {
        ("0..2ms", 2.0),
        ("2..5ms", 5.0),
        ("5..15ms", 15.0),
        ("15..40ms", 40.0),
        ("40..100ms", 100.0),
        ("100..300ms", 300.0),
        (">300ms", double.PositiveInfinity)
    };

    private readonly string _profileName;
    private readonly List<DelaySample> _holds = new();
    private readonly List<DelaySample> _gaps = new();
    private TimeSpan _cpuStart;
    private long _timestampStart;

    public int AttemptedUnits { get; private set; }
    public int SentUnits { get; private set; }
    public int EventsSent { get; private set; }
    public int SkippedUnits { get; private set; }
    public int FailedUnits { get; private set; }
    public int TargetLostIncidents { get; set; }

    public TypingMetrics(string profileName = "Legacy")
    {
        _profileName = profileName;
    }

    public void BeginSession()
    {
        _timestampStart = Stopwatch.GetTimestamp();
        try
        {
            _cpuStart = Process.GetCurrentProcess().TotalProcessorTime;
        }
        catch
        {
            _cpuStart = TimeSpan.Zero;
        }
    }

    public void RecordUnit(InputUnit unit, KeyTiming timing, UnitResult result)
    {
        AttemptedUnits++;
        EventsSent += result.EventsSent;

        switch (result.Outcome)
        {
            case UnitOutcome.Sent:
                SentUnits++;
                if (result.TimestampDown > 0 && result.TimestampUp >= result.TimestampDown)
                {
                    double actualHoldSec = (double)(result.TimestampUp - result.TimestampDown) / Stopwatch.Frequency;
                    _holds.Add(new DelaySample(timing.Hold, TimeSpan.FromSeconds(actualHoldSec)));
                }
                break;
            case UnitOutcome.Skipped:
                SkippedUnits++;
                break;
            case UnitOutcome.Failed:
                FailedUnits++;
                break;
        }
    }

    public void RecordGap(TimeSpan requested, TimeSpan actual)
    {
        _gaps.Add(new DelaySample(requested, actual));
    }

    public SessionMetrics BuildSnapshot()
    {
        long now = Stopwatch.GetTimestamp();
        double elapsedSec = _timestampStart > 0
            ? Math.Max(0.0, (double)(now - _timestampStart) / Stopwatch.Frequency)
            : 0.0;

        double cpuMs = 0.0;
        try
        {
            var cpuNow = Process.GetCurrentProcess().TotalProcessorTime;
            cpuMs = Math.Max(0.0, (cpuNow - _cpuStart).TotalMilliseconds);
        }
        catch
        {
            // Ignora se o processo não permitir leitura.
        }

        double cps = elapsedSec > 0.0 ? SentUnits / elapsedSec : 0.0;

        return new SessionMetrics(
            _profileName,
            AttemptedUnits,
            SentUnits,
            EventsSent,
            SkippedUnits,
            FailedUnits,
            Math.Round(cps, 2),
            Math.Round(elapsedSec, 4),
            Math.Round(cpuMs, 2),
            TargetLostIncidents,
            Summarize(_holds),
            Summarize(_gaps));
    }

    public static TimingHistogramSummary Summarize(IReadOnlyList<DelaySample> samples)
    {
        if (samples.Count == 0) return TimingHistogramSummary.Empty;

        var reqMs = samples.Select(s => s.Requested.TotalMilliseconds).OrderBy(x => x).ToArray();
        var actMs = samples.Select(s => s.Actual.TotalMilliseconds).OrderBy(x => x).ToArray();

        var buckets = new Dictionary<string, int>();
        foreach (var (label, _) in BucketBounds)
        {
            buckets[label] = 0;
        }

        foreach (double v in actMs)
        {
            foreach (var (label, upper) in BucketBounds)
            {
                if (v <= upper)
                {
                    buckets[label]++;
                    break;
                }
            }
        }

        return new TimingHistogramSummary(
            samples.Count,
            Math.Round(Percentile(reqMs, 0.50), 3),
            Math.Round(Percentile(reqMs, 0.95), 3),
            Math.Round(reqMs[^1], 3),
            Math.Round(Percentile(actMs, 0.50), 3),
            Math.Round(Percentile(actMs, 0.95), 3),
            Math.Round(actMs[^1], 3),
            buckets);
    }

    private static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0) return 0.0;
        if (sorted.Length == 1) return sorted[0];
        double idx = p * (sorted.Length - 1);
        int lo = (int)Math.Floor(idx);
        int hi = (int)Math.Ceiling(idx);
        if (lo == hi) return sorted[lo];
        return sorted[lo] + (idx - lo) * (sorted[hi] - sorted[lo]);
    }
}

