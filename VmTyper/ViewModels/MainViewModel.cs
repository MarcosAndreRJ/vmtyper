using System.Collections.ObjectModel;
using System.Windows.Input;
using VmTyper.Documents;
using VmTyper.Helpers;
using VmTyper.Models;
using VmTyper.Resources;
using VmTyper.Services;
using InputMethod = VmTyper.Models.InputMethod;

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

/// <summary>O que a ação primária da barra de execução vai digitar neste momento.</summary>
public enum ExecutionTarget
{
    Document,
    Block,
    Selection,
    Queue
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
    private readonly DocumentManager _docManager;
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
    public int SkippedUnits
    {
        get => _skippedUnits;
        private set
        {
            if (SetField(ref _skippedUnits, value))
            {
                OnPropertyChanged(nameof(UnitsProgressSummary));
            }
        }
    }

    private int _failedUnits;
    public int FailedUnits
    {
        get => _failedUnits;
        private set
        {
            if (SetField(ref _failedUnits, value))
            {
                OnPropertyChanged(nameof(UnitsProgressSummary));
            }
        }
    }

    private string _statusSeverity = "Normal";
    public string StatusSeverity
    {
        get => _statusSeverity;
        private set => SetField(ref _statusSeverity, value);
    }

    private DeliverySemanticState _lastDeliverySemanticState = DeliverySemanticState.Unknown;
    public DeliverySemanticState LastDeliverySemanticState
    {
        get => _lastDeliverySemanticState;
        private set
        {
            if (SetField(ref _lastDeliverySemanticState, value))
            {
                OnPropertyChanged(nameof(LastDeliveryBadge));
            }
        }
    }

    public DeliveryVisualBadge LastDeliveryBadge => DeliverySemantics.GetBadge(LastDeliverySemanticState);

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
                OnPropertyChanged(nameof(CanStart));
                OnPropertyChanged(nameof(CanPause));
                OnPropertyChanged(nameof(PauseResumeButtonText));
                OnPropertyChanged(nameof(PauseResumeLabel));
                OnPropertyChanged(nameof(PauseResumeButtonWithShortcut));
                OnPropertyChanged(nameof(TargetGuardStateText));
                OnPropertyChanged(nameof(TargetIndicatorText));
                NotifyPrimaryActionChanged();
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
    public string PauseResumeButtonWithShortcut => $"{PauseResumeButtonText} ({Settings.PauseResumeHotkey})";
    public string CancelButtonWithShortcut => "CANCELAR (ESC)";

    // --- Barra de sessão (topo, §1.1 e §2) ---
    public string TargetGuardStateText
    {
        get
        {
            if (!Settings.EnableTargetGuard) return Strings.TargetGuardOff;
            if (State == TypingState.PausadoDestinoPerdido) return Strings.TargetGuardLost;
            return Strings.TargetGuardMonitoring;
        }
    }

    public string TargetIndicatorText
    {
        get
        {
            var anchor = _targetGuard.CurrentAnchor;
            if (anchor is not null)
            {
                string title = string.IsNullOrWhiteSpace(anchor.Title)
                    ? "(sem título)"
                    : (anchor.Title.Length > 28 ? anchor.Title[..28] + "…" : anchor.Title);
                return $"🎯 {anchor.ProcessName} — {title} [{TargetGuardStateText}]";
            }

            string expected = Settings.ValidateTargetWindow && Settings.ExpectedBrowser != ExpectedBrowser.Nenhum
                ? $"Esperado: {Settings.ExpectedBrowser}"
                : "Janela ativa ao iniciar";
            return $"🎯 {expected} [{TargetGuardStateText}]";
        }
    }

    public string SelectedMethodTag
    {
        get => Settings.Method.ToString();
        set
        {
            if (!CanEditQueue) return;
            var parsed = value switch
            {
                "TecladoFisico" => InputMethod.TecladoFisico,
                "Unicode" => InputMethod.Unicode,
                _ => InputMethod.Automatico
            };
            if (Settings.Method != parsed)
            {
                Settings.Method = parsed;
                _settingsService.Save(Settings);
                OnPropertyChanged(nameof(SelectedMethodTag));
                OnPropertyChanged(nameof(EngineSummaryText));
            }
        }
    }

    public string SelectedProfileTag
    {
        get => Settings.Features.AdaptiveTiming ? Settings.TypingProfile : "Legacy";
        set
        {
            if (!CanEditQueue || string.IsNullOrWhiteSpace(value)) return;
            if (!string.Equals( value, "Legacy", StringComparison.OrdinalIgnoreCase))
            {
                Settings.Features.AdaptiveTiming = true;
            }
            Settings.TypingProfile = value;
            _settingsService.Save(Settings);
            OnPropertyChanged(nameof(SelectedProfileTag));
            OnPropertyChanged(nameof(EngineSummaryText));
        }
    }

    public string SelectedTargetLayoutTag
    {
        get => string.IsNullOrWhiteSpace(Settings.TargetLayout) ? "HostForeground" : Settings.TargetLayout;
        set
        {
            if (!CanEditQueue || string.IsNullOrWhiteSpace(value)) return;
            if (!string.Equals(Settings.TargetLayout, value, StringComparison.Ordinal))
            {
                Settings.TargetLayout = value;
                _settingsService.Save(Settings);
                OnPropertyChanged(nameof(SelectedTargetLayoutTag));
                OnPropertyChanged(nameof(EngineSummaryText));
            }
        }
    }

    public bool IsEmbeddedEnvironmentVisible => Settings.Features.EmbeddedBrowser;
    public bool IsCdpSubMethodVisible => Settings.Features.EmbeddedBrowser && Settings.Features.CdpInput;

    /// <summary>Resumo compacto do motor de digitação para a barra superior (ex.: "Automático · Natural").</summary>
    public string EngineSummaryText
    {
        get
        {
            string method = SelectedMethodTag switch
            {
                "TecladoFisico" => "Físico",
                "Unicode" => "Unicode",
                _ => "Automático"
            };
            string profile = SelectedProfileTag switch
            {
                "Legacy" => "Legado",
                "Fast" => "Rápido",
                "Compatibility" => "Compatibilidade",
                "Natural" => "Natural",
                "Custom" => "Personalizado",
                var other => other
            };
            string text = $"{method} · {profile}";
            if (IsEmbeddedEnvironmentVisible && SelectedEnvironmentTag == "Embedded") text += " · Integrado";
            return text;
        }
    }

    public string StartShortcutText => Settings.StartHotkey.ToString();
    public string PauseShortcutText => Settings.PauseResumeHotkey.ToString();
    public string PauseResumeLabel => State is TypingState.Pausado or TypingState.PausadoDestinoPerdido ? "Continuar" : "Pausar";

    public string SelectedEnvironmentTag
    {
        get => Settings.Features.EmbeddedBrowser && string.Equals(Settings.TargetEnvironment, "Embedded", StringComparison.OrdinalIgnoreCase)
            ? "Embedded"
            : "External";
        set
        {
            if (!CanEditQueue || string.IsNullOrWhiteSpace(value)) return;
            string normalized = Settings.Features.EmbeddedBrowser && string.Equals(value, "Embedded", StringComparison.OrdinalIgnoreCase)
                ? "Embedded"
                : "External";
            if (!string.Equals(Settings.TargetEnvironment, normalized, StringComparison.Ordinal))
            {
                Settings.TargetEnvironment = normalized;
                _settingsService.Save(Settings);
                OnPropertyChanged(nameof(SelectedEnvironmentTag));
                OnPropertyChanged(nameof(EngineSummaryText));
            }
        }
    }

    public DocumentManager DocManager => _docManager;

    private bool _isSmartEditorMode = false;
    public bool IsSmartEditorMode
    {
        get => _isSmartEditorMode;
        set
        {
            if (SetField(ref _isSmartEditorMode, value))
            {
                OnPropertyChanged(nameof(IsSimpleQueueMode));
                OnPropertyChanged(nameof(HasNextBlock));
                OnPropertyChanged(nameof(CanStart));
                OnPropertyChanged(nameof(NextBlockLabel));
                OnPropertyChanged(nameof(DigitarButtonText));
                OnPropertyChanged(nameof(DigitarButtonWithShortcut));
                NotifyPrimaryActionChanged();
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool IsSimpleQueueMode
    {
        get => !_isSmartEditorMode;
        set => IsSmartEditorMode = !value;
    }

    // --- Fila ---
    public bool HasNextBlock => IsSmartEditorMode
        ? (_docManager.SelectedBlock != null && _docManager.CurrentDocument != null && !string.IsNullOrEmpty(_docManager.SelectedBlock.GetText(_docManager.CurrentDocument.Text)))
        : (_queueService.Current is not null);

    public bool CanStart => !IsBusy && HasPrimaryContent;

    // --- Ação primária contextual (barra de execução) ---------------------------------------------
    private string _editorSelectionText = string.Empty;

    /// <summary>Texto atualmente selecionado no editor (alimentado pelo code-behind/binding do editor).</summary>
    public string EditorSelectionText
    {
        get => _editorSelectionText;
        set
        {
            value ??= string.Empty;
            if (SetField(ref _editorSelectionText, value))
            {
                NotifyPrimaryActionChanged();
            }
        }
    }

    private bool HasActiveBlockContent =>
        _docManager.SelectedBlock != null
        && _docManager.CurrentDocument != null
        && !string.IsNullOrEmpty(_docManager.SelectedBlock.GetText(_docManager.CurrentDocument.Text));

    private bool HasDocumentContent => !string.IsNullOrEmpty(_docManager.CurrentDocument?.Text);

    public ExecutionTarget CurrentExecutionTarget
    {
        get
        {
            if (!IsSmartEditorMode) return ExecutionTarget.Queue;

            var doc = _docManager.CurrentDocument;
            if (!string.IsNullOrEmpty(EditorSelectionText))
            {
                // Seleção idêntica ao bloco ativo (ex.: após clicar na estrutura) é tratada como bloco.
                var sb = _docManager.SelectedBlock;
                bool sameAsBlock = sb != null && doc != null
                    && string.Equals(sb.GetText(doc.Text), EditorSelectionText, StringComparison.Ordinal);
                if (!sameAsBlock) return ExecutionTarget.Selection;
            }

            if (_docManager.SelectedBlock != null && doc != null && doc.Blocks.Count > 1)
                return ExecutionTarget.Block;

            return ExecutionTarget.Document;
        }
    }

    public bool HasPrimaryContent => CurrentExecutionTarget switch
    {
        ExecutionTarget.Queue => _queueService.Current is not null,
        ExecutionTarget.Selection => true,
        ExecutionTarget.Block => HasActiveBlockContent,
        _ => HasDocumentContent
    };

    public bool CanStartPrimary => !IsBusy && HasPrimaryContent;

    public bool CanStartDocument => !IsBusy && HasDocumentContent;
    public bool CanStartBlock => !IsBusy && HasActiveBlockContent;
    public bool CanStartSelection => !IsBusy && !string.IsNullOrEmpty(EditorSelectionText);

    private static string Shorten(string s, int max) => s.Length > max ? s[..max] + "…" : s;

    public string PrimaryActionTitle
    {
        get
        {
            switch (CurrentExecutionTarget)
            {
                case ExecutionTarget.Queue:
                    var current = _queueService.Current;
                    if (current is not null) return $"Digitar bloco {current.Order}";
                    return _queueService.Blocks.Any(b => b.Status == TextBlockStatus.Sent) ? "Fila enviada" : "Digitar";
                case ExecutionTarget.Selection:
                    return $"Digitar {EditorSelectionText.Length:N0} caracteres selecionados";
                case ExecutionTarget.Block:
                    return _docManager.SelectedBlock is { } sb
                        ? $"Digitar bloco: {Shorten(sb.Title, 24)}"
                        : "Digitar bloco";
                default:
                    return "Digitar documento";
            }
        }
    }

    public string PrimaryActionSubtitle
    {
        get
        {
            var doc = _docManager.CurrentDocument;
            switch (CurrentExecutionTarget)
            {
                case ExecutionTarget.Queue:
                    var current = _queueService.Current;
                    return current is not null
                        ? $"Fila · bloco {current.Order} de {_queueService.TotalBlocks}"
                        : $"Fila · {_queueService.CompletedCount} de {_queueService.TotalBlocks} enviados";
                case ExecutionTarget.Selection:
                    return $"Seleção do editor · {EditorSelectionText.Length:N0} caracteres";
                case ExecutionTarget.Block:
                    if (_docManager.SelectedBlock is { } sb && doc != null)
                        return $"{sb.LineRangeDisplay} · {sb.GetText(doc.Text).Length:N0} caracteres";
                    return "Nenhum bloco selecionado";
                default:
                    return doc is null
                        ? "Nenhum documento aberto"
                        : $"{doc.Name} · {doc.CharacterCount:N0} caracteres";
            }
        }
    }

    /// <summary>Dica da ação primária: o que será enviado, ou o motivo de estar desabilitada.</summary>
    public string PrimaryActionHint
    {
        get
        {
            if (IsBusy) return "Já existe uma digitação em andamento. Use Pausar ou Cancelar.";
            if (HasPrimaryContent)
                return $"{PrimaryActionTitle} ({PrimaryActionSubtitle}). Atalho: {Settings.StartHotkey}";

            return CurrentExecutionTarget switch
            {
                ExecutionTarget.Queue => _queueService.Blocks.Any(b => b.Status == TextBlockStatus.Sent)
                    ? "Todos os blocos já foram enviados. Reinicie a fila para digitar novamente."
                    : "A fila está vazia. Adicione conteúdo a um bloco para habilitar.",
                ExecutionTarget.Block => "O bloco selecionado está vazio. Selecione outro bloco na estrutura.",
                _ => _docManager.HasDocument
                    ? "O documento está vazio. Digite, cole ou abra um arquivo para habilitar."
                    : "Nenhum documento aberto. Crie ou abra um documento."
            };
        }
    }

    public string DocumentActionLabel =>
        _docManager.CurrentDocument is { } d ? $"Digitar documento ({d.CharacterCount:N0} caracteres)" : "Digitar documento";

    public string BlockActionLabel =>
        _docManager.SelectedBlock is { } sb ? $"Digitar bloco ativo: {Shorten(sb.Title, 28)}" : "Digitar bloco ativo";

    public string SelectionActionLabel =>
        string.IsNullOrEmpty(EditorSelectionText)
            ? "Digitar seleção (nada selecionado)"
            : $"Digitar seleção ({EditorSelectionText.Length:N0} caracteres)";

    private void NotifyPrimaryActionChanged()
    {
        OnPropertyChanged(nameof(CurrentExecutionTarget));
        OnPropertyChanged(nameof(HasPrimaryContent));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanStartPrimary));
        OnPropertyChanged(nameof(CanStartDocument));
        OnPropertyChanged(nameof(CanStartBlock));
        OnPropertyChanged(nameof(CanStartSelection));
        OnPropertyChanged(nameof(PrimaryActionTitle));
        OnPropertyChanged(nameof(PrimaryActionSubtitle));
        OnPropertyChanged(nameof(PrimaryActionHint));
        OnPropertyChanged(nameof(DocumentActionLabel));
        OnPropertyChanged(nameof(BlockActionLabel));
        OnPropertyChanged(nameof(SelectionActionLabel));

        // StartCommand/StartDocumentCommand/... só existem após o construtor.
        (StartCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (StartDocumentCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (StartBlockCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (StartSelectionCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    public string NextBlockLabel
    {
        get
        {
            if (IsSmartEditorMode)
            {
                if (_docManager.SelectedBlock is { } sb && _docManager.CurrentDocument is { } doc)
                {
                    return $"Bloco Ativo: {sb.Title} ({sb.LineRangeDisplay})";
                }
                return _docManager.HasDocument ? "Nenhum bloco selecionado." : "Nenhum documento aberto.";
            }

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
            if (IsSmartEditorMode)
            {
                if (_docManager.SelectedBlock is { } sb)
                {
                    string shortTitle = sb.Title.Length > 20 ? sb.Title[..20] + "…" : sb.Title;
                    return $"DIGITAR: {shortTitle.ToUpperInvariant()}";
                }
                return "DIGITAR BLOCO";
            }

            var current = _queueService.Current;
            if (current is not null) return $"DIGITAR BLOCO {current.Order}";
            return _queueService.Blocks.Any(b => b.Status == TextBlockStatus.Sent) ? "FILA ENVIADA" : "DIGITAR";
        }
    }

    public string DigitarButtonWithShortcut => $"{DigitarButtonText} ({Settings.StartHotkey})";

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
        set
        {
            if (SetField(ref _typedCharacters, value))
            {
                OnPropertyChanged(nameof(SentUnits));
                OnPropertyChanged(nameof(UnitsProgressSummary));
            }
        }
    }

    public int SentUnits => _typedCharacters;

    private int _totalCharacters;
    public int TotalCharacters
    {
        get => _totalCharacters;
        set
        {
            if (SetField(ref _totalCharacters, value))
            {
                OnPropertyChanged(nameof(UnitsProgressSummary));
            }
        }
    }

    public string UnitsProgressSummary =>
        $"Unidades: {SentUnits}/{TotalCharacters} · Enviadas: {SentUnits} · Puladas: {SkippedUnits} · Falhas: {FailedUnits}";

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
    public ICommand StartDocumentCommand { get; }
    public ICommand StartSelectionCommand { get; }
    public ICommand StartBlockCommand { get; }
    public ICommand PauseResumeCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand ClearHistoryCommand { get; }
    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }
    public ICommand SetAsNextCommand { get; }
    public ICommand SplitAtCaretCommand { get; }
    public ICommand ReSplitCommand { get; }
    public ICommand MergeNextBlockCommand { get; }

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
        TargetGuard? targetGuard = null,
        DocumentManager? docManager = null)
    {
        _typingService = typingService;
        _activeWindowService = activeWindowService;
        _settingsService = settingsService;
        _historyService = historyService;
        _hotkeyService = hotkeyService;
        _queueService = queueService;
        _docManager = docManager ?? new DocumentManager();
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

        _docManager.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName is nameof(DocumentManager.SelectedBlock) or nameof(DocumentManager.CurrentDocument))
            {
                OnQueueChanged();
            }
            else if (e.PropertyName is nameof(DocumentManager.HasPendingSplit))
            {
                // Edição de texto em andamento: atualiza rótulos/contagens sem esperar a divisão em blocos.
                NotifyPrimaryActionChanged();
            }
        };
        _docManager.BlocksRefreshed += OnQueueChanged;

        LogLines = log.Lines;
        Settings = _settingsService.Load();
        ResumeCountdownSeconds = Settings.ResumeCountdownSeconds;

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

        StartCommand = new RelayCommand(async () => await StartAsync(), () => !IsBusy && HasPrimaryContent);
        StartDocumentCommand = new RelayCommand(async () => await StartDocumentAsync(), () => !IsBusy && _docManager.HasDocument && !string.IsNullOrEmpty(_docManager.CurrentDocument?.Text));
        StartBlockCommand = new RelayCommand(async () => await StartSmartBlockAsync(), () => !IsBusy && HasActiveBlockContent);
        // O parâmetro (texto selecionado) é opcional: sem ele usa EditorSelectionText, mantido pelo editor.
        StartSelectionCommand = new RelayCommand(
            async p => await StartSelectionAsync(p as string ?? EditorSelectionText),
            p => !IsBusy && !string.IsNullOrEmpty(p as string ?? EditorSelectionText));
        PauseResumeCommand = new RelayCommand(TogglePause, () => CanPause);
        CancelCommand = new RelayCommand(Cancel, () => IsBusy);
        ClearHistoryCommand = new RelayCommand(() => { History.Clear(); _historyService.Save(History); });

        MoveUpCommand = new RelayCommand(p => { if (p is TextBlockItem b) _queueService.MoveUp(b.Id); }, p => CanEditQueue);
        MoveDownCommand = new RelayCommand(p => { if (p is TextBlockItem b) _queueService.MoveDown(b.Id); }, p => CanEditQueue);
        SetAsNextCommand = new RelayCommand(p => { if (p is TextBlockItem b) _queueService.SetAsNext(b.Id); }, p => CanEditQueue);

        SplitAtCaretCommand = new RelayCommand(p =>
        {
            if (p is int caretOffset)
            {
                _docManager.SplitCurrentBlockAtCaret(caretOffset);
            }
        }, _ => CanEditQueue && _docManager.SelectedBlock != null);

        ReSplitCommand = new RelayCommand(() => _docManager.ReSplitCurrentDocument(), () => CanEditQueue && _docManager.HasDocument);
        MergeNextBlockCommand = new RelayCommand(p =>
        {
            if (p is CodeBlock b)
            {
                _docManager.MergeBlockWithNext(b);
            }
            else if (_docManager.SelectedBlock is { } sb)
            {
                _docManager.MergeBlockWithNext(sb);
            }
        }, _ => CanEditQueue && _docManager.SelectedBlock != null);

        RegisterPermanentHotkeys();
    }

    private void OnQueueChanged()
    {
        OnPropertyChanged(nameof(HasNextBlock));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(NextBlockLabel));
        OnPropertyChanged(nameof(DigitarButtonText));
        OnPropertyChanged(nameof(DigitarButtonWithShortcut));
        OnPropertyChanged(nameof(QueueSummary));
        OnPropertyChanged(nameof(QueueProgressLabel));
        NotifyPrimaryActionChanged();

        (StartCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (SetAsNextCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (MoveUpCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (MoveDownCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    // --- Ações de edição da fila expostas para o code-behind (que cuida das confirmações via MessageBox) ---
    public void AddBlock() => _queueService.AddBlock();
    public void RemoveBlock(Guid id) => _queueService.RemoveBlock(id);
    public void RestartQueue() => _queueService.RestartQueue();
    public void NewQueue() => _queueService.NewQueue();

    public void ReloadSettings()
    {
        Settings = _settingsService.Load();
        ResumeCountdownSeconds = Settings.ResumeCountdownSeconds;
        OnPropertyChanged(nameof(Settings));
        OnPropertyChanged(nameof(EngineSummaryText));
        OnPropertyChanged(nameof(StartShortcutText));
        OnPropertyChanged(nameof(PauseShortcutText));
        OnPropertyChanged(nameof(SelectedMethodTag));
        OnPropertyChanged(nameof(SelectedProfileTag));
        OnPropertyChanged(nameof(SelectedTargetLayoutTag));
        OnPropertyChanged(nameof(SelectedEnvironmentTag));
        OnPropertyChanged(nameof(IsEmbeddedEnvironmentVisible));
        OnPropertyChanged(nameof(IsCdpSubMethodVisible));
        OnPropertyChanged(nameof(TargetGuardStateText));
        OnPropertyChanged(nameof(TargetIndicatorText));
        OnPropertyChanged(nameof(DigitarButtonWithShortcut));
        OnPropertyChanged(nameof(PauseResumeButtonWithShortcut));
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
    /// Inicia a digitação de acordo com o modo ativo:
    /// Se no modo Smart Editor, digita o bloco inteligente selecionado.
    /// Se no modo Simple Queue, digita o próximo bloco da fila.
    /// </summary>
    public async Task StartAsync()
    {
        if (IsSmartEditorMode)
        {
            // Garante que os blocos reflitam o texto atual antes de decidir o que digitar.
            _docManager.FlushPendingSplit();

            // O mesmo critério do rótulo do botão primário: o atalho global nunca digita algo diferente do exibido.
            switch (CurrentExecutionTarget)
            {
                case ExecutionTarget.Selection:
                    await StartSelectionAsync(EditorSelectionText).ConfigureAwait(true);
                    break;
                case ExecutionTarget.Block:
                    await StartSmartBlockAsync().ConfigureAwait(true);
                    break;
                default:
                    await StartDocumentAsync().ConfigureAwait(true);
                    break;
            }
        }
        else
        {
            await StartQueueBlockAsync().ConfigureAwait(true);
        }
    }

    public async Task StartDocumentAsync()
    {
        _docManager.FlushPendingSplit();
        if (_docManager.CurrentDocument is not { } doc || string.IsNullOrEmpty(doc.Text))
        {
            StatusMessage = "Nenhum documento aberto com conteúdo.";
            return;
        }

        using var lease = _coordinator.TryBegin("Main");
        if (lease is null)
        {
            _log.Info("Acionamento ignorado: já existe uma digitação em andamento.");
            return;
        }

        _activeLease = lease;
        try
        {
            bool ok = await TypeTextPayloadAsync(doc.Text, $"Documento completo '{doc.Name}'", lease).ConfigureAwait(true);
            if (ok)
            {
                // O documento inteiro foi enviado: todos os blocos derivados dele também.
                foreach (var b in doc.Blocks) b.Status = TextBlockStatus.Sent;
            }
        }
        finally
        {
            _activeLease = null;
        }
    }

    public async Task StartSelectionAsync(string? selectedText)
    {
        if (string.IsNullOrEmpty(selectedText))
        {
            StatusMessage = "Nenhum texto selecionado no editor.";
            return;
        }

        using var lease = _coordinator.TryBegin("Main");
        if (lease is null)
        {
            _log.Info("Acionamento ignorado: já existe uma digitação em andamento.");
            return;
        }

        _activeLease = lease;
        try
        {
            await TypeTextPayloadAsync(selectedText, "Seleção do editor", lease).ConfigureAwait(true);
        }
        finally
        {
            _activeLease = null;
        }
    }

    public async Task StartSmartBlockAsync()
    {
        _docManager.FlushPendingSplit();
        if (_docManager.SelectedBlock is not { } block || _docManager.CurrentDocument is not { } doc)
        {
            StatusMessage = "Selecione um bloco inteligente para digitar.";
            return;
        }

        string textToType = block.GetText(doc.Text);
        if (string.IsNullOrEmpty(textToType))
        {
            StatusMessage = "O bloco inteligente selecionado está vazio.";
            return;
        }

        using var lease = _coordinator.TryBegin("Main");
        if (lease is null)
        {
            _log.Info("Acionamento ignorado: já existe uma digitação em andamento.");
            return;
        }

        _activeLease = lease;
        block.Status = TextBlockStatus.Typing;
        try
        {
            bool ok = await TypeTextPayloadAsync(textToType, $"Bloco Inteligente: {block.Title} ({block.LineRangeDisplay})", lease).ConfigureAwait(true);
            block.Status = ok ? TextBlockStatus.Sent : TextBlockStatus.Failed;
        }
        catch
        {
            block.Status = TextBlockStatus.Failed;
            throw;
        }
        finally
        {
            _activeLease = null;
        }
    }

    private async Task<bool> TypeTextPayloadAsync(string textToType, string description, TypingSessionLease lease)
    {
        Guid sessionId = lease.SessionId;
        var token = lease.Token;

        State = TypingState.AguardandoContagem;
        _sessionStartedAt = DateTime.Now;
        TotalCharacters = textToType.Length;
        TypedCharacters = 0;

        _log.Info($"Sessão iniciada: {description}");
        _log.Info($"Método: {Settings.Method}");
        _log.Info($"Caracteres: {textToType.Length}");

        try
        {
            int seconds = Math.Max(0, Settings.CountdownSeconds);
            for (int i = seconds; i > 0; i--)
            {
                await lease.Pause.WaitWhilePausedAsync().WaitAsync(token).ConfigureAwait(true);
                CountdownText = $"Iniciando em {i}...";
                StatusMessage = $"{description}: clique no destino (janela do navegador / VM) agora.";
                await _delayAsync(1000, token).ConfigureAwait(true);
                await lease.Pause.WaitWhilePausedAsync().WaitAsync(token).ConfigureAwait(true);
            }
            CountdownText = string.Empty;

            if (Settings.EnableTargetGuard || Settings.ValidateTargetWindow)
            {
                var anchorResult = _targetGuard.TryCaptureAnchor(Settings.ValidateTargetWindow, Settings.ExpectedBrowser);
                OnPropertyChanged(nameof(TargetIndicatorText));
                if (!anchorResult.IsValid)
                {
                    State = TypingState.Erro;
                    StatusSeverity = "Error";
                    LastDeliverySemanticState = DeliverySemanticState.Failed;
                    StatusMessage = anchorResult.ErrorMessage ?? "A janela ativa não é um destino válido.";
                    _log.Warn(StatusMessage);
                    return false;
                }
            }

            var resolvedProfile = TypingProfile.Resolve(Settings);
            var baseOptions = new TypingOptions
            {
                CharDelayMs = Settings.CharDelayMs,
                EnterDelayMs = Settings.EnterDelayMs,
                Method = Settings.Method,
                TargetLayout = Settings.TargetLayout,
                PreserveOriginalIndentation = Settings.PreserveOriginalIndentation,
                IndentationStrategy = IndentationResetStrategy.Auto,
                Substitutions = Settings.CharacterSubstitutions,
                Profile = resolvedProfile
            };

            var preflight = _typingService.Plan(textToType, baseOptions);
            var dryRun = TypingScheduler.DryRun(preflight, resolvedProfile);
            bool fallbackUnicode = false;
            if (preflight.UnsupportedCount > 0)
            {
                var decision = ConfirmPreflightUnsupported?.Invoke(preflight) ?? PreflightDecision.Cancel;
                if (decision == PreflightDecision.Cancel)
                {
                    State = TypingState.Cancelado;
                    StatusSeverity = "Warning";
                    StatusMessage = $"Envio cancelado no pré-voo ({preflight.UnsupportedCount} unidade(s) não suportada(s)).";
                    _log.Info(StatusMessage);
                    return false;
                }
                fallbackUnicode = decision == PreflightDecision.FallbackToUnicode;
            }

            if (preflight.TotalUnits > 0)
            {
                TotalCharacters = preflight.TotalUnits;
            }
            SkippedUnits = 0;
            FailedUnits = 0;
            StatusSeverity = "Normal";

            State = TypingState.Digitando;
            StatusMessage = $"Digitando {description} (estimativa: ~{dryRun.EstimatedDuration.TotalSeconds:0.0}s)...";

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
                Profile = resolvedProfile,
                BeforeUnitAsync = Settings.EnableTargetGuard
                    ? (unitIdx, ct) => CheckTargetGuardBeforeUnitAsync(lease, unitIdx, ct)
                    : null
            };

            var progress = new Helpers.DirectProgress<TypingProgress>(OnProgress);

            _log.Info("Digitação iniciada");
            await _typingService.TypeTextAsync(textToType, options, progress, lease.Pause, token).ConfigureAwait(true);

            double duration = (DateTime.Now - _sessionStartedAt).TotalSeconds;
            if (_skippedUnits > 0 || _failedUnits > 0)
            {
                State = TypingState.Erro;
                StatusSeverity = _failedUnits > 0 ? "Error" : "Warning";
                LastDeliverySemanticState = DeliverySemantics.FromLocalSendResult(UnitOutcome.Failed);
                string warn = $"Envio concluído com ressalvas: {TypedCharacters} enviados, {_skippedUnits} pulados, {_failedUnits} falhas.";
                StatusMessage = warn;
                _log.Warn(warn);
                AddHistoryEntry(sessionId, Guid.Empty, 0, TotalCharacters, completed: false, duration);
                return false;
            }
            else
            {
                State = TypingState.Concluido;
                StatusSeverity = "Normal";
                bool hadFocusIncidents = _targetGuard.Incidents.Count > 0;
                LastDeliverySemanticState = DeliverySemantics.FromLocalSendResult(UnitOutcome.Sent, hadFocusIncidents);
                _log.Info($"Digitação concluída. Unidades enviadas: {TypedCharacters}. Duração: {duration:0.0}s");
                AddHistoryEntry(sessionId, Guid.Empty, 0, TotalCharacters, completed: true, duration);
                StatusMessage = $"Envio concluído ({TypedCharacters} caracteres digitados).";
                return true;
            }
        }
        catch (OperationCanceledException)
        {
            double duration = (DateTime.Now - _sessionStartedAt).TotalSeconds;
            State = TypingState.Cancelado;
            StatusSeverity = "Warning";
            StatusMessage = "Digitação cancelada pelo usuário.";
            _log.Info("Digitação cancelada pelo usuário.");
            AddHistoryEntry(sessionId, Guid.Empty, 0, TotalCharacters, completed: false, duration);
            return false;
        }
        catch (Exception ex)
        {
            State = TypingState.Erro;
            StatusSeverity = "Error";
            LastDeliverySemanticState = DeliverySemanticState.Failed;
            StatusMessage = $"Erro durante digitação: {ex.Message}";
            _log.Error(ex.Message);
            return false;
        }
        finally
        {
            _targetGuard.StopMonitoring();
            OnPropertyChanged(nameof(TargetIndicatorText));
        }
    }

    public async Task StartQueueBlockAsync()
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
                OnPropertyChanged(nameof(TargetIndicatorText));
                if (!anchorResult.IsValid)
                {
                    State = TypingState.Erro;
                    StatusSeverity = "Error";
                    LastDeliverySemanticState = DeliverySemanticState.Failed;
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
                StatusSeverity = "Error";
                LastDeliverySemanticState = DeliverySemanticState.Failed;
                StatusMessage = "O bloco selecionado ficou vazio ou foi removido antes do início.";
                return;
            }

            var resolvedProfile = TypingProfile.Resolve(Settings);
            var baseOptions = new TypingOptions
            {
                CharDelayMs = Settings.CharDelayMs,
                EnterDelayMs = Settings.EnterDelayMs,
                Method = Settings.Method,
                TargetLayout = Settings.TargetLayout,
                PreserveOriginalIndentation = Settings.PreserveOriginalIndentation,
                IndentationStrategy = IndentationResetStrategy.Auto,
                Substitutions = Settings.CharacterSubstitutions,
                Profile = resolvedProfile
            };

            // §2.1 (E) e F2 §4: Pré-voo — calcula unidades e duração estimada.
            var preflight = _typingService.Plan(textToType, baseOptions);
            var dryRun = TypingScheduler.DryRun(preflight, resolvedProfile);
            bool fallbackUnicode = false;
            if (preflight.UnsupportedCount > 0)
            {
                var decision = ConfirmPreflightUnsupported?.Invoke(preflight) ?? PreflightDecision.Cancel;
                if (decision == PreflightDecision.Cancel)
                {
                    State = TypingState.Cancelado;
                    StatusSeverity = "Warning";
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
            SkippedUnits = 0;
            FailedUnits = 0;
            StatusSeverity = "Normal";

            _queueService.BeginTyping(blockId, sessionId);
            _activeBlockId = blockId;
            typingStarted = true;
            State = TypingState.Digitando;
            StatusMessage = $"Digitando Bloco {blockOrder} (estimativa: ~{dryRun.EstimatedDuration.TotalSeconds:0.0}s)...";

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
                Profile = resolvedProfile,
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
                // §2.3 (E) e Fase 5 §1: conclusão com Skipped > 0 ou Failed > 0 termina como PartiallySent/aviso (amarelo ou vermelho).
                State = TypingState.Erro;
                StatusSeverity = _failedUnits > 0 ? "Error" : "Warning";
                LastDeliverySemanticState = DeliverySemantics.FromLocalSendResult(UnitOutcome.Failed);
                string warn = Strings.FormatBlockPartialSent(blockOrder, TypedCharacters, _skippedUnits, _failedUnits);
                StatusMessage = warn;
                _log.Warn(warn);
                AddHistoryEntry(sessionId, blockId, blockOrder, TotalCharacters, completed: false, duration);
                _queueService.CancelBlock(blockId, Math.Max(1, resumeFromIndex + TypedCharacters), sessionId);
            }
            else
            {
                State = TypingState.Concluido;
                StatusSeverity = "Normal";
                bool hadFocusIncidents = _targetGuard.Incidents.Count > 0;
                LastDeliverySemanticState = DeliverySemantics.FromLocalSendResult(UnitOutcome.Sent, hadFocusIncidents);
                _log.Info($"Digitação concluída. Unidades enviadas: {TypedCharacters}. Duração: {duration:0.0}s");

                AddHistoryEntry(sessionId, blockId, blockOrder, TotalCharacters, completed: true, duration);
                _queueService.CompleteBlock(blockId, Settings.ClearTextAfterCompletion, resumeFromIndex + TypedCharacters, sessionId);

                StatusMessage = Strings.FormatBlockSent(blockOrder, TypedCharacters);
            }
        }
        catch (OperationCanceledException)
        {
            double duration = (DateTime.Now - _sessionStartedAt).TotalSeconds;
            State = TypingState.Cancelado;
            StatusSeverity = "Warning";
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
            StatusSeverity = "Error";
            LastDeliverySemanticState = DeliverySemanticState.Failed;
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
            OnPropertyChanged(nameof(TargetIndicatorText));
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
            StatusSeverity = "Warning";
            StatusMessage = "Pausado: destino perdido. Volte à janela de destino e clique em CONTINUAR.";
        }
        return Task.CompletedTask;
    }

    private void AddHistoryEntry(Guid sessionId, Guid blockId, int blockOrder, int characterCount, bool completed, double durationSeconds)
    {
        SessionMetrics? metrics = null;
        if (_typingService is KeyboardInputService kis && kis.LastSessionMetrics is not null)
        {
            metrics = kis.LastSessionMetrics with { TargetLostIncidents = _targetGuard.Incidents.Count };
        }

        var entry = new TypingSession
        {
            SessionId = sessionId,
            BlockId = blockId,
            StartedAt = _sessionStartedAt,
            BlockOrder = blockOrder,
            CharacterCount = characterCount,
            Completed = completed,
            DurationSeconds = durationSeconds,
            Metrics = metrics
        };
        History.Insert(0, entry);
        while (History.Count > 50) History.RemoveAt(History.Count - 1);
        _historyService.Save(History.Reverse());
    }

    private void OnProgress(TypingProgress p)
    {
        TypedCharacters = p.Sent;
        TotalCharacters = p.TotalUnits;
        SkippedUnits = p.Skipped;
        FailedUnits = p.Failed;
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
