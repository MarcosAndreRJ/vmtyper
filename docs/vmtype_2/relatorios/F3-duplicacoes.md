# Relatório da Fase 3 — Investigação e Prevenção de Caracteres Duplicados

- **Data:** 2026-11-09
- **Branch:** `feature/vmtyper2`
- **Escopo:** `docs/vmtype_2/fases/fase-3-duplicacoes.md` (§0 a §7)

---

## 1. Resumo das Entregas

1. **`TypingDiagnosticsEngine` (§1):**
   - Implementado em `VmTyper/Services/TypingDiagnosticsEngine.cs` e integrado a `TypingScheduler` e `KeyboardInputService`.
   - Grava arquivos JSONL por sessão em `%AppData%\VmTyper\diagnostics\session-<sessionId>.jsonl` com linha `header` (versão, perfil, método, layout, flags, resolução de timer via `NtQueryTimerResolution`, SO e `SPI_GETKEYBOARDDELAY`/`SPI_GETKEYBOARDSPEED` do host) e linhas `unit` (`s`, `i`, `cat`, `m`, `vk`, `sc`, `ext`, `mods`, `tDown`, `tUp` em µs a partir de `Stopwatch.GetTimestamp()`, `ret`, `err`, `gapNext`, `hwnd`, `fg`, `note`).
   - Sem conteúdo textual por padrão (`ch` omitido quando `DetailedTrace == false`, testado em `DiagnosticsEngine_DefaultDetailedTraceFalse_DoesNotWriteCharacterContent`).
   - Inclui rotação automática das últimas `N` sessões e botão **"Excluir diagnósticos"** em `DiagnosticsWindow` (`PurgeAllDiagnostics()`, testado em `DiagnosticsEngine_RotationAndPurge_WorkAsExpected`).
2. **Página HTML de diagnóstico (§2):**
   - Criada em `tools/diagnostics-page/index.html` (estática, compatível com `file://`, servidor HTTP local ou `SetVirtualHostNameToFolderMapping`).
   - Registra `keydown`, `keyup`, `beforeinput` e `input` (`key`, `code`, `location`, `repeat`, `isTrusted`, `timeStamp` via `performance.now()` e modificadores), sinaliza imediatamente `repeat === true` ou dois `keydown` do mesmo `code` sem `keyup` intermediário como suspeitos, calcula SHA-256 (12 hex) e contagens por categoria, exporta JSON e expõe `window.__vmtyperDiag` (`getReport()`, `clear()`).
3. **Receptor na VM e painel de fingerprint esperado (§3):**
   - `tools/VmTyper.TestReceiver/KeyboardHook.cs` registra `RepeatedDownWithoutUp` (`WH_KEYBOARD_LL` detectando `KeyDown` repetido do mesmo scan code sem `KeyUp` intermediário).
   - `tools/VmTyper.TestReceiver/ReceiverWindow.cs` exibe na tela em tempo real: `SHA-256(12)`, comprimento (`len` e `graf`), contagens por categoria (`L`, `D`, `P`, `S`, `NL`, `T`), `DownRepetido` e botão **"Corpus esperado..."** que indica a posição da 1ª diferença em relação ao arquivo de corpus carregado.
   - `VmTyper/Views/DiagnosticsWindow.xaml` exibe na tela a mesma linha de fingerprint esperado (`SHA-256(12)`, comprimento, graphemes e contagens por categoria).
4. **Corpora versionados com `.sha256` (§4):**
   - Criados e verificados em `tests/corpora/`: `repetidas.txt`, `acentos-ptbr.txt`, `teclas-mortas-literais.txt`, `numeros-simbolos.txt`, `codigo-delphi.pas`, `codigo-csharp.cs`, `longo-10k.txt` e `tabs-indentacao.txt` (além de `basico.txt`, `acentuacao.txt` e `teclas-mortas.txt`), cada um acompanhado de seu arquivo `<nome>.sha256` verificado em `AllCorpora_Sha256Matches_AndSimulatedExecutionProducesZeroDivergences`.
5. **Classificador de divergências `DivergenceClassifier` (§5):**
   - Implementado em `VmTyper/Services/DivergenceClassifier.cs` usando alinhamento ótimo por diff de Myers em nível de grapheme (`StringInfo.GetTextElementEnumerator`).
   - Classifica `Duplication`, `Omission`, `Substitution`, `AccentuationFailure` (tanto perda/troca de diacrítico quanto acento isolado seguido da vogal base, ex.: `"pão"` → `"p~ao"`), `Transposition` (par adjacente trocado) e `IndentationDivergence` (espaços/tabs no início de linha).
   - Mascara a janela de contexto (`±5` unidades) por categoria quando `detailedTrace == false` e correlaciona cada divergência ao `DiagnosticEventRecord` pelo índice `i` (testado em `DivergenceClassifier_ClassifiesAllDivergenceTypes` e `DivergenceClassifier_MasksContextWhenDetailedTraceFalse_AndCorrelatesEvent`).

---

## 2. Resultados da Matriz de Experimentos (§6)

Os dados brutos gerados automaticamente (`N = 5` repetições por célula) estão publicados em:
- `docs/vmtype_2/relatorios/F3-dados/f3-matriz-local-corpora-metodos-perfis.json`
- `docs/vmtype_2/relatorios/F3-dados/f3-varredura-h10-h14-repetidas.json`

### 2.1 Resumo por Nível, Método e Perfil (`N = 5` repetições por célula)

| Nível | Método | Perfis testados | Corpora | N por célula | Duplicações | Omissões (média por corpus) | Falhas de acentuação | Transposições | Divergência de indentação | Taxa (no método `Automatico` / `Unicode`) |
|---|---|---|---|---:|---:|---:|---:|---:|---:|---:|
| Local (simulador ABNT2 + executor) | `Automatico` | `Legado`, `Rápido`, `Compatibilidade VM`, `Natural` | 8 corpora (`repetidas`, `acentos-ptbr`, `teclas-mortas-literais`, `numeros-simbolos`, `codigo-delphi`, `codigo-csharp`, `tabs-indentacao`, `longo-10k`) | 5 | 0 | 0 | 0 | 0 | 0 | **0,00 / 10k** |
| Local (simulador ABNT2 + executor) | `Unicode` | `Legado`, `Rápido`, `Compatibilidade VM`, `Natural` | 8 corpora | 5 | 0 | 0 | 0 | 0 | 0 | **0,00 / 10k** |
| Local (simulador ABNT2 + executor) | `TecladoFisico` | `Legado`, `Rápido`, `Compatibilidade VM`, `Natural` | 8 corpora | 5 | 0 | > 0 apenas em símbolos não mapeados diretamente no subconjunto físico (`[ ] { } \ | < > ? ' "`) | 0 | 0 | 0 | 0,00 / 10k nos corpora alfanuméricos/acentuados (`repetidas`, `acentos-ptbr`, `tabs-indentacao`, `longo-10k`) |
| Navegador (WebView2 + `tools/diagnostics-page/index.html`) | `SendInput` vs `CDP` | `Legado`, `Natural`, `Compatibilidade VM` | Corpora F3 | Executado na PoC F4 (ver `docs/vmtype_2/relatorios/F4-poc-webview2.md`) | — | — | — | — | — | Ver F4 |
| Console VMware (guest `VmTyper.TestReceiver --hook`) | Todos | Todos | Todos | 0 (roteiro pronto) | — | — | — | — | — | **Pendente (VMware)** |

### 2.2 Status das Hipóteses H1–H14 (§0 e §6)

| # | Hipótese | Nível | Status | Evidência / Observação |
|---|---|---|---|---|
| **H1** | KeyUp perdido | Local, VM | **Sustentada no código v1 (corrigida na F1) / Pendente (VMware)** | Na F1 (§2.2), `PartialFailure_AtEveryPositionOfCombo_LeavesNoOrphanDowns` e `Cancellation_AtEveryPosition_LeavesNoOrphanDowns_AndReportsIndex` demonstraram que falhas parciais ou cancelamentos sem `InjectedKeyTracker` podiam deixar teclas sem `KeyUp`. Com `InjectedKeyTracker`, `StuckKeys = 0` em 100% dos testes locais. No guest VMware permanece **Pendente (VMware)**. |
| **H2** | Auto-repeat no guest por latência de rede | VM | **Pendente (VMware)** | O receptor `VmTyper.TestReceiver` agora contabiliza `RepeatedDownWithoutUp` via `WH_KEYBOARD_LL` para medir diretamente na VM. |
| **H3** | Duplicação no navegador | Navegador | **Refutada localmente (em CDP / DOM local)** | A página `tools/diagnostics-page/index.html` monitora `e.repeat` e `keydown` duplo sem `keyup`; nos testes automatizados em WebView2 (F4), eventos injetados com hold `2–22 ms` não disparam auto-repeat do navegador (`repeat = false`). |
| **H4** | Peculiaridade do console VMware | VM | **Pendente (VMware)** | Depende da comparação presencial `VM − navegador` com o mesmo corpus e perfil. |
| **H5** | Repetição no SO da VM (`repeat delay` curto) | VM | **Pendente (VMware)** | Depende de sessão presencial variando o repeat delay do guest. |
| **H6** | Mudança de foco durante a digitação | Todos | **Sustentada (mitigada pelo `TargetGuard`)** | Testada em `LosingFocusMidTyping_PausesAtExactUnitIndex_WithoutCuttingUnit_AndRequiresConfirmationForNewHwnd`: a perda de foco entre unidades pausa imediatamente sem partir a unidade ao meio. |
| **H7** | Eventos duplicados pelo próprio app | Local | **Refutada após F1** | Com `TypingSessionCoordinator` (sessão única) e fila sem auto-reenvio, o JSONL registra cada índice `i` exatamente uma vez (`0` duplicações em todas as 960 execuções da matriz local). |
| **H8** | Modificadores presos | Local, VM | **Sustentada no código v1 (corrigida na F1)** | Corrigida via verificação pré-sessão (`PhysicalModifierHeld_RefusesToStartSession`) e destravamento de emergência (`EmergencyReleaseTrackedKeys`). |
| **H9** | Falha em tecla morta literal (`~a`, `^^`, `'a'`) | Local, VM | **Sustentada (corrigida na F1)** | Sem `VK_SPACE` após tecla morta literal (defeito N1), `~` + `a` produzia `ã` e `~~` produzia um único `~`. Corrigido e verificado em `teclas-mortas-literais.txt` (`0` divergências em `AllCorpora_Sha256Matches_AndSimulatedExecutionProducesZeroDivergences`). |
| **H10** | Temporização insuficiente (gap/hold extremos) | Todos | **Sustentada para `Task.Delay` sob carga (F2) / Pendente (VMware)** | O experimento F2 (`rhythm-f2-20261109-212909.md`) provou que `Task.Delay` sofria salto para `32,8–62,9 ms` sob carga de CPU, enquanto `HighResolutionDelayProvider` manteve hold máximo em `2,08–2,32 ms`. |
| **H11** | Interferência de substituições em cascata | Local | **Sustentada no código v1 (corrigida na F1)** | No código v1, `string.Replace` sequencial encadeava regras (`Substitutions_SinglePass_DoNotChain_AndDoNotCollideWithPua`). Corrigido pelo tokenizador de passada única no `KeyStrokePlanner`. |
| **H12** | Físico × Automático × Unicode | Todos | **Sustentada localmente** | A matriz `f3-matriz-local-corpora-metodos-perfis.json` demonstra que `TecladoFisico` omite caracteres sem mapeamento físico direto no layout alvo, enquanto `Automatico` combina scan codes reais para todas as teclas do layout (incluindo teclas mortas) com fallback Unicode apenas para símbolos fora do layout (`0` divergências nos 8 corpora). |
| **H13** | Layout host ≠ VM | VM | **Pendente (VMware)** | Depende de teste presencial com layout divergente no guest. |
| **H14** | Repetição de tecla idêntica (`repetidas.txt`) | Todos | **Sustentada preventivamente (`SameKeyMinGapMs`) / Pendente (VMware)** | Na varredura `f3-varredura-h10-h14-repetidas.json`, quando `gapMs = 0` ou `3`, `SameKeyMinGapMs = 20` ou `50` eleva o gap efetivo entre teclas físicas idênticas (`aa`, `ss`, `xx`) para `20,0 ms` e `50,0 ms` respectivamente, evitando colisão de pacotes `KeyUp(i)/KeyDown(i+1)` da mesma tecla sem atrasar teclas distintas. |

---

## 3. Políticas de Prevenção Ativadas (§7)

Conforme a regra do §7, cada política implementada aponta para a hipótese e evidência correspondente, e nenhuma política proibida (remoção de repetidos, Backspace automático ou reenvio por suspeita) existe no código:

1. **Piso de gap entre unidades com a mesma tecla (`SameKeyMinGapMs`, H1/H14):** ativo nos perfis `Rápido` (`20 ms`), `Natural` (`32 ms`) e `Compatibilidade VM` (`50 ms`), verificado em `f3-varredura-h10-h14-repetidas.json`.
2. **Teto estrito de hold (`SafetyLimits.MaxHoldMs`, H2/H5):** limitado a `25–55 ms` nos perfis embutidos e executado via `HighResolutionDelayProvider.HoldExact` na thread `AboveNormal` (`rhythm-f2-20261109-212909.md`).
3. **Desaceleração adaptativa sob sobrecarga do host (`TypingOptions.EnableAdaptiveSlowdown`, H10):** em `TypingScheduler.cs`, quando a duração real de uma unidade excede `Hold + 8 ms` (sinal medido de preempção do SO), acrescenta `+15 ms` ao próximo gap (limitado a `SafetyLimits.MaxGapMs`).
4. **Recomendação de perfil e método por ambiente:** documentada no `docs/vmtype_2/adrs/0008-recomendacao-perfil-e-metodo-por-ambiente.md`.

---

## 4. Critérios de Aceite da Fase 3

- [x] Engine de diagnóstico (`TypingDiagnosticsEngine`), página HTML (`tools/diagnostics-page/index.html`), receptor (`VmTyper.TestReceiver`) e classificador (`DivergenceClassifier`) implementados e com testes automatizados passando.
- [x] Matriz executada no nível local com dados brutos publicados em `docs/vmtype_2/relatorios/F3-dados/`.
- [x] Nível VMware marcado explicitamente como **Pendente (VMware)** com receptor e roteiro prontos.
- [x] Toda política de prevenção ativada aponta para a hipótese e os dados correspondentes.
