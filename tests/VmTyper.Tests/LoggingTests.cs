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

    [Fact]
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

    [Fact]
    public async Task Log_NeverContainsTypedChar()
    {
        const string sentinel = "SentinelaSecreta_QX97!";
        var sub = new CharacterSubstitution { From = "Q", To = "W" };

        await _h.TypeAsync(sentinel, EngineHarness.Options(substitutions: new[] { sub }, diagnostic: true));

        string log = _h.Dir.AllContent();
        Assert.NotEmpty(log);
        Assert.DoesNotContain(sentinel, log);
        Assert.DoesNotContain("'Q'", log);
        Assert.DoesNotContain("'W'", log);
        Assert.DoesNotContain("'X'", log);
        Assert.Contains("1 substituições aplicadas (regras #1)", log);
    }
}
