using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VmTyper.Documents;
using VmTyper.Models;
using VmTyper.Services;
using VmTyper.Tests.Fakes;
using VmTyper.ViewModels;

namespace VmTyper.Tests;

/// <summary>
/// Thread de UI única e compartilhada para os testes de interface. O <see cref="Application"/> do WPF é um singleton
/// por processo e seus recursos (pincéis, estilos) pertencem à thread que o criou: criar uma thread por teste
/// faria os testes seguintes enxergar recursos de uma thread já encerrada. Aqui o Application e todas as janelas
/// vivem no mesmo Dispatcher, com os mesmos dicionários do App.xaml (tema + tokens + design system + controles).
/// </summary>
internal static class UiTestHost
{
    private static readonly object Gate = new();
    private static Dispatcher? _dispatcher;

    private static void EnsureStarted()
    {
        lock (Gate)
        {
            if (_dispatcher != null) return;

            using var ready = new ManualResetEventSlim();
            var thread = new Thread(() =>
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                foreach (var name in new[] { "LightTheme", "Tokens", "DesignSystem", "Controls" })
                {
                    app.Resources.MergedDictionaries.Add(new ResourceDictionary
                    {
                        Source = new Uri($"pack://application:,,,/VmTyper;component/Themes/{name}.xaml", UriKind.Absolute)
                    });
                }

                _dispatcher = Dispatcher.CurrentDispatcher;
                ready.Set();
                Dispatcher.Run();
            })
            {
                IsBackground = true,
                Name = "UiTestHost"
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            ready.Wait();
        }
    }

    public static void Run(Action action)
    {
        EnsureStarted();
        Exception? error = null;
        _dispatcher!.Invoke(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    public static T Run<T>(Func<T> func)
    {
        T result = default!;
        Run(() => result = func());
        return result;
    }

    /// <summary>Processa a fila do Dispatcher por um tempo (timers de debounce, layout pendente) sem bloquear a thread de UI.</summary>
    public static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    /// <summary>Força layout e renderização completos da janela.</summary>
    public static void Settle(Window window)
    {
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        window.UpdateLayout();
    }

    public static void ApplyTheme(AppTheme theme) => ThemeManager.Instance.ApplyTheme(theme, force: true);

    public static Color BrushColor(string key) => ((SolidColorBrush)Application.Current.Resources[key]).Color;

    /// <summary>Renderiza o conteúdo da janela em PNG (Pbgra32) e devolve também os pixels para inspeção.</summary>
    public static (byte[] Pixels, int Width, int Height, BitmapSource Bitmap) Render(FrameworkElement element, double dpi = 96, Brush? background = null)
    {
        int width = (int)Math.Ceiling(element.ActualWidth * dpi / 96);
        int height = (int)Math.Ceiling(element.ActualHeight * dpi / 96);
        var rtb = new RenderTargetBitmap(width, height, dpi, dpi, PixelFormats.Pbgra32);

        if (background is not null)
        {
            // O conteúdo de uma janela não inclui o fundo da própria janela; sem isto o PNG sairia transparente.
            var backdrop = new DrawingVisual();
            using (var context = backdrop.RenderOpen())
            {
                context.DrawRectangle(background, null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            }
            rtb.Render(backdrop);
        }

        rtb.Render(element);
        int stride = width * 4;
        var pixels = new byte[stride * height];
        rtb.CopyPixels(pixels, stride, 0);
        return (pixels, width, height, rtb);
    }

    public static Color PixelAt(byte[] pixels, int width, int x, int y)
    {
        int i = (y * width + x) * 4;
        return Color.FromArgb(pixels[i + 3], pixels[i + 2], pixels[i + 1], pixels[i]);
    }

    /// <summary>Diretório de saída das capturas, definido por VMTYPER_UI_SCREENSHOTS; null = não grava arquivos.</summary>
    public static string? ScreenshotDirectory
    {
        get
        {
            string? dir = Environment.GetEnvironmentVariable("VMTYPER_UI_SCREENSHOTS");
            if (string.IsNullOrWhiteSpace(dir)) return null;
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static void SavePng(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}

/// <summary>Monta a janela principal com serviços falsos e persistência isolada em diretório temporário.</summary>
internal static class UiTestKit
{
    public static (MainWindow Window, MainViewModel ViewModel, TempDir Dir) CreateWindow(Action<AppSettings>? configure = null)
    {
        UiTestHost.ApplyTheme(AppTheme.Light);
        var win = new MainWindow();

        var dir = new TempDir();
        var log = TestLog.Create(dir);
        var harness = new EngineHarness();
        var settings = new SettingsService(dir.File("settings.json"));
        if (configure is not null)
        {
            var s = settings.Load();
            configure(s);
            settings.Save(s);
        }
        var history = new HistoryService(dir.File("history.json"));
        var hotkeys = new FakeHotkeyRegistrar();
        var queue = new TypingQueueService(log, new InMemoryQueueStore(), new ManualAutoSaveScheduler());
        var coord = new TypingSessionCoordinator(hotkeys, log);
        var documents = new DocumentManager(new DocumentPersistenceService(dir.File("documents.json")));

        var vm = new MainViewModel(
            harness.Engine,
            new ActiveWindowService(),
            settings,
            history,
            hotkeys,
            queue,
            log,
            coord,
            (_, _) => Task.CompletedTask,
            docManager: documents);

        win.AttachViewModel(vm);
        return (win, vm, dir);
    }
}
