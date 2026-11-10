using System;
using System.Threading;
using System.Threading.Tasks;
using VmTyper.Helpers;
using VmTyper.Models;
using VmTyper.Services;
using VmTyper.Tests.Fakes;
using Xunit;

namespace VmTyper.Tests;

[Trait("Category", "Unit")]
public sealed class ResourceDisposalAndErrorHandlingTests
{
    [Fact]
    public async Task KeyStrokePlanner_UsInternational_DeadKeyPlusVowel_ProducesAccentWithoutSpace()
    {
        var usIntl = FakeKeyboardLayout.UsInternational();
        using var h = new EngineHarness(usIntl);
        await h.TypeAsync("á");

        var a = h.Analyze();
        Assert.Empty(a.StuckKeys);
        Assert.Empty(a.OrphanKeyUps);
        // Tecla morta (acento) + vogal = 2 KeyDowns principais
        Assert.Equal(2, a.MainKeyDowns.Count);
    }

    [Fact]
    public async Task KeyStrokePlanner_UsInternational_DeadKeyFollowedBySpace_ProducesLiteralAccent()
    {
        var usIntl = FakeKeyboardLayout.UsInternational();
        using var h = new EngineHarness(usIntl);
        await h.TypeAsync("' ");

        var a = h.Analyze();
        Assert.Empty(a.StuckKeys);
        Assert.Empty(a.OrphanKeyUps);
        // No US-Intl, apóstrofo isolado é tecla morta seguida de espaço
        Assert.True(a.MainKeyDowns.Count >= 2);
    }

    [Fact]
    public async Task KeyStrokeExecutor_ProgrammedFailure_ReportsFailureAndLeavesNoOrphanKeys()
    {
        using var h = new EngineHarness();
        // Falha programada no 2º envio nativo
        h.Injector.FailCall(2, accept: 0);

        var options = EngineHarness.Options();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Engine.TypeTextAsync("ABC", options, null, PauseToken.None, CancellationToken.None));

        var a = h.Analyze();
        // Não deve deixar teclas presas
        Assert.Empty(a.StuckKeys);
    }

    [Fact]
    public void TargetGuard_Dispose_StopsMonitoringAndCleansSubscription()
    {
        using var dir = new TempDir();
        var log = TestLog.Create(dir);
        var provider = new FakeForegroundWindowProvider();
        var guard = new TargetGuard(provider, log);

        var anchor = guard.TryCaptureAnchor(validateBrowser: false, ExpectedBrowser.Nenhum);
        Assert.True(anchor.IsValid);
        Assert.NotNull(guard.CurrentAnchor);

        // Dispose deve parar o monitoramento e limpar a âncora
        guard.Dispose();
        Assert.Null(guard.CurrentAnchor);
        Assert.False(guard.IsTargetLost);
    }

    [Fact]
    public void GlobalHotkeyService_Dispose_UnregistersAllHotkeysSafely()
    {
        using var service = new GlobalHotkeyService();
        // Descarte seguro mesmo sem ter registrado nada
        service.Dispose();
        Assert.False(service.IsRegistered(1));
    }

    [Fact]
    public void EmergencyReleaseTrackedKeys_ReleasesOnlyTrackedKeys()
    {
        using var h = new EngineHarness();
        h.Engine.EmergencyReleaseTrackedKeys();

        // Nenhuma tecla falsa solta às cegas
        Assert.Empty(h.Injector.Delivered);
    }
}
