using VmTyper.Models;
using VmTyper.Native;
using VmTyper.Services;
using VmTyper.Tests.Fakes;

namespace VmTyper.Tests;

public sealed class KeyboardInputServiceTests : IDisposable
{
    private readonly EngineHarness _h = new();

    public void Dispose() => _h.Dispose();

    private uint ScanOf(char c, FakeKeyboardLayout? layout = null)
    {
        var l = layout ?? _h.Layout;
        short r = l.VkKeyScan(c, IntPtr.Zero);
        return l.ScanByVk[(uint)(r & 0xFF)];
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
    // Defeitos corrigidos na F1 §2 (E, G, H1, H2, H3, H4, N1, N8) e §5 (N3)
    // ------------------------------------------------------------------

    [Fact]
    [Trait("Known", "E")]
    public async Task UnsupportedChar_IsNotCountedAsSent()
    {
        var progress = new SyncProgress<TypingProgress>();

        await _h.TypeAsync("a☃b", progress: progress);

        Assert.Equal(3, progress.Last.TotalUnits);
        Assert.Equal(2, progress.Last.Sent);
        Assert.Equal(1, progress.Last.Skipped);
        Assert.Equal(2, progress.Last.TypedCharacters);
    }

    [Fact]
    [Trait("Known", "E")]
    public async Task SurrogatePair_IsSingleUnit()
    {
        var progress = new SyncProgress<TypingProgress>();

        await _h.TypeAsync("a😀b", EngineHarness.Options(InputMethod.Unicode), progress);

        Assert.Equal(3, progress.Last.TotalCharacters);
        Assert.Equal(3, progress.Last.TypedCharacters);

        // No modo Unicode, o par substituto envia: down alto, down baixo, up baixo, up alto (§2.1).
        var emojiEvents = _h.Injector.Delivered.Skip(2).Take(4).ToList();
        Assert.Equal(4, emojiEvents.Count);
        Assert.False(emojiEvents[0].IsKeyUp);
        Assert.False(emojiEvents[1].IsKeyUp);
        Assert.True(emojiEvents[2].IsKeyUp);
        Assert.True(emojiEvents[3].IsKeyUp);
        Assert.Equal(emojiEvents[1].Scan, emojiEvents[2].Scan); // baixo sobe primeiro
        Assert.Equal(emojiEvents[0].Scan, emojiEvents[3].Scan); // alto sobe por último
    }

    [Fact]
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

    [Fact]
    [Trait("Known", "H1")]
    public async Task PartialSendInput_ReleasesMainKey()
    {
        // Chamada 1: Shift desce. Chamada 2: 'a' desce. Chamada 3 (subida de 'a') falha.
        _h.Injector.FailCall(3, accept: 0);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _h.TypeAsync("A"));

        Assert.Empty(_h.Analyze().StuckKeys);
    }

    [Fact]
    [Trait("Known", "H2")]
    public async Task ReleaseUsesSameEncodingAsPress()
    {
        // Chamada 1 entrega o Shift (por scan code); chamada 2 ('a' down) falha; a liberação vem depois.
        _h.Injector.FailCall(2, accept: 0);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _h.TypeAsync("A"));

        var a = _h.Analyze();
        Assert.Empty(a.StuckKeys);
        Assert.Empty(a.MismatchedReleases);
    }

    [Fact]
    [Trait("Known", "H3")]
    public async Task SuccessfulBlock_SendsNoOrphanKeyUps()
    {
        await _h.TypeAsync("ab");

        Assert.Empty(_h.Analyze().OrphanKeyUps);
    }

    [Fact]
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

    [Fact]
    [Trait("Known", "N3")]
    public async Task DiagnosticsAndMainSession_CannotRunConcurrently()
    {
        var options = EngineHarness.Options(charDelayMs: 1);
        var main = Task.Run(() => _h.TypeAsync("aaaaaa", options));
        var diagnostics = Task.Run(() => _h.TypeAsync("bbbbbb", options));

        try { await Task.WhenAll(main, diagnostics); }
        catch (InvalidOperationException) { /* rejeitar a segunda sessão também é correto */ }

        var downs = _h.Analyze().MainKeyDowns;
        int switches = downs.Zip(downs.Skip(1)).Count(p => p.First != p.Second);
        Assert.True(switches <= 1, $"Eventos das duas sessões intercalados ({switches} trocas): {string.Join(",", downs.Select(d => d.ToString("X2")))}");
    }

    // ------------------------------------------------------------------
    // Testes adicionais da F1 §2 (N1 ABNT2/US-Intl, N8, H, H4)
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("~a")]
    [InlineData("~ ")]
    [InlineData("^^")]
    public async Task Abnt2_DeadKeyLiterals_SendSpaceAfterEachDeadKey(string input)
    {
        using var h = new EngineHarness(FakeKeyboardLayout.Abnt2());
        await h.TypeAsync(input);

        var a = h.Analyze();
        Assert.Empty(a.StuckKeys);
        Assert.Empty(a.OrphanKeyUps);

        // Para "~a": til + espaço + a (3 teclas principais).
        // Para "~ ": til + espaço (do til literal) + espaço (do caractere espaço) = 3 teclas principais.
        // Para "^^": circunflexo + espaço + circunflexo + espaço = 4 teclas principais.
        int expectedMainDowns = input == "^^" ? 4 : 3;
        Assert.Equal(expectedMainDowns, a.MainKeyDowns.Count);
    }

    [Theory]
    [InlineData("'a'")]
    [InlineData("\"x\"")]
    public async Task UsInternational_QuoteAndDoubleQuoteLiterals_SendSpaceAfterDeadKey(string input)
    {
        var usIntl = FakeKeyboardLayout.UsInternational();
        using var h = new EngineHarness(usIntl);
        await h.TypeAsync(input);

        var a = h.Analyze();
        Assert.Empty(a.StuckKeys);
        Assert.Empty(a.OrphanKeyUps);
        // Aspa inicial (tecla morta + espaço) + letra + aspa final (tecla morta + espaço) = 5 teclas principais.
        Assert.Equal(5, a.MainKeyDowns.Count);
    }

    [Fact]
    [Trait("Known", "N8")]
    public async Task Substitutions_SinglePass_DoNotChain_AndDoNotCollideWithPua()
    {
        var subs = new[]
        {
            new CharacterSubstitution { From = "a", To = "b", Mode = SubstitutionMode.Texto },
            new CharacterSubstitution { From = "b", To = "c", Mode = SubstitutionMode.Texto },
        };

        // Passada única: "ab" -> 'a' vira 'b' (não encadeia para 'c'), 'b' vira 'c' -> resultado "bc".
        await _h.TypeAsync("ab", EngineHarness.Options(substitutions: subs));

        var downs = _h.Analyze().MainKeyDowns;
        Assert.Equal(new[] { ScanOf('b'), ScanOf('c') }, downs);
    }

    [Fact]
    public void Planner_DetectsDuplicateSubstitutionFroms()
    {
        var subs = new[]
        {
            new CharacterSubstitution { From = "/", To = "|" },
            new CharacterSubstitution { From = "/", To = "\\" }
        };

        var dupes = KeyStrokePlanner.FindDuplicateSubstitutionFroms(subs);
        Assert.Equal(new[] { "/" }, dupes);
    }

    [Fact]
    public async Task PartialFailure_AtEveryPositionOfCombo_LeavesNoOrphanDowns()
    {
        // 'Á' no ABNT2 exige 2 combos: (´) [down, up] + (Shift+a) [Shift down, a down, a up, Shift up] = 6 chamadas.
        for (int failAtCall = 1; failAtCall <= 6; failAtCall++)
        {
            using var h = new EngineHarness(FakeKeyboardLayout.Abnt2());
            h.Injector.FailCall(failAtCall, accept: 0);

            await Assert.ThrowsAsync<InvalidOperationException>(() => h.TypeAsync("Á"));

            var analysis = h.Analyze();
            Assert.Empty(analysis.StuckKeys);
            Assert.Empty(analysis.MismatchedReleases);
            Assert.Empty(analysis.OrphanKeyUps);
        }
    }

    [Fact]
    public async Task Cancellation_AtEveryPosition_LeavesNoOrphanDowns_AndReportsIndex()
    {
        const string text = "aÁb\nc";
        for (int cancelAfterUnits = 1; cancelAfterUnits <= 4; cancelAfterUnits++)
        {
            using var h = new EngineHarness(FakeKeyboardLayout.Abnt2());
            using var cts = new CancellationTokenSource();
            int seen = 0;
            var progress = new CallbackProgress<TypingProgress>(p =>
            {
                seen = p.Sent;
                if (p.Sent >= cancelAfterUnits) cts.Cancel();
            });

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                h.Engine.TypeTextAsync(text, EngineHarness.Options(), progress, Helpers.PauseToken.None, cts.Token));

            var analysis = h.Analyze();
            Assert.Empty(analysis.StuckKeys);
            Assert.Empty(analysis.OrphanKeyUps);
            Assert.Equal(cancelAfterUnits, seen);
        }
    }

    private sealed class CallbackProgress<T> : IProgress<T>
    {
        private readonly Action<T> _callback;
        public CallbackProgress(Action<T> callback) => _callback = callback;
        public void Report(T value) => _callback(value);
    }

    [Fact]
    public async Task PhysicalModifierHeld_RefusesToStartSession()
    {
        var previousTimeout = KeyStrokeExecutor.PhysicalModifierTimeout;
        KeyStrokeExecutor.PhysicalModifierTimeout = TimeSpan.FromMilliseconds(60);
        try
        {
            _h.Injector.SetPhysicalKeyDown(Win32.VK_LCONTROL, down: true);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _h.TypeAsync("abc"));
            Assert.Contains("Solte Ctrl/Shift/Alt antes de iniciar", ex.Message);
            Assert.Empty(_h.Injector.Delivered);
        }
        finally
        {
            KeyStrokeExecutor.PhysicalModifierTimeout = previousTimeout;
        }
    }
}
