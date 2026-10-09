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
    private TrayIconService? _trayIconService;
    private MainViewModel? _mainViewModel;

    public static MainViewModel MainViewModelInstance => ((App)Current)._mainViewModel!;
    public static LogService LogServiceInstance => ((App)Current)._logService!;
    public static SettingsService SettingsServiceInstance => ((App)Current)._settingsService!;
    public static ActiveWindowService ActiveWindowServiceInstance => ((App)Current)._activeWindowService!;
    public static GlobalHotkeyService HotkeyServiceInstance => ((App)Current)._hotkeyService!;
    public static IKeyboardInputService TypingServiceInstance => ((App)Current)._typingService!;
    public static TrayIconService TrayIconServiceInstance => ((App)Current)._trayIconService!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;

        _logService = new LogService(Dispatcher.CurrentDispatcher);
        _settingsService = new SettingsService();
        _historyService = new HistoryService();
        _queuePersistenceService = new QueuePersistenceService();
        _queueService = new TypingQueueService(_logService, Dispatcher.CurrentDispatcher, _queuePersistenceService);
        _mappingService = new KeyboardMappingService();
        _typingService = new KeyboardInputService(_mappingService, _logService);
        _activeWindowService = new ActiveWindowService();
        _hotkeyService = new GlobalHotkeyService();
        _trayIconService = new TrayIconService();

        var mainWindow = new MainWindow();

        // A janela de mensagens dos hotkeys precisa de um HWND já criado;
        // inicializamos após a MainWindow existir (SourceInitialized cuida disso).
        mainWindow.SourceInitialized += (_, _) =>
        {
            _hotkeyService.Initialize();
            _mainViewModel = new MainViewModel(
                _typingService, _activeWindowService, _settingsService,
                _historyService, _hotkeyService, _queueService, _logService);
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
        // Garante que nenhum modificador fique preso mesmo em caso de erro
        // totalmente inesperado na UI thread.
        Native.Win32.ReleaseAllModifiers();
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mainViewModel?.Shutdown();
        _hotkeyService?.Dispose();
        _trayIconService?.Dispose();
        base.OnExit(e);
    }
}
