# Plano de fases

## 1. Ordem ajustada (e por quê)

```
F0  Auditoria + linha de base + higiene + testes + harness local
 │
F1  Estabilização (fila, motor de teclado, TargetGuard, persistência, logs, coordenador de sessão)
 │
F1B Migração .NET 8 → .NET 10 LTS (isolada)
 │
F2  Motor de temporização (timer de alta resolução → perfis → scheduler → métricas)
 │        ╲
 │         F3a Motor de diagnóstico + harness navegador (pode começar em paralelo com F2)
 │        ╱
F3  Matriz de experimentos (local → navegador → VMware real*) + prevenção
 │
F4  PoC WebView2 → SendInput vs CDP* → integração experimental
 │
F5  Interface (consolidação)
 │
F6  Regressão final, CI, documentação
```
`*` = exige uma pessoa e/ou acesso autorizado (ver seção 4).

| Mudança em relação ao prompt | Justificativa |
|---|---|
| Higiene do repositório e projeto de testes sobem da F6 para a F0 | Cada build suja 29 arquivos rastreados, e sem testes as Fases 1–3 não conseguem cumprir "coberto por testes" |
| Harness de verdade-terreno local entra na F0/F1 | É ele que prova as correções da F1 e permite calibrar a F2 |
| .NET 10 vira a F1B | O .NET 8 sai de suporte em 10/11/2026. A migração continua isolada, com a suíte da F1 como rede de segurança |
| A F3a (diagnóstico) pode correr em paralelo com a F2 | Os valores-padrão dos perfis devem ser calibrados com dados da F3 |
| A "Fase 7" vira o template de relatório | Ela é um entregável por fase, não uma fase |
| O perfil padrão continua sendo "Legado" | Muda só quando a F3 trouxer evidência |

## 2. Princípios transversais (valem para todas as fases)

1. **Um PR por passo lógico.** Cada um compila, passa nos testes e pode ser revertido sozinho.
2. **Feature flags** em `AppSettings.Features` para tudo que é experimental: `AdaptiveTiming`, `TargetGuardStrict`, `EmbeddedBrowser`, `CdpInput` e `DetailedTrace`.
3. **Nada de texto em logs comuns.** O rastreamento detalhado só é ativado por opt-in explícito e expira ao fim da sessão.
4. **Linguagem dos relatórios:** usar "enviado", "confirmado no receptor X", "suspeito" e "desconhecido". Evitar "validado" e "resolvido" sem a evidência citada.
5. **ADR** ([template](templates/adr.md)) para cada decisão que mude contratos, formatos de arquivo, padrões ou dependências.
6. **Não tocar** na pasta `rea/` nem em arquivos fora do projeto.

## 3. Resumo das fases

| Fase | Objetivo | Principais entregáveis | Critério de saída | Tamanho relativo |
|---|---|---|---|---|
| [F0](fases/fase-0-auditoria-e-linha-de-base.md) | Base reproduzível | branch `feature/vmtyper2`, `.gitignore`, `tests/`, abstrações mínimas, testes de caracterização de A–I, receptor local, relatório de auditoria | build limpo sem sujar a árvore; testes que **reproduzem** os defeitos (vermelhos marcados `Known`) | M |
| [F1](fases/fase-1-estabilizacao.md) | Fluxos previsíveis | máquina de estados da fila, `KeyStrokeExecutor` com rastreamento de teclas, `TargetGuard`, `AtomicJsonStore`, `TypingSessionCoordinator`, correções N1–N4 | todos os testes de A–I e N1–N4 verdes; teste ponta a ponta com o receptor local | G |
| [F1B](fases/fase-1b-migracao-dotnet10.md) | Runtime suportado | `net10.0-windows`, `global.json` | mesma suíte verde antes e depois, sem mudança funcional | P |
| [F2](fases/fase-2-motor-adaptativo.md) | Ritmo preciso e configurável | `HighResolutionDelay`, `TypingProfile`, `TypingTimingPolicy`, `TypingScheduler`, `TypingMetrics`, perfis Legado/Rápido/Natural/Compatibilidade/Personalizado | testes determinísticos com semente; texto preservado em 100% das sequências planejadas; precisão de atraso medida e registrada | G |
| [F3](fases/fase-3-duplicacoes.md) | Evidência sobre duplicação | `TypingDiagnosticsEngine` (JSONL), página HTML de diagnóstico, receptor na VM, classificador de divergências, corpora, relatório quantitativo, políticas de prevenção | relatório separado em local / navegador / VMware, com N, contagens e condições; prevenção só onde há evidência | G |
| [F4](fases/fase-4-webview2.md) | Avaliar o navegador integrado | `experiments/WebView2Poc`, página de diagnóstico, adaptador CDP, relatório da PoC, integração com flag | PoC documentada; CDP demonstrado ou descartado com justificativa; modo externo intacto | G |
| [F5](fases/fase-5-interface.md) | Expor as funções com clareza | painel principal reorganizado, seleção de ambiente, configurações avançadas, painel de diagnóstico | estados Enviado/Confirmado/Suspeito/Desconhecido/Falha distintos; restaurar padrões | M |
| [F6](fases/fase-6-qualidade-e-repositorio.md) | Fechamento | suíte completa, regressão antes/depois, CI no Windows, README e ARCHITECTURE atualizados | checklist de aceite geral preenchido com evidências ou com "pendente" explícito | M |

P/M/G indicam esforço relativo entre as fases, não estimativa de horas.

## 4. Pontos que exigem intervenção humana

| Ponto | Fase | O que é necessário | Sem isso |
|---|---|---|---|
| Autorizar a branch e o push para o remoto | F0 | Ok do dono do repositório | O trabalho fica só local |
| Remover `bin/` e `obj/` do versionamento | F0 | Confirmação (é uma mudança grande no histórico de arquivos, mas não destrutiva) | Cada build continua sujando a árvore |
| VM de teste VMware autorizada + console HTML5 | F3, F4 | Acesso, uma VM descartável, permissão para rodar o receptor dentro dela | O relatório da F3 fica restrito a local + navegador e é marcado "VMware: pendente" |
| Layout de teclado da VM | F1 (N2), F3 | Informar ou confirmar (ABNT2? US-Intl?) | Mantém-se a suposição host = VM, com aviso na UI |
| Autenticação corporativa no WebView2 (SSO, certificados) | F4 | Testar login com a conta do usuário. O agente **nunca** digita credenciais | WebView2 limitado a páginas sem autenticação, registrado como limitação |
| Política de EDR para hook de teclado no receptor de teste | F0, F3 | Confirmar que pode rodar na máquina de teste | Usar apenas o receptor por TextBox (sem hook), com observabilidade menor |
| Aprovação do perfil padrão definitivo | Fim da F3 | Decisão com base no relatório | Mantém-se "Legado" |

## 5. Dependências técnicas entre os componentes novos

```
IInputInjector ─┐
IKeyboardLayout ├─► KeyStrokePlanner ─► KeyStrokeExecutor ─► TypingScheduler ─► TypingSessionCoordinator ─► MainViewModel
IDelayProvider ─┘          │                    │                  ▲   ▲                 ▲
                           │                    └► InjectedKeyTracker  │                 │
                           ▼                                       │   │                 │
                   PreflightReport                    TypingTimingPolicy(TypingProfile)  TargetGuard
                                                                       │
                                                       TypingDiagnosticsEngine / TypingMetrics
```

- `KeyStrokePlanner`: texto → lista de *unidades lógicas* (grapheme), cada uma resolvida para uma sequência de `KeyEvent` ou marcada `Unsupported`. É uma função pura e 100% testável.
- `KeyStrokeExecutor`: executa uma unidade, com garantia de liberação.
- `TypingScheduler`: aplica a temporização entre unidades e trata pausa, cancelamento e guarda.
- `TypingSessionCoordinator`: garante uma única sessão de injeção no processo (principal ou diagnóstico), é dono do ESC global e publica eventos.

## 6. Mapa de rastreabilidade: problemas → fase/passo

| Problema | Corrigido em |
|---|---|
| A, B, D, N9 | F1 §1 Fila |
| E, N1, N2, G, H, N8 | F1 §2 Motor de teclado |
| C, N13 | F1 §3 TargetGuard |
| F, N4 | F1 §4 Persistência e logs |
| N3, N10 | F1 §5 Coordenador de sessão |
| I | F1 §6 Configurações |
| N5, N12 | F2 §1 Temporização precisa |
| N11 | F3 (teste com o receptor) + documentação |
| N6, N7 | F0 (relatório) + F6 (README) |
