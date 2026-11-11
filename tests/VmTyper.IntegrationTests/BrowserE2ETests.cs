using System;
using System.IO;
using Xunit;
using Xunit.Abstractions;

namespace VmTyper.IntegrationTests;

public sealed class BrowserE2EFactAttribute : FactAttribute
{
    public const string Variable = "VMTYPER_E2E_BROWSER";

    public BrowserE2EFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(Variable) != "1")
        {
            Skip = $"E2E com navegador integrado exige WebView2 e desktop interativo; defina {Variable}=1.";
        }
    }
}

[Trait("Category", "E2E-Browser")]
public sealed class BrowserE2ETests
{
    private readonly ITestOutputHelper _output;

    public BrowserE2ETests(ITestOutputHelper output) => _output = output;

    [BrowserE2EFact]
    public void DiagnosticPage_HtmlAssetExists_AndContainsExpectedKeyEventsProtocol()
    {
        string htmlPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tools", "diagnostics-page", "index.html"));
        Assert.True(File.Exists(htmlPath), $"Página de diagnóstico não encontrada em: {htmlPath}");

        string content = File.ReadAllText(htmlPath);
        Assert.Contains("keydown", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("keyup", content, StringComparison.OrdinalIgnoreCase);
        _output.WriteLine($"Página de diagnóstico validada com sucesso ({content.Length} bytes).");
    }
}

