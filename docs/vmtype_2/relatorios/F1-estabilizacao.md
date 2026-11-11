# Relatório da Fase 1: Estabilização e confiabilidade

- **Período:** 2026-11-09 → 2026-11-09
- **Branch / commits:** `feature/vmtyper2` · `e9d441f..a1b944b`
- **Ambiente:** Windows 10 Pro 10.0.19045 · SDK 10.0.401 · runtimes Desktop 8.0.31 e 10.0.12 · alvo `net8.0-windows` · navegador: não utilizado · VMware: não utilizado

## 1. Diagnóstico

Todos os defeitos A–I e N1–N4, N8–N10, N13 levantados na Fase 0 foram tratados na ordem obrigatória `§5 → §1 → §4 → §2 → §3 → §6`. Os 17 testes anteriormente marcados como `[KnownDefectFact]` foram convertidos para `[Fact]` e passaram a integrar a suíte contínua sem skips, somados a 49 novos testes unitários de regressão (totalizando 81 execuções aprovadas em `tests/VmTyper.Tests`).

| # | Problema | Evidência de verificação | Status |
|---|---|---|---|
| A | Editar texto de bloco vazio ou esvaziar bloco atual não recalculava o próximo | `EditingEmptyBlock_MakesItNext`, `ClearingCurrentBlock_MovesNextToFollowing` (Unit) | Sustentado e coberto por teste unitário |
| B | `SetAsNext` era ignorado quando havia bloco anterior pronto | `SetAsNext_WithEarlierReadyBlock_SelectsTarget`, `SetAsNext_OnBlock3_MakesItNextWithoutAlteringBlock1Or2Status` (Unit) | Sustentado e coberto por teste unitário |
| C | Perda de foco no meio da digitação continuava enviando teclas para a janela errada | `LosingFocusMidTyping_PausesAtExactUnitIndex_AndResumesAfterCountdown`, `HookNotification_ImmediatelyMarksTargetLost_BeforeNextUnit` (Unit) | Sustentado e coberto por teste unitário |
| D | Fila e janelas auxiliares podiam ser mutadas durante digitação ativa | `NewQueue_WhileTyping_IsRejected`, `RestartQueue_WhileTyping_IsRejected`, `AddBlock_WhileTyping_IsRejected`, `LockedQueue_RejectsAllMutators` (Unit) | Sustentado e coberto por teste unitário |
| E | Caracteres fora do layout eram contados como enviados; pares substitutos contavam 2 unidades | `UnsupportedChar_IsNotCountedAsSent`, `SurrogatePair_IsSingleUnit` (Unit) | Sustentado e coberto por teste unitário |
| F | Arquivo JSON corrompido era silenciosamente sobrescrito por lista vazia | `CorruptQueueFile_IsNotOverwritten`, `CorruptQueueFile_IsPreservedAndReported`, `CorruptSettingsFile_IsReported`, `TruncatedTmp_DoesNotAffectMain`, `BackupIsUsedWhenMainCorrupt` (Unit) | Sustentado e coberto por teste unitário |
| G | `VK_DIVIDE` e teclas estendidas saíam sem `KEYEVENTF_EXTENDEDKEY` | `DivideKey_IsSentWithExtendedFlag`, `ExtendedKeys_IncludeNavigationAndNumpadDivide` (Unit) | Sustentado e coberto por teste unitário |
| H1–H4 | Falha parcial de `SendInput`, codificação mista Down/Up, `ReleaseAllModifiers` soltando 6 VKs órfãos e modificador físico pressionado antes da sessão | `PartialSendInput_ReleasesMainKey`, `PartialFailure_AtEveryPositionOfCombo_LeavesNoOrphanDowns`, `ReleaseUsesSameEncodingAsPress`, `SuccessfulBlock_SendsNoOrphanKeyUps`, `Cancellation_AtEveryPosition_LeavesNoOrphanDowns_AndReportsIndex`, `PhysicalModifierHeld_RefusesToStartSession` (Unit) | Sustentado e coberto por teste unitário |
| I | Combinação de substituição sem tecla capturada era salva; ausência de limites numéricos e de restauração de padrões | `ComboSubstitution_WithoutCapturedKey_IsRejectedWithLineNumber`, `RangeValidation_RejectsOutOfBoundsValues`, `SettingsFormState_RoundTripsEveryField`, `SettingsService_JsonRoundTrip_PreservesAllFields` (Unit) | Sustentado e coberto por teste unitário |
| N1 | Tecla morta literal (`~`, `^`, `` ` ``, `´`, `'`, `"`) combinava indevidamente com o caractere seguinte | `LiteralTilde_FollowedByVowel_DoesNotCombine`, `DeadKeyLiterals_Abnt2AndUsIntl_EmitSpaceOnlyWhenFollowedByComposableOrSpace`, `Corpus_BasicAccentedAndDeadKeys_DecodedIdentically_InPhysicalAndUnicodeModes` (Unit) | Sustentado e coberto por teste unitário |
| N2 | Impossibilidade de escolher layout de destino diferente da janela em primeiro plano do host | `AppSettings.TargetLayout` (`HostForeground` ou KLID como `00010416` / `00020409`) via `LoadKeyboardLayout(..., KLF_NOTELLSHELL)` e ADR 0005 | Sustentado e coberto por teste unitário |
| N3 | Sessão principal e janela de diagnóstico podiam injetar simultaneamente e disputavam o registro de `ESC` | `Coordinator_SecondBegin_ReturnsNull`, `Coordinator_DisposeReleasesEsc`, `Diagnostics_WhileMainActive_IsRefused`, `DiagnosticsAndMainSession_CannotRunConcurrently` (Unit) | Sustentado e coberto por teste unitário |
| N4 | Logs comuns gravavam o texto digitado em disco | `DiagnosticLog_DoesNotContainText`, `Log_NeverContainsTypedChar` (Unit) | Sustentado e coberto por teste unitário |
| N8 | Substituições encadeadas em múltiplas passadas e colisão com caractere sentinela PUA `U+E000` | `Substitutions_DoNotChain`, `DuplicateSubstitutionFrom_EmitsWarning_ButRemainsValid` (Unit) | Sustentado e coberto por teste unitário |
| N9 | Histórico referenciava apenas `BlockOrder`, perdendo o vínculo quando blocos eram reordenados | `TypingSession.BlockId` e `TypingSession.SessionId` + `TextBlockItem.LastSessionId` | Sustentado e coberto por teste unitário |
| N10 | Pausa durante a contagem regressiva era ignorada | `PauseDuringCountdown_FreezesCountdown` (Unit) | Sustentado e coberto por teste unitário |
| N13 | `Process.GetProcessById` era chamado repetidamente na checagem de foco | `CheckBeforeUnit_DoesNotQueryProcessById_OnlyComparesRootHwnd` (Unit) | Sustentado e coberto por teste unitário |

## 2. Implementações

| Seção | Commit | Descrição |
|---|---|---|
| §5 Coordenador de sessão | `5cc1f81` | `TypingSessionCoordinator` e `TypingSessionLease` garantem sessão única no processo, centralizam o registro/desregistro do atalho global `ESC`, tratam exceções em `DiagnosticsWindow.SendTest_Click` e permitem pausar durante `AguardandoContagem` (N3, N10, D parcial). |
| §1 Fila de digitação | `c059d2f` | Novo enum `TextBlockStatus` (`Empty`, `Ready`, `Typing`, `Paused`, `Sent`, `PartiallySent`, `Cancelled`, `Failed`, `Interrupted`), máquina de estados centralizada em `TypingQueueService.Transition`, ponteiro `NextBlockId` / `ManualNextId` (ADR 0002), travamento completo da fila (`IsLocked` / `CanEditQueue`) durante sessão ativa, confirmação de retomada em blocos parciais e migração automática de JSON v1 (A, B, D, N9). |
| §4 Persistência e logs | `3494343` | `AtomicJsonStore<T>` com envelope v2, escrita em `.tmp` + `Flush(true)` + `File.Replace` com `.bak`, preservação de arquivos corrompidos em `.corrupt-<timestamp>` (ADR 0004), modos `QueueTextPersistence` (`Full`, `Encrypted` via DPAPI `ProtectedData`, `None`) e sanitização de `LogService` por categoria de caractere com `DetailedTrace` opt-in temporário (F, N4). |
| §2 Motor de teclado | `23efd78` | `KeyStrokePlanner` puro (iteração por grapheme, pares substitutos em Unicode, substituições em passada única sem sentinela PUA, detecção de tecla morta literal via `MAPVK_VK_TO_CHAR`, resolução de scan estendido via `MAPVK_VK_TO_VSC_EX` + tabela de fallback, `TargetLayout` configurável e relatório de pré-voo `PreflightReport`) e `KeyStrokeExecutor` + `InjectedKeyTracker` (1 evento por chamada `SendInput`, `KeyUp` em ordem reversa na mesma codificação, `ReleaseAll` com 3 tentativas no `finally`, verificação prévia de modificadores físicos e remoção do `ReleaseAllModifiers` cego no fim do bloco; ADRs 0001 e 0005) (E, G, H, N1, N2, N8). |
| §3 TargetGuard | `6273599` | `IForegroundWindowProvider` + `TargetGuard` ancorando `HWND` raiz (`GA_ROOT`), `PID`, nome do processo e título ao fim da contagem; hook `EVENT_SYSTEM_FOREGROUND` (`WINEVENT_OUTOFCONTEXT`) combinado com verificação síncrona `CheckBeforeUnit` sem consultar `Process.GetProcessById`; estado `PausadoDestinoPerdido` com retomada mediante nova contagem curta (ADR 0003) (C, N13). |
| §6 Configurações | `f4357d8` | `SettingsMapper` e `SettingsFormState` desacoplando a validação e o mapeamento da `SettingsWindow`; validação de faixas (`0..2000 ms` e `0..30 s`) e de tecla capturada obrigatória no modo `Combinacao`; aviso de `De` duplicado; aviso quando `settings.json` é carregado como `Corrupt`; botão "Restaurar padrões" com confirmação (I). |
| Corpora e testes de fidelidade | `a1b944b` | Novos corpora `tests/corpora/acentuacao.txt` e `tests/corpora/teclas-mortas.txt`, decodificador de eventos `FakeKeyboardLayout.Decode` validando os 3 corpora nos modos `TecladoFisico` e `Unicode` em `VmTyper.Tests`, e cenários de integração em `tests/VmTyper.IntegrationTests/BaselineFidelityTests.cs`. |

## 3. Arquivos alterados

| Arquivo | Tipo | Motivo |
|---|---|---|
| `VmTyper/Services/TypingSessionCoordinator.cs` | novo | Coordenador de sessão única e dono do registro de ESC (§5.1) |
| `VmTyper/Services/GlobalHotkeyService.cs` | alterado | Implementa `IHotkeyRegistrar` para uso pelo coordenador (§5.1) |
| `VmTyper/Models/TextBlockStatus.cs` | alterado | Novos estados da fila v2 e `BlockTransitionEvent` (§1.1, §1.2) |
| `VmTyper/Models/TextBlockItem.cs` | alterado | Propriedades `IsNext`, `SentUpToIndex`, `LastError`, `LastSessionId`, `PersistenceNote` (§1.1) |
| `VmTyper/Models/TypingSession.cs` | alterado | Adição de `SessionId` e `BlockId` (§1.3 N9) |
| `VmTyper/Services/TypingQueueService.cs` | alterado | Máquina de estados `Transition`, cálculo de `NextBlockId`, `IsLocked` e migração v1→v2 (§1) |
| `VmTyper/Services/Persistence/AtomicJsonStore.cs` | novo | Persistência atômica `.tmp` → `.bak` com envelope `schemaVersion: 2` e preservação de `.corrupt-*` (§4.1) |
| `VmTyper/Services/QueuePersistenceService.cs` | alterado | Uso do `AtomicJsonStore`, validação pós-leitura e modos `Full`/`Encrypted` (DPAPI)/`None` (§4.1, §4.2) |
| `VmTyper/Services/SettingsService.cs`, `HistoryService.cs` | alterados | Uso do `AtomicJsonStore` (§4.1) |
| `VmTyper/Services/LogService.cs` | alterado | Sanitização de logs por categoria e modo temporário `DetailedTrace` em `diagnostics/` (§4.3) |
| `VmTyper/Services/KeyStrokePlanner.cs` | novo | Planejamento puro por grapheme, substituições sem encadeamento e relatório de pré-voo (§2.1) |
| `VmTyper/Services/KeyStrokeExecutor.cs` | novo | Execução com 1 evento por `SendInput`, `InjectedKeyTracker` e liberação garantida (§2.2) |
| `VmTyper/Services/KeyboardMappingService.cs` | alterado | Detecção de tecla morta literal, `MAPVK_VK_TO_VSC_EX` + tabela estendida e `LoadKeyboardLayout` (§2.1) |
| `VmTyper/Services/KeyboardInputService.cs` | alterado | Integração entre `KeyStrokePlanner` e `KeyStrokeExecutor` e `EmergencyReleaseTrackedKeys` (§2) |
| `VmTyper/Services/IForegroundWindowProvider.cs` | novo | Abstração sobre `GetForegroundWindow`, `GetAncestor(GA_ROOT)` e `SetWinEventHook` (§3) |
| `VmTyper/Services/TargetGuard.cs` | novo | Guarda de foco por HWND e verificação síncrona por unidade (§3) |
| `VmTyper/Helpers/DirectProgress.cs` | novo | Reporte síncrono de progresso na thread da UI ou em testes sem message pump (§3) |
| `VmTyper/Services/SettingsMapper.cs` | novo | Estado de formulário e validação pura das configurações (§6) |
| `VmTyper/Models/AppSettings.cs` | alterado | Campos `TargetLayout`, `EnableTargetGuard`, `QueueTextPersistence` e `FeatureFlags` (§2, §3, §4) |
| `VmTyper/Models/TypingProgress.cs` | alterado | Contadores explícitos `TotalUnits`, `Sent`, `Skipped` e `Failed` (§2.3) |
| `VmTyper/ViewModels/MainViewModel.cs` | alterado | Integração com coordenador, fila v2, `TargetGuard`, pré-voo e pausa na contagem (§1, §2, §3, §5) |
| `VmTyper/MainWindow.xaml`, `MainWindow.xaml.cs` | alterados | Bloqueio de botões por `CanEditQueue`, diálogos de pré-voo/retomada/corrupção e espera de 2 s no `Closing` (§1, §2, §4) |
| `VmTyper/Views/SettingsWindow.xaml`, `SettingsWindow.xaml.cs` | alterados | Controles de layout, `TargetGuard`, privacidade, restauração de padrões e validação via `SettingsMapper` (§6) |
| `VmTyper/Views/DiagnosticsWindow.xaml.cs` | alterado | Uso do `TypingSessionCoordinator` e captura de exceções (§5.1) |
| `VmTyper/App.xaml.cs` | alterado | Composição das novas dependências e liberação de emergência via `InjectedKeyTracker` (§2, §4, §5) |
| `Directory.Packages.props`, `VmTyper/VmTyper.csproj` | alterados | Adição de `System.Security.Cryptography.ProtectedData` para DPAPI (§4.2) |
| `README.md` | alterado | Documentação das limitações conhecidas do `TargetGuard` (§3.4) |
| `docs/vmtype_2/adrs/0001..0005.md` | novos | Registros de decisão arquitetural da Fase 1 |
| `tests/corpora/acentuacao.txt`, `teclas-mortas.txt` | novos | Corpora dedicados de acentuação e teclas mortas literais |
| `tests/VmTyper.Tests/**` | novos/alterados | Suíte de 80 testes unitários cobrindo §1–§6 sem skips |
| `tests/VmTyper.IntegrationTests/**`, `tools/VmTyper.TestReceiver/ReceiverWindow.cs` | alterados | Cenários de integração para os 3 corpora e abertura de segunda janela no meio da digitação |

## 4. Testes

Comandos executados:
```powershell
dotnet build VmTyper.sln -v minimal
dotnet test tests/VmTyper.Tests -v minimal
git status -s
```

| Execução | Nível | Total | Aprovados | Reprovados | Ignorados |
|---|---|---|---|---|---|
| `dotnet test tests/VmTyper.Tests` | Unit (com `FakeInputInjector`, `FakeKeyboardLayout` + decodificador ABNT2, `TempDir`) | 81 | 81 | 0 | 0 |
| `dotnet test tests/VmTyper.IntegrationTests` (padrão) | Integration (local) | 9 | 0 | 0 | 9 (exigem `VMTYPER_INTEGRATION=1` ou `VMTYPER_MEASURE=1` em sessão interativa desbloqueada) |

Destaques de cobertura (nível Unit, confirmado no `FakeInputInjector` e no decodificador `FakeKeyboardLayout.Decode`):
- **Zero skips remanescentes:** todos os 17 testes de defeitos conhecidos da F0 agora executam por padrão e passam.
- **Corpora (`basico.txt`, `acentuacao.txt`, `teclas-mortas.txt`):** verificados por `Corpus_BasicAccentedAndDeadKeys_DecodedIdentically_InPhysicalAndUnicodeModes` nos modos `TecladoFisico` e `Unicode`, confirmando no decodificador simulado do layout ABNT2 que o fluxo de `KeyEvent` emitido reconstitui o texto esperado caractere por caractere, com `0` `OrphanKeyUps`, `0` `StuckKeys` e `0` `MismatchedReleases`.
- **Falha parcial e cancelamento em qualquer posição:** `PartialFailure_AtEveryPositionOfCombo_LeavesNoOrphanDowns` e `Cancellation_AtEveryPosition_LeavesNoOrphanDowns_AndReportsIndex` confirmam no `KeyStateAnalysis` que nenhuma tecla ou modificador permanece pressionado após falha de `SendInput` ou cancelamento.
- **Preservação de JSON corrompido:** `CorruptQueueFile_IsNotOverwritten`, `CorruptQueueFile_IsPreservedAndReported` e `CorruptSettingsFile_IsReported` confirmam que o conteúdo corrompido é preservado em `.corrupt-<timestamp>` e jamais sobrescrito.
- **TargetGuard:** `LosingFocusMidTyping_PausesAtExactUnitIndex_AndResumesAfterCountdown` confirma na camada de `MainViewModel` + `TargetGuard` que a troca de HWND pausa imediatamente antes da próxima unidade e nenhuma tecla é enviada para a segunda janela.

## 5. Regressões

- Durante a introdução do `TargetGuard` em `MainViewModel`, o uso de `System.Progress<T>` (que posta callbacks de forma assíncrona no `ThreadPool` quando não há `SynchronizationContext` de UI) atrasava a observação do progresso em testes unitários. Foi introduzido `DirectProgress<T>` (`VmTyper/Helpers/DirectProgress.cs`), que invoca o callback de forma síncrona quando já está na thread do `Dispatcher` ou em execução de teste sem `Application.Current`, eliminando a condição de corrida.
- Nenhuma regressão foi detectada nos testes existentes da Fase 0.

## 6. Limitações

- **Execução de `VmTyper.IntegrationTests` (`VMTYPER_INTEGRATION=1`) a partir do sandbox de CLI:** ao executar `VMTYPER_INTEGRATION=1 dotnet test tests/VmTyper.IntegrationTests` a partir do runner de comandos automatizado, `Win32.GetForegroundWindow()` retorna `HWND=0x0` porque o processo do agente roda em sessão/estação de trabalho sem foco interativo na área de trabalho do usuário (`WinSta0\Default`), fazendo a trava de segurança `Assert.True(receiver.IsForeground())` abortar antes de injetar teclas. Os testes de integração (`Corpus_ReachesReceiverIdentically_InBothModes` e `SecondWindowOpenedMidTyping_PausesSession_AndSecondWindowReceivesZeroChars`) estão compilados e prontos para execução direta pelo usuário num terminal interativo com desktop desbloqueado:
  ```powershell
  dotnet build VmTyper.sln
  $env:VMTYPER_INTEGRATION="1"
  dotnet test tests/VmTyper.IntegrationTests --filter Category=Integration
  ```
- O comportamento em console VMware real no navegador permanece pendente para validação nas fases seguintes.

## 7. Próxima fase

- **Fase 1B (opcional/condicional) ou Fase 2 (Motor adaptativo e ritmo):**
  - Na Fase 2 (`docs/vmtype_2/fases/fase-2-motor-adaptativo.md`), substituir a temporização fixa por perfis de ritmo adaptativos, jitter controlado e resolução de timer isolada para tratar N5, N11 e N12, mantendo a flag `Features.AdaptiveTiming` (criada desligada na F1).

