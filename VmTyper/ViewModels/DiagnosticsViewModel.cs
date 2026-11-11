using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;
using VmTyper.Helpers;
using VmTyper.Models;
using VmTyper.Services;

namespace VmTyper.ViewModels;

public sealed record DiagnosticTimelineRow(
    int Index,
    string Category,
    string Method,
    string GapComparison,
    string HoldComparison,
    string Result,
    DeliveryVisualBadge Badge);

public sealed record BenchmarkEntryRow(
    string Source,
    string Corpus,
    string Method,
    string Profile,
    int ExpectedLength,
    int ActualLength,
    string Sha12,
    int Divergences,
    DeliveryVisualBadge Badge);

/// <summary>
/// ViewModel do painel de diagnóstico ampliado (Fase 5 §4).
/// Expõe as 4 abas ("Sessão atual / última", "Teste de teclado", "Comparação", "Desempenho por método/perfil")
/// e garante a semântica visual dos 5 estados de entrega (<see cref="DeliverySemanticState"/>).
/// </summary>
public sealed class DiagnosticsViewModel : ObservableObject
{
    public IReadOnlyList<DeliveryVisualBadge> LegendBadges => DeliverySemantics.AllLegendBadges;

    public ObservableCollection<DiagnosticTimelineRow> TimelineEvents { get; } = new();
    public ObservableCollection<DivergenceItem> ComparisonItems { get; } = new();
    public ObservableCollection<BenchmarkEntryRow> BenchmarkRows { get; } = new();

    public DiagnosticsViewModel()
    {
        InitializeCalibration();
    }

    private string _sessionHeaderSummary = "Nenhuma sessão JSONL carregada.";
    public string SessionHeaderSummary
    {
        get => _sessionHeaderSummary;
        private set => SetField(ref _sessionHeaderSummary, value);
    }

    private string _sessionTotalsSummary = "Eventos: 0 · Enviadas: 0 · Puladas: 0 · Falhas: 0";
    public string SessionTotalsSummary
    {
        get => _sessionTotalsSummary;
        private set => SetField(ref _sessionTotalsSummary, value);
    }

    private string _comparisonExpectedText = string.Empty;
    public string ComparisonExpectedText
    {
        get => _comparisonExpectedText;
        set => SetField(ref _comparisonExpectedText, value);
    }

    private string _comparisonSummaryText = "Importe um JSON do receptor ou da página HTML de diagnóstico para comparar.";
    public string ComparisonSummaryText
    {
        get => _comparisonSummaryText;
        private set => SetField(ref _comparisonSummaryText, value);
    }

    private DeliveryVisualBadge _comparisonDeliveryBadge = DeliverySemantics.GetBadge(DeliverySemanticState.Unknown);
    public DeliveryVisualBadge ComparisonDeliveryBadge
    {
        get => _comparisonDeliveryBadge;
        private set => SetField(ref _comparisonDeliveryBadge, value);
    }

    public void LoadLatestSessionFromDirectory(string? diagnosticsDirectory = null)
    {
        string dir = diagnosticsDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VmTyper",
            "diagnostics");

        TimelineEvents.Clear();
        if (!Directory.Exists(dir))
        {
            SessionHeaderSummary = "Diretório de diagnóstico ainda não possui sessões.";
            SessionTotalsSummary = "Eventos: 0 · Enviadas: 0 · Puladas: 0 · Falhas: 0";
            return;
        }

        var latestFile = new DirectoryInfo(dir)
            .GetFiles("session-*.jsonl")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();

        if (latestFile is null)
        {
            SessionHeaderSummary = "Nenhuma sessão JSONL encontrada.";
            SessionTotalsSummary = "Eventos: 0 · Enviadas: 0 · Puladas: 0 · Falhas: 0";
            return;
        }

        LoadSessionJsonlFile(latestFile.FullName);
    }

    public void LoadSessionJsonlFile(string jsonlPath)
    {
        TimelineEvents.Clear();
        if (!File.Exists(jsonlPath))
        {
            SessionHeaderSummary = $"Arquivo não encontrado: {jsonlPath}";
            return;
        }

        DiagnosticHeaderRecord? header = null;
        foreach (string line in File.ReadLines(jsonlPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("type", out var tp) && tp.GetString() == "header")
                {
                    header = JsonSerializer.Deserialize<DiagnosticHeaderRecord>(line);
                    break;
                }
            }
            catch { }
        }

        var events = TypingDiagnosticsEngine.LoadEventsFromJsonl(jsonlPath);
        if (header is not null)
        {
            SessionHeaderSummary =
                $"Sessão {header.SessionId} | Perfil: {header.Profile} | Método: {header.Method} | " +
                $"Layout: {header.Layout} | Timer: {header.TimerResolutionMs} ms";
        }
        else
        {
            SessionHeaderSummary = $"Arquivo carregado: {Path.GetFileName(jsonlPath)}";
        }

        int sent = 0, skipped = 0, failed = 0;
        for (int i = 0; i < events.Count; i++)
        {
            var ev = events[i];
            bool allReturnedOne = ev.ReturnValues.Count > 0 && ev.ReturnValues.All(r => r == 1) && ev.NativeError == 0;
            UnitOutcome status = ev.ReturnValues.Count == 0 && ev.NativeError == 0
                ? UnitOutcome.Skipped
                : (allReturnedOne ? UnitOutcome.Sent : UnitOutcome.Failed);

            if (status == UnitOutcome.Sent) sent++;
            else if (status == UnitOutcome.Skipped) skipped++;
            else failed++;

            double holdObtainedMs = Math.Max(0, (ev.TimestampUpUs - ev.TimestampDownUs) / 1000.0);
            double gapObtainedMs = i + 1 < events.Count
                ? Math.Max(0, (events[i + 1].TimestampDownUs - ev.TimestampUpUs) / 1000.0)
                : ev.GapNextMs;

            // Eventos locais de envio nunca produzem "Confirmado" sem receptor (§4).
            var semanticState = DeliverySemantics.FromLocalSendResult(status);
            var badge = DeliverySemantics.GetBadge(semanticState);

            TimelineEvents.Add(new DiagnosticTimelineRow(
                Index: ev.Index,
                Category: ev.Category,
                Method: ev.EncodingMethod,
                GapComparison: $"{ev.GapNextMs:0.0} ms → {gapObtainedMs:0.0} ms",
                HoldComparison: $"Hold → {holdObtainedMs:0.1} ms",
                Result: status.ToString(),
                Badge: badge));
        }

        SessionTotalsSummary = $"Eventos: {events.Count} · Enviadas: {sent} · Puladas: {skipped} · Falhas: {failed}";
    }

    /// <summary>
    /// Importa o JSON exportado pelo receptor (<c>VmTyper.TestReceiver</c>) ou pela página HTML
    /// (<c>window.__vmtyperDiag.getReport()</c>) e executa o <see cref="DivergenceClassifier"/>.
    /// </summary>
    public DivergenceReport ImportAndCompareObserverJson(string jsonContent, string? expectedTextOverride = null)
    {
        using var doc = JsonDocument.Parse(jsonContent);
        var root = doc.RootElement;

        string actualText = string.Empty;
        if (root.TryGetProperty("text", out var tProp) && tProp.ValueKind == JsonValueKind.String)
        {
            actualText = tProp.GetString() ?? string.Empty;
        }
        else if (root.TryGetProperty("ReceivedText", out var rtProp) && rtProp.ValueKind == JsonValueKind.String)
        {
            actualText = rtProp.GetString() ?? string.Empty;
        }

        string expected = expectedTextOverride ?? ComparisonExpectedText;
        if (string.IsNullOrEmpty(expected) &&
            root.TryGetProperty("ExpectedText", out var expProp) &&
            expProp.ValueKind == JsonValueKind.String)
        {
            expected = expProp.GetString() ?? string.Empty;
        }

        int suspiciousCount = 0;
        if (root.TryGetProperty("suspiciousCount", out var susProp) && susProp.ValueKind == JsonValueKind.Number)
        {
            suspiciousCount = susProp.GetInt32();
        }
        else if (root.TryGetProperty("RepeatedDownWithoutUpCount", out var repProp) && repProp.ValueKind == JsonValueKind.Number)
        {
            suspiciousCount = repProp.GetInt32();
        }

        var report = DivergenceClassifier.Classify(expected, actualText);
        var semantic = DeliverySemantics.FromReceiverComparison(report, suspiciousCount);
        ComparisonDeliveryBadge = DeliverySemantics.GetBadge(semantic);

        ComparisonItems.Clear();
        foreach (var item in report.Items)
        {
            ComparisonItems.Add(item);
        }

        ComparisonSummaryText =
            $"Estado: {ComparisonDeliveryBadge.FullLabel} | Divergências totais: {report.TotalDivergences} " +
            $"(Duplicações: {report.Duplications}, Omissões: {report.Omissions}, Substituições: {report.Substitutions}, " +
            $"Acentuação: {report.AccentuationFailures}, Transposições: {report.Transpositions}, Indentação: {report.IndentationDivergences}) | " +
            $"Suspeitos no receptor: {suspiciousCount}";

        return report;
    }

    public void LoadBenchmarksFromDirectory(string? reportsDataDir = null)
    {
        BenchmarkRows.Clear();
        string dir = reportsDataDir ?? FindDefaultF3DataDir();
        if (!Directory.Exists(dir)) return;

        string f3MatrixPath = Path.Combine(dir, "f3-matriz-local-corpora-metodos-perfis.json");
        if (File.Exists(f3MatrixPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(f3MatrixPath, Encoding.UTF8));
                if (doc.RootElement.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
                {
                    foreach (var e in entries.EnumerateArray())
                    {
                        string corpus = e.TryGetProperty("corpus", out var c) ? (c.GetString() ?? "") : "";
                        string method = e.TryGetProperty("method", out var m) ? (m.GetString() ?? "") : "";
                        string profile = e.TryGetProperty("profile", out var p) ? (p.GetString() ?? "") : "";
                        int len = e.TryGetProperty("expectedGraphemes", out var eg) ? eg.GetInt32() : 0;
                        int actLen = e.TryGetProperty("actualGraphemes", out var ag) ? ag.GetInt32() : 0;
                        string sha = e.TryGetProperty("expectedSha256Hex12", out var s) ? (s.GetString() ?? "") : "";
                        int div = e.TryGetProperty("totalDivergences", out var td) ? td.GetInt32() : 0;
                        var state = div == 0 ? DeliverySemanticState.Confirmed : DeliverySemanticState.Failed;
                        BenchmarkRows.Add(new BenchmarkEntryRow("F3 Local", corpus, method, profile, len, actLen, sha, div, DeliverySemantics.GetBadge(state)));
                    }
                }
            }
            catch { }
        }

        string f4ComparePath = Path.Combine(dir, "f4-webview2-comparison.json");
        if (File.Exists(f4ComparePath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(f4ComparePath, Encoding.UTF8));
                if (doc.RootElement.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
                {
                    foreach (var e in entries.EnumerateArray())
                    {
                        string corpus = e.TryGetProperty("Corpus", out var c) ? (c.GetString() ?? "") : "";
                        string method = e.TryGetProperty("Method", out var m) ? (m.GetString() ?? "") : "";
                        bool fg = !e.TryGetProperty("OsForegroundAvailable", out var fgp) || fgp.GetBoolean();
                        int len = e.TryGetProperty("ExpectedLength", out var el) ? el.GetInt32() : 0;
                        int actLen = e.TryGetProperty("ActualLength", out var al) ? al.GetInt32() : 0;
                        string sha = e.TryGetProperty("ActualSha12", out var s) ? (s.GetString() ?? "") : "";
                        int sus = e.TryGetProperty("SuspiciousCount", out var sp) ? sp.GetInt32() : 0;
                        int div = 0;
                        if (e.TryGetProperty("DivergenceReport", out var dr) && dr.TryGetProperty("TotalDivergences", out var td))
                        {
                            div = td.GetInt32();
                        }

                        DeliverySemanticState state = !fg
                            ? DeliverySemanticState.Unknown
                            : (div > 0 ? DeliverySemanticState.Failed : (sus > 0 ? DeliverySemanticState.Suspicious : DeliverySemanticState.Confirmed));

                        BenchmarkRows.Add(new BenchmarkEntryRow("F4 WebView2", corpus, method, "Legacy(0,0)", len, actLen, sha, div, DeliverySemantics.GetBadge(state)));
                    }
                }
            }
            catch { }
        }
    }

    // ===============================================================
    // Calibração VM (Barra e Acentuação — §Tarefas 1, 3 e 4)
    // ===============================================================

    public IReadOnlyList<string> AvailableSymbols => VmSlashAndAccentCandidates.RecommendedSymbols;

    public ObservableCollection<CandidateOption> AvailableCandidates { get; } = new();
    public ObservableCollection<KeySequenceStep> CalibrationSequenceSteps { get; } = new();

    private string _selectedCalibrationSymbol = "/";
    public string SelectedCalibrationSymbol
    {
        get => _selectedCalibrationSymbol;
        set
        {
            if (SetField(ref _selectedCalibrationSymbol, value))
            {
                UpdateCandidatesForSymbol(value);
            }
        }
    }

    private CandidateOption? _selectedCandidate;
    public CandidateOption? SelectedCandidate
    {
        get => _selectedCandidate;
        set
        {
            if (SetField(ref _selectedCandidate, value))
            {
                UpdateStepsForCandidate(value);
            }
        }
    }

    private string _calibrationObservedResult = string.Empty;
    public string CalibrationObservedResult
    {
        get => _calibrationObservedResult;
        set => SetField(ref _calibrationObservedResult, value);
    }

    private string _calibrationStatusMessage = "Selecione o caractere e o candidato físico, envie para a VM e informe o resultado.";
    public string CalibrationStatusMessage
    {
        get => _calibrationStatusMessage;
        set => SetField(ref _calibrationStatusMessage, value);
    }

    private DeliveryVisualBadge _calibrationDeliveryBadge = DeliverySemantics.GetBadge(DeliverySemanticState.Unknown);
    public DeliveryVisualBadge CalibrationDeliveryBadge
    {
        get => _calibrationDeliveryBadge;
        set => SetField(ref _calibrationDeliveryBadge, value);
    }

    public void InitializeCalibration()
    {
        UpdateCandidatesForSymbol(SelectedCalibrationSymbol);
    }

    public void UpdateCandidatesForSymbol(string symbol)
    {
        AvailableCandidates.Clear();
        var list = VmSlashAndAccentCandidates.GetCandidatesForSymbol(symbol);
        foreach (var c in list)
        {
            AvailableCandidates.Add(c);
        }
        SelectedCandidate = AvailableCandidates.FirstOrDefault();
        CalibrationDeliveryBadge = DeliverySemantics.GetBadge(DeliverySemanticState.Unknown);
        CalibrationObservedResult = string.Empty;
        CalibrationStatusMessage = $"Caractere '{symbol}': {AvailableCandidates.Count} candidato(s) carregado(s).";
    }

    private void UpdateStepsForCandidate(CandidateOption? candidate)
    {
        CalibrationSequenceSteps.Clear();
        if (candidate is null) return;

        if (candidate.SubstitutionTemplate.Mode == SubstitutionMode.Sequencia &&
            candidate.SubstitutionTemplate.SequenceSteps != null)
        {
            foreach (var step in candidate.SubstitutionTemplate.SequenceSteps)
            {
                CalibrationSequenceSteps.Add(step);
            }
        }
        else if (candidate.SubstitutionTemplate.Mode == SubstitutionMode.Combinacao)
        {
            CalibrationSequenceSteps.Add(new KeySequenceStep
            {
                VirtualKey = (ushort)candidate.SubstitutionTemplate.ComboVirtualKey,
                Shift = candidate.SubstitutionTemplate.ComboShift,
                Ctrl = candidate.SubstitutionTemplate.ComboCtrl,
                Alt = candidate.SubstitutionTemplate.ComboAlt,
                Action = KeySequenceAction.Press,
                Label = candidate.SubstitutionTemplate.ComboLabel
            });
        }
    }

    public void SetCalibrationFeedback(DeliverySemanticState state, string feedbackDetails)
    {
        CalibrationDeliveryBadge = DeliverySemantics.GetBadge(state);
        CalibrationStatusMessage = feedbackDetails;
    }

    public AppSettings ApplyApprovedCalibration(AppSettings currentSettings)
    {
        if (SelectedCandidate is null) return currentSettings;

        var template = SelectedCandidate.SubstitutionTemplate;
        var ruleToSave = SettingsMapper.CloneSubstitution(template);
        ruleToSave.From = SelectedCalibrationSymbol;

        var list = new List<CharacterSubstitution>(currentSettings.CharacterSubstitutions);
        int existingIdx = list.FindIndex(s => string.Equals(s.From, SelectedCalibrationSymbol, StringComparison.Ordinal));
        if (existingIdx >= 0)
        {
            list[existingIdx] = ruleToSave;
        }
        else
        {
            list.Add(ruleToSave);
        }

        currentSettings.CharacterSubstitutions = list;
        return currentSettings;
    }

    private static string FindDefaultF3DataDir()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            string candidate = Path.Combine(dir, "docs", "vmtype_2", "relatorios", "F3-dados");
            if (Directory.Exists(candidate)) return candidate;
            dir = Directory.GetParent(dir)?.FullName;
        }
        return Path.Combine(Directory.GetCurrentDirectory(), "docs", "vmtype_2", "relatorios", "F3-dados");
    }
}
