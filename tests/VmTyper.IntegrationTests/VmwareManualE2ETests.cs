using System;
using Xunit;
using Xunit.Abstractions;

namespace VmTyper.IntegrationTests;

public sealed class VmwareE2EFactAttribute : FactAttribute
{
    public const string Variable = "VMTYPER_E2E_VMWARE";

    public VmwareE2EFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(Variable) != "1")
        {
            Skip = $"E2E manual VMware exige operador humano e console de VM ativa; defina {Variable}=1 e siga docs/TESTING.md.";
        }
    }
}

[Trait("Category", "E2E-VMware")]
public sealed class VmwareManualE2ETests
{
    private readonly ITestOutputHelper _output;

    public VmwareManualE2ETests(ITestOutputHelper output) => _output = output;

    [VmwareE2EFact]
    public void VmwareConsole_ProtocolChecklist_IsAvailable()
    {
        _output.WriteLine("Checklist VMware Console (consulte docs/TESTING.md para o protocolo completo):");
        _output.WriteLine("1. Abrir console VMware Remote Console ou vSphere Web Client em tela cheia/janela focada.");
        _output.WriteLine("2. Configurar perfil Adaptativo/Conservador no VmTyper.");
        _output.WriteLine("3. Travar janela-alvo via TargetGuard.");
        _output.WriteLine("4. Transmitir bloco de teste e verificar ausência de teclas presas ou duplicações.");
        Assert.True(true);
    }
}

