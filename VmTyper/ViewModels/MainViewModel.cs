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
public sealed class MainViewModel : ObservableObject
{
    public const int HotkeyIdStart = 1;
    public const int HotkeyIdPauseResume = 2;
    public const int HotkeyIdCancel = 3;

    private readonly IKeyboardInputService _typingService;
    private readonly ActiveWindowService _activeWindowService;
    private readonly SettingsService _settingsService;
    private readonly HistoryService _historyService;
    private readonly GlobalHotkeyService _hotkeyService;
    private readonly TypingQueueService _queueService;
    private readonly LogService _log;

    private CancellationTokenSource? _cts;
    private PauseTokenSource? _pauseTokenSource;
    private DateTime _sessionStartedAt;

    /// <summary>
    /// Trava atômica de reentrância: 0 = ocioso, 1 = existe uma execução em
    /// andamento (contagem, digitando ou pausado). Verificada com
    /// Interlocked.CompareExchange logo na primeira linha de StartAsync,
    /// antes de qualquer outra leitura de estado — garante por construção
    /// que botão + hotkey, ou hotkey pressionado duas vezes em sequência
    /// rápida, nunca conseguem iniciar duas Tasks de digitação
    /// simultâneas, mesmo que a UI (IsBusy) ainda não tenha sido
    /// atualizada no instante exato da segunda tentativa.
    /// </summary>
    private int _isRunning;

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
        TypingState.Concluido => "Concluído",
        TypingState.Cancelado => "Cancelado",
        TypingState.Erro => "Erro",
        _ => State.ToString()
    };

    public bool IsBusy => State is TypingState.AguardandoContagem or TypingState.Digitando or TypingState.Pausado;
    public bool CanPause => State is TypingState.Digitando or TypingState.Pausado;
    public string PauseResumeButtonText => State == TypingState.Pausado ? "CONTINUAR" : "PAUSAR";

    // --- Fila ---
    public bool HasNextBlock => _queueService.Current is not null;

    public string NextBlockLabel
    {
        get
        {
            var current = _queueService.Current;
            if (current is not null) return $"Próximo: Bloco {current.Order} de {_queueService.TotalBlocks}";
            return _queueService.Blocks.Any(b => b.Status == TextBlockStatus.Completed)
                ? "Todos os blocos foram concluídos."
                : "Fila vazia — adicione conteúdo a um bloco.";
        }
    }

    public string DigitarButtonText
    {
        get
        {
            var current = _queueService.Current;
            if (current is not null) return $"DIGITAR BLOCO {current.Order}";
            return _queueService.Blocks.Any(b => b.Status == TextBlockStatus.Completed) ? "FILA CONCLUÍDA" : "DIGITAR";
        }
    }

    public string QueueSummary =>
        $"Blocos: {_queueService.TotalBlocks}   |   Concluídos: {_queueService.CompletedCount}   |   Pendentes: {_queueService.PendingCount}";

    public string QueueProgressLabel =>
        $"Fila: {_queueService.CompletedCount} de {_queueService.TotalBlocks} blocos concluídos";

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
        GlobalHotkeyService hotkeyService,
        TypingQueueService queueService,
        LogService log)
    {
        _typingService = typingService;
        _activeWindowService = activeWindowService;
        _settingsService = settingsService;
        _historyService = historyService;
        _hotkeyService = hotkeyService;
        _queueService = queueService;
        _log = log;

        LogLines = log.Lines;
        Settings = _settingsService.Load();

        foreach (var session in _historyService.Load())
        {
            History.Add(session);
        }

        _queueService.Changed += OnQueueChanged;

        StartCommand = new RelayCommand(async () => await StartAsync(), () => !IsBusy && HasNextBlock);
        PauseResumeCommand = new RelayCommand(TogglePause, () => CanPause);
        CancelCommand = new RelayCommand(Cancel, () => IsBusy);
        ClearHistoryCommand = new RelayCommand(() => { History.Clear(); _historyService.Save(History); });

        MoveUpCommand = new RelayCommand(p => { if (p is TextBlockItem b) _queueService.MoveUp(b.Id); }, p => !IsBusy);
        MoveDownCommand = new RelayCommand(p => { if (p is TextBlockItem b) _queueService.MoveDown(b.Id); }, p => !IsBusy);
        SetAsNextCommand = new RelayCommand(p => { if (p is TextBlockItem b) _queueService.SetAsNext(b.Id); }, p => !IsBusy);

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
    /// global. A trava atômica <see cref="_isRunning"/> é verificada aqui,
    /// antes de qualquer outra coisa: se dois acionamentos chegarem quase
    /// juntos (botão + hotkey, ou hotkey pressionado duas vezes rápido),
    /// somente o primeiro passa — o segundo é descartado imediatamente,
    /// sem nunca chegar a criar um novo CancellationTokenSource ou iniciar
    /// uma segunda Task de digitação.
    /// </summary>
    private async Task StartAsync()
    {
        if (Interlocked.CompareExchange(ref _isRunning, 1, 0) != 0)
        {
            _log.Info("Acionamento ignorado: já existe uma digitação em andamento.");
            return;
        }

        try
        {
            await StartCoreAsync().ConfigureAwait(true);
        }
        finally
        {
            Interlocked.Exchange(ref _isRunning, 0);
        }
    }

    private async Task StartCoreAsync()
    {
        var next = _queueService.PeekNext();
        if (next is null)
        {
            StatusMessage = _queueService.Blocks.Any(b => b.Status == TextBlockStatus.Completed)
                ? "Todos os blocos foram concluídos."
                : "Não há blocos com conteúdo para digitar.";
            return;
        }

        Guid blockId = next.Id;
        int blockOrder = next.Order;

        _cts = new CancellationTokenSource();
        _pauseTokenSource = new PauseTokenSource();
        var token = _cts.Token;

        State = TypingState.AguardandoContagem;
        _sessionStartedAt = DateTime.Now;
        TotalCharacters = next.Text.Length;
        TypedCharacters = 0;

        _log.Info("Sessão iniciada");
        _log.Info($"Bloco: {blockOrder}");
        _log.Info($"Método: {Settings.Method}");
        _log.Info($"Caracteres: {next.Text.Length}");

        // O ESC é registrado como hotkey global já a partir daqui, cobrindo
        // tanto a contagem regressiva quanto a digitação em si — o usuário
        // pode desistir a qualquer momento depois de acionar o bloco.
        bool cancelHotkeyRegistered = _hotkeyService.RegisterHotkey(
            HotkeyIdCancel, 0, Native.Win32.VK_ESCAPE, Cancel);
        if (!cancelHotkeyRegistered)
            _log.Warn("Não foi possível registrar ESC como tecla de emergência (pode já estar em uso).");

        try
        {
            int seconds = Math.Max(0, Settings.CountdownSeconds);
            for (int i = seconds; i > 0; i--)
            {
                CountdownText = $"Iniciando em {i}...";
                StatusMessage = $"Bloco {blockOrder}: clique no destino (janela do navegador / VM) agora.";
                await Task.Delay(1000, token).ConfigureAwait(true);
            }
            CountdownText = string.Empty;

            if (Settings.ValidateTargetWindow)
            {
                bool matches = _activeWindowService.MatchesExpectedBrowser(Settings.ExpectedBrowser, out var actual);
                if (!matches)
                {
                    State = TypingState.Erro;
                    StatusMessage = $"A janela ativa ({actual ?? "desconhecida"}) não corresponde ao destino configurado ({Settings.ExpectedBrowser}).";
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

            _queueService.BeginTyping(blockId);
            State = TypingState.Digitando;
            StatusMessage = $"Digitando Bloco {blockOrder}...";

            var options = new TypingOptions
            {
                CharDelayMs = Settings.CharDelayMs,
                EnterDelayMs = Settings.EnterDelayMs,
                Method = Settings.Method,
                PreserveOriginalIndentation = Settings.PreserveOriginalIndentation,
                IndentationStrategy = IndentationResetStrategy.Auto,
                Substitutions = Settings.CharacterSubstitutions
            };

            var progress = new Progress<TypingProgress>(OnProgress);

            _log.Info("Digitação iniciada");
            await _typingService.TypeTextAsync(block.Text, options, progress, _pauseTokenSource.Token, token)
                .ConfigureAwait(true);

            double duration = (DateTime.Now - _sessionStartedAt).TotalSeconds;
            State = TypingState.Concluido;
            _log.Info($"Digitação concluída. Caracteres enviados: {TypedCharacters}. Duração: {duration:0.0}s");

            AddHistoryEntry(blockOrder, TotalCharacters, completed: true, duration);
            _queueService.CompleteBlock(blockId, Settings.ClearTextAfterCompletion);

            var upcoming = _queueService.Current;
            StatusMessage = upcoming is not null
                ? $"Bloco {blockOrder} concluído. Próximo: Bloco {upcoming.Order}."
                : "Todos os blocos foram concluídos.";
        }
        catch (OperationCanceledException)
        {
            double duration = (DateTime.Now - _sessionStartedAt).TotalSeconds;
            State = TypingState.Cancelado;
            StatusMessage = $"Digitação do Bloco {blockOrder} cancelada pelo usuário.";
            _log.Info("Digitação cancelada pelo usuário.");
            AddHistoryEntry(blockOrder, TotalCharacters, completed: false, duration);
            // O bloco NÃO avança: volta a ser o próximo (Current) da fila.
            _queueService.CancelBlock(blockId);
        }
        catch (Exception ex)
        {
            State = TypingState.Erro;
            StatusMessage = $"Erro no Bloco {blockOrder}: {ex.Message}";
            _log.Error(ex.Message);
            // Mesmo em erro inesperado, o bloco não pode ficar preso em
            // "Digitando" para sempre nem ser considerado concluído.
            _queueService.CancelBlock(blockId);
        }
        finally
        {
            if (cancelHotkeyRegistered)
                _hotkeyService.UnregisterHotkey(HotkeyIdCancel);

            _cts?.Dispose();
            _cts = null;
            _pauseTokenSource = null;
        }
    }

    private void AddHistoryEntry(int blockOrder, int characterCount, bool completed, double durationSeconds)
    {
        var entry = new TypingSession
        {
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
        TypedCharacters = p.TypedCharacters;
        TotalCharacters = p.TotalCharacters;
        PercentComplete = p.PercentComplete;
        ElapsedText = p.Elapsed.ToString(@"mm\:ss");
        CpsText = p.CharactersPerSecond.ToString("0.0");
    }

    private void TogglePause()
    {
        if (_pauseTokenSource is null) return;

        if (State == TypingState.Digitando)
        {
            _pauseTokenSource.Pause();
            State = TypingState.Pausado;
            StatusMessage = "Pausado. Clique em CONTINUAR para retomar.";
            _log.Info("Digitação pausada pelo usuário.");
        }
        else if (State == TypingState.Pausado)
        {
            _pauseTokenSource.Resume();
            State = TypingState.Digitando;
            StatusMessage = "Digitando...";
            _log.Info("Digitação retomada pelo usuário.");
        }
    }

    private void Cancel()
    {
        if (_cts is { IsCancellationRequested: false })
        {
            _cts.Cancel();
            // Se estava pausado, libera a espera para que a exceção de
            // cancelamento seja observada imediatamente.
            _pauseTokenSource?.Resume();
        }
    }

    public void Shutdown()
    {
        Cancel();
        _queueService.Save();
        _hotkeyService.UnregisterHotkey(HotkeyIdStart);
        _hotkeyService.UnregisterHotkey(HotkeyIdPauseResume);
        _hotkeyService.UnregisterHotkey(HotkeyIdCancel);
    }
}
