using VmTyper.Helpers;
using VmTyper.Models;
using VmTyper.Native;

namespace VmTyper.Services;

/// <summary>
/// Fachada de <see cref="IKeyboardInputService"/> sobre <see cref="KeyStrokePlanner"/>,
/// <see cref="KeyStrokeExecutor"/> e <see cref="TypingScheduler"/> (Fase 2 §4).
/// </summary>
public sealed class KeyboardInputService : IKeyboardInputService
{
    private static readonly HighResolutionDelayProvider SharedDelayProvider = new();

    private readonly KeyboardMappingService _mapping;
    private readonly LogService _log;
    private readonly IInputInjector _injector;
    private readonly IKeyboardLayoutProvider _layoutProvider;
    private readonly IDelayProvider _delayProvider;
    private readonly KeyStrokePlanner _planner;
    private readonly InjectedKeyTracker _tracker = new();
    private readonly KeyStrokeExecutor _executor;
    private readonly TypingScheduler _scheduler;
    private int _isTyping;

    internal InjectedKeyTracker Tracker => _tracker;
    internal KeyStrokePlanner Planner => _planner;
    public SessionMetrics? LastSessionMetrics { get; private set; }

    internal KeyboardInputService(
        KeyboardMappingService mapping,
        LogService log,
        IInputInjector injector,
        IKeyboardLayoutProvider layoutProvider,
        IDelayProvider? delayProvider = null)
    {
        _mapping = mapping;
        _log = log;
        _injector = injector;
        _layoutProvider = layoutProvider;
        _delayProvider = delayProvider ?? SharedDelayProvider;
        _planner = new KeyStrokePlanner(mapping);
        _executor = new KeyStrokeExecutor(injector, mapping, _tracker, log, _delayProvider);
        _scheduler = new TypingScheduler(_planner, _executor, _delayProvider, _log);
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

    public DryRunReport DryRun(string text, TypingOptions options)
    {
        var plan = Plan(text, options);
        var profile = options.Profile ?? TypingProfile.Legacy(options.CharDelayMs, options.EnterDelayMs);
        return TypingScheduler.DryRun(plan, profile);
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

        var profile = options.Profile ?? TypingProfile.Legacy(options.CharDelayMs, options.EnterDelayMs);
        LastSessionMetrics = await _scheduler.ExecuteAsync(
            report,
            options,
            profile,
            progress,
            pauseToken,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Normaliza todas as variantes de quebra de linha (\r\n, \r, \n) para
    /// \n, garantindo que cada quebra resulte em exatamente um Enter.
    /// </summary>
    public static string NormalizeLineEndings(string text)
    {
        return text.Replace("\r\n", "\n").Replace("\r", "\n");
    }
}
