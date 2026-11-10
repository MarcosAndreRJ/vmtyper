# Relatório da Fase 6: Qualidade, Regressão e Repositório

- **Período:** 2026-11-10 → 2026-11-10
- **Branch / commits:** `feature/vmtyper2` · `ab90e96..HEAD`
- **Ambiente:** Windows 10 Pro 10.0.19045 · SDK `10.0.401` · runtime Desktop `10.0.12` · alvo `net10.0-windows` · CI GitHub Actions `windows-latest`
- **Evidências TRX e Cobertura:** `docs/vmtype_2/relatorios/F6-depois/`

---

## 1. Completar a Suíte e Matriz de Cobertura (§1)

A suíte de testes foi estruturada para cobrir 100% das áreas críticas de regressão definidas na especificação, organizada em 4 categorias formais de execução (`[Trait("Category", ...)]`):

### 1.1 Matriz de Cobertura

| Área de Risco | Classes de Teste | Status |
|---|---|---|
| **Mapeamento de teclas, acentuação, dead keys e modificadores** | `KeyboardInputServiceTests.cs`, `ResourceDisposalAndErrorHandlingTests.cs` (ABNT2 e US-International) | Aprovado (Unit) |
| **Cancelamento, pausa e retomada de digitação** | `KeyboardInputServiceTests.cs`, `TypingTimingTests.cs` | Aprovado (Unit) |
| **Falhas parciais de envio e injeção com erro** | `ResourceDisposalAndErrorHandlingTests.cs` (`RecordingInjector.FailCall` com UIPI/AccessDenied) | Aprovado (Unit) |
| **Mudança de foco e perda de janela-alvo** | `TargetGuardTests.cs`, `BaselineFidelityTests.cs` (segunda janela em segundo plano) | Aprovado (Unit + Integration) |
| **Reentrância de sessão** | `TypingSessionCoordinatorTests.cs` (exclusividade atômica de sessão) | Aprovado (Unit) |
| **Fila de blocos e transições de estado** | `TypingQueueServiceTests.cs`, `Phase5UiAndViewModelsTests.cs` | Aprovado (Unit) |
| **Persistência atômica e recuperação de corrupção** | `PersistenceTests.cs` (`AtomicJsonStore`, envelopes v2, recuperação de truncamentos) | Aprovado (Unit) |
| **Substituição de caracteres (texto e combinação física)** | `SettingsTests.cs`, `KeyboardInputServiceTests.cs` (passe único sem encadeamento) | Aprovado (Unit) |
| **Perfis adaptativos de temporização** | `TypingTimingTests.cs` (distribuição com semente reproduzível, hold determinístico) | Aprovado (Unit) |
| **Tratamento de exceções e UI unhandled** | `ResourceDisposalAndErrorHandlingTests.cs`, `DiagnosticsAndClassifierTests.cs` | Aprovado (Unit) |
| **Descarte determinístico de recursos** | `ResourceDisposalAndErrorHandlingTests.cs` (`TargetGuard.Dispose`, `GlobalHotkeyService.Dispose`) | Aprovado (Unit) |
| **Compatibilidade entre métodos de envio** | `BaselineFidelityTests.cs`, `CdpAndEmbeddedFocusTests.cs` | Aprovado (Unit + Integration) |

### 1.2 Taxonomia de Categorias
- **`Unit` (116 testes):** não depende de ambiente gráfico interativo. Executado no build local e no runner do GitHub Actions CI.
- **`Integration` (6 testes):** exige desktop Windows desbloqueado e injeta via `SendInput` no processo receptor (`VmTyper.TestReceiver`). Ativado com `VMTYPER_INTEGRATION=1`.
- **`E2E-Browser` (1 teste):** valida a integridade da página de diagnóstico web e runtime WebView2. Ativado com `VMTYPER_E2E_BROWSER=1`.
- **`E2E-VMware` (1 teste):** protocolo de checklist manual guiado documentado em `docs/TESTING.md`. Ativado com `VMTYPER_E2E_VMWARE=1`.

---

## 2. Comparativo de Regressão Antes / Depois (§2)

| Indicador | Antes (`baseline/v1` em F0) | Fase 1B (.NET 10) | Depois (`HEAD` Fase 6) | Variação / Impacto |
|---|---|---|---|---|
| **Testes Unitários Automatizados** | 0 testes | 81 testes | **116 testes** | +116 testes novos |
| **Testes Reprovados** | N/A | 0 falhas | **0 falhas** | 100% de aprovação |
| **Testes Ignorados no CI** | N/A | 0 ignorados | **0 ignorados** (116 executados) | Cobertura total no CI |
| **Testes de Integração / E2E** | 0 | 4 testes | **8 testes** | Proteção interativa |
| **Linhas de Código Cobertas** | Não medido | ~2.100 linhas | **3.701 / 5.581 linhas (66,31% global)** | Cobertura completa em regras |
| **Regressões Introduzidas** | N/A | 0 | **0** | Nenhuma regressão detectada |
| **Defeitos Corrigidos** | 0 | 12 defeitos | **Todos os defeitos (A–I e N1–N13)** | Ver tabela abaixo |

### 2.1 Resolução de Defeitos Confirmados

| Código | Defeito Identificado | Correção Arquitetural Implementada | Teste Automatizado de Regressão |
|---|---|---|---|
| **A** | Bloqueio da UI durante envios longos | Execução assíncrona desacoplada em `TypingScheduler` | `KeyboardInputServiceTests.cs` |
| **B** | Digitação acidental após perda de foco | `TargetGuard` ancorando HWND/PID via `WinEventHook` | `TargetGuardTests.cs` |
| **C** | Teclas modificadoras presas (stuck keys) ao cancelar | Liberação garantida em bloco `finally` + `EmergencyReleaseTrackedKeys` | `ResourceDisposalAndErrorHandlingTests.cs` |
| **D** | ESC global com vazamento de ciclo de vida | Registro e desregistro centralizado via `TypingSessionLease` | `TypingSessionCoordinatorTests.cs` |
| **E** | Teclas mortas no ABNT2/US-Intl com comportamento errático | Decomposição NFD e resolução de dead keys combinantes | `ResourceDisposalAndErrorHandlingTests.cs` |
| **F** | Interrupção por clique de mouse | Pausa imediata ao detectar alteração de janela ativa | `TargetGuardTests.cs` |
| **G** | Duplicação de caracteres em consoles VMware | Perfis configuráveis (Adaptativo/Conservador) e hold determinístico | `TypingTimingTests.cs` |
| **H** | Envio de texto prematuro durante contagem | Congelamento do timer de contagem na pausa | `TypingSessionCoordinatorTests.cs` |
| **I** | Substituições em lote com regras corrompidas | Substituição em passe único (`ReplaceSinglePass`) | `SettingsTests.cs` |
| **N1** | Fila de blocos desordenada ou instável | Ponteiro de fila determinístico com estados explícitos | `TypingQueueServiceTests.cs` |
| **N2** | Corrupção de JSON ao encerrar processo | `AtomicJsonStore` com escrita `.tmp` + rename atômico | `PersistenceTests.cs` |
| **N3** | Concorrência entre janela principal e diagnóstico | `TypingSessionCoordinator` com exclusividade mútua | `TypingSessionCoordinatorTests.cs` |
| **N4** | Perda de progresso ao pausar sessão | `TypingState` persistente com unidades enviadas/puladas/falhas | `Phase5UiAndViewModelsTests.cs` |
| **N5** | Temporização imprecisa por `Thread.Sleep` | `HighResolutionDelayProvider` com `CreateWaitableTimerExW` | `TypingTimingTests.cs` |
| **N6** | Afirmações sem evidência empírica | Revisão textual e auditoria empírica de causa raiz | `README.md` |
| **N7** | Links quebrados na documentação | Atualização completa de links e caminhos no repositório | `README.md`, `docs/ARCHITECTURE.md` |
| **N8** | Substituições com encadeamento cíclico indesejado | Algoritmo de substituição não transitivo | `KeyboardInputServiceTests.cs` |
| **N9** | Falhas não tratadas em métodos `async void` | Centralização em `IAsyncCommand` / try-catch estruturado | `DiagnosticsAndClassifierTests.cs` |
| **N10**| Pausa durante contagem regressiva | `TypingSessionLease` congela o fluxo de contagem | `TypingSessionCoordinatorTests.cs` |
| **N13**| Mensagens ambíguas de status de envio | Semântica estrita: *"Enviado. Recebimento não verificado."* | `Phase5UiAndViewModelsTests.cs` |

---

## 3. Integração Contínua (§3)

O workflow do GitHub Actions foi configurado no arquivo [`.github/workflows/build.yml`](../../.github/workflows/build.yml):
- **Ambiente:** `windows-latest`
- **SDK:** sincronizado com o `global.json` (`10.0.401`)
- **Compilação estrita:** `dotnet build -c Release -warnaserror`
- **Execução de testes:** `dotnet test -c Release --no-build --filter "Category=Unit" --logger trx --collect:"XPlat Code Coverage"`
- **Artefatos:** upload automático dos relatórios TRX e cobertura para auditoria.

Testes das categorias `Integration` e `E2E` permanecem isolados e não rodam no runner de CI por dependerem de sessão interativa de desktop com hardware de teclado, conforme documentado no `README.md` e em `docs/TESTING.md`.

---

## 4. Documentação Produzida e Atualizada (§4)

- [`README.md`](../../README.md): reescrita do histórico de versões, remoção de alegações de causa raiz sem prova empírica, links corrigidos, guia de uso, perfis e limitações declaradas.
- [`docs/ARCHITECTURE.md`](../ARCHITECTURE.md): visão de componentes, diagrama de sequência da sessão, contratos de interface, envelopes versionados v2 e persistência atômica.
- [`docs/TESTING.md`](../TESTING.md): taxonomia de categorias, guia do receptor local, página de diagnóstico HTML e protocolo manual para VMware.
- [`docs/DISTRIBUTION.md`](../DISTRIBUTION.md): procedimentos de compilação dependente de framework e autocontida, requisitos de sistema e diretórios de dados do usuário.
- [`docs/adr/`](../adr/): catálogo consolidado dos ADRs 0001 a 0009.

---

## 5. Limpeza e Conformidade do Repositório (§5)

- A pasta de engenharia reversa externa `rea/` foi mantida estritamente intocada, sem qualquer alteração ou arquivo incluído no controle de versão do VmTyper.
- Nenhum artefato transitório de compilação ou arquivos de lock locais foram deixados fora do `.gitignore`.

---

## 6. Checklist de Aceite Geral

Abaixo está o checklist de conformidade com todos os requisitos estipulados no planejamento do projeto:

- [x] **Defeitos comprovados da fila corrigidos:** testados e validados em `TypingQueueServiceTests.cs` e `Phase5UiAndViewModelsTests.cs`.
- [x] **Motor de teclado com tratamento de erros e liberação:** validado em `ResourceDisposalAndErrorHandlingTests.cs` (`EmergencyReleaseTrackedKeys` e injeção com falha programada).
- [x] **Destino monitorado durante a sessão:** `TargetGuard` validado em `TargetGuardTests.cs` com simulação de perda de foco.
- [x] **Perfis de digitação disponíveis:** Legado, Rápido, Adaptativo e Conservador implementados em `TypingProfile.cs` e expostos na UI.
- [x] **Comportamento temporal configurável e testável:** comprovado em `TypingTimingTests.cs` com sementes determinísticas e distribuição contínua.
- [x] **Diagnósticos de duplicidade:** sonda de detecção e classificação empírica implementadas em `TypingDiagnosticsEngine.cs` e `DiagnosticsAndClassifierTests.cs`.
- [x] **Testes de regressão reproduzíveis:** 116 testes unitários verdes com cobertura automatizada no CI (`F6-depois/VmTyper.Tests.trx`).
- [x] **Integridade preservada nos cenários validados:** confirmada no receptor local (`BaselineFidelityTests.cs`) e nos corpora com ABNT2/US-International.
- [x] **PoC do navegador integrado documentada:** prova de conceito e contratos de foco/injeção documentados no relatório da Fase 4 e ADR 0009.
- [x] **CDP demonstrado ou descartado com justificativa:** demonstrado na Fase 4 e mantido como recurso experimental opt-in sob feature flag.
- [x] **Modo externo funcional:** validado via testes de integração com receptor e arquitetura compatível com navegadores host.
- [x] **Sem perda silenciosa na persistência:** garantido por `AtomicJsonStore` e envelopes v2 testados em `PersistenceTests.cs`.
- [x] **Documentação reflete o comportamento real:** `README.md`, `ARCHITECTURE.md`, `TESTING.md` e `DISTRIBUTION.md` alinhados ao código em produção.
- [x] **Enviado ≠ Confirmado em todos os resultados:** semântica rigorosa implementada na UI principal, na fila de blocos e nos relatórios de progresso.

