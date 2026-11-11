# Relatório da Fase 0: Auditoria e linha de base

- **Período:** 2026-11-09 → em andamento (falta a linha de base de fidelidade, ver §6)
- **Branch / commits:** `feature/vmtyper2` · `dbe87e4..HEAD` (tag local `baseline/v1` = `fd90a31`)
- **Ambiente:** Windows 10 Pro 10.0.19045 · SDK 10.0.401 · runtimes Desktop 8.0.31 e 10.0.12 · alvo `net8.0-windows` · navegador: não utilizado · VMware: não utilizado

## 1. Diagnóstico

### 1.1 Estado inicial (passo 0.1)

| Item | Valor |
|---|---|
| Branch de partida | `docs/vmtyper2-plano` (`dbe87e4`), que contém só a documentação sobre `master` (`fd90a31`) |
| Remoto | `https://github.com/MarcosAndreRJ/vmtyper.git` (nenhum push feito) |
| Alterações locais no projeto | nenhuma; `rea/` não rastreada e intocada |
| `bin/` e `obj/` versionados | 207 arquivos rastreados |

### 1.2 Problemas da avaliação, agora com teste

Status: **Sustentado** (teste reproduz o defeito), **Pendente** (sem teste ainda), **Sustentado por medição**.
Os testes `[KnownDefectFact]` ficam pulados por padrão. Com `VMTYPER_RUN_KNOWN=1`, os 17 falham, e a mensagem de cada falha foi conferida para garantir que ela vem do defeito, não do harness (coluna "Falha observada").

| # | Confirmado por teste | Teste(s) | Falha observada | Status |
|---|---|---|---|---|
| A | sim | `EditingEmptyBlock_MakesItNext`, `ClearingCurrentBlock_MovesNextToFollowing` | bloco editado continua `Pending`; bloco esvaziado continua `Current` | Sustentado |
| B | sim | `SetAsNext_WithEarlierReadyBlock_SelectsTarget` | o bloco 1 continua `Current` depois de `SetAsNext(bloco 3)` | Sustentado |
| C | não | — | o motor v1 não tem verificação de destino para testar; entra junto com o `TargetGuard` (F1 §3) | Pendente |
| D | sim (camada de serviço) | `NewQueue_WhileTyping_IsRejected`, `RestartQueue_WhileTyping_IsRejected`, `AddBlock_WhileTyping_IsRejected` | bloco `Typing` some com `NewQueue`; bloco concluído volta a `Current` com `RestartQueue`; `AddBlock` aceito | Sustentado. A parte de UI (botões fora do `IsEnabled`) é verificada só por leitura |
| E | sim | `UnsupportedChar_IsNotCountedAsSent`, `SurrogatePair_IsSingleUnit` | 3 enviados para "a☃b" (esperado 2); 4 unidades para "a😀b" (esperado 3) | Sustentado |
| F | sim | `CorruptQueueFile_IsNotOverwritten`, `CorruptSettingsFile_IsReported` | o conteúdo corrompido original não existe mais em nenhum arquivo depois do primeiro `Save` | Sustentado |
| G | sim | `DivideKey_IsSentWithExtendedFlag` | `VK_DIVIDE` sai como scan `0x35` sem `KEYEVENTF_EXTENDEDKEY` | Sustentado |
| H1 | sim | `PartialSendInput_ReleasesMainKey` | tecla `0x1E` ('a') fica pressionada quando o envio das subidas falha | Sustentado |
| H2 | sim | `ReleaseUsesSameEncodingAsPress` | Shift descido por scan code é solto por VK (`0xA0`) | Sustentado |
| H3 | sim | `SuccessfulBlock_SendsNoOrphanKeyUps` | 6 KeyUp por VK sem KeyDown correspondente num bloco bem-sucedido | Sustentado |
| H4–H6 | não | — | modificadores físicos, saída durante digitação e UIPI exigem sessão real; ficam para F1 com o receptor | Pendente |
| I | não | — | configurações; F1 §6 | Pendente |
| N1 | sim (layout ABNT2 falso) | `LiteralTilde_FollowedByVowel_DoesNotCombine` | depois da tecla morta `~` vem `a` (`0x1E`), não Espaço. O efeito no destino depende do layout da VM | Sustentado |
| N2 | não | — | layout host ≠ VM; exige a decisão de layout de destino (plano §4) | Pendente |
| N3 | sim (motor) | `DiagnosticsAndMainSession_CannotRunConcurrently` | duas sessões simultâneas no mesmo motor intercalam eventos (7 trocas em 12 teclas) | Sustentado |
| N4 | sim | `DiagnosticLog_DoesNotContainText` | log em disco contém `Char='Z'` | Sustentado |
| N5 | medição | `RhythmBaselineTests` | ver §1.3 | Sustentado por medição |
| N6, N7 | leitura | — | README ainda com a afirmação de causa raiz e os links quebrados; corrigir na F6 | Sustentado (leitura) |
| N8–N11, N13 | não | — | — | Pendente |
| N12 | medição | `RhythmBaselineTests` | ver §1.3 | Sustentado por medição (parcial) |

### 1.3 Linha de base de ritmo (passo 0.8)

Arquivo completo: [evidencias/rhythm-v1-20261109-182119.md](evidencias/rhythm-v1-20261109-182119.md). Mediu-se o motor v1 com o injetor falso, que registra o `Stopwatch` no instante de cada chamada. O número reflete a temporização do motor, sem o custo de `SendInput`.

| Condição | CharDelayMs | Intervalo mediana (ms) | p95 | máx | Hold mediana (ms) | p95 | máx |
|---|---|---|---|---|---|---|---|
| ocioso | 0 | 2,04 | 2,38 | 16,55 | 2,02 | 2,15 | 10,19 |
| ocioso | 1 | 15,79 | 28,87 | 468,68 | 2,01 | 2,09 | 4,42 |
| ocioso | 3 | 16,05 | 29,10 | 312,84 | 2,01 | 2,07 | 75,41 |
| ocioso | 10 | 16,10 | 30,32 | 111,57 | 2,01 | 2,04 | 2,10 |
| ocioso | 30 | 38,82 | 56,86 | 408,01 | 2,01 | 2,04 | 2,11 |
| carga | 0 | 2,02 | 2,11 | 27,24 | 2,01 | 2,06 | 27,22 |
| carga | 1 | 25,65 | 74,56 | 355,69 | 2,02 | 2,33 | 53,10 |
| carga | 3 | 30,84 | 73,01 | 450,18 | 2,02 | 2,22 | 73,63 |
| carga | 10 | 36,39 | 144,81 | 431,92 | 2,02 | 2,28 | 8,01 |
| carga | 30 | 63,08 | 130,24 | 210,75 | 2,02 | 2,65 | 8,55 |

Leitura:
- **N5:** os valores 1, 3 e 10 ms produzem a mesma mediana (cerca de 16 ms) com o sistema ocioso. O valor configurado não é o ritmo entregue.
- **Resolução do timer:** a resolução global oscilou entre 1 ms e 15,625 ms *durante* a execução, porque outros processos a alteram. É a dependência de terceiros que o N5 descreve.
- **N12:** a mediana e o p95 do hold ficam perto de 2 ms, mas há picos de 27 a 75 ms, tanto ocioso quanto com carga. O máximo existe; a causa (preempção do spin, GC ou JIT) não foi isolada.
- Os máximos de intervalo de várias centenas de ms aparecem nas duas condições. A causa não foi investigada nesta fase.

## 2. Implementações

| Passo | Commit | Conteúdo |
|---|---|---|
| 0.1–0.2 | — | tag local `baseline/v1` em `fd90a31`; branch `feature/vmtyper2` |
| 0.3 | `681245a` | `.gitignore` padrão do dotnet; `VmTyper/bin` e `VmTyper/obj` fora do índice (continuam no disco) |
| 0.5 | `23818a6` | `IInputInjector`, `IKeyboardLayoutProvider`, `IQueueStore`, `IAutoSaveScheduler`; construtores com caminho em `QueuePersistenceService`, `SettingsService` e `LogService`; composição real em `App.xaml.cs` |
| 0.4, 0.6 | `59b48c3` | `tests/VmTyper.Tests` (15 verdes + 17 defeitos conhecidos), `Directory.Packages.props` |
| 0.7, 0.8 | `00e2813` | `tools/VmTyper.TestReceiver`, `tests/VmTyper.IntegrationTests`, `tests/corpora/basico.txt` |

## 3. Arquivos alterados

| Arquivo | Tipo | Motivo |
|---|---|---|
| `.gitignore` | novo | higiene (0.3) |
| `VmTyper/bin/**`, `VmTyper/obj/**` | removido do índice | higiene (0.3) |
| `VmTyper/Native/IInputInjector.cs` | novo | ponto de injeção sobre `SendInput` |
| `VmTyper/Native/IKeyboardLayoutProvider.cs` | novo | ponto de injeção sobre o layout |
| `VmTyper/Native/Win32.cs` | alterado | sobrecarga `SendInput(ref INPUT)`; `SendInputs` e `ReleaseAllModifiers` recebem o injetor (as versões sem parâmetro continuam e usam o real) |
| `VmTyper/Services/KeyboardInputService.cs` | alterado | recebe injetor e layout; métodos estáticos viram de instância |
| `VmTyper/Services/KeyboardMappingService.cs` | alterado | recebe o provedor de layout |
| `VmTyper/Services/IQueueStore.cs`, `IAutoSaveScheduler.cs` | novos | fila sem `Dispatcher` |
| `VmTyper/Services/TypingQueueService.cs` | alterado | recebe `IQueueStore` e `IAutoSaveScheduler` |
| `VmTyper/Services/QueuePersistenceService.cs`, `SettingsService.cs`, `LogService.cs` | alterados | construtor interno com caminho |
| `VmTyper/App.xaml.cs` | alterado | composição com as implementações reais |
| `VmTyper/VmTyper.csproj` | alterado | `InternalsVisibleTo` para os projetos de teste |
| `VmTyper.sln`, `Directory.Packages.props` | alterado / novo | projetos de teste e ferramenta; versões fixas |
| `tests/**`, `tools/**` | novos | testes, falsos, receptor, corpus |

## 4. Testes

Comandos executados:
```
dotnet build VmTyper/VmTyper.csproj
git status --short
dotnet test tests/VmTyper.Tests
VMTYPER_RUN_KNOWN=1 dotnet test tests/VmTyper.Tests
VMTYPER_MEASURE=1 dotnet test tests/VmTyper.IntegrationTests
```

| Execução | Total | Aprovados | Reprovados | Ignorados |
|---|---|---|---|---|
| `dotnet test tests/VmTyper.Tests` | 32 | 15 | 0 | 17 (defeitos conhecidos, motivo com o identificador) |
| idem, com `VMTYPER_RUN_KNOWN=1` | 32 | 15 | 17 | 0 |
| `VMTYPER_MEASURE=1 dotnet test tests/VmTyper.IntegrationTests` | 2 | 1 (medição) | 0 | 1 (fidelidade, exige `VMTYPER_INTEGRATION=1`) |

- `dotnet build` seguido de `git status --short`: só `?? rea/` (não rastreada, alheia ao projeto).
- **Nível:** todos os testes de `VmTyper.Tests` são Unit (sem `SendInput` real). A medição de ritmo é Unit com relógio real. A fidelidade com o receptor é Integration (local) e ainda não foi executada.
- Os 15 testes verdes documentam o comportamento atual que deve continuar igual: ordem dos scan codes, Shift em volta da tecla, KeyDown/KeyUp em chamadas separadas, tecla morta + letra para acentos, `\r\n` → um Enter, modo Unicode, cancelamento entre unidades, ida e volta da persistência e regras básicas da fila.

## 5. Regressões

Nenhuma detectada. A refatoração 0.5 só troca chamadas estáticas por chamadas a interfaces cujas implementações reais delegam para as mesmas funções Win32. O build do app segue com 0 erros e 0 avisos. A confirmação *em sessão real* de que o comportamento é idêntico (mesmo resultado do receptor antes e depois do 0.5) está pendente; ver §6.

## 6. Limitações

- **Linha de base de fidelidade (0.7, item 5): pendente.** O teste existe, mas injeta teclas de verdade na sessão do usuário. Por isso não foi executado sem autorização. Para rodá-lo, com o desktop desbloqueado e sem mexer no teclado e no mouse durante cerca de 10 s:
  ```
  dotnet build tools/VmTyper.TestReceiver
  VMTYPER_INTEGRATION=1 dotnet test tests/VmTyper.IntegrationTests --filter Category=Integration
  ```
  O JSON sai em `tests/VmTyper.IntegrationTests/bin/Debug/net8.0-windows/baseline/`. Para comparar antes e depois do passo 0.5, o mesmo corpus precisa ser digitado também a partir de `baseline/v1`, pelo app ou pela janela de diagnóstico.
- O receptor usa o layout do host. N1 e N2 só aparecem no receptor quando o layout ativo tem teclas mortas (ABNT2, US-Intl).
- A medição de ritmo exclui o custo de `SendInput` e o pipeline real (navegador, console, VM).
- O hook `WH_KEYBOARD_LL` do receptor não foi exercitado nesta máquina. A política de EDR não foi confirmada (plano §4).
- Nada foi enviado ao remoto.

## 7. Próxima fase

- Fechar a F0: executar a linha de base de fidelidade (§6) e anexar o JSON a `evidencias/`.
- **F1, Estabilização:** máquina de estados da fila (A, B, D, N9), motor de teclado com rastreamento de teclas (E, G, H, N1, N8), `TargetGuard` (C, N13), `AtomicJsonStore` (F, N4), `TypingSessionCoordinator` (N3, N10). Cada correção troca o `[KnownDefectFact]` correspondente por `[Fact]`.
- Dependências em aberto: autorização de push da branch; layout de teclado da VM (N2); política de EDR para o hook.
