# Relatório da Fase 1B: Migração .NET 8 → .NET 10 LTS

- **Período:** 2026-11-09 → 2026-11-09
- **Branch / commits:** `feature/vmtyper2` · `67187f9..HEAD`
- **Ambiente:** Windows 10 Pro 10.0.19045 · SDK `10.0.401` · runtime Desktop `10.0.12` · alvo `net10.0-windows` · navegador: não utilizado · VMware: não utilizado

## 1. Diagnóstico

O .NET 8 encerra o suporte oficial em 10/11/2026, e o ambiente de desenvolvimento já utiliza o SDK `10.0.401` com o runtime Desktop `10.0.12`. Com a suíte de regressão de 81 testes unitários da Fase 1 ativa, a migração do `TargetFramework` de `net8.0-windows` para `net10.0-windows` foi executada de forma isolada, sem qualquer alteração funcional no código-fonte (`.cs` / `.xaml`).

Itens verificados na migração (Passo 6):
- **`UseWindowsForms=true` (ícone de bandeja):** compilado sem novos avisos de ambiguidade ou obsolescência no SDK 10.0.401.
- **`ApplicationHighDpiMode=PerMonitorV2` e `app.manifest`:** preservados sem conflito (`WFAC010` não disparado em `net10.0-windows`).
- **P/Invoke e layout de structs x64 (`Win32.INPUT`):** o teste `Win32InputStruct_Is40BytesOnX64` confirmou `Marshal.SizeOf<Win32.INPUT>() == 40` em `net10.0-windows`.
- **Compilação com `-warnaserror` (`Debug` e `Release`):** 0 erros e 0 avisos, sem necessidade de adicionar supressões `<NoWarn>`.

## 2. Implementações

- Criado `global.json` na raiz fixando o SDK `10.0.401` com `rollForward: latestFeature`.
- Atualizado `<TargetFramework>net10.0-windows</TargetFramework>` nos 4 projetos da solução:
  - `VmTyper/VmTyper.csproj`
  - `tests/VmTyper.Tests/VmTyper.Tests.csproj`
  - `tests/VmTyper.IntegrationTests/VmTyper.IntegrationTests.csproj`
  - `tools/VmTyper.TestReceiver/VmTyper.TestReceiver.csproj`
- Mantidas as versões centralizadas em `Directory.Packages.props` (`Microsoft.NET.Test.Sdk` `17.14.1`, `xunit` `2.9.3`, `xunit.runner.visualstudio` `3.1.4`, `coverlet.collector` `6.0.4`), que restauram e executam em `.NETCoreApp,Version=v10.0` sem avisos.
- Atualizado `README.md` (§3) com o requisito do SDK do .NET 10 e o caminho `VmTyper\bin\x64\Release\net10.0-windows\VmTyper.exe`.
- Registrado o [ADR 0006](../adrs/0006-avaliacao-publicacao-self-contained-single-file.md) avaliando a publicação *self-contained* / *single-file* para empacotamento na Fase 6, sem misturar com o `.csproj` de desenvolvimento.

## 3. Arquivos alterados

| Arquivo | Tipo | Motivo |
|---|---|---|
| `global.json` | novo | Fixar SDK `10.0.401` (`rollForward: latestFeature`) |
| `VmTyper/VmTyper.csproj` | alterado | `<TargetFramework>net10.0-windows</TargetFramework>` |
| `tests/VmTyper.Tests/VmTyper.Tests.csproj` | alterado | `<TargetFramework>net10.0-windows</TargetFramework>` |
| `tests/VmTyper.IntegrationTests/VmTyper.IntegrationTests.csproj` | alterado | `<TargetFramework>net10.0-windows</TargetFramework>` |
| `tools/VmTyper.TestReceiver/VmTyper.TestReceiver.csproj` | alterado | `<TargetFramework>net10.0-windows</TargetFramework>` |
| `README.md` | alterado | Atualizar seção 3 para .NET 10 SDK e caminho `net10.0-windows` |
| `docs/vmtype_2/adrs/0006-avaliacao-publicacao-self-contained-single-file.md` | novo | Avaliação de publicação portátil (`SelfContained` / `PublishSingleFile`) |
| `docs/vmtype_2/relatorios/F1B-antes/*.trx` | novo | Resultados TRX antes da migração (`net8.0-windows`) |
| `docs/vmtype_2/relatorios/F1B-depois/*.trx` | novo | Resultados TRX depois da migração (`net10.0-windows`) |

## 4. Testes

Comandos executados:
```powershell
# Antes (net8.0-windows)
dotnet test tests/VmTyper.Tests --logger "trx;LogFileName=VmTyper.Tests.trx" --results-directory "docs\vmtype_2\relatorios\F1B-antes" -v minimal
dotnet test tests/VmTyper.IntegrationTests --logger "trx;LogFileName=VmTyper.IntegrationTests.trx" --results-directory "docs\vmtype_2\relatorios\F1B-antes" -v minimal

# Build com -warnaserror (net10.0-windows)
dotnet build VmTyper.sln -warnaserror -v minimal
dotnet build VmTyper.sln -c Release -warnaserror -v minimal

# Depois (net10.0-windows)
dotnet test tests/VmTyper.Tests --logger "trx;LogFileName=VmTyper.Tests.trx" --results-directory "docs\vmtype_2\relatorios\F1B-depois" -v minimal
dotnet test tests/VmTyper.IntegrationTests --logger "trx;LogFileName=VmTyper.IntegrationTests.trx" --results-directory "docs\vmtype_2\relatorios\F1B-depois" -v minimal
```

| Suíte | Antes (`net8.0`) | Depois (`net10.0`) | Evidência TRX |
|---|---|---|---|
| `VmTyper.Tests` (Unit + decodificador ABNT2 nos 3 corpora + `Marshal.SizeOf<INPUT>() == 40`) | 81 total / 81 aprovados / 0 reprovados / 0 ignorados | 81 total / 81 aprovados / 0 reprovados / 0 ignorados | `F1B-antes/VmTyper.Tests.trx` vs. `F1B-depois/VmTyper.Tests.trx` |
| `VmTyper.IntegrationTests` (padrão sem variáveis de sessão real) | 4 métodos (9 casos) ignorados | 4 métodos (9 casos) ignorados | `F1B-antes/VmTyper.IntegrationTests.trx` vs. `F1B-depois/VmTyper.IntegrationTests.trx` |

## 5. Regressões

Nenhuma regressão detectada. Nenhum arquivo `.cs` ou `.xaml` precisou ser modificado.

## 6. Limitações

- Assim como documentado na Fase 1, a execução dos testes de integração que exigem janela ativa em primeiro plano (`VMTYPER_INTEGRATION=1`) e o teste visual manual (Passo 8: abrir o app, digitar no Bloco de Notas, abrir Configurações e bandeja) dependem de sessão interativa do usuário (`WinSta0\Default`), pois o runner do agente opera sem foco de desktop (`GetForegroundWindow() == 0x0`). A paridade de fidelidade dos corpora `basico.txt`, `acentuacao.txt` e `teclas-mortas.txt` entre `net8.0-windows` e `net10.0-windows` foi confirmada na suíte `VmTyper.Tests` via `Corpus_BasicAccentedAndDeadKeys_DecodedIdentically_InPhysicalAndUnicodeModes`.

## 7. Próxima fase

- **Fase 2: Motor adaptativo e ritmo (`docs/vmtype_2/fases/fase-2-motor-adaptativo.md`)** — atacar N5, N11 e N12 atrás da feature flag `Features.AdaptiveTiming`.
