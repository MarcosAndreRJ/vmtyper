using VmTyper.Models;

namespace VmTyper.Services;

public sealed record AnchorValidationResult(bool IsValid, TargetAnchor? Anchor, string? ErrorMessage = null);

public enum TargetCheckStatus
{
    Ok,
    TitleChangedWarning,
    TargetLost
}

public sealed record TargetCheckResult(
    TargetCheckStatus Status,
    TargetLostIncident? Incident = null,
    string? WarningMessage = null);

/// <summary>
/// Estado multi-nível do navegador integrado (Fase 4 §4).
/// Níveis não verificáveis aparecem sempre como <c>"desconhecido"</c>, nunca como <c>"ok"</c>.
/// </summary>
public sealed record EmbeddedFocusSnapshot(
    IntPtr HostWindowHwnd,
    bool IsHostWindowForeground,
    bool IsWebViewControlFocused,
    bool? IsDocumentFocused,
    string? ActiveElementTagOrId,
    bool? IsConsoleElementActive,
    bool IsWebViewProcessAlive = true)
{
    public string HostForegroundStatus => IsHostWindowForeground ? "ok" : "perdido";
    public string ControlFocusStatus => IsWebViewControlFocused ? "ok" : "perdido";
    public string DocumentFocusStatus => IsDocumentFocused switch { true => "ok", false => "perdido", null => "desconhecido" };
    public string ActiveElementStatus => string.IsNullOrWhiteSpace(ActiveElementTagOrId) ? "desconhecido" : ActiveElementTagOrId!;
    public string ConsoleFocusStatus => IsConsoleElementActive switch { true => "ok", false => "perdido", null => "desconhecido" };
    /// <summary>A prontidão interna da VM nunca é verificável pelo host (§4).</summary>
    public string GuestReadyStatus => "desconhecido";
}

public interface IEmbeddedFocusProbe
{
    EmbeddedFocusSnapshot ProbeFocus();
}

/// <summary>
/// Monitora a janela de destino antes e durante a transmissão (§3 TargetGuard: C, N13 / ADR 0003).
/// </summary>
public sealed class TargetGuard : IDisposable
{
    private static readonly Dictionary<ExpectedBrowser, string[]> BrowserProcessNames = new()
    {
        [ExpectedBrowser.Chrome] = new[] { "chrome" },
        [ExpectedBrowser.Edge] = new[] { "msedge" },
        [ExpectedBrowser.Firefox] = new[] { "firefox" },
    };

    private readonly IForegroundWindowProvider _provider;
    private readonly LogService _log;
    private IDisposable? _hookSubscription;
    private volatile bool _targetLostFlag;
    private string? _lastObservedTitle;

    public TargetAnchor? CurrentAnchor { get; private set; }
    public bool IsTargetLost => _targetLostFlag;
    public List<TargetLostIncident> Incidents { get; } = new();

    public TargetGuard(IForegroundWindowProvider provider, LogService log)
    {
        _provider = provider;
        _log = log;
    }

    /// <summary>
    /// No fim da contagem regressiva (§3.1), captura a <see cref="TargetAnchor"/> e falha se:
    /// - o foreground for o próprio VmTyper;
    /// - <paramref name="validateBrowser"/> estiver ligado e o processo não for o navegador esperado.
    /// </summary>
    public AnchorValidationResult TryCaptureAnchor(bool validateBrowser, ExpectedBrowser expectedBrowser)
    {
        var anchor = _provider.CaptureAnchor();
        if (anchor is null || anchor.Hwnd == IntPtr.Zero)
        {
            return new AnchorValidationResult(false, null, "Não foi possível identificar a janela em primeiro plano.");
        }

        if (anchor.Pid == _provider.CurrentProcessId)
        {
            return new AnchorValidationResult(false, anchor, "A janela em primeiro plano ainda é o próprio VmTyper. Clique na janela de destino durante a contagem.");
        }

        if (validateBrowser && expectedBrowser != ExpectedBrowser.Nenhum)
        {
            if (BrowserProcessNames.TryGetValue(expectedBrowser, out var candidates))
            {
                bool matches = candidates.Any(c => string.Equals(c, anchor.ProcessName, StringComparison.OrdinalIgnoreCase));
                if (!matches)
                {
                    return new AnchorValidationResult(
                        false,
                        anchor,
                        $"A janela ativa ({anchor.ProcessName}) não corresponde ao destino configurado ({expectedBrowser}).");
                }
            }
        }

        AcceptNewAnchor(anchor);
        return new AnchorValidationResult(true, anchor);
    }

    public void AcceptNewAnchor(TargetAnchor anchor)
    {
        CurrentAnchor = anchor;
        _lastObservedTitle = anchor.Title;
        _targetLostFlag = false;

        _hookSubscription?.Dispose();
        _hookSubscription = _provider.SubscribeForegroundChanged(hwndRoot =>
        {
            if (CurrentAnchor is not null && hwndRoot != CurrentAnchor.Hwnd)
            {
                _targetLostFlag = true;
            }
        });
    }

    /// <summary>
    /// Verificação síncrona antes de cada unidade (§3.2). Compara <c>GetForegroundRootHwnd()</c>
    /// com a âncora (sem chamar <c>Process.GetProcessById</c>, cobrindo N13) e avalia o título.
    /// </summary>
    public TargetCheckResult CheckBeforeUnit(int unitIndex, bool strictTitleMode)
    {
        var anchor = CurrentAnchor;
        if (anchor is null)
        {
            return new TargetCheckResult(TargetCheckStatus.Ok);
        }

        IntPtr currentHwnd = _provider.GetForegroundRootHwnd();
        if (_targetLostFlag || currentHwnd != anchor.Hwnd)
        {
            _targetLostFlag = true;
            string currentTitle = _provider.GetWindowTitle(currentHwnd);
            var incident = new TargetLostIncident(
                unitIndex,
                anchor.Hwnd,
                anchor.Title,
                currentHwnd,
                currentTitle,
                DateTime.UtcNow);
            Incidents.Add(incident);
            _log.Warn($"[TargetGuard] Destino perdido na unidade #{unitIndex}: de HWND=0x{anchor.Hwnd:X} para HWND=0x{currentHwnd:X}.");
            return new TargetCheckResult(TargetCheckStatus.TargetLost, incident);
        }

        string titleNow = _provider.GetWindowTitle(currentHwnd);
        if (!string.Equals(titleNow, _lastObservedTitle, StringComparison.Ordinal))
        {
            string prevTitle = _lastObservedTitle ?? anchor.Title;
            _lastObservedTitle = titleNow;
            if (strictTitleMode)
            {
                _targetLostFlag = true;
                var incident = new TargetLostIncident(
                    unitIndex,
                    anchor.Hwnd,
                    prevTitle,
                    currentHwnd,
                    titleNow,
                    DateTime.UtcNow);
                Incidents.Add(incident);
                _log.Warn($"[TargetGuardStrict] Mudança de título na unidade #{unitIndex} disparou pausa.");
                return new TargetCheckResult(TargetCheckStatus.TargetLost, incident);
            }

            string warn = $"[TargetGuard] Aviso: título da janela mudou na unidade #{unitIndex}.";
            _log.Warn(warn);
            return new TargetCheckResult(TargetCheckStatus.TitleChangedWarning, WarningMessage: warn);
        }

        return new TargetCheckResult(TargetCheckStatus.Ok);
    }

    /// <summary>
    /// Modo <c>Embedded</c> do <see cref="TargetGuard"/> (Fase 4 §4): verifica os níveis disponíveis
    /// antes de cada unidade no navegador integrado e pausa em qualquer perda verificável.
    /// </summary>
    public TargetCheckResult CheckEmbeddedBeforeUnit(
        int unitIndex,
        IEmbeddedFocusProbe probe,
        bool requireHostForeground = true)
    {
        var snap = probe.ProbeFocus();

        if (!snap.IsWebViewProcessAlive)
        {
            _targetLostFlag = true;
            var incident = new TargetLostIncident(
                unitIndex,
                snap.HostWindowHwnd,
                "WebView2 Ativo",
                IntPtr.Zero,
                "WebView2 ProcessFailed",
                DateTime.UtcNow);
            Incidents.Add(incident);
            _log.Error($"[TargetGuard:Embedded] Processo do WebView2 falhou na unidade #{unitIndex}. Sessão pausada.");
            return new TargetCheckResult(TargetCheckStatus.TargetLost, incident);
        }

        if (requireHostForeground && !snap.IsHostWindowForeground)
        {
            _targetLostFlag = true;
            IntPtr currentFg = _provider.GetForegroundRootHwnd();
            var incident = new TargetLostIncident(
                unitIndex,
                snap.HostWindowHwnd,
                "WebView2 Integrado",
                currentFg,
                _provider.GetWindowTitle(currentFg),
                DateTime.UtcNow);
            Incidents.Add(incident);
            _log.Warn($"[TargetGuard:Embedded] Janela integrada perdeu o foreground na unidade #{unitIndex}.");
            return new TargetCheckResult(TargetCheckStatus.TargetLost, incident);
        }

        if (!snap.IsWebViewControlFocused || snap.IsDocumentFocused == false || snap.IsConsoleElementActive == false)
        {
            _targetLostFlag = true;
            var incident = new TargetLostIncident(
                unitIndex,
                snap.HostWindowHwnd,
                "WebView2 Com Foco",
                snap.HostWindowHwnd,
                $"ctrl={snap.ControlFocusStatus}, doc={snap.DocumentFocusStatus}, console={snap.ConsoleFocusStatus}, vm={snap.GuestReadyStatus}",
                DateTime.UtcNow);
            Incidents.Add(incident);
            _log.Warn($"[TargetGuard:Embedded] Foco interno perdido na unidade #{unitIndex} (ctrl={snap.ControlFocusStatus}, doc={snap.DocumentFocusStatus}, console={snap.ConsoleFocusStatus}, vm={snap.GuestReadyStatus}).");
            return new TargetCheckResult(TargetCheckStatus.TargetLost, incident);
        }

        return new TargetCheckResult(TargetCheckStatus.Ok);
    }

    public void ClearLostFlag() => _targetLostFlag = false;

    public void StopMonitoring()
    {
        _hookSubscription?.Dispose();
        _hookSubscription = null;
        CurrentAnchor = null;
        _targetLostFlag = false;
    }

    public void Dispose() => StopMonitoring();
}
