using VmTyper.Models;

namespace VmTyper.Tests;

public sealed class LoggingTests : IDisposable
{
    private readonly EngineHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task NormalMode_LogDoesNotContainText()
    {
        await _h.TypeAsync("Zk");

        string log = _h.Dir.AllContent();
        Assert.DoesNotContain("'Z'", log);
        Assert.DoesNotContain("'k'", log);
    }

    [KnownDefectFact("N4")]
    [Trait("Known", "N4")]
    public async Task DiagnosticLog_DoesNotContainText()
    {
        var sub = new CharacterSubstitution { From = "k", To = "w" };

        await _h.TypeAsync("Zk", EngineHarness.Options(substitutions: new[] { sub }, diagnostic: true));

        string log = _h.Dir.AllContent();
        Assert.NotEmpty(log);
        foreach (var c in "Zkw")
        {
            Assert.DoesNotContain($"'{c}'", log);
        }
    }
}
