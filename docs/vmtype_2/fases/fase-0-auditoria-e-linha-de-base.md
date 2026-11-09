# Fase 0: Auditoria e linha de base

**Objetivo:** chegar a um ponto de partida reproduzível, em que o build não suja a árvore, os defeitos conhecidos são reproduzidos por testes e existe um receptor local para medir o que chega ao destino.

**Pré-requisitos:** nenhum. **Mudança funcional no app:** nenhuma (só refatorações que não alteram comportamento, para criar pontos de injeção).

---

## Passo 0.1: Registrar o estado inicial
1. `git status`, `git log -1`, `git remote -v` → registrar no relatório (valores atuais na [avaliação §1](../02-avaliacao-da-implementacao.md)).
2. Confirmar que **não** existem alterações locais no projeto. A pasta `rea/` não é rastreada e não pertence ao VmTyper: **não mexer**.
3. `dotnet --list-sdks` e `dotnet --list-runtimes` → registrar.
4. Criar uma tag local `baseline/v1` no commit inicial, para servir de referência nas comparações antes/depois.

## Passo 0.2: Branch de trabalho
```bash
git switch -c feature/vmtyper2
```
O push só acontece com autorização (ver plano §4).

## Passo 0.3: Higiene do repositório (antecipada da F6)
1. Criar `.gitignore` na raiz com o modelo padrão do `dotnet new gitignore` (inclui `bin/`, `obj/`, `.vs/`, `*.user`, `TestResults/`).
2. Remover os artefatos do índice **sem apagar do disco**:
   ```bash
   git rm -r --cached VmTyper/bin VmTyper/obj
   ```
3. Commit isolado: `chore: remove artefatos de build do versionamento`.
4. **Verificação:** `dotnet build` seguido de `git status --short` deve listar **zero** arquivos alterados.

## Passo 0.4: Estrutura de solução e testes
```
VmTyper.sln
├── VmTyper/                         (app atual)
├── tests/
│   ├── VmTyper.Tests/               xUnit, unitários (sem SendInput real)
│   └── VmTyper.IntegrationTests/    xUnit, [Trait("Category","Integration")], injetam no receptor local
└── tools/
    └── VmTyper.TestReceiver/        WPF mínimo: TextBox + hook opcional + exportação JSON
```
- Os testes visam o mesmo TFM do app (`net8.0-windows` agora, `net10.0-windows` depois da F1B).
- Em `VmTyper.csproj`: `<InternalsVisibleTo Include="VmTyper.Tests" />` (e o mesmo para IntegrationTests).
- Pacotes: `xunit`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk`, e opcionalmente `coverlet.collector` para cobertura. Fixe as versões no `Directory.Packages.props`.

## Passo 0.5: Pontos de injeção mínimos (refatoração sem mudança de comportamento)
Criar só o necessário para testar. A lógica não muda.

| Abstração | Implementação real | Falso para testes |
|---|---|---|
| `IInputInjector { uint Send(ReadOnlySpan<Win32.INPUT> inputs); int LastError { get; } }` | `Win32InputInjector` (chama `SendInput`) | `RecordingInjector` grava cada INPUT e permite simular falha parcial (por exemplo, "retornar 1 de 2 na chamada N") |
| `IKeyboardLayoutProvider { IntPtr GetLayout(); short VkKeyScan(char, IntPtr); uint MapVirtualKey(uint, uint, IntPtr); }` | wrapper do `Win32` | tabela fixa ABNT2 / US-Intl |
| `IQueueStore { Load(); Save(); }` | `QueuePersistenceService` | em memória, com injeção de exceções |
| `IAutoSaveScheduler` | `DispatcherTimer` | manual (`Trigger()`) |

- `KeyboardInputService` passa a receber `IInputInjector` e `IKeyboardLayoutProvider` pelo construtor. Os métodos `static` viram métodos de instância.
- `TypingQueueService` recebe `IQueueStore` e `IAutoSaveScheduler` em vez de `Dispatcher`.
- `App.xaml.cs` faz a composição com as implementações reais. **O comportamento fica idêntico.**

## Passo 0.6: Testes de caracterização (reproduzem os defeitos)
Escreva cada teste **descrevendo o comportamento correto esperado** e marque com `[Trait("Known","A")]` etc., começando como **falha conhecida** (`Skip = "Known defect A – corrigido na F1"`, ou um teste que afirma o comportamento atual com o comentário `// CARACTERIZAÇÃO`). Na F1, o skip é retirado.

| Teste | Problema |
|---|---|
| `EditingEmptyBlock_MakesItNext` | A |
| `ClearingCurrentBlock_MovesNextToFollowing` | A (inverso) |
| `SetAsNext_WithEarlierReadyBlock_SelectsTarget` | B |
| `NewQueue_WhileTyping_IsRejected` / `RestartQueue_WhileTyping_IsRejected` | D |
| `UnsupportedChar_IsNotCountedAsSent` | E |
| `SurrogatePair_IsSingleUnit` | E |
| `CorruptQueueFile_IsNotOverwritten` | F |
| `CorruptSettingsFile_IsReported` | F |
| `DivideKey_IsSentWithExtendedFlag` | G |
| `PartialSendInput_ReleasesMainKey` | H1 |
| `SuccessfulBlock_SendsNoOrphanKeyUps` | H3 |
| `ReleaseUsesSameEncodingAsPress` | H2 |
| `LiteralTilde_FollowedByVowel_DoesNotCombine` (layout ABNT2 falso) | N1 |
| `DiagnosticsAndMainSession_CannotRunConcurrently` | N3 |
| `DiagnosticLog_DoesNotContainText` | N4 |

## Passo 0.7: Receptor local de verdade-terreno (`tools/VmTyper.TestReceiver`)
1. Janela WPF com um `TextBox` multilinha (`AcceptsReturn`, `AcceptsTab`) e o título `VmTyper Test Receiver`.
2. Opcional (com flag `--hook`): `WH_KEYBOARD_LL` registra cada evento (vk, scan, flags, `LLKHF_INJECTED`, timestamp `Stopwatch`).
3. Botão/atalho e argumento de linha de comando `--export <arquivo>` que grava `{ text, events[] }` em JSON.
4. Modo pipe nomeado (`--pipe VmTyperReceiver`) para os testes de integração lerem o resultado sem interação humana.
5. **Teste de integração-base:** iniciar o receptor, trazê-lo para o foreground, digitar o corpus `basico.txt` com o motor **atual**, exportar e comparar. Esse resultado é a **linha de base do motor v1**.

> O receptor serve apenas para testes. Ele nunca é distribuído junto do app.

## Passo 0.8: Medição de linha de base do ritmo (N5, N12)
Teste de integração instrumentado (sem mudar o motor): para `CharDelayMs ∈ {0,1,3,10,30}`, medir com `Stopwatch` o intervalo real entre unidades e a duração KeyDown→KeyUp, com o sistema ocioso e com carga (por exemplo, `stress` de CPU em paralelo). Registrar mediana, p95 e máximo. **Esses números serão a referência da F2.**

## Passo 0.9: Relatório de auditoria
Arquivo `docs/vmtype_2/relatorios/F0-auditoria.md`, seguindo o [template](../templates/relatorio-de-fase.md). Deve conter:
- a tabela de problemas da [avaliação](../02-avaliacao-da-implementacao.md), agora com a coluna "Confirmado por teste: sim/não + nome do teste";
- a linha de base de fidelidade (receptor local) e de ritmo (passo 0.8);
- as inconsistências do README (N6, N7).

---

## Critérios de aceite da F0
- [ ] `dotnet build` + `git status` → árvore limpa.
- [ ] `dotnet test tests/VmTyper.Tests` roda, e os defeitos conhecidos aparecem como *skipped/known* com o identificador.
- [ ] O receptor local recebe o corpus básico e exporta JSON.
- [ ] Relatório F0 publicado, com números medidos (e não estimados).
- [ ] Nenhuma mudança de comportamento no app (mesmo resultado do receptor antes e depois do passo 0.5).

## Riscos
- O hook de teclado no receptor pode ser bloqueado por EDR. Nesse caso, use o receptor sem hook (só o TextBox).
- Os testes de integração exigem sessão interativa (desktop desbloqueado) e **não** rodam em CI.
