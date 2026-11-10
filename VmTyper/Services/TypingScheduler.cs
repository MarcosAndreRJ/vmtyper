using System.Diagnostics;
using VmTyper.Helpers;
using VmTyper.Models;

namespace VmTyper.Services;

public sealed record DryRunReport(
    PreflightReport Plan,
    IReadOnlyList<KeyTiming> Timings,
    TimeSpan EstimatedDuration);

/// <summary>
/// Orquestra o plano (<see cref="KeyStrokePlanner"/>), a política de temporização
/// (<see cref="TypingTimingPolicy"/>), o executor (<see cref="KeyStrokeExecutor"/>),
/// o provedor de atraso (<see cref="IDelayProvider"/>) e as métricas (<see cref="TypingMetrics"/>)
/// (Fase 2 §4).
/// </summary>
internal sealed class TypingScheduler
{
    private readonly KeyStrokePlanner _planner;
    private readonly KeyStrokeExecutor _executor;
    private readonly IDelayProvider _delay;
    private readonly LogService _log;

    public TypingScheduler(
        KeyStrokePlanner planner,
        KeyStrokeExecutor executor,
        IDelayProvider delay,
        LogService log)
    {
        _planner = planner;
        _executor = executor;
        _delay = delay;
        _log = log;
    }

    /// <summary>
    /// Modo dry-run (sem injetar): gera a sequência de <see cref="KeyTiming"/> e a
    /// duração total estimada do bloco (§4).
    /// </summary>
    public static DryRunReport DryRun(PreflightReport plan, TypingProfile profile)
    {
        var deterministicProfile = profile.Seed.HasValue ? profile : profile with { Seed = 42 };
        var policy = new TypingTimingPolicy(deterministicProfile);
        var timings = policy.GenerateAll(plan.Units);

        double totalMs = 0.0;
        for (int i = 0; i < plan.Units.Count; i++)
        {
            var u = plan.Units[i];
            var t = timings[i];
            if (u.Kind != UnitKind.Unsupported && u.Events.Count > 0)
            {
                int holdsInUnit = CountHoldsInUnit(u);
                totalMs += holdsInUnit * t.Hold.TotalMilliseconds;
            }
            totalMs += t.GapAfter.TotalMilliseconds;
        }

        return new DryRunReport(plan, timings, TimeSpan.FromMilliseconds(totalMs));
    }

    public static int CountHoldsInUnit(InputUnit unit)
    {
        int count = 0;
        for (int i = 1; i < unit.Events.Count; i++)
        {
            if (unit.Events[i].Kind == KeyEventKind.Up && unit.Events[i - 1].Kind == KeyEventKind.Down)
            {
                count++;
            }
        }
        return Math.Max(1, count);
    }

    public async Task<SessionMetrics> ExecuteAsync(
        PreflightReport report,
        TypingOptions options,
        TypingProfile profile,
        IProgress<TypingProgress>? progress,
        PauseToken pauseToken,
        CancellationToken cancellationToken,
        TypingDiagnosticsEngine? diagnostics = null)
    {
        var policy = new TypingTimingPolicy(profile);
        var contexts = TypingTimingPolicy.BuildContexts(report.Units);
        var metrics = new TypingMetrics(profile.Name);
        metrics.BeginSession();

        if (diagnostics is not null && (options.RecordJsonlDiagnostics || options.DiagnosticMode))
        {
            diagnostics.BeginSession(
                Guid.NewGuid(),
                options,
                profile,
                new FeatureFlags { AdaptiveTiming = profile.Kind != ProfileKind.Legacy, DetailedTrace = _log.DetailedTrace },
                _log.DetailedTrace);
        }

        int totalUnits = report.TotalUnits;
        int sent = 0;
        int skipped = 0;
        int failed = 0;
        double adaptiveBackoffFactor = 1.0;
        var stopwatch = Stopwatch.StartNew();

        if (options.DiagnosticMode)
        {
            _log.Info($"[Diagnóstico] Início. Total de unidades: {totalUnits}. Método: {options.Method}. Perfil: {profile.Name}. PreservarIndentação: {options.PreserveOriginalIndentation}.");
        }

        try
        {
            for (int i = 0; i < report.Units.Count; i++)
            {
                var unit = report.Units[i];
                var ctx = contexts[i];

                cancellationToken.ThrowIfCancellationRequested();
                await pauseToken.WaitWhilePausedAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                if (options.BeforeUnitAsync is not null)
                {
                    await options.BeforeUnitAsync(unit.Index, cancellationToken).ConfigureAwait(false);
                    await pauseToken.WaitWhilePausedAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var timing = policy.Next(ctx);
                if (options.EnableAdaptiveSlowdown && adaptiveBackoffFactor > 1.001 && timing.GapAfter > TimeSpan.Zero)
                {
                    double boostedMs = Math.Min(
                        profile.Limits.MaxGapMs,
                        timing.GapAfter.TotalMilliseconds * adaptiveBackoffFactor);
                    timing = new KeyTiming(
                        timing.Hold,
                        TimeSpan.FromMilliseconds(Math.Round(boostedMs)),
                        timing.Reason + "+AdaptiveSlowdown");
                }

                // Não cancelável dentro da unidade: garante que todo KeyDown tenha seu KeyUp.
                var result = _executor.ExecuteUnit(unit, timing.Hold);
                metrics.RecordUnit(unit, timing, result);
                if (diagnostics is not null && (options.RecordJsonlDiagnostics || options.DiagnosticMode))
                {
                    diagnostics.RecordEvent(unit, timing, result);
                }

                switch (result.Outcome)
                {
                    case UnitOutcome.Sent:
                        sent++;
                        break;
                    case UnitOutcome.Skipped:
                        skipped++;
                        _log.Warn($"Unidade #{unit.Index} não suportada pelo layout ativo e ignorada (Categoria={LogService.Categorize(unit.Text)}).");
                        break;
                    case UnitOutcome.Failed:
                        failed++;
                        throw new InvalidOperationException(result.ErrorMessage ?? $"Falha ao enviar unidade #{unit.Index} (erro Win32: {result.NativeError}).");
                }

                if (options.DiagnosticMode)
                {
                    var firstScan = unit.Events.FirstOrDefault(e => e.Kind == KeyEventKind.Down);
                    _log.Info($"[Diagnóstico] Index={unit.Index} Categoria={LogService.Categorize(unit.Text)} Método={unit.Kind} Enc={firstScan.Enc} Code=0x{firstScan.Code:X4} Eventos={result.EventsSent} HoldMs={timing.Hold.TotalMilliseconds:0} GapMs={timing.GapAfter.TotalMilliseconds:0} Reason={timing.Reason}");
                    if (_log.DetailedTrace)
                    {
                        _log.TraceSensitive($"[Diagnóstico] Index={unit.Index} Grapheme='{Display(unit.Text)}' Método={unit.Kind}");
                    }
                }

                progress?.Report(new TypingProgress
                {
                    TotalUnits = totalUnits,
                    Sent = sent,
                    Skipped = skipped,
                    Failed = failed,
                    Elapsed = stopwatch.Elapsed,
                    CharactersPerSecond = stopwatch.Elapsed.TotalSeconds > 0
                        ? sent / stopwatch.Elapsed.TotalSeconds
                        : 0
                });

                if (timing.GapAfter > TimeSpan.Zero)
                {
                    long gapStart = _delay.Timestamp();
                    await _delay.WaitAsync(timing.GapAfter, cancellationToken).ConfigureAwait(false);
                    long gapEnd = _delay.Timestamp();
                    double actualGapSec = Math.Max(0.0, (double)(gapEnd - gapStart) / Stopwatch.Frequency);
                    var actualSpan = TimeSpan.FromSeconds(actualGapSec);
                    metrics.RecordGap(timing.GapAfter, actualSpan);

                    if (options.EnableAdaptiveSlowdown)
                    {
                        double overshootMs = actualSpan.TotalMilliseconds - timing.GapAfter.TotalMilliseconds;
                        if (overshootMs > 10.0)
                        {
                            adaptiveBackoffFactor = Math.Min(2.0, adaptiveBackoffFactor * 1.4);
                        }
                        else
                        {
                            adaptiveBackoffFactor = Math.Max(1.0, 1.0 + (adaptiveBackoffFactor - 1.0) * 0.80);
                        }
                    }
                }
                else
                {
                    metrics.RecordGap(TimeSpan.Zero, TimeSpan.Zero);
                }
            }
        }
        finally
        {
            diagnostics?.EndSession();
            _executor.Tracker.ReleaseAll(_executor.Injector, _log);
        }

        return metrics.BuildSnapshot();
    }

    private static string Display(string g) => g switch
    {
        "\n" => "\\n",
        "\r" => "\\r",
        "\t" => "\\t",
        _ => g
    };
}

