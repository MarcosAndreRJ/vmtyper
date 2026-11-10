using System.Diagnostics;
using VmTyper.Helpers;
using VmTyper.Models;
using VmTyper.Services;
using VmTyper.Tests.Fakes;
using Xunit;

namespace VmTyper.Tests;

[Trait("Category", "Unit")]
public sealed class TypingTimingTests
{
    private sealed class FakeDelayProvider : IDelayProvider
    {
        public List<TimeSpan> Holds { get; } = new();
        public List<TimeSpan> Waits { get; } = new();
        public Action? OnHold { get; set; }
        public Func<TimeSpan, CancellationToken, ValueTask>? OnWaitAsync { get; set; }

        public long Timestamp() => Stopwatch.GetTimestamp();

        public void HoldExact(TimeSpan d)
        {
            Holds.Add(d);
            OnHold?.Invoke();
        }

        public ValueTask WaitAsync(TimeSpan d, CancellationToken ct)
        {
            Waits.Add(d);
            if (OnWaitAsync is not null)
            {
                return OnWaitAsync(d, ct);
            }
            ct.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
    }

    private static PreflightReport BuildSamplePlan(string text)
    {
        var layout = FakeKeyboardLayout.Abnt2();
        var planner = new KeyStrokePlanner(new KeyboardMappingService(layout));
        return planner.Plan(text, new PlanOptions(
            InputMethod.TecladoFisico,
            layout.GetLayout(),
            Array.Empty<CharacterSubstitution>(),
            PreserveOriginalIndentation: false,
            IndentationResetStrategy.Auto));
    }

    [Fact]
    public void SameSeed_SameTimingSequence()
    {
        var plan = BuildSamplePlan("Olá, mundo! Este é um teste de reprodutibilidade.\n  if (x == 1) { y = 2; }");
        var p1 = TypingProfile.Natural(seed: 123456);
        var p2 = TypingProfile.Natural(seed: 123456);

        var seq1 = new TypingTimingPolicy(p1).GenerateAll(plan.Units);
        var seq2 = new TypingTimingPolicy(p2).GenerateAll(plan.Units);

        Assert.Equal(seq1.Count, seq2.Count);
        for (int i = 0; i < seq1.Count; i++)
        {
            Assert.Equal(seq1[i].Hold, seq2[i].Hold);
            Assert.Equal(seq1[i].GapAfter, seq2[i].GapAfter);
            Assert.Equal(seq1[i].Reason, seq2[i].Reason);
        }
    }

    [Fact]
    public void AllTimings_WithinSafetyLimits()
    {
        // Property-based: 10.000 unidades por perfil.
        var baseUnits = BuildSamplePlan("abbb, c! d? e;\n  for (int i = 0; i < 10; i++) { x = i; }\n").Units;
        var units10k = new List<InputUnit>(10_000);
        while (units10k.Count < 10_000)
        {
            foreach (var u in baseUnits)
            {
                if (units10k.Count >= 10_000) break;
                units10k.Add(u with { Index = units10k.Count });
            }
        }

        var profiles = new[]
        {
            TypingProfile.Legacy(2, 20, seed: 1),
            TypingProfile.Fast(seed: 2),
            TypingProfile.Compatibility(seed: 3),
            TypingProfile.Natural(seed: 4),
            new TypingProfile
            {
                Name = "Custom",
                Kind = ProfileKind.Custom,
                HoldMs = new MsRange(0, 500),
                GapMs = new MsRange(-10, 5000),
                WordPauseMs = new MsRange(0, 1000),
                WordPauseChance = 0.5,
                PunctuationPauseMs = new MsRange(0, 1000),
                EnterPauseMs = new MsRange(0, 3000),
                SpeedVariationPct = 0.5,
                Correlation = 0.9,
                SameKeyMinGapMs = 100,
                Limits = new SafetyLimits(MinHoldMs: 2, MaxHoldMs: 55, MinGapMs: 1, MaxGapMs: 1500),
                Seed = 5
            }
        };

        var contexts = TypingTimingPolicy.BuildContexts(units10k);
        foreach (var profile in profiles)
        {
            var policy = new TypingTimingPolicy(profile);
            var limits = profile.Limits;
            for (int i = 0; i < contexts.Count; i++)
            {
                var t = policy.Next(contexts[i]);
                Assert.InRange(t.Hold.TotalMilliseconds, limits.MinHoldMs, limits.MaxHoldMs);
                Assert.InRange(t.GapAfter.TotalMilliseconds, limits.MinGapMs, limits.MaxGapMs);
            }
        }
    }

    [Fact]
    public async Task Plan_IsIdentical_AcrossProfiles()
    {
        const string corpus = "Teste de plano: áàãâéê, 123! ~a ^^;\n\tfim";
        var profiles = new[]
        {
            TypingProfile.Legacy(0, 0, seed: 10),
            TypingProfile.Fast(seed: 10),
            TypingProfile.Compatibility(seed: 10),
            TypingProfile.Natural(seed: 10)
        };

        List<(ushort Vk, ushort Scan, uint Flags)>? baselineEvents = null;

        foreach (var profile in profiles)
        {
            using var dir = new TempDir();
            var layout = FakeKeyboardLayout.Abnt2();
            var injector = new RecordingInjector();
            var fakeDelay = new FakeDelayProvider();
            var log = TestLog.Create(dir);
            var engine = new KeyboardInputService(
                new KeyboardMappingService(layout), log, injector, layout, fakeDelay);

            var options = new TypingOptions
            {
                Method = InputMethod.TecladoFisico,
                Profile = profile
            };

            await engine.TypeTextAsync(corpus, options, null, PauseToken.None, CancellationToken.None);

            var eventShape = injector.Delivered.Select(e => (e.Vk, e.Scan, e.Flags)).ToList();
            if (baselineEvents is null)
            {
                baselineEvents = eventShape;
            }
            else
            {
                Assert.Equal(baselineEvents, eventShape);
            }
        }
    }

    [Theory]
    [InlineData(ProfileKind.Legacy)]
    [InlineData(ProfileKind.Fast)]
    [InlineData(ProfileKind.Compatibility)]
    [InlineData(ProfileKind.Natural)]
    public async Task Simulated_Receiver_Reconstructs_OriginalText(ProfileKind kind)
    {
        string corpusPath = System.IO.Path.Combine(AppContext.BaseDirectory, "corpora", "basico.txt");
        string corpus = KeyboardInputService.NormalizeLineEndings(await System.IO.File.ReadAllTextAsync(corpusPath)).TrimEnd('\n');

        var profile = kind switch
        {
            ProfileKind.Fast => TypingProfile.Fast(seed: 77),
            ProfileKind.Compatibility => TypingProfile.Compatibility(seed: 77),
            ProfileKind.Natural => TypingProfile.Natural(seed: 77),
            _ => TypingProfile.Legacy(0, 0, seed: 77)
        };

        using var dir = new TempDir();
        var layout = FakeKeyboardLayout.Abnt2();
        var injector = new RecordingInjector();
        var fakeDelay = new FakeDelayProvider();
        var log = TestLog.Create(dir);
        var engine = new KeyboardInputService(
            new KeyboardMappingService(layout), log, injector, layout, fakeDelay);

        await engine.TypeTextAsync(
            corpus,
            new TypingOptions { Method = InputMethod.TecladoFisico, Profile = profile },
            null,
            PauseToken.None,
            CancellationToken.None);

        var analysis = new KeyStateAnalysis(injector.Delivered, layout);
        Assert.Empty(analysis.StuckKeys);
        Assert.Empty(analysis.OrphanKeyUps);
        Assert.Equal(corpus, layout.Decode(injector.Delivered));
    }

    [Fact]
    public void Natural_IsNotIid()
    {
        // Sem pausas de pontuação/palavra/mesma tecla, o gap reflete a deriva correlacionada AR(1) (φ = 0.75).
        var profile = new TypingProfile
        {
            Name = "CorrelatedTest",
            Kind = ProfileKind.Natural,
            HoldMs = MsRange.Fixed(20),
            GapMs = MsRange.Fixed(100),
            WordPauseChance = 0.0,
            PunctuationPauseMs = MsRange.Fixed(0),
            EnterPauseMs = MsRange.Fixed(0),
            SpeedVariationPct = 0.25,
            Correlation = 0.80,
            SameKeyMinGapMs = 0,
            CodeLineMultiplier = 1.0,
            Seed = 999
        };

        // Sequência de letras distintas para isolar a deriva de tempo.
        string text = string.Concat(Enumerable.Repeat("abcdefghi", 300));
        var plan = BuildSamplePlan(text);
        var timings = new TypingTimingPolicy(profile).GenerateAll(plan.Units);
        double[] gaps = timings.Select(t => t.GapAfter.TotalMilliseconds).ToArray();

        double mean = gaps.Average();
        double num = 0.0;
        double den = 0.0;
        for (int i = 0; i < gaps.Length; i++)
        {
            double d = gaps[i] - mean;
            den += d * d;
            if (i > 0)
            {
                num += d * (gaps[i - 1] - mean);
            }
        }

        double lag1Autocorrelation = den > 0 ? num / den : 0.0;
        Assert.True(lag1Autocorrelation > 0.35, $"Autocorrelação lag-1 esperada > 0.35, obtida: {lag1Autocorrelation:0.000}");
    }

    [Fact]
    public void PunctuationAndEnter_GetLongerGaps()
    {
        var profile = TypingProfile.Natural(seed: 42);
        var plan = BuildSamplePlan("abcdef.ghijkl\nmnopqr");
        var timings = new TypingTimingPolicy(profile).GenerateAll(plan.Units);

        var dotTiming = timings[6];   // '.'
        var enterTiming = timings[13]; // '\n'
        var letterGaps = timings
            .Where((t, idx) => idx != 6 && idx != 13)
            .Select(t => t.GapAfter.TotalMilliseconds)
            .ToList();

        double maxLetterGap = letterGaps.Average();
        Assert.Contains("Punctuation", dotTiming.Reason);
        Assert.Contains("Enter", enterTiming.Reason);
        Assert.True(dotTiming.GapAfter.TotalMilliseconds > maxLetterGap);
        Assert.True(enterTiming.GapAfter.TotalMilliseconds > dotTiming.GapAfter.TotalMilliseconds);
    }

    [Fact]
    public async Task Cancel_DuringGap_IsImmediate()
    {
        using var dir = new TempDir();
        var layout = FakeKeyboardLayout.Abnt2();
        var injector = new RecordingInjector();
        using var cts = new CancellationTokenSource();

        var fakeDelay = new FakeDelayProvider
        {
            OnWaitAsync = (_, ct) =>
            {
                // Cancela durante o primeiro gap entre 'a' e 'b'.
                cts.Cancel();
                ct.ThrowIfCancellationRequested();
                return ValueTask.CompletedTask;
            }
        };

        var engine = new KeyboardInputService(
            new KeyboardMappingService(layout), TestLog.Create(dir), injector, layout, fakeDelay);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            engine.TypeTextAsync("abc", new TypingOptions { CharDelayMs = 50 }, null, PauseToken.None, cts.Token));

        var analysis = new KeyStateAnalysis(injector.Delivered, layout);
        // Apenas 'a' foi enviado, e nenhuma tecla ficou presa.
        Assert.Single(analysis.MainKeyDowns);
        Assert.Empty(analysis.StuckKeys);
        Assert.Empty(analysis.OrphanKeyUps);
    }

    [Fact]
    public async Task Cancel_DuringHold_WaitsForKeyUp()
    {
        using var dir = new TempDir();
        var layout = FakeKeyboardLayout.Abnt2();
        var injector = new RecordingInjector();
        using var cts = new CancellationTokenSource();

        var fakeDelay = new FakeDelayProvider
        {
            OnHold = () =>
            {
                // O usuário pressiona ESC exatamente enquanto a tecla 'a' está no meio do Hold (KeyDown enviado, KeyUp pendente).
                cts.Cancel();
            }
        };

        var engine = new KeyboardInputService(
            new KeyboardMappingService(layout), TestLog.Create(dir), injector, layout, fakeDelay);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            engine.TypeTextAsync("ab", new TypingOptions { CharDelayMs = 20 }, null, PauseToken.None, cts.Token));

        var analysis = new KeyStateAnalysis(injector.Delivered, layout);
        // A unidade 'a' completou o KeyUp antes de o cancelamento interromper no gap.
        Assert.Single(analysis.MainKeyDowns);
        Assert.Empty(analysis.StuckKeys);
        Assert.Empty(analysis.OrphanKeyUps);
        Assert.Equal("a", layout.Decode(injector.Delivered));
    }

    [Fact]
    public async Task Legacy_Matches_V1_EventSequence()
    {
        const string text = "Ab1\nc";
        using var h = new EngineHarness(FakeKeyboardLayout.Abnt2());
        await h.TypeAsync(text, EngineHarness.Options(InputMethod.TecladoFisico));

        var fakeDelay = new FakeDelayProvider();
        using var dir = new TempDir();
        var layout = FakeKeyboardLayout.Abnt2();
        var injector2 = new RecordingInjector();
        var engine2 = new KeyboardInputService(
            new KeyboardMappingService(layout), TestLog.Create(dir), injector2, layout, fakeDelay);

        await engine2.TypeTextAsync(
            text,
            new TypingOptions
            {
                Method = InputMethod.TecladoFisico,
                CharDelayMs = 5,
                EnterDelayMs = 25,
                Profile = TypingProfile.Legacy(5, 25)
            },
            null,
            PauseToken.None,
            CancellationToken.None);

        var seq1 = h.Injector.Delivered.Select(e => (e.Vk, e.Scan, e.Flags)).ToList();
        var seq2 = injector2.Delivered.Select(e => (e.Vk, e.Scan, e.Flags)).ToList();
        Assert.Equal(seq1, seq2);

        // E os atrasos solicitados no perfil Legacy são exatamente 5 ms para caracteres e 25 ms para Enter.
        Assert.Equal(
            new[] { 5.0, 5.0, 5.0, 25.0, 5.0 },
            fakeDelay.Waits.Select(w => w.TotalMilliseconds).ToArray());
    }
}
