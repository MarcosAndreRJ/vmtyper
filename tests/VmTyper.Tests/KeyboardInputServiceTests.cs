using VmTyper.Models;
using VmTyper.Tests.Fakes;

namespace VmTyper.Tests;

public sealed class KeyboardInputServiceTests : IDisposable
{
    private readonly EngineHarness _h = new();

    public void Dispose() => _h.Dispose();

    private uint ScanOf(char c)
    {
        short r = _h.Layout.VkKeyScan(c, IntPtr.Zero);
        return _h.Layout.ScanByVk[(uint)(r & 0xFF)];
    }

    // ------------------------------------------------------------------
    // Comportamento atual correto (linha de base, verdes)
    // ------------------------------------------------------------------

    [Fact]
    public async Task PhysicalMode_SendsOneScanKeyPerCharacter_InOrder()
    {
        await _h.TypeAsync("ab\n1");

        var a = _h.Analyze();
        Assert.Equal(new[] { ScanOf('a'), ScanOf('b'), 0x1Cu, ScanOf('1') }, a.MainKeyDowns);
        Assert.Empty(a.StuckKeys);
    }

    [Fact]
    public async Task UppercaseLetter_IsSentWithShiftAroundMainKey()
    {
        await _h.TypeAsync("A");

        var downs = _h.Injector.Delivered.Where(e => !e.IsKeyUp && e.IsScanCode).Select(e => (uint)e.Scan).ToList();
        Assert.Equal(new[] { (uint)FakeKeyboardLayout.ScanLShift, ScanOf('a') }, downs);
    }

    [Fact]
    public async Task KeyDownAndKeyUp_GoInSeparateSendInputCalls()
    {
        await _h.TypeAsync("a");

        var mainEvents = _h.Injector.Delivered.Where(e => e.IsScanCode && e.Scan == ScanOf('a')).ToList();
        Assert.Equal(2, mainEvents.Count);
        Assert.NotEqual(mainEvents[0].Call, mainEvents[1].Call);
    }

    [Fact]
    public async Task AccentedChar_IsSentAsDeadKeyThenBaseLetter()
    {
        await _h.TypeAsync("ã");

        Assert.Equal(new[] { ScanOf('~'), ScanOf('a') }, _h.Analyze().MainKeyDowns);
    }

    [Fact]
    public async Task CrLf_ProducesSingleEnter()
    {
        await _h.TypeAsync("a\r\nb");

        Assert.Equal(1, _h.Analyze().MainKeyDowns.Count(s => s == 0x1C));
    }

    [Fact]
    public async Task UnicodeMode_SendsUnicodeEvents()
    {
        await _h.TypeAsync("é", EngineHarness.Options(InputMethod.Unicode));

        var unicode = _h.Injector.Delivered.Where(e => e.IsUnicode).ToList();
        Assert.Equal(2, unicode.Count);
        Assert.All(unicode, e => Assert.Equal('é', (char)e.Scan));
    }

    [Fact]
    public async Task Cancellation_StopsBeforeNextCharacter()
    {
        using var cts = new CancellationTokenSource();
        var progress = new CancelAfterFirst(cts);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _h.Engine.TypeTextAsync("abc", EngineHarness.Options(), progress, Helpers.PauseToken.None, cts.Token));

        var a = _h.Analyze();
        Assert.Equal(new[] { ScanOf('a') }, a.MainKeyDowns);
        Assert.Empty(a.StuckKeys);
    }

    private sealed class CancelAfterFirst : IProgress<TypingProgress>
    {
        private readonly CancellationTokenSource _cts;
        public CancelAfterFirst(CancellationTokenSource cts) => _cts = cts;
        public void Report(TypingProgress value) => _cts.Cancel();
    }

    // ------------------------------------------------------------------
    // Defeitos conhecidos (comportamento correto esperado)
    // ------------------------------------------------------------------

    [KnownDefectFact("E")]
    [Trait("Known", "E")]
    public async Task UnsupportedChar_IsNotCountedAsSent()
    {
        var progress = new SyncProgress<TypingProgress>();

        await _h.TypeAsync("a☃b", progress: progress);

        Assert.Equal(2, progress.Last.TypedCharacters);
    }

    [KnownDefectFact("E")]
    [Trait("Known", "E")]
    public async Task SurrogatePair_IsSingleUnit()
    {
        var progress = new SyncProgress<TypingProgress>();

        await _h.TypeAsync("a😀b", EngineHarness.Options(InputMethod.Unicode), progress);

        Assert.Equal(3, progress.Last.TotalCharacters);
        Assert.Equal(3, progress.Last.TypedCharacters);
    }

    [KnownDefectFact("G")]
    [Trait("Known", "G")]
    public async Task DivideKey_IsSentWithExtendedFlag()
    {
        var sub = new CharacterSubstitution
        {
            From = "x",
            Mode = SubstitutionMode.Combinacao,
            ComboVirtualKey = 0x6F, // VK_DIVIDE
            ComboLabel = "/ (numpad)"
        };

        await _h.TypeAsync("x", EngineHarness.Options(substitutions: new[] { sub }));

        var down = Assert.Single(_h.Injector.Delivered, e => !e.IsKeyUp && e.IsScanCode);
        Assert.Equal(0x35, down.Scan);
        Assert.True(down.IsExtended, "VK_DIVIDE precisa de KEYEVENTF_EXTENDEDKEY; sem ela vira a tecla / (US) ou ; (ABNT2) do teclado principal.");
    }

    [KnownDefectFact("H1")]
    [Trait("Known", "H1")]
    public async Task PartialSendInput_ReleasesMainKey()
    {
        // Chamada 1: Shift+A descem. Chamada 2 (as subidas) falha por completo.
        _h.Injector.FailCall(2, accept: 0);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _h.TypeAsync("A"));

        Assert.Empty(_h.Analyze().StuckKeys);
    }

    [KnownDefectFact("H2")]
    [Trait("Known", "H2")]
    public async Task ReleaseUsesSameEncodingAsPress()
    {
        // Chamada 1 entrega só o Shift (por scan code); a liberação de emergência vem depois.
        _h.Injector.FailCall(1, accept: 1);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _h.TypeAsync("A"));

        var a = _h.Analyze();
        Assert.Empty(a.StuckKeys);
        Assert.Empty(a.MismatchedReleases);
    }

    [KnownDefectFact("H3")]
    [Trait("Known", "H3")]
    public async Task SuccessfulBlock_SendsNoOrphanKeyUps()
    {
        await _h.TypeAsync("ab");

        Assert.Empty(_h.Analyze().OrphanKeyUps);
    }

    [KnownDefectFact("N1")]
    [Trait("Known", "N1")]
    public async Task LiteralTilde_FollowedByVowel_DoesNotCombine()
    {
        await _h.TypeAsync("~a");

        // No ABNT2, '~' é tecla morta: sozinha ela se combina com o 'a' seguinte (→ 'ã').
        // O til literal exige tecla morta + Espaço antes da próxima tecla.
        var downs = _h.Analyze().MainKeyDowns;
        int dead = downs.IndexOf(ScanOf('~'));
        Assert.True(dead >= 0);
        Assert.True(dead + 1 < downs.Count && downs[dead + 1] == FakeKeyboardLayout.ScanSpace,
            $"Depois da tecla morta veio 0x{(dead + 1 < downs.Count ? downs[dead + 1] : 0):X2}, não Espaço.");
    }

    [KnownDefectFact("N3")]
    [Trait("Known", "N3")]
    public async Task DiagnosticsAndMainSession_CannotRunConcurrently()
    {
        // A sessão principal e a janela de diagnóstico usam a mesma instância do motor.
        var options = EngineHarness.Options(charDelayMs: 1);
        var main = Task.Run(() => _h.TypeAsync("aaaaaa", options));
        var diagnostics = Task.Run(() => _h.TypeAsync("bbbbbb", options));

        try { await Task.WhenAll(main, diagnostics); }
        catch (InvalidOperationException) { /* rejeitar a segunda sessão também é correto */ }

        var downs = _h.Analyze().MainKeyDowns;
        int switches = downs.Zip(downs.Skip(1)).Count(p => p.First != p.Second);
        Assert.True(switches <= 1, $"Eventos das duas sessões intercalados ({switches} trocas): {string.Join(",", downs.Select(d => d.ToString("X2")))}");
    }
}
