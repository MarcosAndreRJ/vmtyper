using System.Diagnostics;
using VmTyper.Helpers;
using VmTyper.Models;
using VmTyper.Native;

namespace VmTyper.Services;

/// <summary>
/// Implementação de referência de <see cref="IKeyboardInputService"/> baseada em
/// <see cref="KeyStrokePlanner"/> (§2.1) e <see cref="KeyStrokeExecutor"/> +
/// <see cref="InjectedKeyTracker"/> (§2.2).
/// </summary>
public sealed class KeyboardInputService : IKeyboardInputService
{
    private readonly KeyboardMappingService _mapping;
    private readonly LogService _log;
    private readonly IInputInjector _injector;
    private readonly IKeyboardLayoutProvider _layoutProvider;
    private readonly KeyStrokePlanner _planner;
    private readonly InjectedKeyTracker _tracker = new();
    private readonly KeyStrokeExecutor _executor;
    private int _isTyping;

    internal InjectedKeyTracker Tracker => _tracker;
    internal KeyStrokePlanner Planner => _planner;

    internal KeyboardInputService(
        KeyboardMappingService mapping,
        LogService log,
        IInputInjector injector,
        IKeyboardLayoutProvider layoutProvider)
    {
        _mapping = mapping;
        _log = log;
        _injector = injector;
        _layoutProvider = layoutProvider;
        _planner = new KeyStrokePlanner(mapping);
        _executor = new KeyStrokeExecutor(injector, mapping, _tracker, log);
    }

    public void EmergencyReleaseTrackedKeys()
    {
        _tracker.ReleaseAll(_injector, _log);
    }

    public PreflightReport Plan(string text, TypingOptions options)
    {
        var layout = _mapping.GetLayout(options.TargetLayout);
        var planOptions = new PlanOptions(
            options.Method,
            layout,
            options.Substitutions,
            options.PreserveOriginalIndentation,
            options.IndentationStrategy,
            options.FallbackUnsupportedToUnicode);
        return _planner.Plan(text, planOptions);
    }

    public async Task TypeTextAsync(
        string text,
        TypingOptions options,
        IProgress<TypingProgress>? progress,
        PauseToken pauseToken,
        CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _isTyping, 1, 0) != 0)
        {
            throw new InvalidOperationException("Já existe uma sessão de digitação ativa no motor.");
        }

        try
        {
            await TypeTextCoreAsync(text, options, progress, pauseToken, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _isTyping, 0);
        }
    }

    private async Task TypeTextCoreAsync(
        string text,
        TypingOptions options,
        IProgress<TypingProgress>? progress,
        PauseToken pauseToken,
        CancellationToken cancellationToken)
    {
        var layout = _mapping.GetLayout(options.TargetLayout);

        // §2.2 item 1 (H4): aguardar até 2 s que nenhum modificador físico esteja pressionado.
        await _executor.WaitForPhysicalModifiersReleasedAsync(layout, KeyStrokeExecutor.PhysicalModifierTimeout, cancellationToken)
            .ConfigureAwait(false);

        var planOptions = new PlanOptions(
            options.Method,
            layout,
            options.Substitutions,
            options.PreserveOriginalIndentation,
            options.IndentationStrategy,
            options.FallbackUnsupportedToUnicode);

        var report = _planner.Plan(text, planOptions);
        if (report.AppliedSubstitutionRules.Count > 0)
        {
            _log.Info($"{report.AppliedSubstitutionRules.Count} substituições aplicadas (regras {string.Join(", ", report.AppliedSubstitutionRules)}).");
        }

        int totalUnits = report.TotalUnits;
        int sent = 0;
        int skipped = 0;
        int failed = 0;
        var stopwatch = Stopwatch.StartNew();

        if (options.DiagnosticMode)
        {
            _log.Info($"[Diagnóstico] Início. Total de unidades: {totalUnits}. Método: {options.Method}. PreservarIndentação: {options.PreserveOriginalIndentation}.");
        }

        try
        {
            foreach (var unit in report.Units)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await pauseToken.WaitWhilePausedAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                if (options.BeforeUnitAsync is not null)
                {
                    await options.BeforeUnitAsync(unit.Index, cancellationToken).ConfigureAwait(false);
                    await pauseToken.WaitWhilePausedAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var result = _executor.ExecuteUnit(unit);
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
                    _log.Info($"[Diagnóstico] Index={unit.Index} Categoria={LogService.Categorize(unit.Text)} Método={unit.Kind} Enc={firstScan.Enc} Code=0x{firstScan.Code:X4} Eventos={result.EventsSent}");
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

                int delay = unit.IsLineBreak ? options.EnterDelayMs : options.CharDelayMs;
                if (delay > 0)
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            // §2.2 itens 5 e 6: solta somente o que o tracker contém (zero KeyUp órfão em sucesso).
            _tracker.ReleaseAll(_injector, _log);
        }
    }

    private static string Display(string g) => g switch
    {
        "\n" => "\\n",
        "\r" => "\\r",
        "\t" => "\\t",
        _ => g
    };

    /// <summary>
    /// Normaliza todas as variantes de quebra de linha (\r\n, \r, \n) para
    /// \n, garantindo que cada quebra resulte em exatamente um Enter.
    /// </summary>
    public static string NormalizeLineEndings(string text)
    {
        return text.Replace("\r\n", "\n").Replace("\r", "\n");
    }
}
