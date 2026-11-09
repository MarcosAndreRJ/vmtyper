using System.Collections.ObjectModel;
using System.Windows.Input;
using VmTyper.Helpers;
using VmTyper.Models;
using VmTyper.Services;

namespace VmTyper.ViewModels;

/// <summary>
/// Orquestra o fluxo completo de digitação: contagem regressiva, validação
/// de janela de destino, envio de caracteres via IKeyboardInputService,
/// progresso, pausa/continuação e cancelamento. A partir de qual bloco da
/// fila digitar, e quando avançar para o próximo, é decidido inteiramente
/// por <see cref="TypingQueueService"/> — este ViewModel apenas pergunta
/// "qual é o próximo?" e informa "terminei com sucesso" ou "cancelei".
/// Não contém nenhuma chamada P/Invoke diretamente — toda a interação com
/// o Windows passa pelos serviços injetados.
/// </summary>
public enum PreflightDecision
{
    Cancel,
    SkipUnsupported,
    FallbackToUnicode
}

public sealed class MainViewModel : ObservableObject
{
    public const int HotkeyIdStart = 1;
    public const int HotkeyIdPauseResume = 2;
    public const int HotkeyIdCancel = 3;

    private readonly IKeyboardInputService _typingService;
    private readonly ActiveWindowService _activeWindowService;
    private readonly SettingsService _settingsService;
    private readonly HistoryService _historyService;
    private readonly IHotkeyRegistrar _hotkeyService;
    private readonly TypingQueueService _queueService;
    private readonly LogService _log;
    private readonly TypingSessionCoordinator _coordinator;
    private readonly TargetGuard _targetGuard;
    private readonly Func<int, CancellationToken, Task> _delayAsync;

    private TypingSessionLease? _activeLease;
    private Guid? _activeBlockId;
    private DateTime _sessionStartedAt;
    private TypingState _stateBeforePause = TypingState.Digitando;

    public int ResumeCountdownSeconds { get; set; } = 2;
    public TargetGuard TargetGuard => _targetGuard;

    /// <summary>
    /// Callback de UI para blocos em PartiallySent / Interrupted / Failed (§1.2 nota ⁴).
    /// Recebe o bloco e retorna: null = cancelar; false = reenviar do início (padrão); true = continuar de SentUpToIndex.
    /// </summary>
    public Func<TextBlockItem, bool?>? ConfirmPartialResume { get; set; }

    /// <summary>
    /// Callback de pré-voo quando o plano contém unidades Unsupported (§2.1 E).
    /// O padrão quando nenhum handler de UI está conectado é Cancelar (nunca pula em silêncio).
    /// </summary>
    public Func<PreflightReport, PreflightDecision>? ConfirmPreflightUnsupported { get; set; }

    /// <summary>
    /// Callback quando o usuário retoma após perda de destino e o HWND mudou (§3.3):
    /// "O destino mudou. Usar a nova janela como destino?". Retorna true somente com confirmação explícita.
    /// </summary>
    public Func<TargetAnchor, TargetAnchor, bool>? ConfirmTargetChanged { get; set; }

    private int _skippedUnits;
    private int _failedUnits;

    public AppSettings Settings { get; private set; }

    public ObservableCollection<TextBlockItem> Blocks => _queueService.Blocks;
    public ObservableCollection<TypingSession> History { get; } = new();
    public ObservableCollection<string> LogLines { get; }

    private TypingState _state = TypingState.Pronto;
    public TypingState State
    {
        get => _state;
        set
        {
            if (SetField(ref _state, value))
            {
                OnPropertyChanged(nameof(StateText));
                OnPropertyChanged(nameof(IsBusy));
                OnPropertyChanged(nameof(CanEditQueue));
                OnPropertyChanged(nameof(CanPause));
                OnPropertyChanged(nameof(PauseResumeButtonText));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public string StateText => State switch
    {
        TypingState.Pronto => "Pronto",
        TypingState.AguardandoContagem => "Aguardando contagem regressiva",
        TypingState.Digitando => "Digitando",
        TypingState.Pausado => "Pausado",
        TypingState.PausadoDestinoPerdido => "Pausado: destino perdido",
        TypingState.Concluido => "Concluído",
        TypingState.Cancelado => "Cancelado",
        TypingState.Erro => "Erro",
        _ => State.ToString()
    };

    public bool IsBusy => State is TypingState.AguardandoContagem or TypingState.Digitando or TypingState.Pausado or TypingState.PausadoDestinoPerdido || _coordinator.IsActive;
    public bool CanEditQueue => !IsBusy;
    public bool CanPause => State is TypingState.AguardandoContagem or TypingState.Digitando or TypingState.Pausado or TypingState.PausadoDestinoPerdido;
    public string PauseResumeButtonText => State is TypingState.Pausado or TypingState.PausadoDestinoPerdido ? "CONTINUAR" : "PAUSAR";

    // --- Fila ---
    public bool HasNextBlock => _queueService.Current is not null;

    public string NextBlockLabel
    {
        get
        {
            var current = _queueService.Current;
            if (current is not null) return $"Próximo: Bloco {current.Order} de {_queueService.TotalBlocks}";
            return _queueService.Blocks.Any(b => b.Status == TextBlockStatus.Sent)
                ? "Todos os blocos foram enviados."
                : "Fila vazia — adicione conteúdo a um bloco.";
        }
    }

    public string DigitarButtonText
    {
        get
        {
            var current = _queueService.Current;
            if (current is not null) return $"DIGITAR BLOCO {current.Order}";
            return _queueService.Blocks.Any(b => b.Status == TextBlockStatus.Sent) ? "FILA ENVIADA" : "DIGITAR";
        }
    }

    public string QueueSummary =>
        $"Blocos: {_queueService.TotalBlocks}   |   Enviados: {_queueService.CompletedCount}   |   Pendentes: {_queueService.PendingCount}";

    public string QueueProgressLabel =>
        $"Fila: {_queueService.CompletedCount} de {_queueService.TotalBlocks} blocos enviados";

    private string _statusMessage = "Pronto para digitar.";
    public string StatusMessage
    {
        get => _statusMessage;
        set => SetField(ref _statusMessage, value);
    }

    private string _countdownText = string.Empty;
    public string CountdownText
    {
        get => _countdownText;
        set => SetField(ref _countdownText, value);
    }

    private int _typedCharacters;
    public int TypedCharacters
    {
        get => _typedCharacters;
        set => SetField(ref _typedCharacters, value);
    }

    private int _totalCharacters;
    public int TotalCharacters
    {
        get => _totalCharacters;
        set => SetField(ref _totalCharacters, value);
    }

    private double _percentComplete;
    public double PercentComplete
    {
        get => _percentComplete;
        set => SetField(ref _percentComplete, value);
    }

    private string _elapsedText = "00:00";
    public string ElapsedText
    {
        get => _elapsedText;
        set => SetField(ref _elapsedText, value);
    }

    private string _cpsText = "0";
    public string CpsText
    {
        get => _cpsText;
        set => SetField(ref _cpsText, value);
    }

    public ICommand StartCommand { get; }
    public ICommand PauseResumeCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand ClearHistoryCommand { get; }
    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }
    public ICommand SetAsNextCommand { get; }

    public MainViewModel(
        IKeyboardInputService typingService,
        ActiveWindowService activeWindowService,
        SettingsService settingsService,
        HistoryService historyService,
        IHotkeyRegistrar hotkeyService,
        TypingQueueService queueService,
        LogService log,
        TypingSessionCoordinator? coordinator = null,
        Func<int, CancellationToken, Task>? delayAsync = null,
        TargetGuard? targetGuard = null)
    {
        _typingService = typingService;
        _activeWindowService = activeWindowService;
        _settingsService = settingsService;
        _historyService = historyService;
        _hotkeyService = hotkeyService;
        _queueService = queueService;
        _log = log;
        _coordinator = coordinator ?? new TypingSessionCoordinator(hotkeyService, log);
        _delayAsync = delayAsync ?? Task.Delay;
        _targetGuard = targetGuard ?? new TargetGuard(Win32ForegroundWindowProvider.Instance, log);

        _coordinator.ActiveChanged += active =>
        {
            _queueService.IsLocked = active;
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(CanEditQueue));
            CommandManager.InvalidateRequerySuggested();
        };

        LogLines = log.Lines;
        Settings = _settingsService.Load();

        _queueService.PersistenceFailed += err =>
        {
            StatusMessage = "As alterações não estão sendo salvas";
            _log.Error("Falha ao persistir fila: " + err);
        };
        _settingsService.PersistenceFailed += err =>
        {
            StatusMessage = "As alterações não estão sendo salvas";
            _log.Error("Falha ao persistir configurações: " + err);
        };
        _historyService.PersistenceFailed += err =>
        {
            StatusMessage = "As alterações não estão sendo salvas";
            _log.Error("Falha ao persistir histórico: " + err);
        };

        if (_queueService.LastLoadResult is { Status: Services.Persistence.LoadStatus.RestoredFromBackup })
        {
            StatusMessage = "Aviso: fila restaurada do backup (.bak).";
            _log.Warn(StatusMessage);
        }
        else if (_queueService.LastLoadResult is { Status: Services.Persistence.LoadStatus.Corrupt } corruptQueue)
        {
            StatusMessage = $"A fila salva não pôde ser lida. Cópia preservada em: {corruptQueue.PreservedCorruptPath}";
            _log.Warn(StatusMessage);
        }

        if (_settingsService.LastLoadResult is { Status: Services.Persistence.LoadStatus.RestoredFromBackup })
        {
            _log.Warn("Configurações restauradas do backup (.bak).");
        }
        else if (_settingsService.LastLoadResult is { Status: Services.Persistence.LoadStatus.Corrupt } corruptSettings)
        {
            _log.Warn($"Configurações corrompidas preservadas em: {corruptSettings.PreservedCorruptPath}");
        }

        foreach (var session in _historyService.Load())
        {
            History.Add(session);
        }

        _queueService.Changed += OnQueueChanged;
        _queueService.SentBlockEdited += block =>
        {
            StatusMessage = $"Aviso: o Bloco {block.Order} já havia sido enviado e voltou para 'Pronto' após a edição.";
            _log.Info(StatusMessage);
        };

        StartCommand = new RelayCommand(async () => await StartAsync(), () => !IsBusy && HasNextBlock);
        PauseResumeCommand = new RelayCommand(TogglePause, () => CanPause);
        CancelCommand = new RelayCommand(Cancel, () => IsBusy);
        ClearHistoryCommand = new RelayCommand(() => { History.Clear(); _historyService.Save(History); });

        MoveUpCommand = new RelayCommand(p => { if (p is TextBlockItem b) _queueService.MoveUp(b.Id); }, p => CanEditQueue);
        MoveDownCommand = new RelayCommand(p => { if (p is TextBlockItem b) _queueService.MoveDown(b.Id); }, p => CanEditQueue);
        SetAsNextCommand = new RelayCommand(p => { if (p is TextBlockItem b) _queueService.SetAsNext(b.Id); }, p => CanEditQueue);

        RegisterPermanentHotkeys();
    }

    private void OnQueueChanged()
    {
        OnPropertyChanged(nameof(HasNextBlock));
        OnPropertyChanged(nameof(NextBlockLabel));
        OnPropertyChanged(nameof(DigitarButtonText));
        OnPropertyChanged(nameof(QueueSummary));
        OnPropertyChanged(nameof(QueueProgressLabel));
        CommandManager.InvalidateRequerySuggested();
    }

    // --- Ações de edição da fila expostas para o code-behind (que cuida das confirmações via MessageBox) ---
    public void AddBlock() => _queueService.AddBlock();
    public void RemoveBlock(Guid id) => _queueService.RemoveBlock(id);
    public void RestartQueue() => _queueService.RestartQueue();
    public void NewQueue() => _queueService.NewQueue();

    public void ReloadSettings()
    {
        Settings = _settingsService.Load();
        OnPropertyChanged(nameof(Settings));
        RegisterPermanentHotkeys();
    }

    private void RegisterPermanentHotkeys()
    {
        _hotkeyService.UnregisterHotkey(HotkeyIdStart);
        _hotkeyService.UnregisterHotkey(HotkeyIdPauseResume);

        bool okStart = _hotkeyService.RegisterHotkey(
            HotkeyIdStart,
            Settings.StartHotkey.ToModifierFlags(),
            Settings.StartHotkey.VirtualKey,
            async () => await StartAsync());
        if (!okStart)
            _log.Warn($"Não foi possível registrar o atalho de início ({Settings.StartHotkey}). Ele pode já estar em uso por outro aplicativo.");

        bool okPause = _hotkeyService.RegisterHotkey(
            HotkeyIdPauseResume,
            Settings.PauseResumeHotkey.ToModifierFlags(),
            Settings.PauseResumeHotkey.VirtualKey,
            TogglePause);
        if (!okPause)
            _log.Warn($"Não foi possível registrar o atalho de pausa ({Settings.PauseResumeHotkey}). Ele pode já estar em uso por outro aplicativo.");
    }

    /// <summary>
    /// Digita exclusivamente o próximo bloco elegível da fila (1 acionamento
    /// = 1 bloco). Chamado tanto pelo botão DIGITAR quanto pelo hotkey
    /// global. O <see cref="TypingSessionCoordinator"/> garante exclusividade
    /// por processo (entre a janela principal e a de diagnóstico) e é o único
    /// responsável por registrar/desregistrar o ESC global.
    /// </summary>
    public async Task StartAsync()
    {
        using var lease = _coordinator.TryBegin("Main");
        if (lease is null)
        {
            _log.Info("Acionamento ignorado: já existe uma digitação em andamento.");
            return;
        }

        _activeLease = lease;
        try
        {
            await StartCoreAsync(lease).ConfigureAwait(true);
        }
        finally
        {
            _activeLease = null;
        }
    }

    private async Task StartCoreAsync(TypingSessionLease lease)
    {
        var next = _queueService.PeekNext();
        if (next is null)
        {
            StatusMessage = _queueService.Blocks.Any(b => b.Status == TextBlockStatus.Sent)
                ? "Todos os blocos foram enviados."
                : "Não há blocos com conteúdo para digitar.";
            return;
        }

        int resumeFromIndex = 0;
        if (next.Status is TextBlockStatus.PartiallySent or TextBlockStatus.Interrupted or TextBlockStatus.Failed)
        {
            if (ConfirmPartialResume is not null)
            {
                bool? resumeChoice = ConfirmPartialResume(next);
                if (resumeChoice is null)
                {
                    StatusMessage = $"Envio do Bloco {next.Order} cancelado na confirmação.";
                    return;
                }
                if (resumeChoice == true && next.SentUpToIndex > 0 && next.SentUpToIndex < next.Text.Length)
                {
                    resumeFromIndex = next.SentUpToIndex;
                }
                else
                {
                    next.SentUpToIndex = 0;
                }
            }
            else
            {
                // Padrão (§1.2 nota ⁴): do início, nunca continuar automaticamente.
                next.SentUpToIndex = 0;
            }
        }

        Guid blockId = next.Id;
        int blockOrder = next.Order;
        Guid sessionId = lease.SessionId;
        var token = lease.Token;

        string textToType = resumeFromIndex > 0 && resumeFromIndex < next.Text.Length
            ? next.Text.Substring(resumeFromIndex)
            : next.Text;

        State = TypingState.AguardandoContagem;
        _sessionStartedAt = DateTime.Now;
        TotalCharacters = textToType.Length;
        TypedCharacters = 0;

        _log.Info("Sessão iniciada");
        _log.Info($"Bloco: {blockOrder} (Id={blockId})");
        _log.Info($"Método: {Settings.Method}");
        _log.Info($"Caracteres: {textToType.Length}");

        bool typingStarted = false;
        try
        {
            int seconds = Math.Max(0, Settings.CountdownSeconds);
            for (int i = seconds; i > 0; i--)
            {
                await lease.Pause.WaitWhilePausedAsync().WaitAsync(token).ConfigureAwait(true);
                CountdownText = $"Iniciando em {i}...";
                StatusMessage = $"Bloco {blockOrder}: clique no destino (janela do navegador / VM) agora.";
                await _delayAsync(1000, token).ConfigureAwait(true);
                await lease.Pause.WaitWhilePausedAsync().WaitAsync(token).ConfigureAwait(true);
            }
            CountdownText = string.Empty;

            if (Settings.EnableTargetGuard || Settings.ValidateTargetWindow)
            {
                var anchorResult = _targetGuard.TryCaptureAnchor(Settings.ValidateTargetWindow, Settings.ExpectedBrowser);
                if (!anchorResult.IsValid)
                {
                    State = TypingState.Erro;
                    StatusMessage = anchorResult.ErrorMessage ?? "A janela ativa não é um destino válido.";
                    _log.Warn(StatusMessage);
                    return;
                }
            }

            // A edição da fila fica bloqueada por toda a sessão (IsBusy),
            // então o bloco não pode ter sido alterado/removido neste ponto
            // — a checagem abaixo é apenas uma rede de segurança defensiva.
            var block = _queueService.Blocks.FirstOrDefault(b => b.Id == blockId);
            if (block is null || block.IsEmpty)
            {
                State = TypingState.Erro;
                StatusMessage = "O bloco selecionado ficou vazio ou foi removido antes do início.";
                return;
            }

            var baseOptions = new TypingOptions
            {
                CharDelayMs = Settings.CharDelayMs,
                EnterDelayMs = Settings.EnterDelayMs,
                Method = Settings.Method,
                TargetLayout = Settings.TargetLayout,
                PreserveOriginalIndentation = Settings.PreserveOriginalIndentation,
                IndentationStrategy = IndentationResetStrategy.Auto,
                Substitutions = Settings.CharacterSubstitutions
            };

            // §2.1 (E): Pré-voo — se houver unidades Unsupported, a sessão não começa em silêncio.
            var preflight = _typingService.Plan(textToType, baseOptions);
            bool fallbackUnicode = false;
            if (preflight.UnsupportedCount > 0)
            {
                var decision = ConfirmPreflightUnsupported?.Invoke(preflight) ?? PreflightDecision.Cancel;
                if (decision == PreflightDecision.Cancel)
                {
                    State = TypingState.Cancelado;
                    StatusMessage = $"Envio cancelado no pré-voo ({preflight.UnsupportedCount} unidade(s) não suportada(s)).";
                    _log.Info(StatusMessage);
                    return;
                }
                fallbackUnicode = decision == PreflightDecision.FallbackToUnicode;
            }

            if (preflight.TotalUnits > 0)
            {
                TotalCharacters = preflight.TotalUnits;
            }
            _skippedUnits = 0;
            _failedUnits = 0;

            _queueService.BeginTyping(blockId, sessionId);
            _activeBlockId = blockId;
            typingStarted = true;
            State = TypingState.Digitando;
            StatusMessage = $"Digitando Bloco {blockOrder}...";

            var options = new TypingOptions
            {
                CharDelayMs = baseOptions.CharDelayMs,
                EnterDelayMs = baseOptions.EnterDelayMs,
                Method = baseOptions.Method,
                TargetLayout = baseOptions.TargetLayout,
                FallbackUnsupportedToUnicode = fallbackUnicode,
                PreserveOriginalIndentation = baseOptions.PreserveOriginalIndentation,
                IndentationStrategy = baseOptions.IndentationStrategy,
                Substitutions = baseOptions.Substitutions,
                BeforeUnitAsync = Settings.EnableTargetGuard
                    ? (unitIdx, ct) => CheckTargetGuardBeforeUnitAsync(lease, unitIdx, ct)
                    : null
            };

            var progress = new Helpers.DirectProgress<TypingProgress>(OnProgress);

            _log.Info("Digitação iniciada");
            await _typingService.TypeTextAsync(textToType, options, progress, lease.Pause, token)
                .ConfigureAwait(true);

            double duration = (DateTime.Now - _sessionStartedAt).TotalSeconds;
            if (_skippedUnits > 0 || _failedUnits > 0)
            {
                // §2.3 (E): conclusão com Skipped > 0 ou Failed > 0 termina como PartiallySent/aviso, nunca Concluído verde.
                State = TypingState.Erro;
                string warn = $"Bloco {blockOrder} enviado parcialmente ({TypedCharacters} enviadas, {_skippedUnits} puladas, {_failedUnits} falhas).";
                StatusMessage = warn;
                _log.Warn(warn);
                AddHistoryEntry(sessionId, blockId, blockOrder, TotalCharacters, completed: false, duration);
                _queueService.CancelBlock(blockId, Math.Max(1, resumeFromIndex + TypedCharacters), sessionId);
            }
            else
            {
                State = TypingState.Concluido;
                _log.Info($"Digitação concluída. Unidades enviadas: {TypedCharacters}. Duração: {duration:0.0}s");

                AddHistoryEntry(sessionId, blockId, blockOrder, TotalCharacters, completed: true, duration);
                _queueService.CompleteBlock(blockId, Settings.ClearTextAfterCompletion, resumeFromIndex + TypedCharacters, sessionId);

                var upcoming = _queueService.Current;
                StatusMessage = upcoming is not null
                    ? $"Bloco {blockOrder} enviado. Próximo: Bloco {upcoming.Order}."
                    : "Todos os blocos foram enviados.";
            }
        }
        catch (OperationCanceledException)
        {
            double duration = (DateTime.Now - _sessionStartedAt).TotalSeconds;
            State = TypingState.Cancelado;
            StatusMessage = $"Digitação do Bloco {blockOrder} cancelada pelo usuário.";
            _log.Info("Digitação cancelada pelo usuário.");
            AddHistoryEntry(sessionId, blockId, blockOrder, TotalCharacters, completed: false, duration);
            if (typingStarted)
            {
                _queueService.CancelBlock(blockId, resumeFromIndex + TypedCharacters, sessionId);
            }
        }
        catch (Exception ex)
        {
            State = TypingState.Erro;
            StatusMessage = $"Erro no Bloco {blockOrder}: {ex.Message}";
            _log.Error(ex.Message);
            if (typingStarted)
            {
                _queueService.FailBlock(blockId, ex.Message, resumeFromIndex + TypedCharacters, sessionId);
            }
        }
        finally
        {
            _targetGuard.StopMonitoring();
            _activeBlockId = null;
        }
    }

    private Task CheckTargetGuardBeforeUnitAsync(TypingSessionLease lease, int unitIndex, CancellationToken ct)
    {
        var check = _targetGuard.CheckBeforeUnit(unitIndex, Settings.Features.TargetGuardStrict);
        if (check.Status == TargetCheckStatus.TargetLost)
        {
            _stateBeforePause = TypingState.Digitando;
            lease.PauseSession();
            if (_activeBlockId.HasValue)
            {
                _queueService.PauseOrResumeBlock(_activeBlockId.Value);
            }
            State = TypingState.PausadoDestinoPerdido;
            StatusMessage = "Pausado: destino perdido. Volte à janela de destino e clique em CONTINUAR.";
        }
        return Task.CompletedTask;
    }

    private void AddHistoryEntry(Guid sessionId, Guid blockId, int blockOrder, int characterCount, bool completed, double durationSeconds)
    {
        var entry = new TypingSession
        {
            SessionId = sessionId,
            BlockId = blockId,
            StartedAt = _sessionStartedAt,
            BlockOrder = blockOrder,
            CharacterCount = characterCount,
            Completed = completed,
            DurationSeconds = durationSeconds
        };
        History.Insert(0, entry);
        while (History.Count > 50) History.RemoveAt(History.Count - 1);
        _historyService.Save(History.Reverse());
    }

    private void OnProgress(TypingProgress p)
    {
        TypedCharacters = p.Sent;
        TotalCharacters = p.TotalUnits;
        _skippedUnits = p.Skipped;
        _failedUnits = p.Failed;
        PercentComplete = p.PercentComplete;
        ElapsedText = p.Elapsed.ToString(@"mm\:ss");
        CpsText = p.CharactersPerSecond.ToString("0.0");
    }

    public void TogglePause()
    {
        var lease = _activeLease;
        if (lease is null) return;

        if (State is TypingState.Digitando or TypingState.AguardandoContagem)
        {
            _stateBeforePause = State;
            lease.PauseSession();
            if (_stateBeforePause == TypingState.Digitando && _activeBlockId.HasValue)
            {
                _queueService.PauseOrResumeBlock(_activeBlockId.Value);
            }
            State = TypingState.Pausado;
            StatusMessage = _stateBeforePause == TypingState.AguardandoContagem
                ? "Contagem pausada. Clique em CONTINUAR para retomar."
                : "Pausado. Clique em CONTINUAR para retomar.";
            _log.Info(_stateBeforePause == TypingState.AguardandoContagem
                ? "Contagem regressiva pausada pelo usuário."
                : "Digitação pausada pelo usuário.");
        }
        else if (State == TypingState.Pausado)
        {
            if (_stateBeforePause == TypingState.Digitando && _activeBlockId.HasValue)
            {
                _queueService.PauseOrResumeBlock(_activeBlockId.Value);
            }
            lease.ResumeSession();
            State = _stateBeforePause;
            StatusMessage = _stateBeforePause == TypingState.AguardandoContagem
                ? "Retomando contagem regressiva..."
                : "Digitando...";
            _log.Info(_stateBeforePause == TypingState.AguardandoContagem
                ? "Contagem regressiva retomada pelo usuário."
                : "Digitação retomada pelo usuário.");
        }
        else if (State == TypingState.PausadoDestinoPerdido)
        {
            _ = ResumeAfterTargetLostAsync(lease);
        }
    }

    public async Task ResumeAfterTargetLostAsync(TypingSessionLease lease)
    {
        if (State != TypingState.PausadoDestinoPerdido) return;
        var token = lease.Token;

        try
        {
            int seconds = Math.Max(0, ResumeCountdownSeconds);
            for (int i = seconds; i > 0; i--)
            {
                CountdownText = $"Retomando em {i}...";
                StatusMessage = "Clique na janela de destino agora para retomar...";
                await _delayAsync(1000, token).ConfigureAwait(true);
            }
            CountdownText = string.Empty;

            var previousAnchor = _targetGuard.CurrentAnchor;
            var candidate = _targetGuard.TryCaptureAnchor(Settings.ValidateTargetWindow, Settings.ExpectedBrowser);
            if (!candidate.IsValid || candidate.Anchor is null)
            {
                StatusMessage = candidate.ErrorMessage ?? "Destino inválido ao retomar. Permanece pausado.";
                _log.Warn(StatusMessage);
                return;
            }

            if (previousAnchor is not null && candidate.Anchor.Hwnd != previousAnchor.Hwnd)
            {
                bool accepted = ConfirmTargetChanged?.Invoke(previousAnchor, candidate.Anchor) ?? false;
                if (!accepted)
                {
                    // Restaura a âncora original e permanece pausado.
                    _targetGuard.AcceptNewAnchor(previousAnchor);
                    StatusMessage = "Retomada cancelada: o destino mudou e não foi confirmado.";
                    _log.Info(StatusMessage);
                    return;
                }
            }

            if (_activeBlockId.HasValue)
            {
                _queueService.PauseOrResumeBlock(_activeBlockId.Value);
            }
            State = TypingState.Digitando;
            StatusMessage = "Digitando...";
            _log.Info("Digitação retomada após revalidação do destino.");
            lease.ResumeSession();
        }
        catch (OperationCanceledException)
        {
            CountdownText = string.Empty;
        }
    }

    public void Cancel()
    {
        _activeLease?.Cancel();
    }

    public void Shutdown()
    {
        _coordinator.CancelAndWait(TimeSpan.FromSeconds(2));
        _queueService.Save();
        _hotkeyService.UnregisterHotkey(HotkeyIdStart);
        _hotkeyService.UnregisterHotkey(HotkeyIdPauseResume);
    }
}
