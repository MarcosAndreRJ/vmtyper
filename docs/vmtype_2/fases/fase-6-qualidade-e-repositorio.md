# Fase 6: Qualidade, regressão e repositório

**Objetivo:** fechar o ciclo com a suíte completa, a comparação antes/depois, o CI e uma documentação que reflita o comportamento real.

> Itens do prompt original que **já foram antecipados**: `.gitignore` e remoção de artefatos (F0), estrutura de testes (F0) e migração para o .NET 10 (F1B). Aqui só se verifica que eles continuam válidos.

---

## §1 Completar a suíte
Matriz de cobertura exigida pelo prompt (6.1), com o local de cada item:

| Área | Projeto / classe de teste |
|---|---|
| Mapeamento de teclas, acentuação, teclas mortas, modificadores | `KeyStrokePlannerTests` (layouts ABNT2/US-Intl falsos) + integração no receptor |
| Cancelamento, pausa e retomada | `TypingSchedulerTests` |
| Falhas parciais de envio | `KeyStrokeExecutorTests` (`RecordingInjector` com falha programada) |
| Mudança de foco | `TargetGuardTests` + integração com segunda janela |
| Reentrância | `TypingSessionCoordinatorTests` |
| Fila de blocos | `TypingQueueServiceTests` (tabela de transições completa) |
| Persistência | `AtomicJsonStoreTests` |
| Substituições | `SubstitutionTests` |
| Perfis adaptativos | `TypingTimingPolicyTests` (property-based) |
| Tratamento de erros | testes por serviço + `DispatcherUnhandledException` |
| Descarte de recursos | `GlobalHotkeyService`, WinEventHook e WebView2: registros removidos no `Dispose` |
| Compatibilidade entre métodos | integração: o mesmo corpus por Físico/Unicode/(CDP) produz o mesmo texto no receptor ou na página |

**Categorias** (`[Trait("Category", ...)]`):
- `Unit`: roda em qualquer lugar, inclusive no CI.
- `Integration`: precisa de desktop interativo e injeta no receptor local.
- `E2E-Browser`: precisa de navegador e da página de diagnóstico.
- `E2E-VMware`: **manual**, segue o roteiro da F3 §3 e é registrado no relatório.

## §2 Regressão antes/depois
1. Rodar a mesma suíte e os mesmos corpora no commit `baseline/v1` (com os testes da F0 aplicados por cherry-pick numa branch temporária) e no `HEAD`.
2. Tabela no relatório: total, aprovados, reprovados, ignorados (com motivo), cobertura (`coverlet`, se disponível), regressões introduzidas e correções.
3. Toda falha conhecida remanescente fica listada, com issue ou justificativa.

## §3 CI (GitHub Actions, `windows-latest`)
`.github/workflows/build.yml`:
```yaml
on: [push, pull_request]
jobs:
  build:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { global-json-file: global.json }
      - run: dotnet restore
      - run: dotnet build -c Release --no-restore -warnaserror
      - run: dotnet test -c Release --no-build --filter "Category=Unit" --logger trx --collect:"XPlat Code Coverage"
      - uses: actions/upload-artifact@v4
        with: { name: test-results, path: '**/TestResults/**' }
```
Os testes `Integration`/`E2E` **não** rodam no CI, porque o runner não tem sessão interativa confiável. O README explica isso.

O push do workflow depende da autorização para enviar ao remoto.

## §4 Documentação
| Arquivo | Conteúdo |
|---|---|
| `README.md` | Reescrever as seções 0.x como "Histórico de mudanças". **Remover** afirmações de causa raiz sem evidência (N6). Corrigir links (N7) e atualizar build/SDK, uso, perfis, TargetGuard e limitações |
| `docs/ARCHITECTURE.md` | Diagrama de componentes (plano §5), fluxo de uma sessão, contratos (`IInputInjector`, `IDelayProvider`...) e formatos de arquivo (envelopes v2) |
| `docs/TESTING.md` | Como rodar cada categoria, como usar o receptor e a página, roteiro VMware |
| `docs/adr/NNNN-*.md` | Decisões tomadas nas F1–F5 |
| `docs/DISTRIBUTION.md` | Publicação (`dotnet publish -c Release -r win-x64`, opção self-contained), requisitos (runtime e WebView2 Runtime opcional) e onde ficam os dados do usuário |
| `docs/vmtype_2/relatorios/` | Relatórios de todas as fases |

## §5 Limpeza final
- Rodar `git status` e comparar com o estado inicial registrado na F0. Nada fora do projeto pode ter sido alterado (a pasta `rea/` fica intocada).
- Revisar as feature flags: o que foi aprovado vira padrão por ADR, e o resto continua experimental.

## Critérios de aceite da F6
- [ ] CI verde em `Unit`.
- [ ] Relatório de regressão antes/depois publicado.
- [ ] README e ARCHITECTURE refletem o comportamento real, com as limitações declaradas.
- [ ] O checklist de aceite geral do prompt foi preenchido item a item, com link para a evidência ou com "Pendente: motivo".

## Checklist de aceite geral (para copiar no relatório final)
- [ ] Defeitos comprovados da fila corrigidos → testes F1 §1
- [ ] Motor de teclado com tratamento de erros e liberação → testes F1 §2
- [ ] Destino monitorado durante a sessão → testes F1 §3
- [ ] Perfis de digitação disponíveis → F2
- [ ] Comportamento temporal configurável e testável → F2 testes com semente
- [ ] Diagnósticos de duplicidade → F3
- [ ] Testes de regressão reproduzíveis → F6 §2
- [ ] Integridade preservada nos cenários validados → F3 (listar os cenários)
- [ ] PoC do navegador integrado documentada → F4
- [ ] CDP demonstrado ou descartado com justificativa → F4
- [ ] Modo externo funcional → suíte + teste manual
- [ ] Sem perda silenciosa na persistência → F1 §4
- [ ] Documentação reflete o comportamento real → F6 §4
- [ ] Enviado ≠ Confirmado em todos os resultados → F5 §4
