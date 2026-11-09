using System.Windows;
using System.Windows.Threading;
using VmTyper.Services;
using VmTyper.ViewModels;

namespace VmTyper;

public partial class App : System.Windows.Application
{
    // Composição manual das dependências (sem framework de DI externo),
    // mantendo o número de dependências de terceiros no mínimo.
    private LogService? _logService;
    private SettingsService? _settingsService;
    private HistoryService? _historyService;
    private QueuePersistenceService? _queuePersistenceService;
    private TypingQueueService? _queueService;
    private KeyboardMappingService? _mappingService;
    private IKeyboardInputService? _typingService;
    private ActiveWindowService? _activeWindowService;
    private GlobalHotkeyService? _hotkeyService;
    private TypingSessionCoordinator? _coordinator;
    private TrayIconService? _trayIconService;
    private MainViewModel? _mainViewModel;

    public static MainViewModel MainViewModelInstance => ((App)Current)._mainViewModel!;
    public static LogService LogServiceInstance => ((App)Current)._logService!;
    public static SettingsService SettingsServiceInstance => ((App)Current)._settingsService!;
    public static ActiveWindowService ActiveWindowServiceInstance => ((App)Current)._activeWindowService!;
    public static GlobalHotkeyService HotkeyServiceInstance => ((App)Current)._hotkeyService!;
    public static TypingSessionCoordinator CoordinatorInstance => ((App)Current)._coordinator!;
    public static IKeyboardInputService TypingServiceInstance => ((App)Current)._typingService!;
    public static TrayIconService TrayIconServiceInstance => ((App)Current)._trayIconService!;
    public static string? QueueLoadCorruptPath => ((App)Current)._queuePersistenceService?.LastLoadResult is { Status: Services.Persistence.LoadStatus.Corrupt } r
        ? r.PreservedCorruptPath
        : null;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;

        _logService = new LogService(Dispatcher.CurrentDispatcher);
        _settingsService = new SettingsService();
        var initialSettings = _settingsService.Load();
        _logService.DetailedTrace = false;
        _historyService = new HistoryService();
        _queuePersistenceService = new QueuePersistenceService(() =>
            (_mainViewModel?.Settings ?? initialSettings).QueueTextPersistence);
        _queueService = new TypingQueueService(
            _logService,
            _queuePersistenceService,
            new DispatcherAutoSaveScheduler(Dispatcher.CurrentDispatcher, TimeSpan.FromSeconds(1.5)));
        _mappingService = new KeyboardMappingService(Native.Win32KeyboardLayoutProvider.Instance);
        _typingService = new KeyboardInputService(
            _mappingService, _logService,
            Native.Win32InputInjector.Instance, Native.Win32KeyboardLayoutProvider.Instance);
        _activeWindowService = new ActiveWindowService();
        _hotkeyService = new GlobalHotkeyService();
        _trayIconService = new TrayIconService();

        var mainWindow = new MainWindow();

        // A janela de mensagens dos hotkeys precisa de um HWND já criado;
        // inicializamos após a MainWindow existir (SourceInitialized cuida disso).
        mainWindow.SourceInitialized += (_, _) =>
        {
            _hotkeyService.Initialize();
            _coordinator = new TypingSessionCoordinator(_hotkeyService, _logService);
            _mainViewModel = new MainViewModel(
                _typingService, _activeWindowService, _settingsService,
                _historyService, _hotkeyService, _queueService, _logService, _coordinator);
            mainWindow.AttachViewModel(_mainViewModel);
        };

        MainWindow = mainWindow;

        var settings = _settingsService.Load();
        if (settings.StartMinimized)
        {
            mainWindow.WindowState = WindowState.Minimized;
        }

        mainWindow.Show();

        if (settings.StartMinimized && settings.MinimizeToTray)
        {
            mainWindow.Hide();
            _trayIconService.Visible = true;
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logService?.Error("Exceção não tratada: " + e.Exception.Message);
        // §2.2 item 6: último recurso baseado no InjectedKeyTracker (nunca solta modificadores por VK às cegas).
        if (_typingService is KeyboardInputService kis)
        {
            kis.EmergencyReleaseTrackedKeys();
        }
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_logService is not null) _logService.DetailedTrace = false;
        _mainViewModel?.Shutdown();
        _hotkeyService?.Dispose();
        _trayIconService?.Dispose();
        base.OnExit(e);
    }
}
