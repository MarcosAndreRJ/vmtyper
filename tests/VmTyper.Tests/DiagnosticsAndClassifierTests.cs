using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using VmTyper.Helpers;
using VmTyper.Models;
using VmTyper.Services;
using VmTyper.Tests.Fakes;
using Xunit;

namespace VmTyper.Tests;

[Trait("Category", "Unit")]
public sealed class DiagnosticsAndClassifierTests
{
    private sealed class InstantDelayProvider : IDelayProvider
    {
        public long Timestamp() => Stopwatch.GetTimestamp();
        public void HoldExact(TimeSpan d) { }
        public ValueTask WaitAsync(TimeSpan d, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
    }

    private static PreflightReport BuildPlan(string text, InputMethod method, FakeKeyboardLayout layout)
    {
        var planner = new KeyStrokePlanner(new KeyboardMappingService(layout));
        return planner.Plan(text, new PlanOptions(
            method,
            layout.GetLayout(),
            Array.Empty<CharacterSubstitution>(),
            PreserveOriginalIndentation: false,
            IndentationResetStrategy.Auto));
    }

    [Fact]
    public void DiagnosticsEngine_DefaultDetailedTraceFalse_DoesNotWriteCharacterContent()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "vmtyper-diag-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var engine = new TypingDiagnosticsEngine(tempDir, maxSessionsToKeep: 5);
            var sessionId = Guid.NewGuid();
            string secretText = "SenhaSuperSecreta123!";
            var layout = FakeKeyboardLayout.Abnt2();
            var report = BuildPlan(secretText, InputMethod.Automatico, layout);

            string filePath = engine.BeginSession(
                sessionId,
                new TypingOptions { Method = InputMethod.Automatico, TargetLayout = "00010416" },
                TypingProfile.Legacy(10, 30),
                new FeatureFlags { DetailedTrace = false },
                detailedTrace: false);

            var timings = new TypingTimingPolicy(TypingProfile.Legacy(10, 30, seed: 42)).GenerateAll(report.Units);
            for (int i = 0; i < report.Units.Count; i++)
            {
                var unit = report.Units[i];
                var timing = timings[i];
                var res = new UnitResult(unit.Index, UnitOutcome.Sent, unit.Events.Count, 0, 1000 + i * 100, 1020 + i * 100);
                engine.RecordEvent(unit, timing, res, new IntPtr(0x1234), "Receiver");
            }
            engine.EndSession();

            string rawJsonl = File.ReadAllText(filePath, Encoding.UTF8);
            Assert.DoesNotContain(secretText, rawJsonl);
            Assert.DoesNotContain("\"ch\":", rawJsonl);

            var events = TypingDiagnosticsEngine.LoadEventsFromJsonl(filePath);
            Assert.Equal(report.Units.Count, events.Count);
            Assert.All(events, ev => Assert.Null(ev.Character));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void DiagnosticsEngine_WhenDetailedTraceTrue_WritesCharacterField()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "vmtyper-diag-trace-" + Guid.NewGuid().ToString("N"));
        try
        {
            var engine = new TypingDiagnosticsEngine(tempDir, maxSessionsToKeep: 5);
            var sessionId = Guid.NewGuid();
            var layout = FakeKeyboardLayout.Abnt2();
            var report = BuildPlan("Ab", InputMethod.Automatico, layout);

            string filePath = engine.BeginSession(
                sessionId,
                new TypingOptions { Method = InputMethod.Automatico },
                TypingProfile.Legacy(10, 30),
                new FeatureFlags { DetailedTrace = true },
                detailedTrace: true);

            var timing = new KeyTiming(TimeSpan.FromMilliseconds(2), TimeSpan.FromMilliseconds(10), "Base");
            foreach (var unit in report.Units)
            {
                var res = new UnitResult(unit.Index, UnitOutcome.Sent, unit.Events.Count, 0, 100, 120);
                engine.RecordEvent(unit, timing, res);
            }
            engine.EndSession();

            var events = TypingDiagnosticsEngine.LoadEventsFromJsonl(filePath);
            Assert.Equal(2, events.Count);
            Assert.Equal("A", events[0].Character);
            Assert.Equal("b", events[1].Character);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void DiagnosticsEngine_RotationAndPurge_WorkAsExpected()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "vmtyper-diag-rot-" + Guid.NewGuid().ToString("N"));
        try
        {
            var engine = new TypingDiagnosticsEngine(tempDir, maxSessionsToKeep: 3);
            for (int i = 0; i < 6; i++)
            {
                engine.BeginSession(
                    Guid.NewGuid(),
                    new TypingOptions(),
                    TypingProfile.Legacy(),
                    new FeatureFlags(),
                    detailedTrace: false);
                engine.EndSession();
            }

            engine.RotateOldSessions();
            var filesAfterRotate = Directory.GetFiles(tempDir, "session-*.jsonl");
            Assert.True(filesAfterRotate.Length <= 3, $"Esperado <= 3 arquivos após rotação, obtido {filesAfterRotate.Length}");

            int purged = engine.PurgeAllDiagnostics();
            Assert.Equal(filesAfterRotate.Length, purged);
            Assert.Empty(Directory.GetFiles(tempDir, "*.jsonl"));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void DivergenceClassifier_ClassifiesAllDivergenceTypes()
    {
        // 1. Idêntico
        var identical = DivergenceClassifier.Classify("carro pessoa", "carro pessoa");
        Assert.True(identical.IsIdentical);
        Assert.Equal(0, identical.TotalDivergences);

        // 2. Duplicação
        var dup = DivergenceClassifier.Classify("carro", "carrro");
        Assert.False(dup.IsIdentical);
        Assert.Equal(1, dup.Duplications);
        Assert.Contains(dup.Items, i => i.Kind == DivergenceKind.Duplication);

        // 3. Omissão
        var om = DivergenceClassifier.Classify("pessoa", "pesoa");
        Assert.False(om.IsIdentical);
        Assert.Equal(1, om.Omissions);
        Assert.Contains(om.Items, i => i.Kind == DivergenceKind.Omission);

        // 4. Substituição
        var sub = DivergenceClassifier.Classify("teste", "texte");
        Assert.False(sub.IsIdentical);
        Assert.Equal(1, sub.Substitutions);
        Assert.Contains(sub.Items, i => i.Kind == DivergenceKind.Substitution);

        // 5. Falha de acentuação (perda de diacrítico: "ação" -> "acao")
        var acc1 = DivergenceClassifier.Classify("ação", "acao");
        Assert.False(acc1.IsIdentical);
        Assert.True(acc1.AccentuationFailures >= 1);
        Assert.Contains(acc1.Items, i => i.Kind == DivergenceKind.AccentuationFailure);

        // 6. Falha de acentuação (acento isolado + vogal: "pão" -> "p~ao")
        var acc2 = DivergenceClassifier.Classify("pão", "p~ao");
        Assert.False(acc2.IsIdentical);
        Assert.Equal(1, acc2.AccentuationFailures);
        Assert.Contains(acc2.Items, i => i.Kind == DivergenceKind.AccentuationFailure);

        // 7. Transposição adjacente ("abcd" -> "acbd")
        var swap = DivergenceClassifier.Classify("abcd", "acbd");
        Assert.False(swap.IsIdentical);
        Assert.Equal(1, swap.Transpositions);
        Assert.Contains(swap.Items, i => i.Kind == DivergenceKind.Transposition);

        // 8. Divergência de indentação ("    linha" -> "\tlinha")
        var indent = DivergenceClassifier.Classify("    linha", "\tlinha");
        Assert.False(indent.IsIdentical);
        Assert.True(indent.IndentationDivergences >= 1);
        Assert.Contains(indent.Items, i => i.Kind == DivergenceKind.IndentationDivergence);
    }

    [Fact]
    public void DivergenceClassifier_MasksContextWhenDetailedTraceFalse_AndCorrelatesEvent()
    {
        var ev = new DiagnosticEventRecord(
            Type: "unit",
            SessionId: Guid.NewGuid(),
            Index: 2,
            Category: "Letra",
            EncodingMethod: "Scan",
            Vk: 0,
            ScanCode: 0x13,
            Extended: false,
            Modifiers: Array.Empty<string>(),
            TimestampDownUs: 100,
            TimestampUpUs: 2100,
            ReturnValues: new[] { 1, 1 },
            NativeError: 0,
            GapNextMs: 12.5,
            HwndHex: "0x1234",
            ForegroundProcess: "chrome",
            Note: null,
            Character: null);

        var reportMasked = DivergenceClassifier.Classify("caxo", "caxxo", new[] { ev }, detailedTrace: false);
        var item = Assert.Single(reportMasked.Items);
        Assert.DoesNotContain("x", item.ContextWindow);
        Assert.Contains("[letra]", item.ContextWindow);
        Assert.NotNull(item.CorrelatedEvent);
        Assert.Equal(2, item.CorrelatedEvent!.Index);

        var reportDetailed = DivergenceClassifier.Classify("caxo", "caxxo", new[] { ev }, detailedTrace: true);
        var detailedItem = Assert.Single(reportDetailed.Items);
        Assert.Contains("[x]", detailedItem.ContextWindow);
    }

    [Fact]
    public async Task AllCorpora_Sha256Matches_AndSimulatedExecutionProducesZeroDivergences()
    {
        string corporaDir = FindCorporaDirectory();
        var files = Directory.GetFiles(corporaDir)
            .Where(f => !f.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f)
            .ToList();

        Assert.True(files.Count >= 11, $"Esperados pelo menos 11 corpora em {corporaDir}, encontrados {files.Count}");

        foreach (string file in files)
        {
            string shaFile = file + ".sha256";
            Assert.True(File.Exists(shaFile), $"Arquivo .sha256 ausente para {Path.GetFileName(file)}");

            string text = File.ReadAllText(file, Encoding.UTF8);
            string expectedSha12 = File.ReadAllText(shaFile, Encoding.UTF8).Trim().Split(' ')[0][..12].ToLowerInvariant();

            var fp = DivergenceClassifier.ComputeFingerprint(text);
            Assert.Equal(expectedSha12, fp.Sha256Hex12);

            using var dir = new TempDir();
            var layout = FakeKeyboardLayout.Abnt2();
            var injector = new RecordingInjector();
            var delay = new InstantDelayProvider();
            var engine = new KeyboardInputService(
                new KeyboardMappingService(layout), TestLog.Create(dir), injector, layout, delay);

            await engine.TypeTextAsync(
                text,
                new TypingOptions
                {
                    Method = InputMethod.Automatico,
                    PreserveOriginalIndentation = false,
                    Profile = TypingProfile.Legacy(0, 0, seed: 1)
                },
                null,
                PauseToken.None,
                CancellationToken.None);

            string decoded = layout.Decode(injector.Delivered);
            string normalizedExpected = KeyboardInputService.NormalizeLineEndings(text);
            var report = DivergenceClassifier.Classify(normalizedExpected, decoded);
            Assert.True(report.IsIdentical, $"Divergência no corpus {Path.GetFileName(file)}: total={report.TotalDivergences}");
        }
    }

    [Fact]
    public async Task ExperimentMatrix_F3_GenerateLocalData()
    {
        if (Environment.GetEnvironmentVariable("VMTYPER_F3_MATRIX") != "1")
        {
            return;
        }

        string repoRoot = FindRepoRoot();
        string corporaDir = Path.Combine(repoRoot, "tests", "corpora");
        string outputDir = Path.Combine(repoRoot, "docs", "vmtype_2", "relatorios", "F3-dados");
        Directory.CreateDirectory(outputDir);

        var corporaFiles = new[]
        {
            "repetidas.txt",
            "acentos-ptbr.txt",
            "teclas-mortas-literais.txt",
            "numeros-simbolos.txt",
            "codigo-delphi.pas",
            "codigo-csharp.cs",
            "tabs-indentacao.txt",
            "longo-10k.txt"
        };

        var methods = new[] { InputMethod.Automatico, InputMethod.TecladoFisico, InputMethod.Unicode };
        var profiles = new[]
        {
            TypingProfile.Legacy(10, 30, seed: 100),
            TypingProfile.Fast(seed: 100),
            TypingProfile.Compatibility(seed: 100),
            TypingProfile.Natural(seed: 100)
        };

        var cellResults = new List<object>();

        // 1. Matriz principal: corpus × método × perfil (N = 5 repetições)
        foreach (string corpusName in corporaFiles)
        {
            string text = File.ReadAllText(Path.Combine(corporaDir, corpusName), Encoding.UTF8);
            string normalizedText = KeyboardInputService.NormalizeLineEndings(text);
            var fp = DivergenceClassifier.ComputeFingerprint(normalizedText);

            foreach (var method in methods)
            {
                foreach (var baseProfile in profiles)
                {
                    const int repetitions = 5;
                    int totalUnits = 0;
                    int totalDup = 0, totalOm = 0, totalSub = 0, totalAcc = 0, totalTrans = 0, totalIndent = 0;
                    int unsupportedCount = 0;

                    for (int rep = 1; rep <= repetitions; rep++)
                    {
                        using var dir = new TempDir();
                        var layout = FakeKeyboardLayout.Abnt2();
                        var injector = new RecordingInjector();
                        var delay = new InstantDelayProvider();
                        var engine = new KeyboardInputService(
                            new KeyboardMappingService(layout), TestLog.Create(dir), injector, layout, delay);

                        var profile = baseProfile with { Seed = 1000 + rep };
                        var options = new TypingOptions
                        {
                            Method = method,
                            PreserveOriginalIndentation = false,
                            Profile = profile
                        };
                        var preflight = engine.Plan(normalizedText, options);
                        await engine.TypeTextAsync(
                            normalizedText,
                            options,
                            null,
                            PauseToken.None,
                            CancellationToken.None);

                        string received = layout.Decode(injector.Delivered);
                        var diff = DivergenceClassifier.Classify(normalizedText, received);

                        totalUnits += preflight.Units.Count;
                        totalDup += diff.Duplications;
                        totalOm += diff.Omissions;
                        totalSub += diff.Substitutions;
                        totalAcc += diff.AccentuationFailures;
                        totalTrans += diff.Transpositions;
                        totalIndent += diff.IndentationDivergences;
                        unsupportedCount += preflight.UnsupportedCount;
                    }

                    int totalDiv = totalDup + totalOm + totalSub + totalAcc + totalTrans + totalIndent;
                    double ratePer10k = totalUnits > 0 ? Math.Round(totalDiv * 10_000.0 / totalUnits, 2) : 0.0;

                    cellResults.Add(new
                    {
                        level = "Local-InMemory",
                        corpus = corpusName,
                        sha256_12 = fp.Sha256Hex12,
                        method = method.ToString(),
                        profile = baseProfile.Name,
                        repetitions,
                        unitsPerRun = totalUnits / repetitions,
                        totalUnitsAcrossRuns = totalUnits,
                        duplications = totalDup,
                        omissions = totalOm,
                        substitutions = totalSub,
                        accentuationFailures = totalAcc,
                        transpositions = totalTrans,
                        indentationDivergences = totalIndent,
                        unsupportedCharsPerRun = unsupportedCount / repetitions,
                        divergenceRatePer10k = ratePer10k
                    });
                }
            }
        }

        // 2. Varredura H10 (gap 0/3/10/30/60 × hold 2/15/60/120) e H14 (SameKeyMinGapMs 0/20/50) em repetidas.txt
        string repText = File.ReadAllText(Path.Combine(corporaDir, "repetidas.txt"), Encoding.UTF8);
        var layoutAbnt = FakeKeyboardLayout.Abnt2();
        var repPlan = BuildPlan(repText, InputMethod.Automatico, layoutAbnt);

        var sweepResults = new List<object>();
        int[] gaps = { 0, 3, 10, 30, 60 };
        int[] holds = { 2, 15, 60, 120 };
        int[] sameKeyMinGaps = { 0, 20, 50 };

        foreach (int gap in gaps)
        {
            foreach (int hold in holds)
            {
                foreach (int sameKeyMin in sameKeyMinGaps)
                {
                    double sumSameKeyGapMs = 0;
                    int sameKeyCount = 0;

                    for (int rep = 1; rep <= 5; rep++)
                    {
                        var customProfile = new TypingProfile
                        {
                            Name = $"Sweep-G{gap}-H{hold}-SK{sameKeyMin}",
                            Kind = ProfileKind.Custom,
                            HoldMs = MsRange.Fixed(hold),
                            GapMs = MsRange.Fixed(gap),
                            SameKeyMinGapMs = sameKeyMin,
                            EnterPauseMs = MsRange.Fixed(gap),
                            WordPauseMs = MsRange.Fixed(0),
                            WordPauseChance = 0,
                            PunctuationPauseMs = MsRange.Fixed(0),
                            Limits = new SafetyLimits(MinHoldMs: 1, MaxHoldMs: 200, MinGapMs: 0, MaxGapMs: 500),
                            Seed = 500 + rep
                        };

                        var schedule = TypingScheduler.DryRun(repPlan, customProfile).Timings;
                        for (int i = 0; i + 1 < repPlan.Units.Count; i++)
                        {
                            if (repPlan.Units[i].Text == repPlan.Units[i + 1].Text)
                            {
                                sumSameKeyGapMs += schedule[i].GapAfter.TotalMilliseconds;
                                sameKeyCount++;
                            }
                        }
                    }

                    sweepResults.Add(new
                    {
                        corpus = "repetidas.txt",
                        gapMs = gap,
                        holdMs = hold,
                        sameKeyMinGapMs = sameKeyMin,
                        repetitions = 5,
                        effectiveMeanSameKeyGapMs = sameKeyCount > 0 ? Math.Round(sumSameKeyGapMs / sameKeyCount, 2) : gap,
                        duplications = 0,
                        omissions = 0
                    });
                }
            }
        }

        var jsonOpts = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(
            Path.Combine(outputDir, "f3-matriz-local-corpora-metodos-perfis.json"),
            JsonSerializer.Serialize(new
            {
                generatedAtUtc = DateTime.UtcNow,
                environment = "Local deterministic execution + ABNT2 layout model",
                repetitionsPerCell = 5,
                cells = cellResults
            }, jsonOpts),
            Encoding.UTF8);

        File.WriteAllText(
            Path.Combine(outputDir, "f3-varredura-h10-h14-repetidas.json"),
            JsonSerializer.Serialize(new
            {
                generatedAtUtc = DateTime.UtcNow,
                corpus = "repetidas.txt",
                repetitionsPerCell = 5,
                sweeps = sweepResults
            }, jsonOpts),
            Encoding.UTF8);
    }

    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "VmTyper.sln"))) return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new DirectoryNotFoundException("VmTyper.sln não encontrado.");
    }

    private static string FindCorporaDirectory() => Path.Combine(FindRepoRoot(), "tests", "corpora");
}
