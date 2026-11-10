# Relatório da Fase 2: Motor de temporização configurável ("digitação humana e adaptativa")

- **Período:** 2026-11-09 → 2026-11-09
- **Branch / commits:** `feature/vmtyper2` · `ac3dada..HEAD`
- **Ambiente:** Windows 10 Pro 10.0.19045 · SDK `10.0.401` · runtime Desktop `10.0.12` · alvo `net10.0-windows` · navegador: não utilizado · VMware: não utilizado

## 1. Diagnóstico

### 1.1 Problemas tratados (N5, N11, N12)

| # | Problema | Evidência de verificação | Status |
|---|---|---|---|
| N5 | `Task.Delay(1)`, `Task.Delay(3)` e `Task.Delay(10)` produziam ~15,6 ms ociosos e ~33 ms sob carga por dependerem da resolução global do timer do Windows | `MeasureF2_LegacyTaskDelay_Vs_HighResolutionProvider_IdleAndUnderLoad` ([evidencias/rhythm-f2-20261109-212909.md](evidencias/rhythm-f2-20261109-212909.md)) | Sustentado por medição e tratado por `HighResolutionDelayProvider` |
| N11 | Ausência de variação correlacionada, pausas contextuais (palavra, pontuação, Enter, linhas de código) e piso para teclas repetidas consecutivas | `SameSeed_SameTimingSequence`, `AllTimings_WithinSafetyLimits`, `Natural_IsNotIid`, `PunctuationAndEnter_GetLongerGaps` (`TypingTimingTests`) | Sustentado e coberto por testes unitários |
| N12 | Preempção de thread do pool estendia o hold e o gap sob carga de CPU | `HighResolutionDelayProvider` com thread dedicada `ThreadPriority.AboveNormal` reduziu o máximo de Hold sob carga para `2,08–2,32 ms` nos cenários `CharDelayMs = 1..30` ([evidencias/rhythm-f2-20261109-212909.md](evidencias/rhythm-f2-20261109-212909.md)) | Sustentado por medição |

### 1.2 Medição comparativa de precisão (§1.3)

Arquivo completo: [evidencias/rhythm-f2-20261109-212909.md](evidencias/rhythm-f2-20261109-212909.md). Intervalo esperado = `Hold (2 ms) + CharDelayMs`.

| Provedor | Condição | `CharDelayMs` | N | Intervalo med (ms) | p95 | máx | Hold med (ms) | p95 | máx | CPU motor (ms) | Timer SO (ms) |
|---|---|---|---|---|---|---|---|---|---|---|---|
| Legado (`Task.Delay`) | ocioso | 0 | 300 | 2,04 | 2,35 | 10,71 | 2,01 | 2,28 | 10,68 | 750,0 | 15,625 |
| Legado (`Task.Delay`) | ocioso | 1 | 300 | 15,57 | 29,20 | 91,95 | 2,01 | 2,07 | 7,74 | 1546,9 | 15,625 |
| Legado (`Task.Delay`) | ocioso | 3 | 300 | 15,73 | 26,91 | 31,01 | 2,01 | 2,03 | 2,28 | 437,5 | 15,625 |
| Legado (`Task.Delay`) | ocioso | 10 | 100 | 15,73 | 26,37 | 44,31 | 2,01 | 2,09 | 15,94 | 171,9 | 1 |
| Legado (`Task.Delay`) | ocioso | 30 | 100 | 39,17 | 68,29 | 196,95 | 2,02 | 3,17 | 11,34 | 328,1 | 1 |
| Legado (`Task.Delay`) | carga | 0 | 300 | 2,04 | 2,31 | 12,52 | 2,01 | 2,21 | 12,48 | 703,1 | 1 |
| Legado (`Task.Delay`) | carga | 1 | 300 | 32,82 | 34,95 | 45,40 | 2,01 | 2,07 | 8,63 | 500,0 | 1 |
| Legado (`Task.Delay`) | carga | 3 | 300 | 32,96 | 35,20 | 41,20 | 2,01 | 2,08 | 2,34 | 546,9 | 1 |
| Legado (`Task.Delay`) | carga | 10 | 100 | 33,23 | 36,17 | 48,88 | 2,01 | 2,05 | 2,13 | 265,6 | 1 |
| Legado (`Task.Delay`) | carga | 30 | 100 | 62,98 | 68,07 | 73,30 | 2,01 | 2,06 | 2,16 | 187,5 | 1 |
| `HighResolutionDelayProvider` | ocioso | 0 | 300 | 2,02 | 2,12 | 5,64 | 2,01 | 2,09 | 5,62 | 656,3 | 1 |
| `HighResolutionDelayProvider` | ocioso | 1 | 300 | 3,35 | 4,11 | 7,96 | 2,01 | 2,03 | 2,20 | 609,4 | 1 |
| `HighResolutionDelayProvider` | ocioso | 3 | 300 | 5,20 | 5,95 | 6,53 | 2,01 | 2,02 | 2,18 | 390,6 | 1 |
| `HighResolutionDelayProvider` | ocioso | 10 | 100 | 12,35 | 12,94 | 13,27 | 2,01 | 2,04 | 2,12 | 187,5 | 1 |
| `HighResolutionDelayProvider` | ocioso | 30 | 100 | 32,34 | 32,91 | 40,65 | 2,01 | 2,03 | 2,13 | 140,6 | 1 |
| `HighResolutionDelayProvider` | carga | 0 | 300 | 2,01 | 2,07 | 2,36 | 2,01 | 2,04 | 2,32 | 640,6 | 1 |
| `HighResolutionDelayProvider` | carga | 1 | 300 | 3,25 | 3,59 | 4,19 | 2,01 | 2,03 | 2,10 | 531,3 | 1 |
| `HighResolutionDelayProvider` | carga | 3 | 300 | 5,16 | 5,92 | 6,26 | 2,01 | 2,03 | 2,15 | 515,6 | 1 |
| `HighResolutionDelayProvider` | carga | 10 | 100 | 12,27 | 12,95 | 13,19 | 2,01 | 2,03 | 2,13 | 31,3 | 1 |
| `HighResolutionDelayProvider` | carga | 30 | 100 | 32,28 | 33,07 | 33,44 | 2,01 | 2,04 | 2,08 | 203,1 | 1 |

Leitura:
- Com `HighResolutionDelayProvider`, os intervalos medianos para `CharDelayMs = 1, 3, 10, 30` (alvos de `3, 5, 12, 32 ms` somando o Hold de `2 ms`) foram **`3,35 / 5,20 / 12,35 / 32,34 ms`** com o sistema ocioso e **`3,25 / 5,16 / 12,27 / 32,28 ms`** sob carga total nos 8 processadores lógicos.
- Sem alterar `timeBeginPeriod` global, `CreateWaitableTimerExW(..., CREATE_WAITABLE_TIMER_HIGH_RESOLUTION)` eliminou o degrau de `15,625 ms` observado em `Legado (Task.Delay)`.

## 2. Implementações

- **§1 `IDelayProvider` e `HighResolutionDelayProvider`:** `CreateWaitableTimerExW` (`CREATE_WAITABLE_TIMER_HIGH_RESOLUTION`) + `WaitForMultipleObjects` (timer + handle de cancelamento) em thread dedicada `ThreadPriority.AboveNormal`, com fallback híbrido sem `timeBeginPeriod` e `HoldExact` não cancelável.
- **§2 `TypingProfile` e `SafetyLimits`:** perfis `Legacy` (padrão), `Fast`, `Compatibility`, `Natural` e `Custom`, documentados no [ADR 0007](../adrs/0007-perfis-iniciais-de-temporizacao.md).
- **§3 `TypingTimingPolicy`:** função pura e determinística (por `Seed`) com deriva correlacionada AR(1), gap base log-normal truncado, pausas contextuais (fim de palavra proporcional ao comprimento, pontuação, Enter e multiplicador para linhas de código), piso `SameKeyMinGapMs` para teclas repetidas consecutivas e clamp final em `SafetyLimits`.
- **§4 `TypingScheduler`:** une o plano (`KeyStrokePlanner`), a política (`TypingTimingPolicy`), o executor (`KeyStrokeExecutor`), o provedor de atraso (`IDelayProvider`) e as métricas (`TypingMetrics`), incluindo modo `DryRun` e estimativa de duração no pré-voo. `KeyboardInputService` atua como fachada mantendo `IKeyboardInputService`.
- **§5 `TypingMetrics` / `SessionMetrics`:** coleta unidades tentadas/enviadas/puladas/falhas, eventos enviados, CPS médio, tempo total, tempo de CPU do processo, incidentes de foco e histogramas (solicitado vs. obtido) de Hold e Gap, persistidos em `TypingSession.Metrics`.
- **§6 Configuração e UI mínima:** `AppSettings.TypingProfile`, `AppSettings.CustomProfile`, `AdaptiveTimingCheck` e `TypingProfileCombo` na aba Digitação de `SettingsWindow` (com `AdaptiveTiming` desligada por padrão, mantendo apenas o perfil `Legacy` ativo até ser habilitada).

## 3. Arquivos alterados

| Arquivo | Tipo | Motivo |
|---|---|---|
| `VmTyper/Services/IDelayProvider.cs` | novo | Interface de temporização precisa (§1.1) |
| `VmTyper/Services/HighResolutionDelayProvider.cs` | novo | Implementação com `CreateWaitableTimerExW` e thread `AboveNormal` (§1.2) |
| `VmTyper/Models/TypingProfile.cs` | novo | Modelo `TypingProfile`, `ProfileKind`, `MsRange` e `SafetyLimits` (§2) |
| `VmTyper/Services/TypingTimingPolicy.cs` | novo | Política AR(1) + log-normal truncada + pausas contextuais + `SameKeyMinGapMs` (§3) |
| `VmTyper/Services/TypingScheduler.cs` | novo | Orquestrador de execução, `DryRun` e estimativa de duração (§4) |
| `VmTyper/Models/SessionMetrics.cs` | novo | `TypingMetrics`, `SessionMetrics` e `TimingHistogramSummary` (§5) |
| `VmTyper/Services/KeyStrokeExecutor.cs` | alterado | Suporte a `IDelayProvider` e `TimeSpan holdDuration` (§1, §4) |
| `VmTyper/Services/KeyboardInputService.cs` | alterado | Fachada sobre `TypingScheduler`, expondo `DryRun` e `LastSessionMetrics` (§4) |
| `VmTyper/Models/TypingOptions.cs`, `TypingSession.cs`, `AppSettings.cs` | alterados | Propriedades `Profile`, `Metrics`, `TypingProfile` e `CustomProfile` (§2, §5, §6) |
| `VmTyper/ViewModels/MainViewModel.cs` | alterado | Resolução de perfil, estimativa no status e gravação de `SessionMetrics` no histórico (§4, §5) |
| `VmTyper/Services/SettingsMapper.cs`, `Views/SettingsWindow.xaml`, `Views/SettingsWindow.xaml.cs` | alterados | Controles `AdaptiveTimingCheck` e `TypingProfileCombo` (§6) |
| `docs/vmtype_2/adrs/0007-perfis-iniciais-de-temporizacao.md` | novo | Registro das hipóteses iniciais de perfil para calibração na F3 |
| `docs/vmtype_2/relatorios/evidencias/rhythm-f2-20261109-212909.md` | novo | Medição de precisão Legado vs. `HighResolutionDelayProvider` (§1.3) |
| `tests/VmTyper.Tests/TypingTimingTests.cs`, `SettingsTests.cs` | novo / alterado | 12 novos testes unitários e property-based (10k unidades/perfil) |
| `tests/VmTyper.IntegrationTests/RhythmBaselineTests.cs`, `BaselineFidelityTests.cs` | alterados | Medição comparativa F2 e teste de integração para todos os perfis |

## 4. Testes

Comandos executados:
```powershell
dotnet build VmTyper.sln -warnaserror -v minimal
dotnet test tests/VmTyper.Tests -v minimal
$env:VMTYPER_MEASURE="1"; dotnet test tests/VmTyper.IntegrationTests --filter "FullyQualifiedName~MeasureF2_LegacyTaskDelay_Vs_HighResolutionProvider_IdleAndUnderLoad" -v normal
```

| Execução | Nível | Total | Aprovados | Reprovados | Ignorados |
|---|---|---|---|---|---|
| `dotnet test tests/VmTyper.Tests` | Unit (incluindo property-based 10k unidades × 5 perfis e decodificador ABNT2) | 93 | 93 | 0 | 0 |
| `VMTYPER_MEASURE=1 dotnet test tests/VmTyper.IntegrationTests --filter ...MeasureF2...` | Integration / Medição (relógio e threads reais) | 1 | 1 | 0 | 0 |

## 5. Regressões

Nenhuma regressão detectada; toda a suíte de 81 testes da Fase 1/1B permaneceu verde (agora somando 93 testes em `VmTyper.Tests`), e `Legacy_Matches_V1_EventSequence` + `Plan_IsIdentical_AcrossProfiles` garantem que o plano de eventos é invariante em relação ao perfil de temporização.

## 6. Limitações

- Os valores dos perfis `Fast`, `Compatibility` e `Natural` são hipóteses iniciais (ADR 0007) e ainda não foram confrontados com a matriz experimental de duplicação (que pertence à Fase 3). Por isso, `Legacy` permanece como padrão e `AdaptiveTiming` vem desligada de fábrica.
- A execução de `Corpus_AllProfiles_ReachReceiverIdentically` com injeção real na janela do receptor (`VMTYPER_INTEGRATION=1`) requer sessão interativa com desktop desbloqueado (`GetForegroundWindow() != 0`).

## 7. Próxima fase

- **Fase 3: Investigação e mitigação de duplicações (`docs/vmtype_2/fases/fase-3-duplicacoes.md`)** — instrumentar o alinhamento de sequência no receptor, testar as hipóteses de duplicação (hold, gap entre teclas idênticas, modificadores, autoindentação) e calibrar os perfis com base em dados medidos.
