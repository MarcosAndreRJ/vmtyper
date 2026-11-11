using VmTyper.Models;
using VmTyper.Services;
using VmTyper.Tests.Fakes;
using VmTyper.ViewModels;
using Xunit;

namespace VmTyper.Tests;

[Trait("Category", "Unit")]
public sealed class TypingHighlightTests
{
    private static PreflightReport Plan(string text, params CharacterSubstitution[] subs)
    {
        var layout = FakeKeyboardLayout.Abnt2();
        var planner = new KeyStrokePlanner(new KeyboardMappingService(layout));
        return planner.Plan(text, new PlanOptions(
            InputMethod.TecladoFisico, layout.GetLayout(), subs));
    }

    [Fact]
    public void Planner_RecordsSourceRangeOfEachUnit_InNormalizedText()
    {
        var report = Plan("ab\r\ncd");

        // Normalizado: "ab\ncd"
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, report.Units.Select(u => u.SourceStart));
        Assert.All(report.Units, u => Assert.Equal(1, u.SourceLength));
    }

    [Fact]
    public void Planner_UnitsOfATextSubstitution_ShareTheSourceRangeOfTheOriginal()
    {
        var report = Plan("x->y", new CharacterSubstitution { From = "->", To = "=>" });

        // 'x' (0), "=>" gera 2 unidades com origem 1..3, 'y' (3)
        Assert.Equal(new[] { 0, 1, 1, 3 }, report.Units.Select(u => u.SourceStart));
        Assert.Equal(new[] { 1, 2, 2, 1 }, report.Units.Select(u => u.SourceLength));
    }

    [Fact]
    public void NormalizedToRawMap_SkipsTheCarriageReturnOfCrLf()
    {
        var map = MainViewModel.BuildNormalizedToRawMap("ab\r\ncd");

        // normalizado "ab\ncd": a=0 b=1 \n=3 (o \r anterior é descartado) c=4 d=5, fim=6
        Assert.Equal(new[] { 0, 1, 3, 4, 5, 6 }, map);
    }

    [Theory]
    [InlineData("begin Foo_1 := 2;", 8, 6, 5)]  // dentro da palavra Foo_1 (começa em 6)
    [InlineData("begin Foo_1 := 2;", 0, 0, 5)]  // início da palavra
    [InlineData("a := b;", 2, 2, 1)]            // símbolo isolado: o próprio caractere
    [InlineData("a b", 1, -1, 0)]               // espaço: sem destaque
    public void ExpandToWord_ReturnsTheWordAroundThePosition(string text, int index, int expectedStart, int expectedLength)
    {
        var (start, length) = MainViewModel.ExpandToWord(text, index, 1);
        Assert.Equal(expectedStart, start);
        Assert.Equal(expectedLength, length);
    }
}
