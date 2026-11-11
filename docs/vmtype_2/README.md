# VmTyper 2.0: plano de implementação

Este diretório reúne três coisas: a análise do prompt mestre do VmTyper 2.0, a avaliação técnica do código atual e o passo a passo de implementação dividido em fases.

> **Estado:** documentação de planejamento. Nenhum código de produção foi alterado para produzi-la. A única execução feita foi um build de linha de base (seção 1 de [02-avaliacao-da-implementacao.md](02-avaliacao-da-implementacao.md)).

## Como ler

| Documento | Para que serve |
|---|---|
| [01-analise-do-prompt.md](01-analise-do-prompt.md) | Pontos fortes, lacunas, contradições e ajustes recomendados ao prompt |
| [02-avaliacao-da-implementacao.md](02-avaliacao-da-implementacao.md) | Auditoria do código: problemas A–I confirmados (ou não) com arquivo e linha, mais problemas novos |
| [03-plano-de-fases.md](03-plano-de-fases.md) | Visão geral das fases, dependências, ordem ajustada, marcos e pontos que exigem pessoa |
| [fases/fase-0-auditoria-e-linha-de-base.md](fases/fase-0-auditoria-e-linha-de-base.md) | Branch, higiene do repositório, projeto de testes, testes de caracterização |
| [fases/fase-1-estabilizacao.md](fases/fase-1-estabilizacao.md) | Fila, motor de teclado, TargetGuard, persistência, logs |
| [fases/fase-1b-migracao-dotnet10.md](fases/fase-1b-migracao-dotnet10.md) | Migração isolada .NET 8 → .NET 10 LTS |
| [fases/fase-2-motor-adaptativo.md](fases/fase-2-motor-adaptativo.md) | Temporização de alta resolução, perfis, scheduler, métricas |
| [fases/fase-3-duplicacoes.md](fases/fase-3-duplicacoes.md) | Ground truth, diagnóstico, matriz de experimentos, prevenção |
| [fases/fase-4-webview2.md](fases/fase-4-webview2.md) | PoC WebView2, SendInput vs CDP, foco, autenticação |
| [fases/fase-5-interface.md](fases/fase-5-interface.md) | Painel principal, configurações avançadas, diagnóstico visual |
| [fases/fase-6-qualidade-e-repositorio.md](fases/fase-6-qualidade-e-repositorio.md) | Suíte final, regressão, CI, documentação |
| [templates/relatorio-de-fase.md](templates/relatorio-de-fase.md) | Modelo obrigatório do relatório ao fim de cada fase (a antiga "Fase 7") |
| [templates/adr.md](templates/adr.md) | Modelo de registro de decisão arquitetural |

## Convenções

- Os caminhos de código são relativos à raiz do repositório (`VmTyper/Services/...`).
- **Severidade:** Crítica (perda de dados ou texto errado sem aviso) > Alta > Média > Baixa.
- **Status de uma constatação:** *Confirmado no código* (lido e raciocinado), *Confirmado por teste* (só depois da Fase 0), *Hipótese* (precisa de evidência).
- Nenhum número de melhoria aparece aqui. Métricas só entram nos relatórios de fase, a partir de medições reais.
