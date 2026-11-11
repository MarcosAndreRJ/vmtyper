using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using VmTyper.Helpers;
using VmTyper.Models;
using VmTyper.Native;
using VmTyper.Services;
using InputMethod = VmTyper.Models.InputMethod;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace WebView2Poc;

public partial class MainWindow : Window
{
    private const string VirtualHost = "diag.vmtyper.local";
    private const string DefaultDiagUrl = "https://diag.vmtyper.local/index.html";

    private readonly string? _autoCompareOutputPath;
    private readonly bool _allowSso;
    private readonly LogService _log;
    private bool _isReady;

    public MainWindow(string? autoCompareOutputPath = null, bool allowSso = false)
    {
        InitializeComponent();
        _log = new LogService(Dispatcher);
        _autoCompareOutputPath = autoCompareOutputPath;
        _allowSso = allowSso;

        Loaded += async (_, _) => await InitializeWebViewAsync();
    }

    private async Task InitializeWebViewAsync()
    {
        // §1.2 Antes de criar o controle, verificar GetAvailableBrowserVersionString().
        string? runtimeVersion = null;
        try
        {
            runtimeVersion = CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            runtimeVersion = null;
        }
        catch (Exception ex)
        {
            AppendLog($"Erro ao consultar versão do WebView2 Runtime: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(runtimeVersion))
        {
            BrowserView.Visibility = Visibility.Collapsed;
            RuntimeWarningText.Visibility = Visibility.Visible;
            RuntimeWarningText.Text =
                "O Microsoft Edge WebView2 Runtime não foi encontrado nesta máquina.\n" +
                "Instale o Evergreen WebView2 Runtime pelos canais aprovados da organização antes de usar o navegador integrado.";
            StatusBanner.Text = "WebView2 Runtime ausente.";
            return;
        }

        try
        {
            string userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VmTyper",
                "WebView2Profile");
            Directory.CreateDirectory(userDataFolder);

            var envOptions = new CoreWebView2EnvironmentOptions
            {
                AllowSingleSignOnUsingOSPrimaryAccount = _allowSso
            };

            var env = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: userDataFolder,
                options: envOptions);

            await BrowserView.EnsureCoreWebView2Async(env);
            BrowserView.Visibility = Visibility.Visible;

            var core = BrowserView.CoreWebView2;

            // §1.3 Indicador de carregamento e erros amigáveis
            core.NavigationStarting += (_, args) =>
            {
                UrlBox.Text = args.Uri;
                StatusBanner.Text = "Carregando...";
            };

            core.NavigationCompleted += (_, args) =>
            {
                if (args.IsSuccess)
                {
                    StatusBanner.Text = $"Pronto (WebView2 {runtimeVersion})";
                }
                else
                {
                    StatusBanner.Text = $"Erro de navegação: {TranslateWebError(args.WebErrorStatus)}";
                    AppendLog($"[NavigationError] {args.WebErrorStatus}: {TranslateWebError(args.WebErrorStatus)}");
                }
            };

            // §2 Nunca ignorar ServerCertificateErrorDetected automaticamente
            core.ServerCertificateErrorDetected += (_, args) =>
            {
                args.Action = CoreWebView2ServerCertificateErrorAction.Default;
                AppendLog($"[Segurança] Certificado de servidor inválido em {args.RequestUri} ({args.ErrorStatus}) — bloqueado (ação padrão).");
            };

            // §2 ClientCertificateRequested: deixar o usuário escolher, sem seleção automática silenciosa
            core.ClientCertificateRequested += (_, args) =>
            {
                args.Handled = false;
                AppendLog($"[Segurança] Certificado de cliente solicitado por {args.Host}:{args.Port} — exibindo seletor nativo ao usuário.");
            };

            // §5.4 ProcessFailed: pausa imediatamente e oferece recarregar
            core.ProcessFailed += (_, args) =>
            {
                StatusBanner.Text = $"Falha no processo do navegador ({args.ProcessFailedKind}). Clique em Atualizar.";
                AppendLog($"[ProcessFailed] {args.ProcessFailedKind} — sessão interrompida por segurança.");
            };

            // §1.4 Página-alvo mapeada via SetVirtualHostNameToFolderMapping
            string diagFolder = FindDiagnosticsPageFolder();
            core.SetVirtualHostNameToFolderMapping(
                VirtualHost,
                diagFolder,
                CoreWebView2HostResourceAccessKind.Allow);

            var navTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnInitialNav(object? s, CoreWebView2NavigationCompletedEventArgs e)
            {
                core.NavigationCompleted -= OnInitialNav;
                navTcs.TrySetResult(e.IsSuccess);
            }
            core.NavigationCompleted += OnInitialNav;

            core.Navigate(DefaultDiagUrl);
            for (int attempt = 0; attempt < 100; attempt++)
            {
                await navTcs.Task;
                string diagType = await core.ExecuteScriptAsync("typeof window.__vmtyperDiag");
                if (diagType.Contains("object", StringComparison.Ordinal))
                {
                    break;
                }
                await Task.Delay(30);
            }
            await core.CallDevToolsProtocolMethodAsync("Page.bringToFront", "{}");
            _isReady = true;

            AppendLog($"WebView2 inicializado (Runtime={runtimeVersion}, SSO={_allowSso}, Mapeamento=https://{VirtualHost}/ -> {diagFolder})");

            if (!string.IsNullOrWhiteSpace(_autoCompareOutputPath))
            {
                await RunAutomatedComparisonAndExitAsync(_autoCompareOutputPath, runtimeVersion);
            }
        }
        catch (Exception ex)
        {
            StatusBanner.Text = "Falha na inicialização.";
            AppendLog($"Erro ao inicializar WebView2: {ex}");
            if (!string.IsNullOrWhiteSpace(_autoCompareOutputPath))
            {
                Close();
            }
        }
    }

    private async void RunSendInput_Click(object sender, RoutedEventArgs e)
    {
        if (!_isReady) return;
        string sample = "Teste SendInput: carro pessoa ação ~a ^^ 123!";
        var res = await ExecuteSendInputRunAsync("manual-sendinput", sample, TypingProfile.Legacy(8, 25));
        AppendLog($"[SendInput] len={res.ActualLength}, trusted={res.TrustedDownCount}, untrusted={res.UntrustedDownCount}, suspeitos={res.SuspiciousCount}, div={res.DivergenceReport.TotalDivergences}");
    }

    private async void RunCdpDead_Click(object sender, RoutedEventArgs e)
    {
        if (!_isReady) return;
        string sample = "Teste CDP (Dead+Base): carro pessoa ação ~a ^^ 123!";
        var res = await ExecuteCdpRunAsync("manual-cdp-dead", sample, TypingProfile.Legacy(8, 25), CdpDeadKeyStrategy.DeadKeyThenComposed);
        AppendLog($"[CDP Dead+Base] len={res.ActualLength}, trusted={res.TrustedDownCount}, untrusted={res.UntrustedDownCount}, suspeitos={res.SuspiciousCount}, div={res.DivergenceReport.TotalDivergences}");
    }

    private async void RunCdpComposed_Click(object sender, RoutedEventArgs e)
    {
        if (!_isReady) return;
        string sample = "Teste CDP (Composed): carro pessoa ação ~a ^^ 123!";
        var res = await ExecuteCdpRunAsync("manual-cdp-composed", sample, TypingProfile.Legacy(8, 25), CdpDeadKeyStrategy.ComposedTextDirect);
        AppendLog($"[CDP Composed] len={res.ActualLength}, trusted={res.TrustedDownCount}, untrusted={res.UntrustedDownCount}, suspeitos={res.SuspiciousCount}, div={res.DivergenceReport.TotalDivergences}");
    }

    private async void RunComparison_Click(object sender, RoutedEventArgs e)
    {
        if (!_isReady) return;
        string outPath = Path.Combine(Path.GetTempPath(), $"f4-poc-compare-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        await RunAutomatedComparisonAsync(outPath, CoreWebView2Environment.GetAvailableBrowserVersionString());
        AppendLog($"Comparação salva em: {outPath}");
    }

    private async Task RunAutomatedComparisonAndExitAsync(string outputPath, string runtimeVersion)
    {
        try
        {
            await RunAutomatedComparisonAsync(outputPath, runtimeVersion);
        }
        finally
        {
            Close();
        }
    }

    private async Task RunAutomatedComparisonAsync(string outputPath, string runtimeVersion)
    {
        string corporaDir = Path.Combine(FindRepoRoot(), "tests", "corpora");
        string[] targetCorpora =
        {
            "basico.txt",
            "repetidas.txt",
            "acentos-ptbr.txt",
            "teclas-mortas-literais.txt",
            "numeros-simbolos.txt",
            "tabs-indentacao.txt"
        };

        var results = new List<MethodComparisonEntry>();

        foreach (string corpusFile in targetCorpora)
        {
            string fullPath = Path.Combine(corporaDir, corpusFile);
            if (!File.Exists(fullPath)) continue;
            string corpusText = KeyboardInputService.NormalizeLineEndings(await File.ReadAllTextAsync(fullPath, Encoding.UTF8));

            // Método B1: CDP DeadKeyThenComposed
            var b1 = await ExecuteCdpRunAsync(corpusFile, corpusText, TypingProfile.Legacy(0, 0, seed: 42), CdpDeadKeyStrategy.DeadKeyThenComposed);
            results.Add(b1);
            AppendLog($"[AutoCompare] {corpusFile} B1 (CDP Dead+Base): len={b1.ActualLength}/{b1.ExpectedLength}, sha={b1.ActualSha12}, div={b1.DivergenceReport.TotalDivergences}");

            // Método B2: CDP ComposedTextDirect
            var b2 = await ExecuteCdpRunAsync(corpusFile, corpusText, TypingProfile.Legacy(0, 0, seed: 42), CdpDeadKeyStrategy.ComposedTextDirect);
            results.Add(b2);
            AppendLog($"[AutoCompare] {corpusFile} B2 (CDP Composed): len={b2.ActualLength}/{b2.ExpectedLength}, sha={b2.ActualSha12}, div={b2.DivergenceReport.TotalDivergences}");

            // Método A: SendInput (tenta focar a janela se o desktop permitir foreground)
            var a = await ExecuteSendInputRunAsync(corpusFile, corpusText, TypingProfile.Legacy(0, 0, seed: 42));
            results.Add(a);
            AppendLog($"[AutoCompare] {corpusFile} A (SendInput): fg={a.OsForegroundAvailable}, len={a.ActualLength}/{a.ExpectedLength}");
        }

        string? dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var summary = new
        {
            generatedAtUtc = DateTime.UtcNow,
            webView2NuGetVersion = "1.0.4314.35",
            webView2RuntimeVersion = runtimeVersion,
            virtualHostUrl = DefaultDiagUrl,
            entries = results
        };

        await File.WriteAllTextAsync(
            outputPath,
            JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }),
            Encoding.UTF8);
    }

    private async Task<MethodComparisonEntry> ExecuteCdpRunAsync(
        string corpusName,
        string text,
        TypingProfile profile,
        CdpDeadKeyStrategy strategy)
    {
        await ResetAndFocusPageTextareaAsync();

        var dispatcher = new WebView2CdpDispatcher(BrowserView.CoreWebView2);
        var cdpInjector = new CdpInputInjector(dispatcher, strategy);
        var layoutProvider = new Win32KeyboardLayoutProvider();
        var mapping = new KeyboardMappingService(layoutProvider);
        var fastDelay = new FastPocDelayProvider();
        var service = new KeyboardInputService(mapping, _log, cdpInjector, layoutProvider, fastDelay);

        await Task.Run(async () =>
        {
            await service.TypeTextAsync(
                text,
                new TypingOptions
                {
                    Method = InputMethod.Automatico,
                    PreserveOriginalIndentation = false,
                    Profile = profile
                },
                null,
                PauseToken.None,
                CancellationToken.None);
        });

        await Task.Delay(50);
        return await CollectPageReportAsync(corpusName, $"CDP-{strategy}", text);
    }

    private async Task<MethodComparisonEntry> ExecuteSendInputRunAsync(
        string corpusName,
        string text,
        TypingProfile profile)
    {
        await ResetAndFocusPageTextareaAsync();

        Activate();
        Topmost = true;
        Focus();
        BrowserView.Focus();
        Keyboard.Focus(BrowserView);
        await ResetAndFocusPageTextareaAsync();

        IntPtr fg = Win32.GetForegroundWindow();
        bool hasOsForeground = fg != IntPtr.Zero;

        if (!hasOsForeground)
        {
            var rawDiff = DivergenceClassifier.Classify(text, string.Empty);
            return new MethodComparisonEntry(
                Corpus: corpusName,
                Method: "SendInput-Win32",
                OsForegroundAvailable: false,
                ExpectedLength: text.Length,
                ActualLength: 0,
                ExpectedSha12: DivergenceClassifier.ComputeFingerprint(text).Sha256Hex12,
                ActualSha12: "000000000000",
                KeyDownCount: 0,
                KeyUpCount: 0,
                TrustedDownCount: 0,
                UntrustedDownCount: 0,
                RepeatCount: 0,
                DoubleDownWithoutUpCount: 0,
                SuspiciousCount: 0,
                EmptyCodeCount: 0,
                DeadKeyEventCount: 0,
                DivergenceReport: rawDiff with { Items = Array.Empty<DivergenceItem>() },
                Note: "Sessão executada sem desktop interativo em foreground (GetForegroundWindow=0x0); SendInput requer foco Win32 ativo.");
        }

        var injector = new Win32InputInjector();
        var layoutProvider = new Win32KeyboardLayoutProvider();
        var mapping = new KeyboardMappingService(layoutProvider);
        var service = new KeyboardInputService(mapping, _log, injector, layoutProvider);

        await service.TypeTextAsync(
            text,
            new TypingOptions
            {
                Method = InputMethod.Automatico,
                PreserveOriginalIndentation = false,
                Profile = profile
            },
            null,
            PauseToken.None,
            CancellationToken.None);

        await Task.Delay(80);
        return await CollectPageReportAsync(corpusName, "SendInput-Win32", text, osForegroundAvailable: true);
    }

    private async Task ResetAndFocusPageTextareaAsync()
    {
        await BrowserView.CoreWebView2.CallDevToolsProtocolMethodAsync("Page.bringToFront", "{}");
        await BrowserView.CoreWebView2.ExecuteScriptAsync(
            "(function(){ if(!window.__vmtyperDiag) return false; window.__vmtyperDiag.clear(); return window.__vmtyperDiag.focusTarget(); })()");
    }

    private async Task<MethodComparisonEntry> CollectPageReportAsync(
        string corpusName,
        string methodLabel,
        string expectedText,
        bool osForegroundAvailable = true)
    {
        string rawJson = await BrowserView.CoreWebView2.ExecuteScriptAsync(
            "JSON.stringify(window.__vmtyperDiag.getReport())");
        string unescapedJson = JsonSerializer.Deserialize<string>(rawJson) ?? "{}";
        using var doc = JsonDocument.Parse(unescapedJson);
        var root = doc.RootElement;

        string actualText = root.TryGetProperty("text", out var tProp) ? (tProp.GetString() ?? string.Empty) : string.Empty;
        string actualSha12 = root.TryGetProperty("sha256Hex12", out var sProp) ? (sProp.GetString() ?? "") : "";
        int suspiciousCount = root.TryGetProperty("suspiciousCount", out var susProp) ? susProp.GetInt32() : 0;

        int keyDowns = 0, keyUps = 0, trustedDowns = 0, untrustedDowns = 0, emptyCodes = 0, deadKeys = 0;
        int repeatCount = 0, doubleDownCount = 0;
        if (root.TryGetProperty("events", out var evArray) && evArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var ev in evArray.EnumerateArray())
            {
                string type = ev.TryGetProperty("type", out var tp) ? (tp.GetString() ?? "") : "";
                bool isTrusted = ev.TryGetProperty("isTrusted", out var tr) && tr.GetBoolean();
                string code = ev.TryGetProperty("code", out var cp) ? (cp.GetString() ?? "") : "";
                string key = ev.TryGetProperty("key", out var kp) ? (kp.GetString() ?? "") : "";
                string? susReason = ev.TryGetProperty("suspiciousReason", out var sr) && sr.ValueKind == JsonValueKind.String ? sr.GetString() : null;

                if (type == "keydown")
                {
                    keyDowns++;
                    if (isTrusted) trustedDowns++;
                    else untrustedDowns++;
                    if (string.IsNullOrEmpty(code)) emptyCodes++;
                    if (key == "Dead") deadKeys++;
                    if (susReason == "repeat=true") repeatCount++;
                    else if (susReason == "double-keydown-without-keyup") doubleDownCount++;
                }
                else if (type == "keyup")
                {
                    keyUps++;
                }
            }
        }

        var diff = DivergenceClassifier.Classify(expectedText, actualText);
        var expFp = DivergenceClassifier.ComputeFingerprint(expectedText);

        return new MethodComparisonEntry(
            Corpus: corpusName,
            Method: methodLabel,
            OsForegroundAvailable: osForegroundAvailable,
            ExpectedLength: expFp.CharLength,
            ActualLength: actualText.Length,
            ExpectedSha12: expFp.Sha256Hex12,
            ActualSha12: actualSha12,
            KeyDownCount: keyDowns,
            KeyUpCount: keyUps,
            TrustedDownCount: trustedDowns,
            UntrustedDownCount: untrustedDowns,
            RepeatCount: repeatCount,
            DoubleDownWithoutUpCount: doubleDownCount,
            SuspiciousCount: suspiciousCount,
            EmptyCodeCount: emptyCodes,
            DeadKeyEventCount: deadKeys,
            DivergenceReport: diff,
            Note: null);
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_isReady && BrowserView.CoreWebView2.CanGoBack) BrowserView.CoreWebView2.GoBack();
    }

    private void Forward_Click(object sender, RoutedEventArgs e)
    {
        if (_isReady && BrowserView.CoreWebView2.CanGoForward) BrowserView.CoreWebView2.GoForward();
    }

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (_isReady) BrowserView.CoreWebView2.Reload();
    }

    private void Navigate_Click(object sender, RoutedEventArgs e) => NavigateToUrlBox();

    private void UrlBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            NavigateToUrlBox();
            e.Handled = true;
        }
    }

    private void NavigateToUrlBox()
    {
        if (!_isReady) return;
        string raw = UrlBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(raw)) return;
        if (!raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            raw = "https://" + raw;
        }
        BrowserView.CoreWebView2.Navigate(raw);
    }

    private void AppendLog(string message)
    {
        string line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        ResultLogBox.AppendText(line + Environment.NewLine);
        ResultLogBox.ScrollToEnd();
    }

    private static string TranslateWebError(CoreWebView2WebErrorStatus status) => status switch
    {
        CoreWebView2WebErrorStatus.CertificateCommonNameIsIncorrect or
        CoreWebView2WebErrorStatus.CertificateExpired or
        CoreWebView2WebErrorStatus.CertificateIsInvalid or
        CoreWebView2WebErrorStatus.CertificateRevoked =>
            "Certificado TLS do servidor inválido ou expirado.",
        CoreWebView2WebErrorStatus.HostNameNotResolved =>
            "Não foi possível resolver o endereço do servidor (DNS).",
        CoreWebView2WebErrorStatus.ConnectionAborted or
        CoreWebView2WebErrorStatus.ConnectionReset or
        CoreWebView2WebErrorStatus.Disconnected =>
            "A conexão com o servidor foi interrompida.",
        CoreWebView2WebErrorStatus.Timeout =>
            "Tempo limite de conexão esgotado.",
        _ => $"Falha de rede ou carregamento ({status})."
    };

    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "VmTyper.sln"))) return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        return Directory.GetCurrentDirectory();
    }

    private static string FindDiagnosticsPageFolder() =>
        Path.Combine(FindRepoRoot(), "tools", "diagnostics-page");

    private sealed class FastPocDelayProvider : IDelayProvider
    {
        public long Timestamp() => System.Diagnostics.Stopwatch.GetTimestamp();
        public void HoldExact(TimeSpan d) { }
        public ValueTask WaitAsync(TimeSpan d, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class WebView2CdpDispatcher : ICdpDispatcher
    {
        private readonly CoreWebView2 _core;
        public WebView2CdpDispatcher(CoreWebView2 core) => _core = core;

        public Task<string> CallDevToolsProtocolMethodAsync(string methodName, string parametersJson, CancellationToken ct = default)
        {
            if (Application.Current.Dispatcher.CheckAccess())
            {
                var task = _core.CallDevToolsProtocolMethodAsync(methodName, parametersJson);
                if (!task.IsCompleted)
                {
                    var frame = new System.Windows.Threading.DispatcherFrame();
                    _ = task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
                    System.Windows.Threading.Dispatcher.PushFrame(frame);
                }
                return task;
            }
            return Application.Current.Dispatcher.InvokeAsync(
                () => _core.CallDevToolsProtocolMethodAsync(methodName, parametersJson)).Task.Unwrap();
        }
    }
}

public sealed record MethodComparisonEntry(
    string Corpus,
    string Method,
    bool OsForegroundAvailable,
    int ExpectedLength,
    int ActualLength,
    string ExpectedSha12,
    string ActualSha12,
    int KeyDownCount,
    int KeyUpCount,
    int TrustedDownCount,
    int UntrustedDownCount,
    int RepeatCount,
    int DoubleDownWithoutUpCount,
    int SuspiciousCount,
    int EmptyCodeCount,
    int DeadKeyEventCount,
    DivergenceReport DivergenceReport,
    string? Note);
