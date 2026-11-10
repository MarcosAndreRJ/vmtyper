# Relatório da Fase 5 — Interface e Experiência de Uso

- **Data:** 2026-11-09
- **Branch:** `feature/vmtyper2`
- **Escopo:** `docs/vmtype_2/fases/fase-5-interface.md` (§1 a §5)

---

## 1. Painel Principal e Seleção de Ambiente (§1 e §2)

1. **Barra de sessão (topo) em `VmTyper/MainWindow.xaml` e `VmTyper/ViewModels/MainViewModel.cs`:**
   - Indicador do destino (`TargetIndicatorText`) exibindo ícone, processo, título truncado e estado do `TargetGuard` (`🛡 Monitorando`, `⚠ Perdido` ou `○ Desligado`).
   - Seletores compactos de **Método** (`Automático`, `Físico`, `Unicode`), **Perfil** (`Legado`, `Rápido`, `Compatibilidade`, `Natural`, `Personalizado`) e **Layout de destino** (`HostForeground`, `00010416`, `00020409`, `00000409`).
   - Seletor de **Ambiente** (`Externo` / `Integrado (experimental)`), visível somente quando a feature flag `EmbeddedBrowser` está habilitada e bloqueado durante qualquer sessão ativa (`CanEditQueue == false`). Selecionar `Integrado` abre `EmbeddedBrowserWindow`.
2. **Fila de blocos (`VmTyper/Models/TextBlockItem.cs`):**
   - Cada estado possui ícone e texto próprios além de cor de apoio (`○ Vazio`, `● Pronto`, `▶ Próximo ▶`, `⌨ Digitando`, `⏸ Pausado`, `↗ Enviado`, `◐ Parcial (N/M)`, `✗ Falha`, `⚠ Interrompido`), testado em `TextBlockItem_AllStates_ExposeDistinctIconTextAndSupportingColor`.
3. **Barra de execução (rodapé) e mensagens de conclusão:**
   - Botões `DIGITAR BLOCO N`, `PAUSAR`/`CONTINUAR` e `CANCELAR (ESC)` exibem os atalhos configurados nos rótulos.
   - Progresso em unidades com `Enviadas · Puladas · Falhas` (`UnitsProgressSummary`), tempo decorrido (`ElapsedText`), CPS (`CpsText`) e botão `"Diagnóstico"`.
   - Mensagem de conclusão sem receptor segue o formato `"Bloco N: M unidades enviadas. Recebimento não verificado."` (`Resources/Strings.resx`), sem jamais afirmar `"concluído com sucesso"` ou `"Confirmado"`. Quando `Skipped > 0` ou `Failed > 0`, `StatusSeverity` assume `Warning` (amarelo) ou `Error` (vermelho) e o bloco permanece em `PartiallySent`.

---

## 2. Configurações Avançadas e `SettingsViewModel` (§3)

- A cópia manual no code-behind foi migrada para `VmTyper/ViewModels/SettingsViewModel.cs` implementando `INotifyDataErrorInfo` (fechando o item **I** com teste automatizado de ida e volta e validação em `SettingsViewModel_Validation_RoundTrip_AndRestoreDefaultsPerTabAndGlobal`).
- `VmTyper/Views/SettingsWindow.xaml` foi organizado nas 8 abas especificadas:
  1. **Digitação:** presets, intervalos, método, layout de destino, perfil + editor completo do perfil `Personalizado` (`HoldMs`, `GapMs`, `EnterPauseMs`, `PunctuationPauseMs`, `WordPauseMs`, `WordPauseChance`, `SameKeyMinGapMs`, `CodeLineMultiplier`, `Correlation`) com validação de limites e prévia `dry-run` em tempo real (`DryRunPreviewText`).
  2. **Segurança de foco:** `EnableTargetGuard`, modo estrito (`TargetGuardStrict`), navegador esperado (`ValidateTargetWindow` + `ExpectedBrowser`) e contagem regressiva de retomada (`ResumeCountdownSeconds`).
  3. **Substituições:** aviso visual de regra duplicada (`DuplicateSubstitutionsWarning`) e validação de combinação sem tecla capturada.
  4. **Atalhos:** captura de atalhos globais e comportamento de janela/bandeja.
  5. **Persistência:** `QueueTextPersistence` (`Full`, `Encrypted`, `None`), limpeza após envio, caminho dos arquivos e botão `"Abrir pasta"`.
  6. **Diagnóstico:** gravação JSONL on/off (`EnableDiagnosticsJsonl`), `DetailedTrace` com aviso de conteúdo sensível, retenção (`DiagnosticsRetentionSessions`) e botão `"Excluir diagnósticos"`.
  7. **Navegador integrado:** flag `EmbeddedBrowser`, caminho do perfil isolado e botão `"Limpar dados do perfil"` com confirmação.
  8. **Experimental:** lista completa de feature flags (`AdaptiveTiming`, `TargetGuardStrict`, `EmbeddedBrowser`, `CdpInput`, `DetailedTrace`) com descrição.
- Botões **Restaurar aba atual** (`RestoreTabDefaults`) e **Restaurar todos os padrões** (`RestoreAllDefaults`), ambos com diálogo de confirmação.

---

## 3. Painel de Diagnóstico Ampliado e Semântica dos 5 Estados de Entrega (§4)

Implementados `VmTyper/Models/DeliverySemantics.cs`, `VmTyper/ViewModels/DiagnosticsViewModel.cs` e `VmTyper/Views/DiagnosticsWindow.xaml`:

| Estado (`DeliverySemanticState`) | Rótulo Visual (Ícone + Texto) | Cor (`ColorHex` / `BackgroundHex`) | Regra de Derivação |
|---|---|---|---|
| `Sent` | `↗ Enviado` | `#1E6BB8` / `#EBF3FA` | `SendInput`/CDP retornou sucesso, sem observação de receptor/página |
| `Confirmed` | `✓ Confirmado` | `#1F7A3E` / `#EAF6EE` | Somente após cruzamento com JSON do receptor/página com `IsIdentical == true` e `suspiciousCount == 0` |
| `Suspicious` | `⚠ Suspeito` | `#B86B11` / `#FFF5E5` | Indício (`repeat`, Down duplo ou instabilidade de foco), sem divergência confirmada |
| `Unknown` | `? Desconhecido` | `#555555` / `#F2F2F2` | Nenhuma observação disponível |
| `Failed` | `✗ Falha` | `#B82E2E` / `#FDECEC` | Erro nativo, unidade pulada ou divergência confirmada no classificador |

As 4 abas do `DiagnosticsWindow`:
1. **Sessão atual / última:** linha do tempo de eventos do `.jsonl` (índice, categoria, método, gap e hold, retorno e estado semântico).
2. **Teste de teclado:** envio de teste protegido pelo `TypingSessionCoordinator` com impressão digital SHA-256(12) esperada ao vivo.
3. **Comparação:** importação do JSON do receptor ou da página HTML de diagnóstico executando `DivergenceClassifier.Classify` e exibindo contagens por classe e o badge semântico derivado.
4. **Desempenho por método/perfil:** tabela carregada automaticamente dos benchmarks em `docs/vmtype_2/relatorios/F3-dados/`.

---

## 4. Acessibilidade, Consistência e Testes (§5)

- Textos em pt-BR centralizados em `VmTyper/Resources/Strings.resx` e `VmTyper/Resources/Strings.cs`.
- Todos os botões de `MainWindow.xaml`, `SettingsWindow.xaml`, `DiagnosticsWindow.xaml` e `EmbeddedBrowserWindow.xaml` possuem `AutomationProperties.Name` definido (verificado automaticamente por inspeção de árvore XAML em `Phase5Smoke_TwoBlocks_QueueStates_AndAccessibility`).
- Suíte unitária (`tests/VmTyper.Tests/Phase5UiAndViewModelsTests.cs`): **110/110 testes aprovados** (`dotnet test tests/VmTyper.Tests -p:Platform=x64`), zero avisos de compilação (`dotnet build VmTyper.sln -warnaserror`).

---

## 5. Critérios de Aceite da Fase 5

- [x] Todas as funções novas acessíveis pela UI, com as experimentais atrás de flags (`AdaptiveTiming`, `TargetGuardStrict`, `EmbeddedBrowser`, `CdpInput`, `DetailedTrace`).
- [x] Os cinco estados de entrega (`Enviado`, `Confirmado`, `Suspeito`, `Desconhecido`, `Falha`) visual e semanticamente distintos, e `"Confirmado"` nunca derivado apenas de `SendInput` (testado em `DeliverySemantics_ConfirmedIsNeverDerivedFromLocalSent` e `DiagnosticsViewModel_TimelineAndObserverComparison_EnforceFiveSemanticStates`).
- [x] Restaurar padrões funcionando por aba e globalmente (testado em `SettingsViewModel_Validation_RoundTrip_AndRestoreDefaultsPerTabAndGlobal`).
- [x] Nenhuma regressão na suíte (110/110 testes passando).
